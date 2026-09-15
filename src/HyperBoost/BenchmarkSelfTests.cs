using System.Globalization;
using System.Text;

namespace HyperBoost;

internal static class BenchmarkSelfTests
{
    public static string Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "HyperBoost-BenchmarkSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baselineCsv = Path.Combine(root, "synthetic-60.csv");
            WriteCsv(baselineCsv, "DisplayedTime", BuildCadence(16.6667, 600, (180, 28), (360, 60), (480, 250)));
            var baseline = PresentMonBenchmarkService.ParseCsv(baselineCsv, new(1, 1, BenchmarkMode.Off));
            Assert(baseline.Frames > 550, "parser descartó demasiados frames");
            Assert(baseline.FrameTimeSource == "DisplayedTime", "fuente inicial incorrecta");
            Assert(baseline.SevereStallCount >= 2, "severe stalls 60 FPS no detectados");
            Assert(baseline.RelativeSpikeCount >= 2, "relative spikes 60 FPS no detectados");
            Assert(baseline.ExtremeStallCount == 1, "freeze extremo no separado");
            Assert(baseline.PercentileExcludedExtremeCount == 1, "freeze extremo no documentado fuera de percentiles");
            Assert(baseline.P999Indicative, "p99.9 corto no quedó indicativo");

            TestLockedSource(root);
            TestRelativeSpikes();
            TestIndependentEvidenceFamilies();
            TestThreePairsPreliminary(baseline);
            TestSixPairsMeasurable(baseline);
            TestNoisySession(baseline);
            TestRegression(baseline);
            RecoveryJournal.RunSelfTest(root);

            return "Benchmark Integrity self-test OK · fuente fija · evidencia independiente · 3/6/9 · CI pareado · ruido/regresión · p99.9 · spikes 60/120/240 · recovery/PID/ownership";
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static void TestLockedSource(string root)
    {
        var alternate = Path.Combine(root, "alternate.csv");
        var sb = new StringBuilder("Application,ProcessID,DisplayedTime,MsBetweenDisplayChange\n");
        for (var i = 0; i < 200; i++) sb.AppendLine($"game.exe,123,NA,{16.0.ToString(CultureInfo.InvariantCulture)}");
        File.WriteAllText(alternate, sb.ToString(), Encoding.UTF8);
        var selected = PresentMonBenchmarkService.ParseCsv(alternate, new(2, 1, BenchmarkMode.On));
        Assert(selected.FrameTimeSource == "MsBetweenDisplayChange", "fallback inicial no seleccionó fuente válida");

        var rejected = false;
        try { PresentMonBenchmarkService.ParseCsv(alternate, new(2, 1, BenchmarkMode.On), "DisplayedTime"); }
        catch (InvalidDataException ex) when (ex.Message.Contains("PASADA INVÁLIDA", StringComparison.Ordinal)) { rejected = true; }
        Assert(rejected, "una fuente distinta pudo sustituir silenciosamente la fuente de sesión");
    }

    static void TestRelativeSpikes()
    {
        foreach (var cadence in new[] { 16.6667, 8.3333, 4.1667 })
        {
            var frames = BuildCadence(cadence, 500, (150, Math.Max(cadence * 2.2, cadence + 8)), (350, 45));
            Assert(PresentMonBenchmarkService.CountRelativeSpikes(frames) >= 2, $"spikes relativos no detectados a {1000 / cadence:0} FPS");
        }

        var transition = Enumerable.Repeat(4.1667, 180).Concat(Enumerable.Repeat(16.6667, 180)).ToList();
        Assert(PresentMonBenchmarkService.CountRelativeSpikes(transition) <= 2, "una transición sostenida de carga fue contada como una ráfaga de spikes");
    }

    static void TestIndependentEvidenceFamilies()
    {
        Assert(BenchmarkAnalyzer.PrimaryMetricKeys.SequenceEqual(new[] { "AverageFps", "P99FrameTime" }), "jerarquía primaria inesperada");
        Assert(BenchmarkAnalyzer.PrimaryEvidenceFamilies.Distinct(StringComparer.Ordinal).Count() == BenchmarkAnalyzer.PrimaryEvidenceFamilies.Count, "dos transformaciones cuentan como evidencia primaria independiente");
        Assert(BenchmarkAnalyzer.Low1EvidenceFamily == "tail-p99" && !BenchmarkAnalyzer.PrimaryMetricKeys.Contains("Low1Fps"), "1% Low volvió a contar como voto independiente de p99");
    }

    static void TestThreePairsPreliminary(BenchmarkCaptureResult template)
    {
        var analysis = BenchmarkAnalyzer.Analyze(BuildPairs(template, 3, _ => 4, _ => 8));
        Assert(analysis.Verdict.StartsWith("RESULTADO PRELIMINAR", StringComparison.Ordinal), "3 pares produjeron un veredicto fuerte");
        Assert(!analysis.AverageFpsConfidenceInterval.Stable && !analysis.P99ConfidenceInterval.Stable, "3 pares mostraron precisión artificial");
    }

    static void TestSixPairsMeasurable(BenchmarkCaptureResult template)
    {
        var analysis = BenchmarkAnalyzer.Analyze(BuildPairs(template, 6, i => 3.5 + i * 0.08, i => 7.0 + i * 0.1));
        Assert(analysis.Verdict.StartsWith("MEJORA MEDIBLE", StringComparison.Ordinal), "6 pares consistentes no permitieron conclusión normal");
        Assert(analysis.AverageFpsConfidenceInterval is { Stable: true, LowerPercent: > 0 }, "IC pareado de FPS no excluye cero");
        Assert(analysis.P99ConfidenceInterval is { Stable: true, LowerPercent: > 0 }, "IC pareado de p99 no excluye cero");
    }

    static void TestNoisySession(BenchmarkCaptureResult template)
    {
        var deltas = new[] { 15d, -13d, 12d, -16d, 11d, -14d };
        var samples = BuildPairs(template, 6, i => deltas[i], i => deltas[(i + 1) % deltas.Length]);
        for (var i = 0; i < samples.Count; i += 2)
        {
            var factor = i % 4 == 0 ? 0.72 : 1.28;
            samples[i] = samples[i] with { AverageFps = 100 * factor, P99FrameTimeMs = 18 / factor };
        }
        var analysis = BenchmarkAnalyzer.Analyze(samples);
        Assert(analysis.Verdict.StartsWith("SESIÓN DEMASIADO RUIDOSA", StringComparison.Ordinal), "ruido alto produjo una afirmación de mejora");
    }

    static void TestRegression(BenchmarkCaptureResult template)
    {
        var analysis = BenchmarkAnalyzer.Analyze(BuildPairs(template, 6, _ => -5, _ => -8));
        Assert(analysis.Verdict.StartsWith("REGRESIÓN MEDIBLE", StringComparison.Ordinal), "regresión consistente no fue detectada");
    }

    static List<BenchmarkCaptureResult> BuildPairs(
        BenchmarkCaptureResult template,
        int pairs,
        Func<int, double> averageDelta,
        Func<int, double> p99Improvement)
    {
        var list = new List<BenchmarkCaptureResult>();
        for (var i = 0; i < pairs; i++)
        {
            var offAvg = 100 + i * 0.05;
            var offP99 = 18 + i * 0.01;
            var avgDelta = averageDelta(i);
            var p99Delta = p99Improvement(i);
            var onAvg = offAvg * (1 + avgDelta / 100d);
            var onP99 = offP99 * (1 - p99Delta / 100d);
            var off = template with
            {
                Sequence = i * 2 + 1,
                Pair = i + 1,
                Mode = BenchmarkMode.Off,
                FrameTimeSource = "DisplayedTime",
                AverageFps = offAvg,
                P99FrameTimeMs = offP99,
                Low1Fps = 1000d / offP99,
                P95FrameTimeMs = 14,
                SevereStallRatePercent = 1.2,
                RelativeSpikeRatePercent = 1.5
            };
            var on = off with
            {
                Sequence = i * 2 + 2,
                Mode = BenchmarkMode.On,
                AverageFps = onAvg,
                P99FrameTimeMs = onP99,
                Low1Fps = 1000d / onP99,
                P95FrameTimeMs = 13,
                SevereStallRatePercent = 0.8,
                RelativeSpikeRatePercent = 1.0
            };
            list.Add(off);
            list.Add(on);
        }
        return list;
    }

    static List<double> BuildCadence(double cadence, int count, params (int Index, double Value)[] changes)
    {
        var values = Enumerable.Repeat(cadence, count).ToList();
        foreach (var change in changes) values[change.Index] = change.Value;
        return values;
    }

    static void WriteCsv(string path, string source, IReadOnlyList<double> values)
    {
        var sb = new StringBuilder($"Application,ProcessID,{source}\n");
        foreach (var value in values)
            sb.AppendLine($"game.exe,123,{value.ToString(CultureInfo.InvariantCulture)}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Self-test: " + message);
    }
}
