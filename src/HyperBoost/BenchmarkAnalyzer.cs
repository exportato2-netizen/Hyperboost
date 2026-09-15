using System.Globalization;
using System.Text;

namespace HyperBoost;

public static class BenchmarkAnalyzer
{
    public static IReadOnlyList<string> PrimaryMetricKeys { get; } = ["AverageFps", "P99FrameTime"];
    public static IReadOnlyList<string> PrimaryEvidenceFamilies { get; } = ["central-throughput", "tail-p99"];
    public const string Low1EvidenceFamily = "tail-p99";

    public static BenchmarkAnalysis Analyze(IReadOnlyList<BenchmarkCaptureResult> captures)
    {
        var pairs = captures.GroupBy(x => x.Pair)
            .Select(g => new
            {
                Off = g.FirstOrDefault(x => x.Mode == BenchmarkMode.Off),
                On = g.FirstOrDefault(x => x.Mode == BenchmarkMode.On)
            })
            .Where(x => x.Off is not null && x.On is not null)
            .Select(x => (Off: x.Off!, On: x.On!))
            .OrderBy(x => x.Off.Pair)
            .ToList();
        if (pairs.Count == 0) throw new InvalidOperationException("No hay pares OFF/ON completos.");

        var sources = pairs.SelectMany(x => new[] { x.Off.FrameTimeSource, x.On.FrameTimeSource })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count != 1)
            throw new InvalidDataException("PASADA INVÁLIDA · las capturas comparables no usan una única fuente de frametime.");

        var off = pairs.Select(x => x.Off).ToList();
        var on = pairs.Select(x => x.On).ToList();
        var pairAvg = pairs.Select(x => HigherBetterDelta(x.Off.AverageFps, x.On.AverageFps)).ToList();
        var pairP99 = pairs.Select(x => LowerBetterDelta(x.Off.P99FrameTimeMs, x.On.P99FrameTimeMs)).ToList();
        var pairP95 = pairs.Select(x => LowerBetterDelta(x.Off.P95FrameTimeMs, x.On.P95FrameTimeMs)).ToList();
        var pairSevere = pairs.Select(x => LowerBetterDelta(x.Off.SevereStallRatePercent, x.On.SevereStallRatePercent)).ToList();
        var pairRelative = pairs.Select(x => LowerBetterDelta(x.Off.RelativeSpikeRatePercent, x.On.RelativeSpikeRatePercent)).ToList();

        var offAvg = off.Average(x => x.AverageFps);
        var onAvg = on.Average(x => x.AverageFps);
        var offP99 = off.Average(x => x.P99FrameTimeMs);
        var onP99 = on.Average(x => x.P99FrameTimeMs);
        var offLow1 = ReciprocalFps(offP99);
        var onLow1 = ReciprocalFps(onP99);
        var offP999 = off.Average(x => x.P999FrameTimeMs);
        var onP999 = on.Average(x => x.P999FrameTimeMs);
        var offLow01 = ReciprocalFps(offP999);
        var onLow01 = ReciprocalFps(onP999);
        var offP95 = off.Average(x => x.P95FrameTimeMs);
        var onP95 = on.Average(x => x.P95FrameTimeMs);
        var offSevere = off.Average(x => x.SevereStallRatePercent);
        var onSevere = on.Average(x => x.SevereStallRatePercent);
        var offRelative = off.Average(x => x.RelativeSpikeRatePercent);
        var onRelative = on.Average(x => x.RelativeSpikeRatePercent);

        var avgDelta = pairAvg.Average();
        var p99Delta = pairP99.Average();
        var low1Delta = HigherBetterDelta(offLow1, onLow1);
        var low01Delta = HigherBetterDelta(offLow01, onLow01);
        var p95Delta = pairP95.Average();
        var severeDelta = pairSevere.Average();
        var relativeDelta = pairRelative.Average();

        var noiseAvg = ConservativeNoise(off.Select(x => x.AverageFps));
        var noiseP99 = ConservativeNoise(off.Select(x => x.P99FrameTimeMs));
        // 1% Low no agrega información: su ruido se muestra derivado de p99.
        var noiseLow1 = noiseP99;
        var avgCi = BootstrapMeanInterval(pairAvg);
        var p99Ci = BootstrapMeanInterval(pairP99);
        var needed = (int)Math.Ceiling(pairs.Count * 2d / 3d);
        var avgThreshold = Math.Max(1.0, noiseAvg * 0.75);
        var p99Threshold = Math.Max(1.5, noiseP99 * 0.75);

        var avgPositive = DecisivePositive(avgDelta, avgThreshold, pairAvg, needed, avgCi);
        var p99Positive = DecisivePositive(p99Delta, p99Threshold, pairP99, needed, p99Ci);
        var avgNegative = DecisiveNegative(avgDelta, avgThreshold, pairAvg, needed, avgCi);
        var p99Negative = DecisiveNegative(p99Delta, p99Threshold, pairP99, needed, p99Ci);
        var noisy = noiseAvg > 5 || noiseP99 > 12 ||
                    StdDev(pairAvg) > 8 || StdDev(pairP99) > 15;

        string verdict;
        string evidence;
        if (pairs.Count < 6)
        {
            evidence = $"PRELIMINAR · {pairs.Count} pares";
            verdict = "RESULTADO PRELIMINAR · tres pares sirven para detectar una señal, no para evidencia persistente de alta confianza.";
        }
        else
        {
            evidence = pairs.Count >= 9 ? "EXTENDIDA · 9 pares" : "ESTÁNDAR · 6 pares";
            if (noisy)
                verdict = "SESIÓN DEMASIADO RUIDOSA · la variabilidad impide separar de forma defendible la intervención del contenido de la prueba.";
            else if (avgNegative || p99Negative)
                verdict = "REGRESIÓN MEDIBLE · al menos una métrica primaria empeoró con intervalo pareado fuera de cero y magnitud práctica.";
            else if ((avgPositive || p99Positive) && !avgNegative && !p99Negative)
                verdict = "MEJORA MEDIBLE · una métrica primaria superó magnitud práctica, consistencia e intervalo pareado; la otra no contradice la señal.";
            else if (PositiveSignal(avgDelta, pairAvg) || PositiveSignal(p99Delta, pairP99))
                verdict = "SEÑAL POSITIVA, AÚN NO CONCLUYENTE · la dirección favorece ON, pero el intervalo o la consistencia aún no sostienen una conclusión.";
            else
                verdict = "SIN MEJORA DEMOSTRABLE · esta sesión no separó el efecto del ruido; no significa que el efecto sea exactamente cero.";
        }

        var p999Indicative = captures.Any(x => x.P999Indicative);
        var extremeStalls = captures.Sum(x => x.ExtremeStallCount);
        var report = BuildReport(
            verdict, evidence, sources[0], pairs.Count,
            offAvg, onAvg, avgDelta, avgCi,
            offP99, onP99, p99Delta, p99Ci,
            offLow1, onLow1, low1Delta,
            offLow01, onLow01, low01Delta, p999Indicative,
            offP95, onP95, p95Delta,
            offSevere, onSevere, severeDelta,
            offRelative, onRelative, relativeDelta,
            noiseAvg, noiseP99, captures, pairAvg, pairP99, extremeStalls);

        return new(
            verdict, evidence, pairs.Count,
            offAvg, onAvg, avgDelta,
            offLow1, onLow1, low1Delta,
            offLow01, onLow01, low01Delta,
            offP95, onP95, p95Delta,
            offP99, onP99, p99Delta,
            offSevere, onSevere, severeDelta,
            noiseAvg, noiseLow1, noiseP99,
            avgCi, p99Ci,
            offRelative, onRelative, relativeDelta,
            extremeStalls, p999Indicative,
            report);
    }

    internal static ConfidenceInterval BootstrapMeanInterval(IReadOnlyList<double> pairedDeltas)
    {
        if (pairedDeltas.Count < 6)
            return new(null, null, false, pairedDeltas.Count, "Bootstrap percentil sobre deltas pareados; mínimo 6 pares");

        const int iterations = 20_000;
        var seed = 17;
        foreach (var value in pairedDeltas)
            seed = unchecked(seed * 31 + (int)Math.Round(value * 1000));
        var random = new Random(seed);
        var means = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var sum = 0d;
            for (var j = 0; j < pairedDeltas.Count; j++)
                sum += pairedDeltas[random.Next(pairedDeltas.Count)];
            means[i] = sum / pairedDeltas.Count;
        }
        Array.Sort(means);
        return new(
            QuantileSorted(means, 0.025),
            QuantileSorted(means, 0.975),
            true,
            pairedDeltas.Count,
            "Bootstrap percentil 95% sobre deltas OFF/ON pareados (20.000 remuestras; no frames IID)");
    }

    static string BuildReport(
        string verdict, string evidence, string source, int pairCount,
        double offAvg, double onAvg, double avgDelta, ConfidenceInterval avgCi,
        double offP99, double onP99, double p99Delta, ConfidenceInterval p99Ci,
        double offLow1, double onLow1, double low1Delta,
        double offLow01, double onLow01, double low01Delta, bool p999Indicative,
        double offP95, double onP95, double p95Delta,
        double offSevere, double onSevere, double severeDelta,
        double offRelative, double onRelative, double relativeDelta,
        double noiseAvg, double noiseP99,
        IReadOnlyList<BenchmarkCaptureResult> captures,
        IReadOnlyList<double> pairAvg,
        IReadOnlyList<double> pairP99,
        int extremeStalls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HYPERBOOST A/B BENCHMARK · METHODOLOGY v3")
          .AppendLine("========================================")
          .AppendLine(verdict)
          .AppendLine($"Nivel de evidencia: {evidence}")
          .AppendLine($"Fuente única de frametime: {source}")
          .AppendLine()
          .AppendLine("MÉTRICAS PRIMARIAS")
          .AppendLine($"FPS promedio        OFF {offAvg,8:0.00}   ON {onAvg,8:0.00}   Δ {Signed(avgDelta)}% · {avgCi.Display}")
          .AppendLine($"Frametime p99       OFF {offP99,8:0.00}ms ON {onP99,8:0.00}ms mejora {Signed(p99Delta)}% · {p99Ci.Display}")
          .AppendLine($"1% Low derivado     OFF {offLow1,8:0.00}   ON {onLow1,8:0.00}   Δ {Signed(low1Delta)}%")
          .AppendLine("  ↳ 1% Low = 1000/p99: es la misma familia estadística y NO cuenta como un voto independiente.")
          .AppendLine()
          .AppendLine("MÉTRICAS SECUNDARIAS")
          .AppendLine($"Frametime p95       OFF {offP95,8:0.00}ms ON {onP95,8:0.00}ms mejora {Signed(p95Delta)}%")
          .AppendLine($"Severe Stall rate   OFF {offSevere,8:0.000}% ON {onSevere,8:0.000}% mejora {Signed(severeDelta)}%")
          .AppendLine($"Relative Spike rate OFF {offRelative,8:0.000}% ON {onRelative,8:0.000}% mejora {Signed(relativeDelta)}%")
          .AppendLine()
          .AppendLine($"0.1% Low {(p999Indicative ? "· INDICATIVO" : "")}  OFF {offLow01,8:0.00}   ON {onLow01,8:0.00}   Δ {Signed(low01Delta)}%")
          .AppendLine("  ↳ No participa en el veredicto principal mientras la cola p99.9 sea pequeña.")
          .AppendLine()
          .AppendLine("VARIABILIDAD OFF (contexto, no estimador exacto)")
          .AppendLine($"FPS promedio: {noiseAvg:0.00}% · p99: {noiseP99:0.00}%")
          .AppendLine()
          .AppendLine("DELTA PRIMARIO POR PAR (ON vs OFF)");

        for (var i = 0; i < pairAvg.Count; i++)
            sb.AppendLine($"Par {i + 1}: FPS {Signed(pairAvg[i])}% · p99 mejora {Signed(pairP99[i])}%");

        sb.AppendLine().AppendLine("PASADAS CRUDAS");
        foreach (var x in captures.OrderBy(x => x.Sequence))
        {
            sb.AppendLine($"#{x.Sequence:00} P{x.Pair} {x.Mode,-3} · {x.AverageFps:0.00} FPS · 1% {x.Low1Fps:0.00} · 0.1% {x.Low01Fps:0.00}{(x.P999Indicative ? " indicativo" : "")} · p99 {x.P99FrameTimeMs:0.00} ms · severe {x.SevereStallCount} · spikes {x.RelativeSpikeCount} · extreme {x.ExtremeStallCount} · {x.Frames} frames · fuente {x.FrameTimeSource}");
            if (x.P999Indicative)
                sb.AppendLine($"    cola p99.9: ~{x.P999TailSamples} muestra(s); interpretar con cautela.");
            if (x.PercentileExcludedExtremeCount > 0)
                sb.AppendLine($"    {x.PercentileExcludedExtremeCount} freeze(s) ≥200 ms se conservaron en contadores/average, pero se separaron de percentiles para que una anomalía no domine una cola corta.");
            if (x.InvalidDataCount > 0)
                sb.AppendLine($"    {x.InvalidDataCount} fila(s) inválida(s) para la fuente fijada fueron registradas.");
        }

        sb.AppendLine()
          .AppendLine($"Extreme stalls totales (≥200 ms): {extremeStalls}")
          .AppendLine("OVERHEAD OBSERVADO DURANTE CAPTURA")
          .AppendLine($"HyperBoost CPU promedio: {captures.Average(x => x.HyperBoostCpuPercent):0.000}% · Working Set: {captures.Average(x => x.HyperBoostWorkingSetMb):0.0} MB · I/O por pasada: {captures.Average(x => x.HyperBoostIoMb):0.000} MB")
          .AppendLine($"PresentMon CPU promedio: {captures.Average(x => x.PresentMonCpuPercent):0.000}% · Working Set: {captures.Average(x => x.PresentMonWorkingSetMb):0.0} MB")
          .AppendLine()
          .AppendLine("Metodología: AB/BA contrabalanceado. La inferencia usa deltas por par completo y bootstrap de pares; nunca trata frames consecutivos como observaciones IID. Magnitud mínima práctica, consistencia de signo, intervalo y variabilidad deben concordar. Tres pares siempre producen resultado preliminar.")
          .AppendLine("Los CSV originales se conservan. 'Sin mejora demostrable' no significa 'efecto inexistente'.");
        return sb.ToString();
    }

    static bool DecisivePositive(double mean, double threshold, IReadOnlyList<double> values, int needed, ConfidenceInterval ci)
        => mean >= threshold && values.Count(x => x > 0) >= needed && ci.Stable && ci.LowerPercent > 0;

    static bool DecisiveNegative(double mean, double threshold, IReadOnlyList<double> values, int needed, ConfidenceInterval ci)
        => mean <= -threshold && values.Count(x => x < 0) >= needed && ci.Stable && ci.UpperPercent < 0;

    static bool PositiveSignal(double mean, IReadOnlyList<double> values)
        => mean > 0 && values.Count(x => x > 0) >= Math.Ceiling(values.Count / 2d);

    static double ConservativeNoise(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).ToList();
        if (values.Count < 2) return 0;
        var mean = values.Average();
        var standard = Math.Abs(mean) < 1e-9 ? 0 : StdDev(values) / Math.Abs(mean) * 100d;
        var sorted = values.OrderBy(x => x).ToList();
        var median = QuantileSorted(sorted, 0.5);
        var deviations = values.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToList();
        var robust = Math.Abs(median) < 1e-9 ? 0 : 1.4826 * QuantileSorted(deviations, 0.5) / Math.Abs(median) * 100d;
        return Math.Max(robust, standard * 0.5);
    }

    static double StdDev(IEnumerable<double> source)
    {
        var values = source.ToList();
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(x => Math.Pow(x - mean, 2)) / (values.Count - 1));
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

    static double ReciprocalFps(double frameTimeMs) => frameTimeMs <= 0 ? 0 : 1000d / frameTimeMs;
    static double HigherBetterDelta(double off, double on) => Math.Abs(off) < 1e-9 ? 0 : (on - off) / off * 100d;
    static double LowerBetterDelta(double off, double on) => Math.Abs(off) < 1e-9 ? (Math.Abs(on) < 1e-9 ? 0 : -100) : (off - on) / off * 100d;
    static string Signed(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
}
