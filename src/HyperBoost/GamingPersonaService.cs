using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperBoost;

public sealed record GameProcessCandidate(int Id, string Name, string WindowTitle)
{
    public string Display => string.IsNullOrWhiteSpace(WindowTitle)
        ? $"{Name} · PID {Id}"
        : $"{Name} · PID {Id} · {WindowTitle}";
}

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

    static readonly HashSet<string> NeverAutomatic = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "Discord", "obs64", "gameoverlayui", "GameBar", "GameBarFTServer",
        "NVIDIA Share", "nvcontainer", "RTSS", "MSIAfterburner", "lghub", "lghub_agent",
        "RazerAppEngine", "ArmouryCrate", "LightingService",
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgc", "vgtray", "FACEIT"
    };

    readonly Dictionary<int, BackgroundSnapshot> adjustedBackground = [];
    readonly Dictionary<int, CpuObservation> cpuObservations = [];

    int gamePid;
    ulong gameCreationTime;
    bool enabled;
    bool engaged;
    bool useEcoQos;
    bool useMemoryPriority;
    DateTime lastReconcileUtc = DateTime.MinValue;
    string status = "Gaming Persona detenida.";

    public bool IsEnabled => enabled;
    public bool IsEngaged => engaged;
    public int AdjustedBackgroundCount => adjustedBackground.Values.Count(x => x.PowerChanged);
    public int MemoryAdjustedCount => adjustedBackground.Values.Count(x => x.MemoryChanged);
    public string Status => status;

    public IReadOnlyList<GameProcessCandidate> GetCandidates()
    {
        var result = new List<GameProcessCandidate>();
        var currentSession = Process.GetCurrentProcess().SessionId;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.SessionId != currentSession || p.MainWindowHandle == IntPtr.Zero) continue;
                    var title = p.MainWindowTitle?.Trim() ?? "";
                    if (title.Length == 0) continue;
                    result.Add(new(p.Id, p.ProcessName, title));
                }
                catch
                {
                    // Procesos protegidos o que terminan durante el escaneo se omiten.
                }
            }
        }

        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToList();
    }

    public string Start(GameProcessCandidate game, bool ecoQos, bool memoryPriority)
    {
        if (enabled) throw new InvalidOperationException("Gaming Persona ya está armada. Deténla antes de elegir otro juego.");

        if (!TryReadCreationTime(game.Id, out var creationTime))
            throw new InvalidOperationException("El proceso seleccionado terminó o Windows no permitió verificar su identidad.");

        gamePid = game.Id;
        gameCreationTime = creationTime;
        useEcoQos = ecoQos;
        useMemoryPriority = memoryPriority;
        enabled = true;
        engaged = false;
        status = $"Gaming Persona armada para {game.Name} · PID {game.Id}. HyperBoost no modifica el juego; solo administrará competencia segura alrededor de él cuando tenga foco.";

        Tick();
        return status;
    }

    public string Stop()
    {
        if (!enabled && !engaged && adjustedBackground.Count == 0)
            return "Gaming Persona ya estaba detenida.";

        var restore = Disengage();
        enabled = false;
        gamePid = 0;
        gameCreationTime = 0;
        status = restore.Errors == 0
            ? "Gaming Persona detenida. Los ajustes temporales de background fueron restaurados."
            : $"Gaming Persona detenida. Hubo {restore.Errors} restauraciones que Windows no permitió; esos ajustes desaparecen al cerrar sus procesos.";
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
                ? "El juego terminó. Gaming Persona restauró los procesos secundarios y se desarmó."
                : "El juego terminó. Gaming Persona se desarmó; algún proceso secundario terminó antes de restaurarse.";
            return;
        }

        var foregroundPid = GetForegroundProcessId();
        if (foregroundPid == gamePid)
        {
            if (!engaged)
            {
                engaged = true;
                ReconcileBackground();
                UpdateActiveStatus();
                return;
            }

            if (DateTime.UtcNow - lastReconcileUtc >= TimeSpan.FromSeconds(5))
            {
                ReconcileBackground();
                UpdateActiveStatus();
            }
        }
        else if (engaged)
        {
            var restore = Disengage();
            status = restore.Errors == 0
                ? "Juego fuera de foco: Gaming Persona quedó en espera y restauró el background."
                : "Juego fuera de foco: Gaming Persona quedó en espera; algún proceso secundario terminó antes de restaurarse.";
        }
    }

    RestoreResult Disengage()
    {
        var errors = RestoreBackground();
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
                    if (!SafeAutomaticBackground.Contains(name)) continue;
                    seen.Add(process.Id);

                    adjustedBackground.TryGetValue(process.Id, out var snapshot);
                    if (snapshot is not null && !IsSameProcess(snapshot.Pid, snapshot.CreationTime))
                    {
                        adjustedBackground.Remove(process.Id);
                        cpuObservations.Remove(process.Id);
                        snapshot = null;
                    }

                    var activeEnough = IsProcessActive(process);
                    if (!activeEnough && snapshot is null) continue;

                    if (snapshot is null)
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
                    // Procesos que cambian/terminan o niegan información se ignoran.
                }
            }
        }

        foreach (var pid in adjustedBackground.Keys.ToList())
        {
            if (seen.Contains(pid)) continue;
            RestoreBackgroundProcess(adjustedBackground[pid]);
            adjustedBackground.Remove(pid);
            cpuObservations.Remove(pid);
        }
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

            // Si la aplicación ya controla explícitamente EXECUTION_SPEED, respetamos su decisión y no la pisamos.
            var appOwnsExecutionQos = havePower && (power.ControlMask & PowerThrottlingExecutionSpeed) != 0;
            if (useEcoQos && havePower && !appOwnsExecutionQos)
            {
                var eco = power;
                eco.Version = 1;
                eco.ControlMask |= PowerThrottlingExecutionSpeed;
                eco.StateMask |= PowerThrottlingExecutionSpeed;
                if (SetProcessInformationPower(handle, ProcessPowerThrottling, ref eco, powerSize))
                    snapshot.PowerChanged = true;
            }

            // Solo se baja 5 -> 4; si la app ya usa otra prioridad, se respeta.
            if (useMemoryPriority && pressure.ShouldLowerBackgroundMemory && haveMemory && memory.MemoryPriority == 5)
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
        if (!useMemoryPriority || !snapshot.MemoryAvailable || snapshot.OriginalMemoryPriority != 5) return;
        if (!IsSameProcess(snapshot.Pid, snapshot.CreationTime)) return;

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, snapshot.Pid);
        if (handle == IntPtr.Zero) return;

        try
        {
            var size = (uint)Marshal.SizeOf<MemoryPriorityInformation>();
            if (pressure.ShouldLowerBackgroundMemory && !snapshot.MemoryChanged)
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
        status = $"Gaming Persona ACTIVA · juego gestionado por Windows/NVIDIA · background EcoQoS: {AdjustedBackgroundCount} · memoria 5→4: {MemoryAdjustedCount} · RAM usada: {pressure.MemoryLoad}% ({pressure.AvailableGb:0.0} GB libres).";
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

            var eligible = SafeAutomaticBackground.Contains(b.Name);
            var recommendation = eligible
                ? "Elegible para EcoQoS conservador mientras el juego tenga foco, salvo que la app ya gestione su propio QoS."
                : NeverAutomatic.Contains(b.Name)
                    ? "Solo observar: Windows/NVIDIA, multimedia, launcher, overlay, periférico o anti-cheat mantienen autoridad."
                    : "Solo diagnóstico; HyperBoost no lo modifica automáticamente.";

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
                    map[process.Id] = new(process.ProcessName, process.TotalProcessorTime, process.WorkingSet64, TryReadIoBytes(process.Id));
                }
                catch
                {
                    // Solo diagnóstico: no elevar privilegios para inspeccionar procesos protegidos.
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
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory)) return new(0, 0, false);
        var availableGb = memory.AvailablePhysical / 1073741824d;
        var shouldLower = memory.MemoryLoad >= 75 || availableGb < 6;
        return new(memory.MemoryLoad, availableGb, shouldLower);
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
        catch { /* Los cambios por proceso desaparecen con sus procesos. */ }
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
