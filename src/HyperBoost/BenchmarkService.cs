using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HyperBoost;

public enum BenchmarkMode
{
    Off,
    On
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
    int StutterCount,
    double StutterThresholdMs,
    double StutterRatePercent);

public sealed record BenchmarkAnalysis(
    string Verdict,
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
    string ReportText);

public sealed class AbBenchmarkSession
{
    readonly List<BenchmarkPlanItem> plan;
    readonly List<BenchmarkCaptureResult> results = [];

    public AbBenchmarkSession(GameProcessCandidate game, int pairs, int captureSeconds)
    {
        if (pairs is < 2 or > 9) throw new ArgumentOutOfRangeException(nameof(pairs));
        if (captureSeconds is < 10 or > 300) throw new ArgumentOutOfRangeException(nameof(captureSeconds));

        Game = game;
        Pairs = pairs;
        CaptureSeconds = captureSeconds;
        plan = BuildCounterbalancedPlan(pairs);

        var safeName = string.Concat(game.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HyperBoost", "Benchmarks",
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}-pid{game.Id}");
        Directory.CreateDirectory(RootDirectory);
    }

    public GameProcessCandidate Game { get; }
    public int Pairs { get; }
    public int CaptureSeconds { get; }
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
        results.Add(result);
    }

    public BenchmarkAnalysis Analyze() => BenchmarkAnalyzer.Analyze(results);

    public async Task SaveReportAsync(BenchmarkAnalysis analysis, CancellationToken cancellationToken = default)
    {
        var reportPath = Path.Combine(RootDirectory, "HyperBoost-AB-report.txt");
        await File.WriteAllTextAsync(reportPath, analysis.ReportText, Encoding.UTF8, cancellationToken);

        var jsonPath = Path.Combine(RootDirectory, "HyperBoost-AB-results.json");
        var payload = new
        {
            schema = 1,
            game = Game,
            pairs = Pairs,
            captureSeconds = CaptureSeconds,
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

    static List<BenchmarkPlanItem> BuildCounterbalancedPlan(int pairs)
    {
        var list = new List<BenchmarkPlanItem>(pairs * 2);
        var sequence = 1;
        for (var pair = 1; pair <= pairs; pair++)
        {
            // AB/BA alternado reduce el sesgo de calentamiento, boost y deriva térmica.
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
            throw new FileNotFoundException($"No se encontró {PresentMonFileName} junto a HyperBoost.exe. Usa el ZIP oficial completo de HyperBoost Beta 0.5.", PresentMonPath);

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

        psi.ArgumentList.Add("--process_id");
        psi.ArgumentList.Add(targetPid.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--output_file");
        psi.ArgumentList.Add(csvPath);
        psi.ArgumentList.Add("--hotkey");
        psi.ArgumentList.Add(CaptureHotkey);
        psi.ArgumentList.Add("--timed");
        psi.ArgumentList.Add(captureSeconds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--terminate_after_timed");
        psi.ArgumentList.Add("--no_console_stats");
        psi.ArgumentList.Add("--exclude_dropped");
        psi.ArgumentList.Add("--no_track_input");
        psi.ArgumentList.Add("--session_name");
        psi.ArgumentList.Add($"HB_AB_{targetPid}_{plan.Sequence}_{Guid.NewGuid():N}");

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("Windows no pudo iniciar PresentMon.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            // Da tiempo a PresentMon para registrar su hotkey global. Si muere aquí, el error es inmediato.
            await Task.Delay(700, cancellationToken);
            if (process.HasExited)
            {
                var earlyOut = await stdoutTask;
                var earlyErr = await stderrTask;
                throw new InvalidOperationException($"PresentMon terminó antes de quedar listo (código {process.ExitCode}). {earlyErr} {earlyOut}".Trim());
            }

            readyForHotkey?.Invoke();
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"PresentMon terminó con código {process.ExitCode}. {stderr} {stdout}".Trim());
            if (!File.Exists(csvPath))
                throw new InvalidDataException("PresentMon terminó sin generar CSV. Confirma que el juego siguió abierto y que pulsaste CTRL+SHIFT+F11 una sola vez para iniciar la captura.");

            var result = ParseCsv(csvPath, plan);
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

    public static BenchmarkCaptureResult ParseCsv(string csvPath, BenchmarkPlanItem plan)
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
                if (value < 0.1 || value > 1000) continue;
                collected[metric].Add(value);
            }
        }

        if (rows < 30) throw new InvalidDataException($"PresentMon solo entregó {rows} filas; la muestra es demasiado pequeña para un benchmark.");

        // DisplayedTime es la primera preferencia si cubre la gran mayoría de la captura; si no,
        // se usa la cadencia de Present() para no mezclar fuentes dentro de una misma pasada.
        var source = candidates.FirstOrDefault(x => collected[x].Count >= Math.Max(30, rows * 0.80));
        if (source is null) throw new InvalidDataException("El CSV no contiene una métrica de frametime utilizable en al menos 80% de las filas.");

        var values = collected[source];
        if (values.Count > 2)
        {
            // El primer intervalo puede comenzar antes de la ventana exacta de grabación.
            values = values.Skip(1).ToList();
        }

        values.Sort();
        if (values.Count < 30) throw new InvalidDataException("Muestra insuficiente después de limpiar los límites de captura.");

        var mean = values.Average();
        var median = QuantileSorted(values, 0.50);
        var p95 = QuantileSorted(values, 0.95);
        var p99 = QuantileSorted(values, 0.99);
        var p999 = QuantileSorted(values, 0.999);
        var stutterThreshold = Math.Max(33.333, median * 2.5);
        var stutters = values.Count(x => x > stutterThreshold);
        var duration = values.Sum() / 1000d;

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
            stutters,
            stutterThreshold,
            stutters * 100d / values.Count);
    }

    public static string RunSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "HyperBoost-BenchmarkSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var csv = Path.Combine(root, "synthetic.csv");
            var sb = new StringBuilder();
            sb.AppendLine("Application,ProcessID,DisplayedTime,MsBetweenPresents");
            for (var i = 0; i < 240; i++)
            {
                var ms = i is 120 or 180 ? 45.0 : 16.6667;
                sb.AppendLine($"game.exe,123,{ms.ToString(CultureInfo.InvariantCulture)},{ms.ToString(CultureInfo.InvariantCulture)}");
            }
            File.WriteAllText(csv, sb.ToString(), Encoding.UTF8);

            var off = ParseCsv(csv, new BenchmarkPlanItem(1, 1, BenchmarkMode.Off));
            if (off.Frames < 200 || off.AverageFps is < 50 or > 65 || off.P99FrameTimeMs < 16)
                throw new InvalidOperationException("Benchmark parser self-test produced implausible metrics.");

            var samples = new List<BenchmarkCaptureResult>();
            for (var pair = 1; pair <= 3; pair++)
            {
                samples.Add(off with { Sequence = pair * 2 - 1, Pair = pair, Mode = BenchmarkMode.Off, AverageFps = 100, Low1Fps = 70, Low01Fps = 55, P95FrameTimeMs = 13, P99FrameTimeMs = 18, StutterRatePercent = 1.5 });
                samples.Add(off with { Sequence = pair * 2, Pair = pair, Mode = BenchmarkMode.On, AverageFps = 104, Low1Fps = 76, Low01Fps = 60, P95FrameTimeMs = 12, P99FrameTimeMs = 16, StutterRatePercent = 1.0 });
            }
            var analysis = BenchmarkAnalyzer.Analyze(samples);
            if (!analysis.Verdict.StartsWith("MEJORA MEDIBLE", StringComparison.Ordinal))
                throw new InvalidOperationException("Benchmark analyzer self-test did not recognize a consistent synthetic improvement.");

            return "Benchmark self-test OK";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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

public static class BenchmarkAnalyzer
{
    public static BenchmarkAnalysis Analyze(IReadOnlyList<BenchmarkCaptureResult> captures)
    {
        var off = captures.Where(x => x.Mode == BenchmarkMode.Off).ToList();
        var on = captures.Where(x => x.Mode == BenchmarkMode.On).ToList();
        if (off.Count == 0 || on.Count == 0)
            throw new InvalidOperationException("Se necesita al menos una pasada OFF y una ON.");

        var pairs = captures.GroupBy(x => x.Pair)
            .Select(g => new { Off = g.FirstOrDefault(x => x.Mode == BenchmarkMode.Off), On = g.FirstOrDefault(x => x.Mode == BenchmarkMode.On) })
            .Where(x => x.Off is not null && x.On is not null)
            .Select(x => (Off: x.Off!, On: x.On!))
            .ToList();
        if (pairs.Count == 0) throw new InvalidOperationException("No hay pares OFF/ON completos.");

        var offAvg = off.Average(x => x.AverageFps);
        var onAvg = on.Average(x => x.AverageFps);
        var offLow1 = off.Average(x => x.Low1Fps);
        var onLow1 = on.Average(x => x.Low1Fps);
        var offLow01 = off.Average(x => x.Low01Fps);
        var onLow01 = on.Average(x => x.Low01Fps);
        var offP95 = off.Average(x => x.P95FrameTimeMs);
        var onP95 = on.Average(x => x.P95FrameTimeMs);
        var offP99 = off.Average(x => x.P99FrameTimeMs);
        var onP99 = on.Average(x => x.P99FrameTimeMs);
        var offStutter = off.Average(x => x.StutterRatePercent);
        var onStutter = on.Average(x => x.StutterRatePercent);

        var pairAvg = pairs.Select(x => HigherBetterDelta(x.Off.AverageFps, x.On.AverageFps)).ToList();
        var pairLow1 = pairs.Select(x => HigherBetterDelta(x.Off.Low1Fps, x.On.Low1Fps)).ToList();
        var pairLow01 = pairs.Select(x => HigherBetterDelta(x.Off.Low01Fps, x.On.Low01Fps)).ToList();
        var pairP95 = pairs.Select(x => LowerBetterDelta(x.Off.P95FrameTimeMs, x.On.P95FrameTimeMs)).ToList();
        var pairP99 = pairs.Select(x => LowerBetterDelta(x.Off.P99FrameTimeMs, x.On.P99FrameTimeMs)).ToList();
        var pairStutter = pairs.Select(x => LowerBetterDelta(x.Off.StutterRatePercent, x.On.StutterRatePercent)).ToList();

        var avgDelta = pairAvg.Average();
        var low1Delta = pairLow1.Average();
        var low01Delta = pairLow01.Average();
        var p95Delta = pairP95.Average();
        var p99Delta = pairP99.Average();
        var stutterDelta = pairStutter.Average();

        var noiseAvg = CoefficientOfVariation(off.Select(x => x.AverageFps));
        var noiseLow1 = CoefficientOfVariation(off.Select(x => x.Low1Fps));
        var noiseP99 = CoefficientOfVariation(off.Select(x => x.P99FrameTimeMs));
        var thresholdAvg = Math.Max(1.0, noiseAvg * 1.5);
        var thresholdLow1 = Math.Max(1.5, noiseLow1 * 1.5);
        var thresholdP99 = Math.Max(1.5, noiseP99 * 1.5);
        var needed = Math.Max(1, (int)Math.Ceiling(pairs.Count * 2d / 3d));

        var avgPositive = avgDelta > thresholdAvg && pairAvg.Count(x => x > 0) >= needed;
        var lowPositive = low1Delta > thresholdLow1 && pairLow1.Count(x => x > 0) >= needed;
        var p99Positive = p99Delta > thresholdP99 && pairP99.Count(x => x > 0) >= needed;

        var avgNegative = avgDelta < -thresholdAvg && pairAvg.Count(x => x < 0) >= needed;
        var lowNegative = low1Delta < -thresholdLow1 && pairLow1.Count(x => x < 0) >= needed;
        var p99Negative = p99Delta < -thresholdP99 && pairP99.Count(x => x < 0) >= needed;

        string verdict;
        if (pairs.Count < 3)
            verdict = "RESULTADO PRELIMINAR · completa al menos 3 pares para separar mejor señal de ruido.";
        else if (lowNegative || p99Negative || (avgNegative && !lowPositive && !p99Positive))
            verdict = "REGRESIÓN MEDIBLE · HyperBoost empeoró una métrica primaria por encima del ruido de la línea base.";
        else if ((lowPositive && p99Positive) || (avgPositive && (lowPositive || p99Positive)))
            verdict = "MEJORA MEDIBLE · la señal supera el ruido de las pasadas OFF y es consistente entre pares.";
        else if (avgPositive || lowPositive || p99Positive)
            verdict = "SEÑAL POSITIVA, AÚN NO CONCLUYENTE · una métrica supera el ruido, pero falta confirmación en otra métrica primaria.";
        else
            verdict = "SIN MEJORA DEMOSTRABLE · la diferencia observada queda dentro del ruido o no es consistente entre pares.";

        var report = BuildReport(
            verdict, pairs.Count, offAvg, onAvg, avgDelta,
            offLow1, onLow1, low1Delta, offLow01, onLow01, low01Delta,
            offP95, onP95, p95Delta, offP99, onP99, p99Delta,
            offStutter, onStutter, stutterDelta,
            noiseAvg, noiseLow1, noiseP99, captures, pairAvg, pairLow1, pairP99);

        return new BenchmarkAnalysis(
            verdict,
            pairs.Count,
            offAvg, onAvg, avgDelta,
            offLow1, onLow1, low1Delta,
            offLow01, onLow01, low01Delta,
            offP95, onP95, p95Delta,
            offP99, onP99, p99Delta,
            offStutter, onStutter, stutterDelta,
            noiseAvg, noiseLow1, noiseP99,
            report);
    }

    static string BuildReport(
        string verdict,
        int pairCount,
        double offAvg, double onAvg, double avgDelta,
        double offLow1, double onLow1, double low1Delta,
        double offLow01, double onLow01, double low01Delta,
        double offP95, double onP95, double p95Delta,
        double offP99, double onP99, double p99Delta,
        double offStutter, double onStutter, double stutterDelta,
        double noiseAvg, double noiseLow1, double noiseP99,
        IReadOnlyList<BenchmarkCaptureResult> captures,
        IReadOnlyList<double> pairAvg,
        IReadOnlyList<double> pairLow1,
        IReadOnlyList<double> pairP99)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HYPERBOOST A/B BENCHMARK")
          .AppendLine("========================")
          .AppendLine(verdict)
          .AppendLine()
          .AppendLine($"Pares completos: {pairCount}")
          .AppendLine($"FPS promedio       OFF {offAvg,8:0.00}   ON {onAvg,8:0.00}   Δ {avgDelta,+0.00;-0.00;0.00}%")
          .AppendLine($"1% Low (1/p99)     OFF {offLow1,8:0.00}   ON {onLow1,8:0.00}   Δ {low1Delta,+0.00;-0.00;0.00}%")
          .AppendLine($"0.1% Low (1/p99.9) OFF {offLow01,8:0.00}   ON {onLow01,8:0.00}   Δ {low01Delta,+0.00;-0.00;0.00}%")
          .AppendLine($"Frametime p95      OFF {offP95,8:0.00}ms ON {onP95,8:0.00}ms mejora {p95Delta,+0.00;-0.00;0.00}%")
          .AppendLine($"Frametime p99      OFF {offP99,8:0.00}ms ON {onP99,8:0.00}ms mejora {p99Delta,+0.00;-0.00;0.00}%")
          .AppendLine($"Stutter rate       OFF {offStutter,8:0.000}% ON {onStutter,8:0.000}% mejora {stutterDelta,+0.00;-0.00;0.00}%")
          .AppendLine()
          .AppendLine("RUIDO DE LÍNEA BASE (CV entre pasadas OFF)")
          .AppendLine($"FPS promedio: {noiseAvg:0.00}% · 1% Low: {noiseLow1:0.00}% · p99: {noiseP99:0.00}%")
          .AppendLine()
          .AppendLine("DELTA POR PAR (ON vs OFF)");

        for (var i = 0; i < pairAvg.Count; i++)
            sb.AppendLine($"Par {i + 1}: FPS {pairAvg[i],+0.00;-0.00;0.00}% · 1% Low {pairLow1[i],+0.00;-0.00;0.00}% · p99 mejora {pairP99[i],+0.00;-0.00;0.00}%");

        sb.AppendLine().AppendLine("PASADAS CRUDAS");
        foreach (var x in captures.OrderBy(x => x.Sequence))
            sb.AppendLine($"#{x.Sequence:00} P{x.Pair} {x.Mode,-3} · {x.AverageFps:0.00} FPS · 1% {x.Low1Fps:0.00} · 0.1% {x.Low01Fps:0.00} · p99 {x.P99FrameTimeMs:0.00} ms · stutter {x.StutterRatePercent:0.000}% · {x.Frames} frames · fuente {x.FrameTimeSource}");

        sb.AppendLine()
          .AppendLine("Metodología: plan AB/BA contrabalanceado por pares. 1% Low = 1000 / percentil 99 de frametime; 0.1% Low = 1000 / percentil 99.9. Stutter = frametime > max(33.333 ms, 2.5× mediana). El veredicto exige superar 1.5× la variación relativa observada en OFF (con mínimos conservadores) y consistencia de signo en al menos 2/3 de los pares.")
          .AppendLine("Los CSV originales de PresentMon se conservan junto a este reporte para auditoría.");
        return sb.ToString();
    }

    static double HigherBetterDelta(double off, double on)
        => Math.Abs(off) < 1e-9 ? 0 : (on - off) / off * 100d;

    static double LowerBetterDelta(double off, double on)
    {
        if (Math.Abs(off) < 1e-9) return Math.Abs(on) < 1e-9 ? 0 : -100;
        return (off - on) / off * 100d;
    }

    static double CoefficientOfVariation(IEnumerable<double> source)
    {
        var values = source.ToList();
        if (values.Count < 2) return 0;
        var mean = values.Average();
        if (Math.Abs(mean) < 1e-9) return 0;
        var sum = values.Sum(x => Math.Pow(x - mean, 2));
        var sd = Math.Sqrt(sum / (values.Count - 1));
        return Math.Abs(sd / mean) * 100d;
    }
}
