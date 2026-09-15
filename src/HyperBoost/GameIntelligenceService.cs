using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HyperBoost;

public sealed class GameIntelligenceService
{
    readonly SemaphoreSlim mutex = new(1, 1);
    readonly string rootDirectory;
    readonly string libraryPath;
    readonly string benchmarksRoot;
    readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    GameIntelligenceDocument document = new();
    bool initialized;
    string? unreadableOriginalPath;

    public GameIntelligenceService(string? rootDirectory = null, string? benchmarksRoot = null)
    {
        var hyperBoostRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HyperBoost");
        this.rootDirectory = rootDirectory ?? Path.Combine(hyperBoostRoot, "GameIntelligence");
        this.benchmarksRoot = benchmarksRoot ?? Path.Combine(hyperBoostRoot, "Benchmarks");
        libraryPath = Path.Combine(this.rootDirectory, "evidence-library-v1.json");
    }

    public IReadOnlyList<GameProfile> Profiles => document.Profiles
        .OrderByDescending(x => x.LastRunUtc)
        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string Status { get; private set; } = "Game Intelligence aún no se cargó.";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await mutex.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            Directory.CreateDirectory(rootDirectory);
            await LoadAsync(cancellationToken);
            var imported = await ImportExistingBenchmarksAsync(cancellationToken);
            if (imported > 0) await SaveAtomicAsync(cancellationToken);
            initialized = true;
            Status = unreadableOriginalPath is null
                ? $"Game Intelligence listo · {document.Profiles.Count} perfil(es) · {document.Profiles.Sum(x => x.Sessions.Count)} sesión(es)."
                : $"La biblioteca anterior no se pudo leer y se preservó en {unreadableOriginalPath}. Se inició una biblioteca recuperable nueva.";
        }
        finally
        {
            mutex.Release();
        }
    }

    public async Task<GameProfile> RecordSessionAsync(
        AbBenchmarkSession session,
        BenchmarkAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            var evidence = FromCompletedSession(session, analysis);
            var profile = FindOrCreateProfile(evidence);
            if (!profile.Sessions.Any(x => string.Equals(x.SessionId, evidence.SessionId, StringComparison.Ordinal)))
                profile.Sessions.Add(evidence);
            UpdateProfile(profile, evidence);
            await SaveAtomicAsync(cancellationToken);
            Status = $"Sesión añadida a {profile.Name}; la recomendación se recalculó por contexto y política exacta.";
            return profile;
        }
        finally
        {
            mutex.Release();
        }
    }

    public async Task RefreshImportsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            var imported = await ImportExistingBenchmarksAsync(cancellationToken);
            if (imported > 0) await SaveAtomicAsync(cancellationToken);
            Status = $"Game Intelligence listo · {document.Profiles.Count} perfil(es) · {document.Profiles.Sum(x => x.Sessions.Count)} sesión(es) · {imported} importada(s) ahora.";
        }
        finally
        {
            mutex.Release();
        }
    }

    public IReadOnlyList<EvidenceSummary> Analyze(GameProfile profile)
        => profile.Sessions
            .Select(x => x.Policies)
            .DistinctBy(x => x.Key, StringComparer.Ordinal)
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(policy => GameEvidenceAnalyzer.Analyze(profile.Sessions, policy))
            .ToList();

    public string BuildProfileReport(GameProfile? profile)
    {
        if (profile is null)
            return "SIN DATOS\n\nCompleta un benchmark A/B. Game Hub creará el perfil local automáticamente y conservará las sesiones antiguas sin mezclarlas a ciegas.";

        var sb = new StringBuilder();
        sb.AppendLine(profile.Name.ToUpperInvariant())
          .AppendLine($"Perfil local: {profile.ProfileId}")
          .AppendLine($"Ejecutable: {profile.LastKnownExecutablePath ?? "identidad no disponible"}")
          .AppendLine($"Sesiones: {profile.Sessions.Count} · última observación {profile.LastRunUtc.ToLocalTime():yyyy-MM-dd HH:mm}")
          .AppendLine("Agregación: misma huella, ejecutable, fuente, escenario, duración, resolución, ajustes conocidos y política ON exacta.")
          .AppendLine(new string('-', 78));

        foreach (var summary in Analyze(profile))
        {
            sb.AppendLine(summary.ToDisplayText());
            var sessions = profile.Sessions
                .Where(x => string.Equals(x.Policies.Key, summary.PolicyKey, StringComparison.Ordinal))
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(8);
            foreach (var session in sessions)
            {
                sb.AppendLine($"    {session.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {session.PairCount} pares · {session.ScenarioType.DisplayName()} · FPS {session.Analysis.AverageFpsDeltaPercent:+0.00;-0.00;0.00}% · p99 {session.Analysis.P99ImprovementPercent:+0.00;-0.00;0.00}%")
                  .AppendLine($"      {FirstLine(session.Analysis.Verdict)}{(session.ComparabilityLimitation is null ? "" : " · " + session.ComparabilityLimitation)}");
            }
            sb.AppendLine(new string('-', 78));
        }

        sb.AppendLine("Los niveles describen evidencia para este PC + juego + contexto. No prueban que una política funcione en otros equipos.");
        return sb.ToString();
    }

    async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(libraryPath))
        {
            document = new();
            return;
        }

        try
        {
            await using var stream = new FileStream(libraryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            var loaded = await JsonSerializer.DeserializeAsync<GameIntelligenceDocument>(stream, jsonOptions, cancellationToken)
                         ?? throw new InvalidDataException("Documento JSON vacío.");
            if (loaded.SchemaVersion > GameIntelligenceDocument.CurrentSchemaVersion)
                throw new InvalidDataException($"schemaVersion {loaded.SchemaVersion} pertenece a una versión más nueva.");
            loaded.SchemaVersion = GameIntelligenceDocument.CurrentSchemaVersion;
            loaded.Profiles ??= [];
            foreach (var profile in loaded.Profiles)
            {
                profile.Sessions ??= [];
                foreach (var session in profile.Sessions)
                {
                    session.Policies ??= new(false, false);
                    session.Analysis ??= new();
                    session.Captures ??= [];
                }
            }
            document = loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            unreadableOriginalPath = Path.Combine(rootDirectory, $"evidence-library-unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            try { File.Copy(libraryPath, unreadableOriginalPath, false); }
            catch { unreadableOriginalPath = libraryPath + " (no se pudo copiar; no fue eliminado)"; }
            document = new();
        }
    }

    async Task<int> ImportExistingBenchmarksAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(benchmarksRoot)) return 0;
        string[] files;
        try { files = Directory.GetFiles(benchmarksRoot, "HyperBoost-AB-results.json", SearchOption.AllDirectories); }
        catch { return 0; }

        var imported = 0;
        foreach (var file in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(file);
            if (document.Profiles.SelectMany(x => x.Sessions).Any(x => SamePath(x.SourceResultsPath, fullPath))) continue;
            var session = await TryImportAsync(fullPath, cancellationToken);
            if (session is null) continue;
            var profile = FindOrCreateProfile(session);
            if (profile.Sessions.Any(x => string.Equals(x.SessionId, session.SessionId, StringComparison.Ordinal))) continue;
            profile.Sessions.Add(session);
            UpdateProfile(profile, session);
            imported++;
        }
        return imported;
    }

    async Task<GameEvidenceSession?> TryImportAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;
            var schema = TryInt(root, "schemaVersion");
            var gameName = TryNestedString(root, "game", "Name") ?? Path.GetFileName(Path.GetDirectoryName(path)) ?? "Juego importado";
            var created = TryDate(root, "createdAt") ?? File.GetLastWriteTimeUtc(path);

            if (schema < 3 || !root.TryGetProperty("environmentFingerprint", out var environmentJson))
            {
                return new()
                {
                    SessionId = StableId(path + "|" + created.Ticks),
                    CreatedAtUtc = created.ToUniversalTime(),
                    LastObservedRunUtc = created.ToUniversalTime(),
                    SourceResultsSchemaVersion = schema,
                    SourceResultsPath = path,
                    GameName = gameName,
                    PairCount = TryInt(root, "pairs"),
                    CaptureSeconds = TryInt(root, "captureSeconds"),
                    Policies = ReadPolicies(root),
                    LegacyIncomplete = true,
                    ComparabilityLimitation = $"schema {schema}: sin huella suficiente; histórico conservado, no agregado"
                };
            }

            var environment = environmentJson.Deserialize<BenchmarkEnvironmentFingerprint>(jsonOptions);
            if (environment is null) return null;
            var analysis = root.TryGetProperty("analysis", out var analysisJson)
                ? ReadAnalysis(analysisJson)
                : new BenchmarkEvidenceSnapshot { Verdict = "SIN DATOS · análisis no disponible" };
            var captures = root.TryGetProperty("results", out var resultsJson)
                ? resultsJson.Deserialize<List<BenchmarkCaptureResult>>(jsonOptions) ?? []
                : [];
            var settings = TryString(root, "userReportedSettings") ?? environment.UserReportedSettings;
            return new()
            {
                SessionId = string.IsNullOrWhiteSpace(environment.SessionId) ? StableId(path + "|" + created.Ticks) : environment.SessionId,
                CreatedAtUtc = environment.CreatedAtUtc == default ? created.ToUniversalTime() : environment.CreatedAtUtc,
                LastObservedRunUtc = ProcessStart(environment) ?? created.ToUniversalTime(),
                SourceResultsSchemaVersion = schema,
                SourceResultsPath = path,
                HyperBoostVersion = environment.HyperBoostVersion,
                GameName = gameName,
                ExecutablePath = environment.ExecutablePath,
                Policies = ReadPolicies(root),
                ScenarioType = environment.ScenarioType,
                UserReportedResolution = TryString(root, "userReportedResolution") ?? environment.UserReportedResolution,
                UserReportedSettings = settings,
                SessionFrameTimeSource = TryString(root, "sessionFrameTimeSource") ?? "",
                CaptureSeconds = TryInt(root, "captureSeconds"),
                PairCount = TryInt(root, "pairs"),
                Environment = environment,
                Analysis = analysis,
                Captures = captures,
                ComparabilityLimitation = string.IsNullOrWhiteSpace(settings)
                    ? "ajustes del juego no declarados; evidencia agregada limitada a BAJA"
                    : null
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    static GameEvidenceSession FromCompletedSession(AbBenchmarkSession session, BenchmarkAnalysis analysis)
        => new()
        {
            SessionId = session.EnvironmentFingerprint.SessionId,
            CreatedAtUtc = session.EnvironmentFingerprint.CreatedAtUtc,
            LastObservedRunUtc = ProcessStart(session.EnvironmentFingerprint) ?? session.EnvironmentFingerprint.CreatedAtUtc,
            SourceResultsSchemaVersion = 4,
            SourceResultsPath = Path.Combine(session.RootDirectory, "HyperBoost-AB-results.json"),
            HyperBoostVersion = session.HyperBoostVersion,
            GameName = session.Game.Name,
            ExecutablePath = session.EnvironmentFingerprint.ExecutablePath,
            Policies = new(session.UseEcoQos, session.UseMemoryPriority),
            ScenarioType = session.ScenarioType,
            UserReportedResolution = session.UserReportedResolution,
            UserReportedSettings = session.UserReportedSettings,
            SessionFrameTimeSource = session.SessionFrameTimeSource ?? "",
            CaptureSeconds = session.CaptureSeconds,
            PairCount = analysis.CompletedPairs,
            Environment = session.EnvironmentFingerprint,
            Analysis = BenchmarkEvidenceSnapshot.From(analysis),
            Captures = session.Results.ToList(),
            ComparabilityLimitation = string.IsNullOrWhiteSpace(session.UserReportedSettings)
                ? "ajustes del juego no declarados; evidencia agregada limitada a BAJA"
                : null
        };

    GameProfile FindOrCreateProfile(GameEvidenceSession session)
    {
        var identity = ProfileIdentity(session.GameName, session.ExecutablePath);
        var profile = document.Profiles.FirstOrDefault(x => string.Equals(x.ExecutableIdentity, identity, StringComparison.OrdinalIgnoreCase));
        profile ??= document.Profiles.FirstOrDefault(x =>
            string.Equals(x.Name, session.GameName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(session.ExecutablePath) || string.IsNullOrWhiteSpace(x.LastKnownExecutablePath)));
        if (profile is not null && !string.IsNullOrWhiteSpace(session.ExecutablePath) && string.IsNullOrWhiteSpace(profile.LastKnownExecutablePath))
            profile.ExecutableIdentity = identity;
        if (profile is not null) return profile;
        profile = new()
        {
            ProfileId = StableId(identity),
            Name = session.GameName,
            ExecutableIdentity = identity,
            LastKnownExecutablePath = session.ExecutablePath,
            CreatedAtUtc = session.CreatedAtUtc,
            LastRunUtc = session.LastObservedRunUtc
        };
        document.Profiles.Add(profile);
        return profile;
    }

    static void UpdateProfile(GameProfile profile, GameEvidenceSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.GameName)) profile.Name = session.GameName;
        if (!string.IsNullOrWhiteSpace(session.ExecutablePath)) profile.LastKnownExecutablePath = session.ExecutablePath;
        if (session.LastObservedRunUtc > profile.LastRunUtc) profile.LastRunUtc = session.LastObservedRunUtc;
    }

    async Task SaveAtomicAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootDirectory);
        document.SchemaVersion = GameIntelligenceDocument.CurrentSchemaVersion;
        document.UpdatedAtUtc = DateTime.UtcNow;
        var temp = libraryPath + ".tmp";
        var backup = libraryPath + ".bak";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            if (File.Exists(libraryPath))
                File.Replace(temp, libraryPath, backup, true);
            else
                File.Move(temp, libraryPath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    static BenchmarkEvidenceSnapshot ReadAnalysis(JsonElement value)
        => new()
        {
            Verdict = TryString(value, "Verdict") ?? TryString(value, "verdict") ?? "SIN DATOS",
            SessionEvidenceLevel = TryString(value, "EvidenceLevel") ?? TryString(value, "evidenceLevel") ?? "SIN DATOS",
            CompletedPairs = TryIntEither(value, "CompletedPairs", "completedPairs"),
            AverageFpsDeltaPercent = TryDoubleEither(value, "AverageFpsDeltaPercent", "averageFpsDeltaPercent"),
            P99ImprovementPercent = TryDoubleEither(value, "P99ImprovementPercent", "p99ImprovementPercent"),
            BaselineNoiseAverageFpsPercent = TryDoubleEither(value, "BaselineNoiseAverageFpsPercent", "baselineNoiseAverageFpsPercent"),
            BaselineNoiseP99Percent = TryDoubleEither(value, "BaselineNoiseP99Percent", "baselineNoiseP99Percent"),
            AverageFpsConfidenceInterval = ReadInterval(value, "AverageFpsConfidenceInterval", "averageFpsConfidenceInterval"),
            P99ConfidenceInterval = ReadInterval(value, "P99ConfidenceInterval", "p99ConfidenceInterval")
        };

    static ConfidenceInterval ReadInterval(JsonElement root, string pascal, string camel)
    {
        if (!TryPropertyEither(root, pascal, camel, out var value)) return new(null, null, false, 0, "No disponible");
        return new(
            TryNullableDouble(value, "LowerPercent", "lowerPercent"),
            TryNullableDouble(value, "UpperPercent", "upperPercent"),
            TryBoolEither(value, "Stable", "stable"),
            TryIntEither(value, "PairCount", "pairCount"),
            TryString(value, "Method") ?? TryString(value, "method") ?? "No disponible");
    }

    static PolicyScheme ReadPolicies(JsonElement root)
    {
        if (!root.TryGetProperty("onPolicies", out var policies)) return new(false, false);
        return new(TryBoolEither(policies, "EcoQos", "ecoQos"), TryBoolEither(policies, "MemoryPriority", "memoryPriority"));
    }

    static string ProfileIdentity(string game, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try { return "path:" + Path.GetFullPath(executablePath).Trim().ToUpperInvariant(); }
            catch { return "path:" + executablePath.Trim().ToUpperInvariant(); }
        }
        return "name:" + game.Trim().ToUpperInvariant();
    }

    static DateTime? ProcessStart(BenchmarkEnvironmentFingerprint environment)
    {
        if (!environment.ProcessStartTimeUtcTicks.HasValue) return null;
        try { return new DateTime(environment.ProcessStartTimeUtcTicks.Value, DateTimeKind.Utc); }
        catch { return null; }
    }

    static string StableId(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    static string FirstLine(string value)
    {
        var index = value.IndexOfAny(['\r', '\n']);
        return index < 0 ? value : value[..index];
    }

    static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    static int TryInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    static int TryIntEither(JsonElement root, string a, string b)
        => TryPropertyEither(root, a, b, out var value) && value.TryGetInt32(out var result) ? result : 0;
    static double TryDoubleEither(JsonElement root, string a, string b)
        => TryPropertyEither(root, a, b, out var value) && value.TryGetDouble(out var result) ? result : 0;
    static double? TryNullableDouble(JsonElement root, string a, string b)
        => TryPropertyEither(root, a, b, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetDouble(out var result) ? result : null;
    static bool TryBoolEither(JsonElement root, string a, string b)
        => TryPropertyEither(root, a, b, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    static bool TryPropertyEither(JsonElement root, string a, string b, out JsonElement value)
        => root.TryGetProperty(a, out value) || root.TryGetProperty(b, out value);
    static string? TryString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static string? TryNestedString(JsonElement root, string parent, string name)
        => root.TryGetProperty(parent, out var value) ? TryString(value, name) ?? TryString(value, char.ToLowerInvariant(name[0]) + name[1..]) : null;
    static DateTime? TryDate(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetDateTime(out var result) ? result : null;
}
