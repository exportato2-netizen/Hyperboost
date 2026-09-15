using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace HyperBoost;

public sealed record BenchmarkEnvironmentFingerprint(
    int SchemaVersion,
    string SessionId,
    string HyperBoostVersion,
    string WindowsBuild,
    string Cpu,
    string Gpu,
    string GpuDriverVersion,
    double RamTotalGb,
    string Game,
    int InitialPid,
    long? ProcessStartTimeUtcTicks,
    string? ExecutablePath,
    long? ExecutableLastWriteUtcTicks,
    long? ExecutableSizeBytes,
    string PresentMonVersion,
    string PresentMonSha256,
    bool EcoQosEnabled,
    bool MemoryPriorityExperimentalEnabled,
    BenchmarkScenarioType ScenarioType,
    string? UserReportedResolution,
    DateTime CreatedAtUtc,
    int CaptureSeconds,
    int PlannedPairs);

public static class BenchmarkEnvironmentReader
{
    public static BenchmarkEnvironmentFingerprint Capture(
        GameProcessCandidate game,
        string hyperBoostVersion,
        int pairs,
        int captureSeconds,
        bool useEcoQos,
        bool useMemoryPriority,
        BenchmarkScenarioType scenarioType,
        string? userReportedResolution)
    {
        var cpu = "No detectado";
        var gpus = new List<string>();
        var drivers = new List<string>();
        var ram = 0d;

        TryWmi("SELECT Name FROM Win32_Processor", item => cpu = item["Name"]?.ToString()?.Trim() ?? cpu);
        TryWmi("SELECT Name,DriverVersion FROM Win32_VideoController", item =>
        {
            var gpuName = item["Name"]?.ToString()?.Trim();
            var driverVersion = item["DriverVersion"]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(gpuName) && !gpus.Contains(gpuName, StringComparer.OrdinalIgnoreCase))
                gpus.Add(gpuName);
            if (!string.IsNullOrWhiteSpace(driverVersion) && !drivers.Contains(driverVersion, StringComparer.OrdinalIgnoreCase))
                drivers.Add(driverVersion);
        });
        TryWmi("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", item =>
        {
            if (ulong.TryParse(item["TotalPhysicalMemory"]?.ToString(), out var bytes)) ram = bytes / 1073741824d;
        });

        string? executable = null;
        long? modified = null;
        long? size = null;
        long? start = null;
        try
        {
            using var process = Process.GetProcessById(game.Id);
            start = process.StartTime.ToUniversalTime().Ticks;
            executable = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            {
                var info = new FileInfo(executable);
                modified = info.LastWriteTimeUtc.Ticks;
                size = info.Length;
            }
        }
        catch
        {
            // Metadata opcional: nunca se elevan privilegios para leerla.
        }

        return new(
            3,
            Guid.NewGuid().ToString("N"),
            hyperBoostVersion,
            $"{RuntimeInformation.OSDescription} · {Environment.OSVersion.Version}",
            cpu,
            gpus.Count == 0 ? "No detectada" : string.Join(" | ", gpus),
            drivers.Count == 0 ? "No detectado" : string.Join(" | ", drivers),
            ram,
            game.Name,
            game.Id,
            start,
            executable,
            modified,
            size,
            "2.5.1",
            PresentMonBenchmarkService.PresentMonExpectedSha256,
            useEcoQos,
            useMemoryPriority,
            scenarioType,
            string.IsNullOrWhiteSpace(userReportedResolution) ? null : userReportedResolution.Trim(),
            DateTime.UtcNow,
            captureSeconds,
            pairs);
    }

    static void TryWmi(string query, Action<ManagementObject> action)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject item in searcher.Get())
            {
                using (item) action(item);
            }
        }
        catch
        {
            // La huella admite campos no disponibles; nunca bloquea la captura.
        }
    }
}

public readonly record struct ProcessOverheadSnapshot(
    DateTime TimestampUtc,
    TimeSpan CpuTime,
    long WorkingSetBytes,
    ulong IoBytes)
{
    public static ProcessOverheadSnapshot Capture(Process process)
    {
        ulong io = 0;
        try
        {
            if (GetProcessIoCounters(process.Handle, out var counters))
                io = counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount;
        }
        catch { }

        try { process.Refresh(); } catch { }
        return new(DateTime.UtcNow, SafeCpu(process), SafeWorkingSet(process), io);
    }

    public static double CpuPercent(ProcessOverheadSnapshot start, ProcessOverheadSnapshot end)
    {
        var elapsed = Math.Max(0.001, (end.TimestampUtc - start.TimestampUtc).TotalSeconds);
        var cpu = Math.Max(0, (end.CpuTime - start.CpuTime).TotalSeconds);
        return cpu / elapsed / Math.Max(1, Environment.ProcessorCount) * 100d;
    }

    public static double IoMb(ProcessOverheadSnapshot start, ProcessOverheadSnapshot end)
        => Math.Max(0, (double)end.IoBytes - start.IoBytes) / 1_048_576d;

    static TimeSpan SafeCpu(Process process) { try { return process.TotalProcessorTime; } catch { return TimeSpan.Zero; } }
    static long SafeWorkingSet(Process process) { try { return process.WorkingSet64; } catch { return 0; } }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out IoCounters ioCounters);
}
