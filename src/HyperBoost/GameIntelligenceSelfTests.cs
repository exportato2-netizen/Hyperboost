using System.Text.Json;

namespace HyperBoost;

internal static class GameIntelligenceSelfTests
{
    public static string Run()
    {
        TestCompatibilityFingerprint();
        TestPolicyIsolationAndEvidenceLevels();
        TestContextChangedAndUnknownSettings();
        TestReadinessTriState();
        TestAtomicStoreAndLegacyImport();
        return "Game Intelligence self-test OK · compatibilidad · políticas exactas · niveles de evidencia · contexto cambiado · storage/migración · Readiness tri-state";
    }

    static void TestCompatibilityFingerprint()
    {
        var baseline = Session("a", "MEJORA MEDIBLE", 6);
        Assert(GameEvidenceAnalyzer.Compare(baseline, Session("b", "MEJORA MEDIBLE", 6)).Kind == HistoricalCompatibility.Compatible,
            "sesiones idénticas no fueron compatibles");
        Assert(GameEvidenceAnalyzer.Compare(baseline, Session("b", "MEJORA MEDIBLE", 6, driver: "999")).Kind == HistoricalCompatibility.ContextChanged,
            "cambio de driver fue agregado");
        Assert(GameEvidenceAnalyzer.Compare(baseline, Session("b", "MEJORA MEDIBLE", 6, source: "MsBetweenPresents")).Kind == HistoricalCompatibility.ContextChanged,
            "cambio de fuente fue agregado");
        Assert(GameEvidenceAnalyzer.Compare(baseline, Session("b", "MEJORA MEDIBLE", 6, executableTicks: 99)).Kind == HistoricalCompatibility.ContextChanged,
            "cambio de ejecutable fue agregado");
        Assert(GameEvidenceAnalyzer.Compare(baseline, Session("b", "MEJORA MEDIBLE", 6, resolution: "1920x1080")).Kind == HistoricalCompatibility.ContextChanged,
            "cambio de resolución fue agregado");
        Assert(GameEvidenceAnalyzer.Compare(baseline, Session("b", "MEJORA MEDIBLE", 6, settings: "Ultra cap 120")).Kind == HistoricalCompatibility.ContextChanged,
            "cambio de ajustes fue agregado");
        Assert(GameEvidenceAnalyzer.Compare(baseline, Session("b", "MEJORA MEDIBLE", 6, policy: new(true, true))).Kind == HistoricalCompatibility.ContextChanged,
            "esquemas de políticas diferentes fueron atribuidos juntos");
    }

    static void TestPolicyIsolationAndEvidenceLevels()
    {
        var eco = new PolicyScheme(true, false);
        Assert(GameEvidenceAnalyzer.Analyze([], eco).Level == GameEvidenceLevel.NoData, "SIN DATOS no disponible");
        Assert(GameEvidenceAnalyzer.Analyze([Session("p", "RESULTADO PRELIMINAR", 3)], eco).Level == GameEvidenceLevel.Preliminary,
            "3 pares no quedaron preliminares en biblioteca");
        Assert(GameEvidenceAnalyzer.Analyze([Session("l", "MEJORA MEDIBLE", 6)], eco).Level == GameEvidenceLevel.Low,
            "una sesión estándar no produjo evidencia baja");

        var moderate = new[]
        {
            Session("m1", "MEJORA MEDIBLE", 6),
            Session("m2", "MEJORA MEDIBLE", 6)
        };
        Assert(GameEvidenceAnalyzer.Analyze(moderate, eco).Level == GameEvidenceLevel.Moderate,
            "dos sesiones compatibles/12 pares no produjeron evidencia moderada");

        var high = Enumerable.Range(1, 4).Select(i => Session("h" + i, "MEJORA MEDIBLE", 6)).ToList();
        Assert(GameEvidenceAnalyzer.Analyze(high, eco).Level == GameEvidenceLevel.High,
            "cuatro sesiones compatibles/24 pares no produjeron evidencia alta");

        var contradictory = new[]
        {
            Session("c1", "MEJORA MEDIBLE", 6),
            Session("c2", "REGRESIÓN MEDIBLE", 6, avg: -5, p99: -6)
        };
        Assert(GameEvidenceAnalyzer.Analyze(contradictory, eco).Level == GameEvidenceLevel.Contradictory,
            "mejora y regresión medidas no produjeron resultados contradictorios");

        var combined = Session("combined", "MEJORA MEDIBLE", 6, policy: new(true, true));
        var isolated = GameEvidenceAnalyzer.Analyze(high.Append(combined), eco);
        Assert(isolated.TotalSessions == 4 && isolated.CompatibleSessions == 4,
            "una combinación EcoQoS+Memory se atribuyó a EcoQoS individual");
    }

    static void TestContextChangedAndUnknownSettings()
    {
        var historical = Enumerable.Range(1, 4).Select(i => Session("old" + i, "MEJORA MEDIBLE", 6)).ToList();
        var current = Session("current", "SIN DATOS", 6, driver: "new-driver");
        Assert(GameEvidenceAnalyzer.Analyze(historical, new(true, false), current).Level == GameEvidenceLevel.ContextChanged,
            "una huella externa distinta no produjo CONTEXTO CAMBIÓ");

        var unknown = Enumerable.Range(1, 4)
            .Select(i => Session("u" + i, "MEJORA MEDIBLE", 6, settings: null))
            .ToList();
        Assert(GameEvidenceAnalyzer.Analyze(unknown, new(true, false)).Level == GameEvidenceLevel.Low,
            "ajustes desconocidos no limitaron la confianza a BAJA");

        var legacy = Session("legacy", "MEJORA MEDIBLE", 9);
        legacy.LegacyIncomplete = true;
        legacy.Environment = null;
        var mixed = GameEvidenceAnalyzer.Analyze(historical.Append(legacy), new(true, false));
        Assert(mixed.InsufficientMetadataSessions == 1 && mixed.CompatibleSessions == 4,
            "sesión legacy se agregó o se perdió en vez de conservarse separada");
    }

    static void TestReadinessTriState()
    {
        var gpu = new GpuScanResult(true, "synthetic", [new(100, "game", 70, "3D")], 2);
        var ready = GamingReadinessAnalyzer.Analyze(new(
            "No hacer cambios", "synthetic", [], 70, 0, 42, 18, [], gpu, 5));
        Assert(ready.State == GamingReadinessState.Ready, "muestra limpia no produjo LISTO");

        var sync = new InterferenceSample("OneDrive", 200, 0.2, 300, 1, true, "synthetic");
        var attention = GamingReadinessAnalyzer.Analyze(new(
            "Observar", "synthetic", [], 70, 0, 78, 5, [sync], gpu, 5));
        Assert(attention.State == GamingReadinessState.Attention, "presión moderada/sincronizador no produjo ATENCIÓN");

        var busy = new InterferenceSample("worker", 300, 6, 500, 0, false, "synthetic");
        var interference = GamingReadinessAnalyzer.Analyze(new(
            "Observar", "synthetic", [], 70, 0, 90, 2, [busy], gpu, 5));
        Assert(interference.State == GamingReadinessState.InterferenceDetected,
            "presión fuerte no produjo INTERFERENCIA DETECTADA");
    }

    static void TestAtomicStoreAndLegacyImport()
    {
        var root = Path.Combine(Path.GetTempPath(), "HyperBoost-GameIntelligenceSelfTest-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "library");
        var benchmarks = Path.Combine(root, "benchmarks");
        Directory.CreateDirectory(Path.Combine(benchmarks, "modern"));
        Directory.CreateDirectory(Path.Combine(benchmarks, "legacy"));
        try
        {
            var modern = Session("stored-modern", "MEJORA MEDIBLE", 6);
            var modernPayload = new
            {
                schemaVersion = 4,
                game = new { Id = 10, Name = modern.GameName, WindowTitle = "Synthetic" },
                environmentFingerprint = modern.Environment,
                pairs = modern.PairCount,
                captureSeconds = modern.CaptureSeconds,
                userReportedResolution = modern.UserReportedResolution,
                userReportedSettings = modern.UserReportedSettings,
                sessionFrameTimeSource = modern.SessionFrameTimeSource,
                onPolicies = new { ecoQos = true, memoryPriority = false },
                createdAt = modern.CreatedAtUtc,
                results = Array.Empty<BenchmarkCaptureResult>(),
                analysis = modern.Analysis
            };
            File.WriteAllText(
                Path.Combine(benchmarks, "modern", "HyperBoost-AB-results.json"),
                JsonSerializer.Serialize(modernPayload));
            File.WriteAllText(
                Path.Combine(benchmarks, "legacy", "HyperBoost-AB-results.json"),
                "{\"schemaVersion\":2,\"game\":{\"Name\":\"SyntheticGame\"},\"pairs\":3,\"captureSeconds\":15,\"onPolicies\":{\"ecoQos\":true,\"memoryPriority\":false}}" );

            var service = new GameIntelligenceService(library, benchmarks);
            Task.Run(() => service.InitializeAsync()).GetAwaiter().GetResult();
            Assert(service.Profiles.Sum(x => x.Sessions.Count) == 2, "importación moderna/legacy incompleta");
            Assert(service.Profiles.SelectMany(x => x.Sessions).Count(x => x.LegacyIncomplete) == 1,
                "schema legado no fue conservado como histórico no agregable");
            Assert(File.Exists(Path.Combine(library, "evidence-library-v1.json")), "persistencia atómica no produjo documento");
            Assert(!File.Exists(Path.Combine(library, "evidence-library-v1.json.tmp")), "quedó temporal tras escritura atómica");

            var reloaded = new GameIntelligenceService(library, benchmarks);
            Task.Run(() => reloaded.InitializeAsync()).GetAwaiter().GetResult();
            Assert(reloaded.Profiles.Sum(x => x.Sessions.Count) == 2, "reload duplicó o perdió sesiones importadas");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static GameEvidenceSession Session(
        string id,
        string verdict,
        int pairs,
        string driver = "32.0.15.9000",
        string source = "DisplayedTime",
        long executableTicks = 42,
        string? resolution = "2560x1440",
        string? settings = "Ultra · cap 144 · patch 1.0",
        PolicyScheme? policy = null,
        double avg = 5,
        double p99 = 7)
    {
        var created = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(id.GetHashCode(StringComparison.Ordinal) & 255);
        var environment = new BenchmarkEnvironmentFingerprint(
            4,
            id,
            "0.7.0.0",
            "Windows 11 10.0.26100",
            "Synthetic CPU",
            "Synthetic GPU",
            driver,
            32,
            "SyntheticGame",
            100,
            123,
            @"C:\Games\Synthetic\game.exe",
            executableTicks,
            1_000_000,
            "2.5.1",
            PresentMonBenchmarkService.PresentMonExpectedSha256,
            policy?.EcoQos ?? true,
            policy?.MemoryPriorityExperimental ?? false,
            BenchmarkScenarioType.BuiltInBenchmark,
            resolution,
            settings,
            created,
            30,
            pairs);
        var stable = pairs >= 6;
        return new()
        {
            SessionId = id,
            CreatedAtUtc = created,
            LastObservedRunUtc = created,
            SourceResultsSchemaVersion = 4,
            SourceResultsPath = $@"C:\Synthetic\{id}\HyperBoost-AB-results.json",
            HyperBoostVersion = "0.7.0.0",
            GameName = "SyntheticGame",
            ExecutablePath = environment.ExecutablePath,
            Policies = policy ?? new(true, false),
            ScenarioType = BenchmarkScenarioType.BuiltInBenchmark,
            UserReportedResolution = resolution,
            UserReportedSettings = settings,
            SessionFrameTimeSource = source,
            CaptureSeconds = 30,
            PairCount = pairs,
            Environment = environment,
            Analysis = new()
            {
                Verdict = verdict,
                SessionEvidenceLevel = stable ? "ESTÁNDAR" : "PRELIMINAR",
                CompletedPairs = pairs,
                AverageFpsDeltaPercent = avg,
                P99ImprovementPercent = p99,
                AverageFpsConfidenceInterval = new(stable ? avg - 1 : null, stable ? avg + 1 : null, stable, pairs, "synthetic paired"),
                P99ConfidenceInterval = new(stable ? p99 - 1 : null, stable ? p99 + 1 : null, stable, pairs, "synthetic paired")
            },
            ComparabilityLimitation = string.IsNullOrWhiteSpace(settings) ? "ajustes no declarados" : null
        };
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Game Intelligence self-test: " + message);
    }
}
