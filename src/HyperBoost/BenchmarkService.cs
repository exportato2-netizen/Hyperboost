using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HyperBoost;

public enum BenchmarkMode
{
    Off,
    On
}

public enum BenchmarkScenarioType
{
    BuiltInBenchmark,
    RepeatableRoute,
    ManualScene,
    StressTest
}

public static class BenchmarkScenarioTypeExtensions
{
    public static string DisplayName(this BenchmarkScenarioType value) => value switch
    {
        BenchmarkScenarioType.BuiltInBenchmark => "Benchmark integrado",
        BenchmarkScenarioType.RepeatableRoute => "Ruta repetible",
        BenchmarkScenarioType.ManualScene => "Escena manual",
        BenchmarkScenarioType.StressTest => "Stress test",
        _ => value.ToString()
    };
}

public sealed record BenchmarkPlanItem(int Sequence, int Pair, BenchmarkMode Mode)
{
    public string Label => $"Pasada {Sequence} · Par {Pair} · HyperBoost {Mode.ToString().ToUpperInvariant()}";
}

public sealed record BenchmarkCaptureResult(
    int Sequence,
    int Pair,
    BenchmarkMode Mode,
    DateTime CapturedAt,
    string CsvPath,
    string FrameTimeSource,
    int Frames,
    double DurationSeconds,
    double AverageFps,
    double Low1Fps,
    double Low01Fps,
    double MedianFrameTimeMs,
    double P95FrameTimeMs,
    double P99FrameTimeMs,
    double P999FrameTimeMs,
    int SevereStallCount,
    double SevereStallThresholdMs,
    double SevereStallRatePercent,
    int RelativeSpikeCount,
    double RelativeSpikeRatePercent,
    int ExtremeStallCount,
    int InvalidDataCount,
    int PercentileExcludedExtremeCount,
    int P999TailSamples,
    bool P999Indicative,
    double HyperBoostCpuPercent,
    double HyperBoostWorkingSetMb,
    double HyperBoostIoMb,
    double PresentMonCpuPercent,
    double PresentMonWorkingSetMb,
    double PresentMonIoMb)
{
    // Compatibilidad de lectura/UI con sesiones Beta 0.5/0.6.
    public int StutterCount => SevereStallCount;
    public double StutterThresholdMs => SevereStallThresholdMs;
    public double StutterRatePercent => SevereStallRatePercent;
}

public sealed record ConfidenceInterval(
    double? LowerPercent,
    double? UpperPercent,
    bool Stable,
    int PairCount,
    string Method)
{
    public string Display => Stable && LowerPercent.HasValue && UpperPercent.HasValue
        ? $"IC 95% [{LowerPercent:+0.00;-0.00;0.00}%, {UpperPercent:+0.00;-0.00;0.00}%]"
        : "INTERVALO NO ESTABLE / EVIDENCIA PRELIMINAR";
}

public sealed record BenchmarkAnalysis(
    string Verdict,
    string EvidenceLevel,
    int CompletedPairs,
    double OffAverageFps,
    double OnAverageFps,
    double AverageFpsDeltaPercent,
    double OffLow1Fps,
    double OnLow1Fps,
    double Low1DeltaPercent,
    double OffLow01Fps,
    double OnLow01Fps,
    double Low01DeltaPercent,
    double OffP95Ms,
    double OnP95Ms,
    double P95ImprovementPercent,
    double OffP99Ms,
    double OnP99Ms,
    double P99ImprovementPercent,
    double OffStutterRatePercent,
    double OnStutterRatePercent,
    double StutterRateImprovementPercent,
    double BaselineNoiseAverageFpsPercent,
    double BaselineNoiseLow1Percent,
    double BaselineNoiseP99Percent,
    ConfidenceInterval AverageFpsConfidenceInterval,
    ConfidenceInterval P99ConfidenceInterval,
    double OffRelativeSpikeRatePercent,
    double OnRelativeSpikeRatePercent,
    double RelativeSpikeRateImprovementPercent,
    int TotalExtremeStalls,
    bool P999Indicative,
    string ReportText);

public sealed class AbBenchmarkSession
{
    readonly List<BenchmarkPlanItem> plan;
    readonly List<BenchmarkCaptureResult> results = [];

    public AbBenchmarkSession(
        GameProcessCandidate game,
        int pairs,
        int captureSeconds,
        bool useEcoQos,
        bool useMemoryPriority,
        BenchmarkScenarioType scenarioType,
        string? userReportedResolution,
        string? userReportedSettings = null)
    {
        if (pairs is not (3 or 6 or 9)) throw new ArgumentOutOfRangeException(nameof(pairs), "Los niveles válidos son 3, 6 o 9 pares.");
        if (captureSeconds is < 10 or > 300) throw new ArgumentOutOfRangeException(nameof(captureSeconds));

        Game = game;
        Pairs = pairs;
        CaptureSeconds = captureSeconds;
        UseEcoQos = useEcoQos;
        UseMemoryPriority = useMemoryPriority;
        ScenarioType = scenarioType;
        UserReportedResolution = string.IsNullOrWhiteSpace(userReportedResolution) ? null : userReportedResolution.Trim();
        UserReportedSettings = string.IsNullOrWhiteSpace(userReportedSettings) ? null : userReportedSettings.Trim();
        HyperBoostVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        EnvironmentFingerprint = BenchmarkEnvironmentReader.Capture(
            game, HyperBoostVersion, pairs, captureSeconds, useEcoQos, useMemoryPriority,
            scenarioType, UserReportedResolution, UserReportedSettings);
        plan = BuildCounterbalancedPlan(pairs);

        var invalid = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(game.Name.Select(c => invalid.Contains(c) ? '_' : c));
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HyperBoost", "Benchmarks",
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}-pid{game.Id}");
        Directory.CreateDirectory(RootDirectory);
    }

    public GameProcessCandidate Game { get; }
    public int Pairs { get; }
    public int CaptureSeconds { get; }
    public bool UseEcoQos { get; }
    public bool UseMemoryPriority { get; }
    public BenchmarkScenarioType ScenarioType { get; }
    public string? UserReportedResolution { get; }
    public string? UserReportedSettings { get; }
    public string HyperBoostVersion { get; }
    public BenchmarkEnvironmentFingerprint EnvironmentFingerprint { get; }
    public string? SessionFrameTimeSource { get; private set; }
    public List<double> GateDurationMs { get; } = [];
    public List<double> GpuScannerDurationMs { get; } = [];
    public string RootDirectory { get; }
    public IReadOnlyList<BenchmarkPlanItem> Plan => plan;
    public IReadOnlyList<BenchmarkCaptureResult> Results => results;
    public bool IsComplete => results.Count >= plan.Count;
    public BenchmarkPlanItem? Next => IsComplete ? null : plan[results.Count];

    public void Add(BenchmarkCaptureResult result)
    {
        var expected = Next ?? throw new InvalidOperationException("La sesión A/B ya está completa.");
        if (result.Sequence != expected.Sequence || result.Pair != expected.Pair || result.Mode != expected.Mode)
            throw new InvalidOperationException("La captura no corresponde a la siguiente pasada del plan A/B.");
        if (SessionFrameTimeSource is null)
            SessionFrameTimeSource = result.FrameTimeSource;
        else if (!string.Equals(SessionFrameTimeSource, result.FrameTimeSource, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"PASADA INVÁLIDA: la sesión está bloqueada en {SessionFrameTimeSource}, pero la captura entregó {result.FrameTimeSource}.");
        results.Add(result);
    }

    public void RecordGateOverhead(double gateDurationMs, double gpuScannerDurationMs)
    {
        if (gateDurationMs >= 0) GateDurationMs.Add(gateDurationMs);
        if (gpuScannerDurationMs >= 0) GpuScannerDurationMs.Add(gpuScannerDurationMs);
    }

    public BenchmarkAnalysis Analyze()
    {
        var analysis = BenchmarkAnalyzer.Analyze(results);
        return analysis with { ReportText = BuildSessionHeader() + analysis.ReportText };
    }

    public async Task SaveReportAsync(BenchmarkAnalysis analysis, CancellationToken cancellationToken = default)
    {
        var reportPath = Path.Combine(RootDirectory, "HyperBoost-AB-report.txt");
        await File.WriteAllTextAsync(reportPath, analysis.ReportText, Encoding.UTF8, cancellationToken);

        var jsonPath = Path.Combine(RootDirectory, "HyperBoost-AB-results.json");
        var payload = new
        {
            schemaVersion = 4,
            hyperBoostVersion = HyperBoostVersion,
            presentMonVersion = "2.5.1",
            presentMonSha256 = PresentMonBenchmarkService.PresentMonExpectedSha256,
            game = Game,
            environmentFingerprint = EnvironmentFingerprint,
            pairs = Pairs,
            captureSeconds = CaptureSeconds,
            benchmarkType = ScenarioType.DisplayName(),
            userReportedResolution = UserReportedResolution,
            userReportedSettings = UserReportedSettings,
            sessionFrameTimeSource = SessionFrameTimeSource,
            onPolicies = new { ecoQos = UseEcoQos, memoryPriority = UseMemoryPriority },
            overhead = new
            {
                gateAverageMs = GateDurationMs.Count == 0 ? (double?)null : GateDurationMs.Average(),
                gpuScannerAverageMs = GpuScannerDurationMs.Count == 0 ? (double?)null : GpuScannerDurationMs.Average(),
                captureHyperBoostCpuAveragePercent = results.Count == 0 ? (double?)null : results.Average(x => x.HyperBoostCpuPercent),
                captureHyperBoostWorkingSetAverageMb = results.Count == 0 ? (double?)null : results.Average(x => x.HyperBoostWorkingSetMb),
                captureHyperBoostIoAverageMb = results.Count == 0 ? (double?)null : results.Average(x => x.HyperBoostIoMb),
                presentMonCpuAveragePercent = results.Count == 0 ? (double?)null : results.Average(x => x.PresentMonCpuPercent),
                presentMonWorkingSetAverageMb = results.Count == 0 ? (double?)null : results.Average(x => x.PresentMonWorkingSetMb),
                presentMonIoAverageMb = results.Count == 0 ? (double?)null : results.Average(x => x.PresentMonIoMb)
            },
            plan,
            createdAt = DateTime.Now,
            results,
            analysis
        };
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8,
            cancellationToken);
    }

    string BuildSessionHeader()
    {
        var sb = new StringBuilder();
        sb.AppendLine("SESIÓN REPRODUCIBLE")
          .AppendLine($"HyperBoost: {HyperBoostVersion}")
          .AppendLine($"PresentMon: 2.5.1 · SHA-256 {PresentMonBenchmarkService.PresentMonExpectedSha256}")
          .AppendLine($"Juego: {Game.Name} · PID inicial {Game.Id} · {Game.WindowTitle}")
          .AppendLine($"Tipo: {ScenarioType.DisplayName()} · resolución informada: {UserReportedResolution ?? "no informada"}")
          .AppendLine($"Ajustes/versión del juego: {UserReportedSettings ?? "no informados · la evidencia histórica quedará limitada"}")
          .AppendLine($"Plan: {Pairs} pares ({EvidenceLabel(Pairs)}) · {CaptureSeconds}s por pasada · AB/BA contrabalanceado")
          .AppendLine($"Fuente de frametime bloqueada: {SessionFrameTimeSource ?? "se decide en la primera pasada válida"}")
          .AppendLine($"Políticas ON congeladas: EcoQoS={(UseEcoQos ? "sí" : "no")} · Memory Priority EXPERIMENTAL={(UseMemoryPriority ? "sí" : "no")}")
          .AppendLine($"Overhead Gate observado: {(GateDurationMs.Count == 0 ? "sin muestra" : $"{GateDurationMs.Average():0.0} ms promedio")} · scanner GPU WMI: {(GpuScannerDurationMs.Count == 0 ? "sin muestra" : $"{GpuScannerDurationMs.Average():0.0} ms promedio")}")
          .AppendLine(new string('-', 72));
        return sb.ToString();
    }

    public static string EvidenceLabel(int pairs) => pairs switch
    {
        3 => "Rápido · resultado preliminar",
        6 => "Estándar · recomendado",
        9 => "Extendido · confirmación fuerte",
        _ => "No válido"
    };

    static List<BenchmarkPlanItem> BuildCounterbalancedPlan(int pairs)
    {
        var list = new List<BenchmarkPlanItem>(pairs * 2);
        var sequence = 1;
        for (var pair = 1; pair <= pairs; pair++)
        {
            var first = pair % 2 == 1 ? BenchmarkMode.Off : BenchmarkMode.On;
            var second = first == BenchmarkMode.Off ? BenchmarkMode.On : BenchmarkMode.Off;
            list.Add(new(sequence++, pair, first));
            list.Add(new(sequence++, pair, second));
        }
        return list;
    }
}

public sealed class PresentMonBenchmarkService
{
    public const string PresentMonFileName = "PresentMon-2.5.1-x64.exe";
    public const string PresentMonExpectedSha256 = "9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191";
    public const string CaptureHotkey = "CTRL+SHIFT+F11";

    public string PresentMonPath => Path.Combine(AppContext.BaseDirectory, PresentMonFileName);

    public string ValidatePresentMon()
    {
        if (!File.Exists(PresentMonPath))
            throw new FileNotFoundException($"No se encontró {PresentMonFileName} junto a HyperBoost.exe. Usa el ZIP oficial completo de HyperBoost Beta 0.7.", PresentMonPath);

        using var stream = File.OpenRead(PresentMonPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, PresentMonExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"PresentMon no pasó la verificación SHA-256. Esperado {PresentMonExpectedSha256}; recibido {actual}. HyperBoost no ejecutará un binario no verificado.");

        return actual;
    }

    public async Task<BenchmarkCaptureResult> CaptureWithHotkeyAsync(
        BenchmarkPlanItem plan,
        int targetPid,
        int captureSeconds,
        string sessionDirectory,
        string? requiredFrameTimeSource,
        Action? readyForHotkey,
        CancellationToken cancellationToken = default)
    {
        ValidatePresentMon();
        if (!IsProcessAlive(targetPid))
            throw new InvalidOperationException("El proceso del juego terminó antes de iniciar la captura.");

        Directory.CreateDirectory(sessionDirectory);
        var mode = plan.Mode.ToString().ToUpperInvariant();
        var csvPath = Path.Combine(sessionDirectory, $"pass-{plan.Sequence:00}-pair-{plan.Pair:00}-{mode}.csv");
        if (File.Exists(csvPath)) File.Delete(csvPath);

        var psi = new ProcessStartInfo(PresentMonPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = sessionDirectory
        };

        AddArg(psi, "--process_id", targetPid.ToString(CultureInfo.InvariantCulture));
        AddArg(psi, "--output_file", csvPath);
        AddArg(psi, "--hotkey", CaptureHotkey);
        AddArg(psi, "--timed", captureSeconds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--terminate_after_timed");
        psi.ArgumentList.Add("--no_console_stats");
        psi.ArgumentList.Add("--exclude_dropped");
        psi.ArgumentList.Add("--no_track_input");
        AddArg(psi, "--session_name", $"HB_AB_{targetPid}_{plan.Sequence}_{Guid.NewGuid():N}");

        using var process = new Process { StartInfo = psi };
        using var hyperBoostProcess = Process.GetCurrentProcess();
        var hyperStart = ProcessOverheadSnapshot.Capture(hyperBoostProcess);
        if (!process.Start()) throw new InvalidOperationException("Windows no pudo iniciar PresentMon.");
        var presentStart = ProcessOverheadSnapshot.Capture(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await Task.Delay(700, cancellationToken);
            if (process.HasExited)
            {
                var earlyOut = await stdoutTask;
                var earlyErr = await stderrTask;
                throw new InvalidOperationException($"PresentMon terminó antes de quedar listo (código {process.ExitCode}). {earlyErr} {earlyOut}".Trim());
            }

            var hyperReady = ProcessOverheadSnapshot.Capture(hyperBoostProcess);
            var presentReady = ProcessOverheadSnapshot.Capture(process);
            readyForHotkey?.Invoke();
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"PresentMon terminó con código {process.ExitCode}. {stderr} {stdout}".Trim());
            if (!File.Exists(csvPath))
                throw new InvalidDataException("PresentMon terminó sin generar CSV. Confirma que el juego siguió abierto y que pulsaste CTRL+SHIFT+F11 una sola vez para iniciar la captura.");

            var hyperEnd = ProcessOverheadSnapshot.Capture(hyperBoostProcess);
            var presentEnd = ProcessOverheadSnapshot.Capture(process);
            var result = ParseCsv(csvPath, plan, requiredFrameTimeSource) with
            {
                HyperBoostCpuPercent = ProcessOverheadSnapshot.CpuPercent(hyperStart, hyperEnd),
                HyperBoostWorkingSetMb = Math.Max(hyperReady.WorkingSetBytes, hyperEnd.WorkingSetBytes) / 1_048_576d,
                HyperBoostIoMb = ProcessOverheadSnapshot.IoMb(hyperStart, hyperEnd),
                PresentMonCpuPercent = ProcessOverheadSnapshot.CpuPercent(presentStart, presentEnd),
                PresentMonWorkingSetMb = Math.Max(presentReady.WorkingSetBytes, presentEnd.WorkingSetBytes) / 1_048_576d,
                PresentMonIoMb = ProcessOverheadSnapshot.IoMb(presentStart, presentEnd)
            };
            var sidecar = Path.ChangeExtension(csvPath, ".result.json");
            await File.WriteAllTextAsync(
                sidecar,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8,
                cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(csvPath);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    public static BenchmarkCaptureResult ParseCsv(string csvPath, BenchmarkPlanItem plan, string? requiredFrameTimeSource = null)
    {
        using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) throw new InvalidDataException("CSV de PresentMon vacío.");

        var headers = ParseCsvLine(headerLine);
        var index = headers
            .Select((name, i) => (name: name.Trim(), i))
            .Where(x => x.name.Length > 0)
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().i, StringComparer.OrdinalIgnoreCase);

        var candidates = new[] { "DisplayedTime", "MsBetweenDisplayChange", "MsBetweenPresents", "FrameTime", "MsBetweenAppStart" };
        var collected = candidates.ToDictionary(x => x, _ => new List<double>(), StringComparer.OrdinalIgnoreCase);
        var rows = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            rows++;
            foreach (var metric in candidates)
            {
                if (!index.TryGetValue(metric, out var col) || col >= fields.Count) continue;
                if (!TryInvariantDouble(fields[col], out var value)) continue;
                // 200/500/1000 ms pueden ser freezes reales. Solo se rechazan
                // valores físicamente inválidos o claramente corruptos (>10 s).
                if (value < 0.1 || value > 10_000) continue;
                collected[metric].Add(value);
            }
        }

        if (rows < 30) throw new InvalidDataException($"PresentMon solo entregó {rows} filas; la muestra es demasiado pequeña para un benchmark.");

        string? source;
        if (!string.IsNullOrWhiteSpace(requiredFrameTimeSource))
        {
            source = candidates.FirstOrDefault(x => string.Equals(x, requiredFrameTimeSource, StringComparison.OrdinalIgnoreCase));
            if (source is null)
                throw new InvalidDataException($"PASADA INVÁLIDA: fuente bloqueada desconocida ({requiredFrameTimeSource}).");
            if (collected[source].Count < Math.Max(30, rows * 0.80))
                throw new InvalidDataException($"PASADA INVÁLIDA: la sesión usa {source}, pero esta captura solo tuvo {collected[source].Count}/{rows} valores válidos. No se cambió silenciosamente a otra fuente.");
        }
        else
        {
            source = candidates.FirstOrDefault(x => collected[x].Count >= Math.Max(30, rows * 0.80));
            if (source is null) throw new InvalidDataException("PASADA INVÁLIDA: el CSV no contiene una fuente de frametime utilizable en al menos 80% de las filas.");
        }

        var values = collected[source].ToList();
        if (values.Count > 2) values = values.Skip(1).ToList();
        if (values.Count < 30) throw new InvalidDataException("Muestra insuficiente después de limpiar los límites de captura.");

        var mean = values.Average();
        var sorted = values.OrderBy(x => x).ToList();
        var median = QuantileSorted(sorted, 0.50);
        var extremeStalls = values.Count(x => x >= 200);
        var percentileValues = values.Where(x => x < 200).OrderBy(x => x).ToList();
        var excludedExtreme = extremeStalls;
        if (percentileValues.Count < 30)
        {
            percentileValues = sorted;
            excludedExtreme = 0;
        }
        var p95 = QuantileSorted(percentileValues, 0.95);
        var p99 = QuantileSorted(percentileValues, 0.99);
        var p999 = QuantileSorted(percentileValues, 0.999);
        var severeThreshold = Math.Max(33.333, median * 2.5);
        var severeStalls = values.Count(x => x > severeThreshold);
        var relativeSpikes = CountRelativeSpikes(values);
        var duration = values.Sum() / 1000d;
        var p999TailSamples = Math.Max(1, (int)Math.Ceiling(percentileValues.Count * 0.001));
        var p999Indicative = p999TailSamples < 20;
        var invalidData = Math.Max(0, rows - collected[source].Count);

        return new BenchmarkCaptureResult(
            plan.Sequence,
            plan.Pair,
            plan.Mode,
            DateTime.Now,
            csvPath,
            source,
            values.Count,
            duration,
            1000d / mean,
            1000d / p99,
            1000d / p999,
            median,
            p95,
            p99,
            p999,
            severeStalls,
            severeThreshold,
            severeStalls * 100d / values.Count,
            relativeSpikes,
            relativeSpikes * 100d / values.Count,
            extremeStalls,
            invalidData,
            excludedExtreme,
            p999TailSamples,
            p999Indicative,
            0, 0, 0, 0, 0, 0);
    }

    internal static int CountRelativeSpikes(IReadOnlyList<double> frameTimes)
    {
        const int window = 31;
        const int minimumHistory = 15;
        var count = 0;
        var insideExcursion = false;

        for (var i = 0; i < frameTimes.Count; i++)
        {
            var start = Math.Max(0, i - window);
            var historyLength = i - start;
            if (historyLength < minimumHistory) continue;
            var history = frameTimes.Skip(start).Take(historyLength).OrderBy(x => x).ToList();
            var localMedian = QuantileSorted(history, 0.5);
            var threshold = Math.Max(localMedian * 1.8, localMedian + 5.0);
            var spike = frameTimes[i] >= threshold;

            if (spike && !insideExcursion)
            {
                count++;
                insideExcursion = true;
            }
            else if (!spike)
            {
                insideExcursion = false;
            }
        }
        return count;
    }

    public static string RunSelfTest()
        => BenchmarkSelfTests.Run();

    static void AddArg(ProcessStartInfo psi, string name, string value)
    {
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add(value);
    }

    static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static bool TryInvariantDouble(string value, out double result)
    {
        if (string.Equals(value.Trim(), "NA", StringComparison.OrdinalIgnoreCase))
        {
            result = 0;
            return false;
        }
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    }

    static double QuantileSorted(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var position = Math.Clamp(q, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
