namespace HyperBoost;

public static class GameEvidenceAnalyzer
{
    public static CompatibilityDecision Compare(GameEvidenceSession reference, GameEvidenceSession candidate)
    {
        if (reference.LegacyIncomplete || candidate.LegacyIncomplete || reference.Environment is null || candidate.Environment is null)
            return new(HistoricalCompatibility.InsufficientMetadata, "La sesión procede de un formato anterior sin huella completa; se conserva, pero no se agrega.");

        if (!string.Equals(reference.Policies.Key, candidate.Policies.Key, StringComparison.Ordinal))
            return Changed("El esquema exacto de políticas ON es distinto.");
        if (!Same(reference.SessionFrameTimeSource, candidate.SessionFrameTimeSource))
            return Changed("La fuente de frametime cambió.");
        if (reference.ScenarioType != candidate.ScenarioType)
            return Changed("El tipo de benchmark cambió.");
        if (reference.CaptureSeconds != candidate.CaptureSeconds)
            return Changed("La duración por pasada cambió.");

        var a = reference.Environment;
        var b = candidate.Environment;
        if (!Same(a.HyperBoostVersion, b.HyperBoostVersion)) return Changed("La versión de HyperBoost cambió.");
        if (!Same(a.WindowsBuild, b.WindowsBuild)) return Changed("La build de Windows cambió.");
        if (!Same(a.Cpu, b.Cpu)) return Changed("La CPU detectada cambió.");
        if (!Same(a.Gpu, b.Gpu)) return Changed("La GPU detectada cambió.");
        if (!Same(a.GpuDriverVersion, b.GpuDriverVersion)) return Changed("El driver GPU cambió.");
        if (Math.Abs(a.RamTotalGb - b.RamTotalGb) > 0.5) return Changed("La RAM total detectada cambió.");
        if (!Same(a.PresentMonVersion, b.PresentMonVersion) || !Same(a.PresentMonSha256, b.PresentMonSha256))
            return Changed("La versión o el hash de PresentMon cambió.");

        if (string.IsNullOrWhiteSpace(a.ExecutablePath) || string.IsNullOrWhiteSpace(b.ExecutablePath) ||
            !a.ExecutableLastWriteUtcTicks.HasValue || !b.ExecutableLastWriteUtcTicks.HasValue ||
            !a.ExecutableSizeBytes.HasValue || !b.ExecutableSizeBytes.HasValue)
            return new(HistoricalCompatibility.InsufficientMetadata, "Windows no permitió completar la identidad barata del ejecutable.");
        if (!SamePath(a.ExecutablePath, b.ExecutablePath) ||
            a.ExecutableLastWriteUtcTicks != b.ExecutableLastWriteUtcTicks ||
            a.ExecutableSizeBytes != b.ExecutableSizeBytes)
            return Changed("El ejecutable o su versión cambió.");

        var unknownContext = new List<string>();
        var resolutionA = reference.UserReportedResolution ?? a.UserReportedResolution;
        var resolutionB = candidate.UserReportedResolution ?? b.UserReportedResolution;
        if (!string.IsNullOrWhiteSpace(resolutionA) && !string.IsNullOrWhiteSpace(resolutionB) && !Same(resolutionA, resolutionB))
            return Changed("La resolución declarada cambió.");
        if (string.IsNullOrWhiteSpace(resolutionA) != string.IsNullOrWhiteSpace(resolutionB))
            return new(HistoricalCompatibility.InsufficientMetadata, "Solo una sesión declaró resolución; no se mezclan.");
        if (string.IsNullOrWhiteSpace(resolutionA)) unknownContext.Add("resolución");

        var settingsA = reference.UserReportedSettings ?? a.UserReportedSettings;
        var settingsB = candidate.UserReportedSettings ?? b.UserReportedSettings;
        if (!string.IsNullOrWhiteSpace(settingsA) && !string.IsNullOrWhiteSpace(settingsB) && !Same(settingsA, settingsB))
            return Changed("La etiqueta de ajustes/versión del juego cambió.");
        if (string.IsNullOrWhiteSpace(settingsA) != string.IsNullOrWhiteSpace(settingsB))
            return new(HistoricalCompatibility.InsufficientMetadata, "Solo una sesión declaró ajustes/versión del juego; no se mezclan.");
        if (string.IsNullOrWhiteSpace(settingsA)) unknownContext.Add("ajustes del juego");
        if (unknownContext.Count > 0)
            return new(HistoricalCompatibility.CompatibleWithUnknownSettings, $"Contexto técnico compatible, pero no se declaró {string.Join(" ni ", unknownContext)}; la evidencia queda limitada a BAJA.");

        return new(HistoricalCompatibility.Compatible, "Misma huella técnica, ejecutable, fuente, escenario, resolución, ajustes y políticas ON.");
    }

    public static EvidenceSummary Analyze(
        IEnumerable<GameEvidenceSession> source,
        PolicyScheme policy,
        GameEvidenceSession? currentContext = null)
    {
        var all = source
            .Where(x => string.Equals(x.Policies.Key, policy.Key, StringComparison.Ordinal))
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        if (all.Count == 0)
            return new(policy.Key, policy.DisplayName, GameEvidenceLevel.NoData, "SIN DATOS", "Ejecuta una sesión A/B para este esquema exacto de políticas.", 0, 0, 0, 0, 0, null, null, []);

        var reference = currentContext ?? all.LastOrDefault(x => !x.LegacyIncomplete && x.Environment is not null) ?? all[^1];
        var referenceIsStored = all.Any(x => string.Equals(x.SessionId, reference.SessionId, StringComparison.Ordinal));
        var compatible = new List<GameEvidenceSession>();
        var changed = 0;
        var insufficient = 0;
        var exactKnownContext = HasCompleteKnownContext(reference);
        var notes = new List<string>();

        foreach (var session in all)
        {
            if (referenceIsStored && string.Equals(session.SessionId, reference.SessionId, StringComparison.Ordinal))
            {
                compatible.Add(session);
                if (!HasCompleteKnownContext(session)) exactKnownContext = false;
                continue;
            }

            var decision = Compare(reference, session);
            notes.Add(decision.Explanation);
            switch (decision.Kind)
            {
                case HistoricalCompatibility.Compatible:
                    compatible.Add(session);
                    break;
                case HistoricalCompatibility.CompatibleWithUnknownSettings:
                    compatible.Add(session);
                    exactKnownContext = false;
                    break;
                case HistoricalCompatibility.ContextChanged:
                    changed++;
                    break;
                default:
                    insufficient++;
                    break;
            }
        }

        if (compatible.Count == 0)
        {
            var reason = changed > 0
                ? "La huella actual no coincide con las sesiones guardadas; se conservan como histórico sin sumarlas."
                : "Las sesiones se conservan, pero su metadata no permite agregarlas de forma defendible.";
            return new(policy.Key, policy.DisplayName, GameEvidenceLevel.ContextChanged, "CONTEXTO CAMBIÓ", reason,
                all.Count, 0, changed, insufficient, 0, null, null, notes.Distinct().ToList());
        }

        var eligible = compatible.Where(IsEligibleForPersistentEvidence).ToList();
        var measuredImprovement = eligible.Count(x => x.IsMeasuredImprovement);
        var measuredRegression = eligible.Count(x => x.IsMeasuredRegression);
        var contradictory = measuredImprovement > 0 && measuredRegression > 0;
        var totalPairs = compatible.Sum(x => x.PairCount);
        var highQuality = eligible.Count(x => x.ScenarioQuality >= 2);
        var noisy = compatible.Count(x => x.IsNoisy);
        var consistency = DirectionConsistency(eligible);

        GameEvidenceLevel level;
        if (contradictory)
            level = GameEvidenceLevel.Contradictory;
        else if (eligible.Count >= 4 && totalPairs >= 24 && highQuality >= 3 && consistency >= 0.75 && noisy == 0 && exactKnownContext)
            level = GameEvidenceLevel.High;
        else if (eligible.Count >= 2 && totalPairs >= 12 && highQuality >= 1 && consistency >= 2d / 3d && exactKnownContext)
            level = GameEvidenceLevel.Moderate;
        else if (eligible.Count >= 1)
            level = GameEvidenceLevel.Low;
        else
            level = GameEvidenceLevel.Preliminary;

        if (!exactKnownContext && level is GameEvidenceLevel.Moderate or GameEvidenceLevel.High)
            level = GameEvidenceLevel.Low;

        var outcome = Outcome(eligible, compatible);
        var recommendation = Recommendation(policy, level, outcome, compatible.Count, changed, insufficient);
        var avg = compatible.Count == 0 ? (double?)null : compatible.Average(x => x.Analysis.AverageFpsDeltaPercent);
        var p99 = compatible.Count == 0 ? (double?)null : compatible.Average(x => x.Analysis.P99ImprovementPercent);
        return new(
            policy.Key,
            policy.DisplayName,
            level,
            outcome,
            recommendation,
            all.Count,
            compatible.Count,
            changed,
            insufficient,
            totalPairs,
            avg,
            p99,
            notes.Distinct(StringComparer.Ordinal).ToList());
    }

    static bool IsEligibleForPersistentEvidence(GameEvidenceSession session)
        => !session.LegacyIncomplete && !session.IsPreliminary && !session.IsNoisy && !session.IsInvalid &&
           session.PairCount >= 6 &&
           session.Analysis.AverageFpsConfidenceInterval.Stable &&
           session.Analysis.P99ConfidenceInterval.Stable;

    static bool HasCompleteKnownContext(GameEvidenceSession session)
    {
        var environment = session.Environment;
        if (session.LegacyIncomplete || environment is null) return false;
        return !string.IsNullOrWhiteSpace(environment.ExecutablePath) &&
               environment.ExecutableLastWriteUtcTicks.HasValue &&
               environment.ExecutableSizeBytes.HasValue &&
               !string.IsNullOrWhiteSpace(session.UserReportedResolution ?? environment.UserReportedResolution) &&
               !string.IsNullOrWhiteSpace(session.UserReportedSettings ?? environment.UserReportedSettings) &&
               !string.IsNullOrWhiteSpace(session.SessionFrameTimeSource);
    }

    static double DirectionConsistency(IReadOnlyList<GameEvidenceSession> sessions)
    {
        if (sessions.Count == 0) return 0;
        var groups = sessions.GroupBy(DirectionClass).Select(x => x.Count()).ToList();
        return groups.Max() / (double)sessions.Count;
    }

    static string DirectionClass(GameEvidenceSession session)
    {
        if (session.IsMeasuredImprovement) return "improvement";
        if (session.IsMeasuredRegression) return "regression";
        if (session.IsPositiveSignal) return "positive-signal";
        return "not-demonstrated";
    }

    static string Outcome(IReadOnlyList<GameEvidenceSession> eligible, IReadOnlyList<GameEvidenceSession> compatible)
    {
        if (eligible.Any(x => x.IsMeasuredImprovement) && eligible.Any(x => x.IsMeasuredRegression))
            return "RESULTADOS CONTRADICTORIOS";
        if (eligible.Any(x => x.IsMeasuredRegression))
            return eligible.Count(x => x.IsMeasuredRegression) > 1 ? "REGRESIÓN REPETIDA" : "REGRESIÓN OBSERVADA";
        if (eligible.Any(x => x.IsMeasuredImprovement))
            return eligible.Count(x => x.IsMeasuredImprovement) > 1 ? "MEJORA REPETIBLE" : "MEJORA OBSERVADA";
        if (compatible.Any(x => x.IsPositiveSignal))
            return "SEÑAL POSITIVA, AÚN NO CONCLUYENTE";
        return "SIN BENEFICIO DEMOSTRABLE";
    }

    static string Recommendation(
        PolicyScheme policy,
        GameEvidenceLevel level,
        string outcome,
        int compatible,
        int changed,
        int insufficient)
    {
        if (level == GameEvidenceLevel.Contradictory)
            return $"No recomendar {policy.DisplayName}: las sesiones compatibles discrepan. Repite un escenario integrado o una ruta más controlada.";
        if (level == GameEvidenceLevel.Preliminary)
            return $"Aún no recomendar {policy.DisplayName}. Completa al menos una sesión estándar de 6 pares; 3 pares solo exploran una señal.";
        if (outcome.Contains("REGRESIÓN", StringComparison.Ordinal))
            return $"Mantener {policy.DisplayName} desactivado para este juego, este PC y esta huella mientras una sesión nueva no contradiga la regresión.";
        if (outcome.Contains("MEJORA", StringComparison.Ordinal))
            return $"{policy.DisplayName} mostró una mejora compatible con esta huella. La recomendación es local; no se generaliza a otros PCs, drivers o ajustes.";
        var context = changed + insufficient > 0 ? $" {changed + insufficient} sesión(es) históricas no suman por contexto/metadata." : "";
        return $"No hay beneficio demostrable de {policy.DisplayName} en las {compatible} sesión(es) compatibles; esto no prueba un efecto exactamente cero.{context}";
    }

    static CompatibilityDecision Changed(string message) => new(HistoricalCompatibility.ContextChanged, message);
    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return Same(a, b); }
    }
}

public static class GamingReadinessAnalyzer
{
    static readonly HashSet<string> CaptureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "Streamlabs OBS", "Streamlabs", "XSplit.Core", "Medal", "Overwolf",
        "GameBar", "GameBarFTServer", "NVIDIA Share"
    };

    static readonly HashSet<string> SynchronizerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "Dropbox", "GoogleDriveFS", "AdobeCollabSync", "Creative Cloud", "CCXProcess"
    };

    public static GamingReadinessResult Analyze(BottleneckGateResult sample)
    {
        var reasons = new List<string>();
        var cpuTotal = sample.Interference.Sum(x => x.CpuPercent);
        var strongestCpu = sample.Interference.OrderByDescending(x => x.CpuPercent).FirstOrDefault();
        var capture = ProcessNames(sample).Where(CaptureNames.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sync = ProcessNames(sample).Where(SynchronizerNames.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var highIo = sample.Interference.Where(x => x.IoMbPerSec >= 5).OrderByDescending(x => x.IoMbPerSec).ToList();

        var severe = false;
        var attention = false;
        if (sample.MemoryLoad >= 85 || sample.AvailableMemoryGb < 3)
        {
            severe = true;
            reasons.Add($"Presión de RAM alta: {sample.MemoryLoad}% usada y {sample.AvailableMemoryGb:0.0} GB libres.");
        }
        else if (sample.MemoryLoad >= 75 || sample.AvailableMemoryGb < 6)
        {
            attention = true;
            reasons.Add($"Presión de RAM visible: {sample.MemoryLoad}% usada y {sample.AvailableMemoryGb:0.0} GB libres.");
        }

        if (cpuTotal >= 5 || strongestCpu?.CpuPercent >= 3)
        {
            severe = true;
            reasons.Add($"Actividad CPU secundaria medible: {cpuTotal:0.00}% agregada en la ventana puntual.");
        }
        else if (cpuTotal >= 1 || strongestCpu?.CpuPercent >= 0.5)
        {
            attention = true;
            reasons.Add($"Actividad CPU secundaria visible: {cpuTotal:0.00}% agregada.");
        }

        if (sample.Gpu.Available && sample.HighestOtherGpuPercent >= 15)
        {
            severe = true;
            reasons.Add($"Otro proceso coincidió con {sample.HighestOtherGpuPercent:0.0}% en su motor GPU más ocupado; HyperBoost solo lo observa.");
        }
        else if (sample.Gpu.Available && sample.HighestOtherGpuPercent >= 5)
        {
            attention = true;
            reasons.Add($"Actividad GPU secundaria visible: {sample.HighestOtherGpuPercent:0.0}% en el motor más ocupado.");
        }
        else if (!sample.Gpu.Available)
        {
            attention = true;
            reasons.Add("Windows no expuso contadores WDDM; la sección GPU queda indeterminada, no negativa.");
        }

        if (highIo.Count > 0)
        {
            attention = true;
            reasons.Add($"I/O alto coincidió con la muestra: {highIo[0].Process} {highIo[0].IoMbPerSec:0.00} MB/s. EcoQoS no limita ese tráfico.");
        }
        if (capture.Count > 0)
        {
            attention = true;
            reasons.Add("Herramientas de captura observadas: " + string.Join(", ", capture));
        }
        if (sync.Count > 0)
        {
            attention = true;
            reasons.Add("Sincronizadores observados: " + string.Join(", ", sync));
        }

        var state = severe
            ? GamingReadinessState.InterferenceDetected
            : attention ? GamingReadinessState.Attention : GamingReadinessState.Ready;
        var cpu = strongestCpu is null
            ? "sin actividad secundaria relevante en la ventana"
            : $"{cpuTotal:0.00}% agregado · mayor {strongestCpu.Process} {strongestCpu.CpuPercent:0.00}%";
        var gpu = sample.Gpu.Available
            ? $"juego {sample.GameGpuPercent:0.0}% · mayor proceso secundario {sample.HighestOtherGpuPercent:0.0}% · solo lectura"
            : "contadores WDDM no disponibles · indeterminado";
        var ram = $"{sample.MemoryLoad}% usada · {sample.AvailableMemoryGb:0.0} GB libres";
        var background = sample.Interference.Count == 0
            ? "sin procesos secundarios relevantes observados"
            : $"{sample.Interference.Count} proceso(s) con actividad/working set visible";
        return new(
            state,
            cpu,
            gpu,
            ram,
            background,
            capture.Count == 0 ? "ninguna herramienta visible en la muestra" : string.Join(", ", capture),
            sync.Count == 0 ? "ningún sincronizador visible en la muestra" : string.Join(", ", sync),
            reasons);
    }

    static IEnumerable<string> ProcessNames(BottleneckGateResult sample)
        => sample.Interference.Select(x => x.Process).Concat(sample.Gpu.Processes.Select(x => x.Process));
}
