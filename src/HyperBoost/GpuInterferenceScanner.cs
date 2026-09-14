using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace HyperBoost;

public sealed record GpuProcessUsage(
    int Pid,
    string Process,
    double GpuPercent,
    string BusiestEngine);

public sealed record GpuScanResult(
    bool Available,
    string Detail,
    IReadOnlyList<GpuProcessUsage> Processes)
{
    public double UsageFor(int pid)
        => Processes.FirstOrDefault(x => x.Pid == pid)?.GpuPercent ?? 0;
}

public sealed class GpuInterferenceScanner
{
    static readonly Regex PidRegex = new(@"(?:^|_)pid_(\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex EngineRegex = new(@"(?:^|_)engtype_([^_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<GpuScanResult> ScanAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(cancellationToken), cancellationToken);

    GpuScanResult Scan(CancellationToken cancellationToken)
    {
        try
        {
            var byPid = new Dictionary<int, Dictionary<string, double>>();
            using var searcher = new ManagementObjectSearcher(
                "root\\cimv2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = item["Name"]?.ToString() ?? "";
                    var pidMatch = PidRegex.Match(name);
                    if (!pidMatch.Success || !int.TryParse(pidMatch.Groups[1].Value, out var pid) || pid <= 0) continue;

                    var engineMatch = EngineRegex.Match(name);
                    var engine = engineMatch.Success ? engineMatch.Groups[1].Value : "GPU";
                    if (!TryDouble(item["UtilizationPercentage"], out var utilization)) continue;
                    utilization = Math.Clamp(utilization, 0, 100);

                    if (!byPid.TryGetValue(pid, out var engines))
                    {
                        engines = new(StringComparer.OrdinalIgnoreCase);
                        byPid[pid] = engines;
                    }

                    if (!engines.TryGetValue(engine, out var previous) || utilization > previous)
                        engines[engine] = utilization;
                }
            }

            var rows = new List<GpuProcessUsage>();
            foreach (var (pid, engines) in byPid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engines.Count == 0) continue;
                var busiest = engines.MaxBy(x => x.Value);
                if (busiest.Value < 0.05) continue;
                rows.Add(new(pid, ProcessName(pid), busiest.Value, busiest.Key));
            }

            return new(
                true,
                "GPU leído desde contadores WDDM de Windows. El porcentaje por proceso usa su motor GPU más ocupado, sin escribir configuración del driver.",
                rows.OrderByDescending(x => x.GpuPercent).Take(20).ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, "Windows no expuso los contadores GPU WDDM en este equipo/sesión; el Gate seguirá funcionando con CPU, RAM e I/O.", Array.Empty<GpuProcessUsage>());
        }
    }

    static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    static bool TryDouble(object? value, out double result)
    {
        try
        {
            result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(result);
        }
        catch
        {
            result = 0;
            return false;
        }
    }
}
