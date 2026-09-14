using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperBoost;

public sealed class SystemEventCoordinator : IDisposable
{
    const uint EventSystemForeground = 0x0003;
    const uint WineventOutOfContext = 0x0000;
    const uint WaitObject0 = 0x00000000;
    const uint WaitFailed = 0xFFFFFFFF;
    const uint Infinite = 0xFFFFFFFF;

    readonly WinEventDelegate winEventDelegate;
    readonly IntPtr foregroundHook;
    readonly IntPtr lowMemoryHandle;
    readonly IntPtr highMemoryHandle;
    readonly IntPtr stopEvent;
    readonly Thread? memoryThread;
    Process? watchedProcess;
    bool disposed;

    public event Action<int>? ForegroundProcessChanged;
    public event Action<bool>? MemoryPressureChanged;
    public event Action? WatchedProcessExited;

    public bool ForegroundHookAvailable => foregroundHook != IntPtr.Zero;
    public bool MemoryNotificationsAvailable => lowMemoryHandle != IntPtr.Zero && highMemoryHandle != IntPtr.Zero;

    public SystemEventCoordinator()
    {
        winEventDelegate = OnWinEvent;
        foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            winEventDelegate,
            0,
            0,
            WineventOutOfContext);

        lowMemoryHandle = CreateMemoryResourceNotification(0);
        highMemoryHandle = CreateMemoryResourceNotification(1);
        stopEvent = CreateEventW(IntPtr.Zero, true, false, null);

        if (MemoryNotificationsAvailable && stopEvent != IntPtr.Zero)
        {
            memoryThread = new Thread(MemoryLoop)
            {
                IsBackground = true,
                Name = "HyperBoost-MemorySignals"
            };
            memoryThread.Start();
        }
    }

    public static int GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return unchecked((int)pid);
    }

    public void WatchProcessExit(int pid)
    {
        StopWatchingProcess();
        try
        {
            watchedProcess = Process.GetProcessById(pid);
            watchedProcess.EnableRaisingEvents = true;
            watchedProcess.Exited += WatchedProcess_Exited;
            if (watchedProcess.HasExited)
                WatchedProcessExited?.Invoke();
        }
        catch
        {
            StopWatchingProcess();
            throw new InvalidOperationException("No se pudo vigilar el proceso del juego. Puede haber terminado antes de armar la Persona.");
        }
    }

    public void StopWatchingProcess()
    {
        var p = watchedProcess;
        watchedProcess = null;
        if (p is null) return;
        try { p.Exited -= WatchedProcess_Exited; }
        catch { }
        p.Dispose();
    }

    void WatchedProcess_Exited(object? sender, EventArgs e)
        => WatchedProcessExited?.Invoke();

    void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (disposed || hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0) ForegroundProcessChanged?.Invoke(unchecked((int)pid));
    }

    void MemoryLoop()
    {
        var state = MemorySignalState.Neutral;
        try
        {
            if (TryQuery(lowMemoryHandle, out var low) && low)
            {
                state = MemorySignalState.Low;
                MemoryPressureChanged?.Invoke(true);
            }
            else if (TryQuery(highMemoryHandle, out var high) && high)
            {
                state = MemorySignalState.High;
                MemoryPressureChanged?.Invoke(false);
            }

            while (!disposed)
            {
                IntPtr[] handles;
                if (state == MemorySignalState.Low)
                    handles = [stopEvent, highMemoryHandle];
                else if (state == MemorySignalState.High)
                    handles = [stopEvent, lowMemoryHandle];
                else
                    handles = [stopEvent, lowMemoryHandle, highMemoryHandle];

                var wait = WaitForMultipleObjects((uint)handles.Length, handles, false, Infinite);
                if (wait == WaitFailed || wait == WaitObject0) return;

                var index = (int)(wait - WaitObject0);
                if (state == MemorySignalState.Low)
                {
                    state = MemorySignalState.High;
                    MemoryPressureChanged?.Invoke(false);
                }
                else if (state == MemorySignalState.High)
                {
                    state = MemorySignalState.Low;
                    MemoryPressureChanged?.Invoke(true);
                }
                else if (index == 1)
                {
                    state = MemorySignalState.Low;
                    MemoryPressureChanged?.Invoke(true);
                }
                else if (index == 2)
                {
                    state = MemorySignalState.High;
                    MemoryPressureChanged?.Invoke(false);
                }
            }
        }
        catch
        {
            // Señal auxiliar: si el proveedor falla, la Persona conserva sus comprobaciones fail-safe.
        }
    }

    static bool TryQuery(IntPtr handle, out bool state)
    {
        state = false;
        return handle != IntPtr.Zero && QueryMemoryResourceNotification(handle, out state);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopWatchingProcess();
        if (stopEvent != IntPtr.Zero) SetEvent(stopEvent);
        if (memoryThread is { IsAlive: true }) memoryThread.Join(500);
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        if (lowMemoryHandle != IntPtr.Zero) CloseHandle(lowMemoryHandle);
        if (highMemoryHandle != IntPtr.Zero) CloseHandle(highMemoryHandle);
        if (stopEvent != IntPtr.Zero) CloseHandle(stopEvent);
    }

    enum MemorySignalState { Neutral, Low, High }

    delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHookAssembly, WinEventDelegate eventHookHandle, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryMemoryResourceNotification(IntPtr resourceNotificationHandle, [MarshalAs(UnmanagedType.Bool)] out bool resourceState);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEvent(IntPtr eventHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForMultipleObjects(uint count, [In] IntPtr[] handles, bool waitAll, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}

public sealed class SelfBackgroundMode : IDisposable
{
    const uint ProcessModeBackgroundBegin = 0x00100000;
    const uint ProcessModeBackgroundEnd = 0x00200000;
    bool active;

    public bool IsActive => active;

    public bool SetActive(bool value)
    {
        if (value == active) return true;
        var flag = value ? ProcessModeBackgroundBegin : ProcessModeBackgroundEnd;
        if (!SetPriorityClass(GetCurrentProcess(), flag))
            return false;
        active = value;
        return true;
    }

    public void Dispose()
    {
        if (active) SetActive(false);
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetPriorityClass(IntPtr process, uint priorityClass);
}
