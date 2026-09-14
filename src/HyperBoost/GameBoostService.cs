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

public sealed class GameBoostService : IDisposable
{
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint ProcessSetInformation = 0x0200;
    const int ProcessPowerThrottling = 4;
    const uint PowerThrottlingExecutionSpeed = 0x1;
    const uint PowerThrottlingIgnoreTimerResolution = 0x4;
    const uint NormalPriorityClass = 0x20;
    const uint AboveNormalPriorityClass = 0x8000;

    BoostSession? session;
    public bool IsActive => session is not null;

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
                    // Procesos protegidos o que terminan durante el escaneo simplemente se omiten.
                }
            }
        }

        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToList();
    }

    public string Start(int pid, bool useAboveNormalPriority, bool honorTimerRequests)
    {
        if (session is not null) throw new InvalidOperationException("Ya hay un boost de sesión activo. Deténlo antes de elegir otro juego.");

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, pid);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows o el anti-cheat no permitió abrir ese proceso con permisos de ajuste. No se modificó nada.");

        try
        {
            var creationTime = ReadCreationTime(handle);
            var originalPower = new ProcessPowerThrottlingState { Version = 1 };
            var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
            if (!GetProcessInformation(handle, ProcessPowerThrottling, ref originalPower, size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo leer el estado QoS original; por seguridad no se aplicó el boost.");

            uint originalPriority = 0;
            var priorityChanged = false;
            if (useAboveNormalPriority)
            {
                originalPriority = GetPriorityClass(handle);
                if (originalPriority == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo leer la prioridad original; por seguridad no se aplicó el boost.");
            }

            var boosted = originalPower;
            boosted.Version = 1;
            boosted.ControlMask |= PowerThrottlingExecutionSpeed;
            boosted.StateMask &= ~PowerThrottlingExecutionSpeed;

            if (honorTimerRequests)
            {
                boosted.ControlMask |= PowerThrottlingIgnoreTimerResolution;
                boosted.StateMask &= ~PowerThrottlingIgnoreTimerResolution;
            }

            if (!SetProcessInformation(handle, ProcessPowerThrottling, ref boosted, size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo desactivar el power throttling para ese proceso.");

            try
            {
                if (useAboveNormalPriority && originalPriority == NormalPriorityClass)
                {
                    if (!SetPriorityClass(handle, AboveNormalPriorityClass))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rechazó la prioridad Above Normal.");
                    priorityChanged = true;
                }
            }
            catch
            {
                SetProcessInformation(handle, ProcessPowerThrottling, ref originalPower, size);
                throw;
            }

            // No retenemos el handle durante el juego: reduce superficie de incompatibilidad con anti-cheat.
            session = new(pid, creationTime, originalPower, originalPriority, priorityChanged);

            var extras = new List<string> { "HighQoS por proceso" };
            if (honorTimerRequests) extras.Add("peticiones de temporizador respetadas");
            if (priorityChanged) extras.Add("prioridad Above Normal");
            return $"Boost activo para PID {pid}: {string.Join(", ", extras)}. No hay inyección, cambios de GPU ni cambios globales de Windows.";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public string Stop()
    {
        if (session is null) return "No hay un boost de sesión activo.";
        var s = session;
        session = null;

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, s.Pid);
        if (handle == IntPtr.Zero)
            return "El juego terminó o ya no permite acceso al proceso. No se tocó ningún otro PID; al cerrar el juego los ajustes de proceso desaparecen.";

        try
        {
            if (ReadCreationTime(handle) != s.CreationTime)
                return "El PID fue reutilizado por otro proceso. HyperBoost no lo modificó.";

            var errors = new List<string>();
            var power = s.OriginalPower;
            var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
            if (!SetProcessInformation(handle, ProcessPowerThrottling, ref power, size))
                errors.Add("QoS");

            if (s.PriorityChanged && !SetPriorityClass(handle, s.OriginalPriority))
                errors.Add("prioridad");

            return errors.Count == 0
                ? "Boost detenido y estado original del proceso restaurado."
                : $"El boost se detuvo, pero Windows no permitió restaurar: {string.Join(", ", errors)}. Al cerrar el juego esos ajustes dejan de existir.";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static ulong ReadCreationTime(IntPtr handle)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo verificar la identidad del proceso.");
        return creation.Value;
    }

    public void Dispose()
    {
        try { Stop(); }
        catch { /* Al cerrar HyperBoost nunca se intenta evadir un proceso que niegue acceso. */ }
    }

    sealed record BoostSession(int Pid, ulong CreationTime, ProcessPowerThrottlingState OriginalPower, uint OriginalPriority, bool PriorityChanged);

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr process, out FileTime creationTime, out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);
}
