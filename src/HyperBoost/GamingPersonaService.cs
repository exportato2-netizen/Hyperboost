using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperBoost;

public sealed record InterferenceSample(
    string Process,
    int Pid,
    double CpuPercent,
    double WorkingSetMb,
    double IoMbPerSec,
    bool EligibleForAutomaticQoS,
    string Recommendation);

public sealed class GamingPersonaService : IDisposable
{
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint ProcessSetInformation = 0x0200;
    const int ProcessMemoryPriority = 0;
    const int ProcessPowerThrottling = 4;
    const uint PowerThrottlingExecutionSpeed = 0x1;
    const uint MemoryPriorityBelowNormal = 4;

    static readonly HashSet<string> SafeAutomaticBackground = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "Dropbox", "GoogleDriveFS",
        "CCXProcess", "AdobeIPCBroker", "AdobeCollabSync",
        "Creative Cloud", "Adobe Desktop Service"
    };

    static readonly HashSet<string> OptionalBackground = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx",
        "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper"
    };

    static readonly HashSet<string> ExplicitDoNotTouch = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "Discord", "obs64", "gameoverlayui", "GameBar", "GameBarFTServer",
        "NVIDIA Share", "nvcontainer", "RTSS", "MSIAfterburner", "lghub", "lghub_agent",
        "RazerAppEngine", "ArmouryCrate", "LightingService",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgc", "vgtray", "FACEIT"
    };

    readonly GameBoostService gameBoost = new();
    readonly Dictionary<int, BackgroundSnapshot> adjustedBackground = [];
    readonly Dictionary<int, CpuObservation> cpuObservations = [];

    int gamePid;
    ulong gameCreationTime;
    bool enabled;
    bool engaged;
    bool useAboveNormal;
    bool honorTimerRequests;
    bool useEcoQos;
    bool useMemoryPriority;
    bool includeOptionalBackground;
    DateTime lastReconcileUtc = DateTime.MinValue;
    string status = "Gaming Persona detenida.";

    public bool IsEnabled => enabled;
    public bool IsEngaged => engaged;
    public int AdjustedBackgroundCount => adjustedBackground.Values.Count(x => x.PowerChanged);
    public int MemoryAdjustedCount => adjustedBackground.Values.Count(x => x.MemoryChanged);
    public string Status => status;

    public IReadOnlyList<GameProcessCandidate> GetCandidates() => gameBoost.GetCandidates();

    public string Start(
        GameProcessCandidate game,
        bool aboveNormal,
        bool timers,
        bool ecoQos,
        bool memoryPriority,
        bool includeOptional)
    {
        if (enabled) throw new InvalidOperationException("Gaming Persona ya está armada. Deténla antes de elegir otro juego.");

        if (!TryReadCreationTime(game.Id, out var creationTime))
            throw new InvalidOperationException("El proceso seleccionado terminó o Windows no permitió verificar su identidad.");

        gamePid = game.Id;
        gameCreationTime = creationTime;
        useAboveNormal = aboveNormal;
        honorTimerRequests = timers;
        useEcoQos = ecoQos;
        useMemoryPriority = memoryPriority;
        includeOptionalBackground = includeOptional;
        enabled = true;
        engaged = false;
        status = $"Gaming Persona armada para {game.Name} · PID {game.Id}. Se activará solo cuando el juego esté en primer plano.";

        Tick();
        return status;
    }

    public string Stop()
    {
        if (!enabled && !engaged && adjustedBackground.Count == 0 && !gameBoost.IsActive)
            return "Gaming Persona ya estaba detenida.";

        var restore = Disengage();
        enabled = false;
        gamePid = 0;
        gameCreationTime = 0;
        status = restore.Errors == 0
            ? "Gaming Persona detenida. Todos los ajustes de sesión que seguían disponibles fueron restaurados."
            : $"Gaming Persona detenida. Hubo {restore.Errors} restauraciones que Windows no permitió; los ajustes por proceso desaparecen al cerrar esos procesos.";
        return status;
    }

    public void Tick()
    {
        if (!enabled) return;

        if (!IsSameProcess(gamePid, gameCreationTime))
        {
            var restore = Disengage();
            enabled = false;
            status = restore.Errors == 0
                ? "El juego terminó. Gaming Persona restauró los ajustes de sesión y se desarmó."
                : "El juego terminó. Gaming Persona se desarmó; algunos procesos secundarios ya habían terminado antes de restaurarlos.";
            return;
        }

        var foregroundPid = GetForegroundProcessId();
        if (foregroundPid == gamePid)
        {
            if (!engaged)
            {
                Engage();
                return;
            }

            if (DateTime.UtcNow - lastReconcileUtc >= TimeSpan.FromSeconds(3))
            {
                ReconcileBackground();
                UpdateActiveStatus();
            }
        }
        else if (engaged)
        {
            var restore = Disengage();
            status = restore.Errors == 0
                ? "Juego fuera de foco: Gaming Persona quedó en espera y restauró los cambios temporales."
                : "Juego fuera de foco: Gaming Persona quedó en espera; algún proceso secundario terminó antes de restaurarse.";
        }
    }

    void Engage()
    {
        var notes = new List<string>();

        try
        {
            notes.Add(gameBoost.Start(gamePid, useAboveNormal, honorTimerRequests));
        }
        catch (Exception ex)
        {
            notes.Add("Windows/anti-cheat no permitió ajustar el proceso del juego: " + ex.Message);
        }

        engaged = true;
        ReconcileBackground();
        UpdateActiveStatus();
    }

    RestoreResult Disengage()
    {
        var errors = RestoreBackground();

        if (gameBoost.IsActive)
        {
            try
            {
                var result = gameBoost.Stop();
                if (result.Contains("no permitió restaurar", StringComparison.OrdinalIgnoreCase)) errors++;
            }
            catch
            {
                errors++;
            }
        }

        engaged = false;
        cpuObservations.Clear();
        return new(errors);
    }

    void ReconcileBackground()
    {
        lastReconcileUtc = DateTime.UtcNow;
        var memoryPressure = ReadMemoryPressure();
        var currentSession = Process.GetCurrentProcess().SessionId;
        var seen = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == gamePid || process.Id == Environment.ProcessId || process.SessionId != currentSession) continue;
                    var name = process.ProcessName;
                    if (!ShouldConsiderForBackgroundQoS(name)) continue;
                    seen.Add(process.Id);

                    // Un proceso completamente inactivo no necesita ser modificado. Dos observaciones consecutivas
                    // permiten detectar actividad sin bloquear el hilo de interfaz.
                    var activeEnough = IsProcessActive(process);
                    if (!activeEnough && !adjustedBackground.ContainsKey(process.Id)) continue;

                    if (!adjustedBackground.TryGetValue(process.Id, out var snapshot))
                    {
                        snapshot = TryApplyBackgroundPolicy(process.Id, name, memoryPressure);
                        if (snapshot is not null) adjustedBackground[process.Id] = snapshot;
                    }
                    else
                    {
                        ReconcileMemoryPriority(snapshot, memoryPressure);
                    }
                }
                catch
                {
                    // Procesos que cambian/terminan durante el barrido o niegan información se ignoran.
                }
            }
        }

        // Si un proceso ajustado terminó, ya no existe nada que restaurar. Si dejó de cumplir la política
        // por un cambio de configuración, se restaura mientras conserve la misma identidad.
        foreach (var pid in adjustedBackground.Keys.ToList())
        {
            if (seen.Contains(pid)) continue;
            RestoreBackgroundProcess(adjustedBackground[pid]);
            adjustedBackground.Remove(pid);
            cpuObservations.Remove(pid);
        }
    }

    bool ShouldConsiderForBackgroundQoS(string name)
    {
        if (ExplicitDoNotTouch.Contains(name)) return false;
        if (SafeAutomaticBackground.Contains(name)) return true;
        return includeOptionalBackground && OptionalBackground.Contains(name);
    }

    bool IsProcessActive(Process process)
    {
        var now = DateTime.UtcNow;
        var totalCpu = process.TotalProcessorTime;
        var workingSet = process.WorkingSet64;

        if (!cpuObservations.TryGetValue(process.Id, out var previous))
        {
            cpuObservations[process.Id] = new(totalCpu, now, workingSet);
            return workingSet >= 150L * 1024 * 1024;
        }

        cpuObservations[process.Id] = new(totalCpu, now, workingSet);
        var elapsedMs = Math.Max(1, (now - previous.TimestampUtc).TotalMilliseconds);
        var cpu = (totalCpu - previous.CpuTime).TotalMilliseconds / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100d;
        return cpu >= 0.15 || workingSet >= 200L * 1024 * 1024;
    }

    BackgroundSnapshot? TryApplyBackgroundPolicy(int pid, string name, MemoryPressure pressure)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var creation = ReadCreationTime(handle);
            var power = new ProcessPowerThrottlingState { Version = 1 };
            var powerSize = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
            var havePower = GetProcessInformationPower(handle, ProcessPowerThrottling, ref power, powerSize);

            var memory = new MemoryPriorityInformation();
            var memorySize = (uint)Marshal.SizeOf<MemoryPriorityInformation>();
            var haveMemory = GetProcessInformationMemory(handle, ProcessMemoryPriority, ref memory, memorySize);

            var snapshot = new BackgroundSnapshot(pid, name, creation, power, memory.MemoryPriority, havePower, haveMemory);

            if (useEcoQos && havePower)
            {
                var eco = power;
                eco.Version = 1;
                eco.ControlMask |= PowerThrottlingExecutionSpeed;
                eco.StateMask |= PowerThrottlingExecutionSpeed;
                if (SetProcessInformationPower(handle, ProcessPowerThrottling, ref eco, powerSize))
                    snapshot.PowerChanged = true;
            }

            if (useMemoryPriority && pressure.ShouldLowerBackgroundMemory && haveMemory && memory.MemoryPriority > MemoryPriorityBelowNormal)
            {
                var lower = new MemoryPriorityInformation { MemoryPriority = MemoryPriorityBelowNormal };
                if (SetProcessInformationMemory(handle, ProcessMemoryPriority, ref lower, memorySize))
                    snapshot.MemoryChanged = true;
            }

            return snapshot.PowerChanged || snapshot.MemoryChanged ? snapshot : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    void ReconcileMemoryPriority(BackgroundSnapshot snapshot, MemoryPressure pressure)
    {
        if (!useMemoryPriority || !snapshot.MemoryAvailable) return;
        if (!IsSameProcess(snapshot.Pid, snapshot.CreationTime)) return;

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, snapshot.Pid);
        if (handle == IntPtr.Zero) return;

        try
        {
            var size = (uint)Marshal.SizeOf<MemoryPriorityInformation>();
            if (pressure.ShouldLowerBackgroundMemory && !snapshot.MemoryChanged && snapshot.OriginalMemoryPriority > MemoryPriorityBelowNormal)
            {
                var lower = new MemoryPriorityInformation { MemoryPriority = MemoryPriorityBelowNormal };
                if (SetProcessInformationMemory(handle, ProcessMemoryPriority, ref lower, size)) snapshot.MemoryChanged = true;
            }
            else if (!pressure.ShouldLowerBackgroundMemory && snapshot.MemoryChanged)
            {
                var original = new MemoryPriorityInformation { MemoryPriority = snapshot.OriginalMemoryPriority };
                if (SetProcessInformationMemory(handle, ProcessMemoryPriority, ref original, size)) snapshot.MemoryChanged = false;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    int RestoreBackground()
    {
        var errors = 0;
        foreach (var snapshot in adjustedBackground.Values)
            if (!RestoreBackgroundProcess(snapshot)) errors++;
        adjustedBackground.Clear();
        return errors;
    }

    bool RestoreBackgroundProcess(BackgroundSnapshot snapshot)
    {
        if (!IsSameProcess(snapshot.Pid, snapshot.CreationTime)) return true;

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, snapshot.Pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            var ok = true;
            if (snapshot.PowerChanged && snapshot.PowerAvailable)
            {
                var originalPower = snapshot.OriginalPower;
                var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
                if (!SetProcessInformationPower(handle, ProcessPowerThrottling, ref originalPower, size)) ok = false;
            }

            if (snapshot.MemoryChanged && snapshot.MemoryAvailable)
            {
                var originalMemory = new MemoryPriorityInformation { MemoryPriority = snapshot.OriginalMemoryPriority };
                var size = (uint)Marshal.SizeOf<MemoryPriorityInformation>();
                if (!SetProcessInformationMemory(handle, ProcessMemoryPriority, ref originalMemory, size)) ok = false;
            }
            return ok;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    void UpdateActiveStatus()
    {
        var pressure = ReadMemoryPressure();
        status = $"Gaming Persona ACTIVA · background EcoQoS: {AdjustedBackgroundCount} · memoria 5→4: {MemoryAdjustedCount} · RAM usada: {pressure.MemoryLoad}% ({pressure.AvailableGb:0.0} GB libres).";
    }

    public async Task<IReadOnlyList<InterferenceSample>> ScanInterferenceAsync(int excludedGamePid)
    {
        var first = CaptureInterferencePoints(excludedGamePid);
        await Task.Delay(800);
        var second = CaptureInterferencePoints(excludedGamePid);
        const double seconds = 0.8;

        var result = new List<InterferenceSample>();
        foreach (var (pid, b) in second)
        {
            if (!first.TryGetValue(pid, out var a) || !a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)) continue;

            var cpuMs = Math.Max(0, (b.CpuTime - a.CpuTime).TotalMilliseconds);
            var cpuPercent = cpuMs / (seconds * 1000d) / Math.Max(1, Environment.ProcessorCount) * 100d;
            var io = Math.Max(0, b.IoBytes - a.IoBytes) / seconds / 1_048_576d;
            var workingMb = b.WorkingSetBytes / 1_048_576d;
            if (cpuPercent < 0.03 && io < 0.05 && workingMb < 100) continue;

            var eligible = SafeAutomaticBackground.Contains(b.Name) || OptionalBackground.Contains(b.Name);
            var recommendation = ExplicitDoNotTouch.Contains(b.Name)
                ? "Solo observar: audio/captura/overlay/periférico/anti-cheat protegido por política."
                : SafeAutomaticBackground.Contains(b.Name)
                    ? "Elegible automáticamente para EcoQoS mientras el juego esté en foco."
                    : OptionalBackground.Contains(b.Name)
                        ? "Elegible solo si activas el grupo opcional de navegadores/launchers."
                        : "Solo diagnóstico en Beta 0.3; no se modifica automáticamente.";

            result.Add(new(b.Name, pid, cpuPercent, workingMb, io, eligible, recommendation));
        }

        return result
            .OrderByDescending(x => x.CpuPercent * 2 + x.IoMbPerSec + x.WorkingSetMb / 2048d)
            .Take(15)
            .ToList();
    }

    Dictionary<int, InterferencePoint> CaptureInterferencePoints(int excludedGamePid)
    {
        var map = new Dictionary<int, InterferencePoint>();
        var currentSession = Process.GetCurrentProcess().SessionId;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == excludedGamePid || process.Id == Environment.ProcessId || process.SessionId != currentSession) continue;
                    var name = process.ProcessName;
                    var cpu = process.TotalProcessorTime;
                    var working = process.WorkingSet64;
                    var io = TryReadIoBytes(process.Id);
                    map[process.Id] = new(name, cpu, working, io);
                }
                catch
                {
                    // Solo diagnóstico: ignorar procesos inaccesibles es más seguro que elevar privilegios.
                }
            }
        }

        return map;
    }

    static ulong TryReadIoBytes(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            return GetProcessIoCounters(handle, out var io) ? io.ReadTransferCount + io.WriteTransferCount : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static MemoryPressure ReadMemoryPressure()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return new(0, 0, false);
        var availableGb = status.AvailablePhysical / 1073741824d;
        // Umbral deliberadamente conservador: solo se toca Memory Priority cuando hay presión real.
        var shouldLower = status.MemoryLoad >= 75 || availableGb < 6;
        return new(status.MemoryLoad, availableGb, shouldLower);
    }

    static int GetForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out var pid);
        return unchecked((int)pid);
    }

    static bool TryReadCreationTime(int pid, out ulong creation)
    {
        creation = 0;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            creation = ReadCreationTime(handle);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static bool IsSameProcess(int pid, ulong creation)
        => pid > 0 && TryReadCreationTime(pid, out var current) && current == creation;

    static ulong ReadCreationTime(IntPtr handle)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return creation.Value;
    }

    public void Dispose()
    {
        try { Stop(); }
        catch { /* Los cambios por proceso desaparecen con sus procesos; nunca se fuerza acceso al cerrar. */ }
        gameBoost.Dispose();
    }

    sealed class BackgroundSnapshot(
        int pid,
        string name,
        ulong creationTime,
        ProcessPowerThrottlingState originalPower,
        uint originalMemoryPriority,
        bool powerAvailable,
        bool memoryAvailable)
    {
        public int Pid { get; } = pid;
        public string Name { get; } = name;
        public ulong CreationTime { get; } = creationTime;
        public ProcessPowerThrottlingState OriginalPower { get; } = originalPower;
        public uint OriginalMemoryPriority { get; } = originalMemoryPriority;
        public bool PowerAvailable { get; } = powerAvailable;
        public bool MemoryAvailable { get; } = memoryAvailable;
        public bool PowerChanged { get; set; }
        public bool MemoryChanged { get; set; }
    }

    sealed record CpuObservation(TimeSpan CpuTime, DateTime TimestampUtc, long WorkingSetBytes);
    sealed record InterferencePoint(string Name, TimeSpan CpuTime, long WorkingSetBytes, ulong IoBytes);
    sealed record RestoreResult(int Errors);
    readonly record struct MemoryPressure(uint MemoryLoad, double AvailableGb, bool ShouldLowerBackgroundMemory);

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryPriorityInformation
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcessInformation")]
    static extern bool GetProcessInformationPower(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    static extern bool SetProcessInformationPower(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcessInformation")]
    static extern bool GetProcessInformationMemory(IntPtr process, int informationClass, ref MemoryPriorityInformation information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    static extern bool SetProcessInformationMemory(IntPtr process, int informationClass, ref MemoryPriorityInformation information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime, out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out IoCounters ioCounters);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
