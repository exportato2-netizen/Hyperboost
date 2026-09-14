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
    const uint StillActive = 259;

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
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows o el anti-cheat no permitió abrir ese proceso con permisos de ajuste. No se modificó nada.");

        try
        {
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

            session = new(handle, pid, originalPower, originalPriority, priorityChanged);
            handle = IntPtr.Zero;

            var extras = new List<string> { "HighQoS por proceso" };
            if (honorTimerRequests) extras.Add("peticiones de temporizador respetadas");
            if (priorityChanged) extras.Add("prioridad Above Normal");
            return $"Boost activo para PID {pid}: {string.Join(", ", extras)}. No hay inyección, cambios de GPU ni cambios globales de Windows.";
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    public string Stop()
    {
        if (session is null) return "No hay un boost de sesión activo.";
        var s = session;
        session = null;

        try
        {
            if (!GetExitCodeProcess(s.Handle, out var exitCode) || exitCode != StillActive)
                return "El proceso ya terminó; sus ajustes de sesión desaparecieron con él.";

            var errors = new List<string>();
            var power = s.OriginalPower;
            var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
            if (!SetProcessInformation(s.Handle, ProcessPowerThrottling, ref power, size))
                errors.Add("QoS");

            if (s.PriorityChanged && !SetPriorityClass(s.Handle, s.OriginalPriority))
                errors.Add("prioridad");

            return errors.Count == 0
                ? "Boost detenido y estado original del proceso restaurado."
                : $"El boost se detuvo, pero Windows no permitió restaurar: {string.Join(", ", errors)}. Al cerrar el juego esos ajustes dejan de existir.";
        }
        finally
        {
            CloseHandle(s.Handle);
        }
    }

    public void Dispose()
    {
        if (session is null) return;
        try { Stop(); }
        catch
        {
            if (session is not null)
            {
                CloseHandle(session.Handle);
                session = null;
            }
        }
    }

    sealed record BoostSession(IntPtr Handle, int Pid, ProcessPowerThrottlingState OriginalPower, uint OriginalPriority, bool PriorityChanged);

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
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
    static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
