using System.Text;

namespace HyperBoost;

public enum GameEvidenceLevel
{
    NoData,
    Preliminary,
    Low,
    Moderate,
    High,
    Contradictory,
    ContextChanged
}

public static class GameEvidenceLevelExtensions
{
    public static string DisplayName(this GameEvidenceLevel value) => value switch
    {
        GameEvidenceLevel.NoData => "SIN DATOS",
        GameEvidenceLevel.Preliminary => "PRELIMINAR",
        GameEvidenceLevel.Low => "EVIDENCIA BAJA",
        GameEvidenceLevel.Moderate => "EVIDENCIA MODERADA",
        GameEvidenceLevel.High => "EVIDENCIA ALTA",
        GameEvidenceLevel.Contradictory => "RESULTADOS CONTRADICTORIOS",
        GameEvidenceLevel.ContextChanged => "CONTEXTO CAMBIÓ",
        _ => value.ToString().ToUpperInvariant()
    };
}

public enum HistoricalCompatibility
{
    Compatible,
    CompatibleWithUnknownSettings,
    ContextChanged,
    InsufficientMetadata
}

public sealed record CompatibilityDecision(
    HistoricalCompatibility Kind,
    string Explanation)
{
    public bool CanAggregate => Kind is HistoricalCompatibility.Compatible or HistoricalCompatibility.CompatibleWithUnknownSettings;
    public bool HasExactKnownSettings => Kind == HistoricalCompatibility.Compatible;
}

public sealed record PolicyScheme(bool EcoQos, bool MemoryPriorityExperimental)
{
    public string Key => $"eco:{EcoQos.ToString().ToLowerInvariant()}|memory:{MemoryPriorityExperimental.ToString().ToLowerInvariant()}";

    public string DisplayName => (EcoQos, MemoryPriorityExperimental) switch
    {
        (true, false) => "EcoQoS",
        (false, true) => "Memory Priority · EXPERIMENTAL",
        (true, true) => "EcoQoS + Memory Priority · EXPERIMENTAL",
        _ => "Sin políticas ON"
    };
}

public sealed class BenchmarkEvidenceSnapshot
{
    public string Verdict { get; set; } = "SIN DATOS";
    public string SessionEvidenceLevel { get; set; } = "SIN DATOS";
    public int CompletedPairs { get; set; }
    public double AverageFpsDeltaPercent { get; set; }
    public double P99ImprovementPercent { get; set; }
    public double BaselineNoiseAverageFpsPercent { get; set; }
    public double BaselineNoiseP99Percent { get; set; }
    public ConfidenceInterval AverageFpsConfidenceInterval { get; set; } = new(null, null, false, 0, "No disponible");
    public ConfidenceInterval P99ConfidenceInterval { get; set; } = new(null, null, false, 0, "No disponible");

    public static BenchmarkEvidenceSnapshot From(BenchmarkAnalysis analysis) => new()
    {
        Verdict = analysis.Verdict,
        SessionEvidenceLevel = analysis.EvidenceLevel,
        CompletedPairs = analysis.CompletedPairs,
        AverageFpsDeltaPercent = analysis.AverageFpsDeltaPercent,
        P99ImprovementPercent = analysis.P99ImprovementPercent,
        BaselineNoiseAverageFpsPercent = analysis.BaselineNoiseAverageFpsPercent,
        BaselineNoiseP99Percent = analysis.BaselineNoiseP99Percent,
        AverageFpsConfidenceInterval = analysis.AverageFpsConfidenceInterval,
        P99ConfidenceInterval = analysis.P99ConfidenceInterval
    };
}

public sealed class GameEvidenceSession
{
    public int SchemaVersion { get; set; } = 1;
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int SourceResultsSchemaVersion { get; set; }
    public string SourceResultsPath { get; set; } = "";
    public string HyperBoostVersion { get; set; } = "unknown";
    public string GameName { get; set; } = "Juego";
    public string? ExecutablePath { get; set; }
    public DateTime LastObservedRunUtc { get; set; } = DateTime.UtcNow;
    public PolicyScheme Policies { get; set; } = new(false, false);
    public BenchmarkScenarioType ScenarioType { get; set; } = BenchmarkScenarioType.ManualScene;
    public string? UserReportedResolution { get; set; }
    public string? UserReportedSettings { get; set; }
    public string SessionFrameTimeSource { get; set; } = "";
    public int CaptureSeconds { get; set; }
    public int PairCount { get; set; }
    public BenchmarkEnvironmentFingerprint? Environment { get; set; }
    public BenchmarkEvidenceSnapshot Analysis { get; set; } = new();
    public List<BenchmarkCaptureResult> Captures { get; set; } = [];
    public bool LegacyIncomplete { get; set; }
    public string? ComparabilityLimitation { get; set; }

    public bool IsPreliminary => PairCount < 6 || Analysis.Verdict.StartsWith("RESULTADO PRELIMINAR", StringComparison.Ordinal);
    public bool IsNoisy => Analysis.Verdict.StartsWith("SESIÓN DEMASIADO RUIDOSA", StringComparison.Ordinal);
    public bool IsInvalid => Analysis.Verdict.StartsWith("PASADA INVÁLIDA", StringComparison.Ordinal);
    public bool IsMeasuredImprovement => Analysis.Verdict.StartsWith("MEJORA MEDIBLE", StringComparison.Ordinal);
    public bool IsMeasuredRegression => Analysis.Verdict.StartsWith("REGRESIÓN MEDIBLE", StringComparison.Ordinal);
    public bool IsPositiveSignal => Analysis.Verdict.StartsWith("SEÑAL POSITIVA", StringComparison.Ordinal);

    public int ScenarioQuality => ScenarioType switch
    {
        BenchmarkScenarioType.BuiltInBenchmark => 3,
        BenchmarkScenarioType.RepeatableRoute => 2,
        BenchmarkScenarioType.StressTest => 1,
        _ => 0
    };
}

public sealed class GameProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Juego";
    public string ExecutableIdentity { get; set; } = "";
    public string? LastKnownExecutablePath { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
    public List<GameEvidenceSession> Sessions { get; set; } = [];

    public string Display => $"{Name} · {Sessions.Count} sesión(es) · última {LastRunUtc.ToLocalTime():yyyy-MM-dd}";
}

public sealed class GameIntelligenceDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<GameProfile> Profiles { get; set; } = [];
}

public sealed record EvidenceSummary(
    string PolicyKey,
    string PolicyDisplayName,
    GameEvidenceLevel Level,
    string Outcome,
    string Recommendation,
    int TotalSessions,
    int CompatibleSessions,
    int ContextChangedSessions,
    int InsufficientMetadataSessions,
    int TotalCompatiblePairs,
    double? MeanAverageFpsDeltaPercent,
    double? MeanP99ImprovementPercent,
    IReadOnlyList<string> CompatibilityNotes)
{
    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(PolicyDisplayName.ToUpperInvariant())
          .AppendLine($"  Nivel: {Level.DisplayName()}")
          .AppendLine($"  Resultado: {Outcome}")
          .AppendLine($"  Sesiones: {CompatibleSessions} compatibles / {TotalSessions} totales · {TotalCompatiblePairs} pares compatibles")
          .AppendLine($"  Contexto distinto: {ContextChangedSessions} · metadata insuficiente: {InsufficientMetadataSessions}");
        if (MeanAverageFpsDeltaPercent.HasValue)
            sb.AppendLine($"  Δ FPS promedio: {MeanAverageFpsDeltaPercent:+0.00;-0.00;0.00}%");
        if (MeanP99ImprovementPercent.HasValue)
            sb.AppendLine($"  mejora p99: {MeanP99ImprovementPercent:+0.00;-0.00;0.00}%");
        sb.AppendLine($"  Recomendación: {Recommendation}");
        foreach (var note in CompatibilityNotes.Take(3)) sb.AppendLine($"  · {note}");
        return sb.ToString();
    }
}

public enum GamingReadinessState
{
    Ready,
    Attention,
    InterferenceDetected
}

public static class GamingReadinessStateExtensions
{
    public static string DisplayName(this GamingReadinessState value) => value switch
    {
        GamingReadinessState.Ready => "LISTO",
        GamingReadinessState.Attention => "ATENCIÓN",
        GamingReadinessState.InterferenceDetected => "INTERFERENCIA DETECTADA",
        _ => value.ToString().ToUpperInvariant()
    };
}

public sealed record GamingReadinessResult(
    GamingReadinessState State,
    string Cpu,
    string Gpu,
    string Ram,
    string Background,
    string CaptureTools,
    string Synchronizers,
    IReadOnlyList<string> Reasons)
{
    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ESTADO       {State.DisplayName()}")
          .AppendLine($"CPU          {Cpu}")
          .AppendLine($"GPU          {Gpu}")
          .AppendLine($"RAM          {Ram}")
          .AppendLine($"BACKGROUND   {Background}")
          .AppendLine($"CAPTURA      {CaptureTools}")
          .AppendLine($"SINCRONIZA.  {Synchronizers}");
        if (Reasons.Count > 0)
        {
            sb.AppendLine().AppendLine("SEÑALES");
            foreach (var reason in Reasons) sb.AppendLine("  · " + reason);
        }
        sb.AppendLine().AppendLine("Lectura puntual; describe interferencias observadas, no causalidad de stutter.");
        return sb.ToString();
    }
}
