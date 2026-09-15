using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HyperBoost;

public sealed record BottleneckGateResult(
    string Decision,
    string Summary,
    IReadOnlyList<int> EcoQosPids,
    double GameGpuPercent,
    double HighestOtherGpuPercent,
    uint MemoryLoad,
    double AvailableMemoryGb,
    IReadOnlyList<InterferenceSample> Interference,
    GpuScanResult Gpu,
    double EvaluationDurationMs)
{
    public bool AllowEcoQos => EcoQosPids.Count > 0;

    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DECISIÓN   {Decision}")
          .AppendLine($"RAZÓN      {Summary}")
          .AppendLine($"GPU JUEGO  {(Gpu.Available ? $"{GameGpuPercent:0.0}%" : "no disponible")}")
          .AppendLine($"GPU OTROS  {(Gpu.Available ? $"{HighestOtherGpuPercent:0.0}%" : "no disponible")}")
          .AppendLine($"RAM        {MemoryLoad}% usada · {AvailableMemoryGb:0.0} GB libres")
          .AppendLine($"EcoQoS     {(AllowEcoQos ? $"PID(s) aprobados: {string.Join(", ", EcoQosPids)}" : "ningún PID aprobado")}")
          .AppendLine("ALCANCE    EcoQoS afecta scheduling/execution QoS; no limita directamente disco ni red.");

        if (Gpu.Processes.Count > 0)
        {
            sb.AppendLine().AppendLine("GPU · procesos más activos");
            foreach (var x in Gpu.Processes.Take(6))
                sb.AppendLine($"  {x.Process,-24} PID {x.Pid,7}  {x.GpuPercent,6:0.0}%  {x.BusiestEngine}");
        }

        var active = Interference.Take(6).ToList();
        if (active.Count > 0)
        {
            sb.AppendLine().AppendLine("CPU / I-O / RAM · interferencias observadas");
            foreach (var x in active)
                sb.AppendLine($"  {x.Process,-24} CPU {x.CpuPercent,6:0.00}%  I/O {x.IoMbPerSec,6:0.00} MB/s  RAM {x.WorkingSetMb,6:0} MB");
        }

        return sb.ToString();
    }
}

public sealed class BottleneckGateService
{
    static readonly HashSet<string> ExpectedGpuProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "System", "explorer", "HyperBoost"
    };

    readonly GpuInterferenceScanner gpuScanner = new();

    public async Task<BottleneckGateResult> EvaluateAsync(
        int gamePid,
        GamingPersonaService persona,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var interferenceTask = persona.ScanInterferenceAsync(gamePid, cancellationToken);
        var gpuTask = gpuScanner.ScanAsync(cancellationToken);
        await Task.WhenAll(interferenceTask, gpuTask);
        cancellationToken.ThrowIfCancellationRequested();

        var interference = await interferenceTask;
        var gpu = await gpuTask;
        var memory = ReadMemory();

        var gameGpu = gpu.UsageFor(gamePid);
        var otherGpu = gpu.Processes
            .Where(x => x.Pid != gamePid && !ExpectedGpuProcesses.Contains(x.Process))
            .Select(x => x.GpuPercent)
            .DefaultIfEmpty(0)
            .Max();

        var strongSafeBackground = interference
            .Where(x => x.EligibleForAutomaticQoS)
            .Where(x => x.CpuPercent >= 0.25)
            .ToList();

        var ecoPids = strongSafeBackground.Select(x => x.Pid).Distinct().ToList();
        var memoryPressure = memory.MemoryLoad >= 75 || memory.AvailableGb < 6;
        var gpuSaturated = gpu.Available && gameGpu >= 90;
        var gpuCompetition = gpu.Available && otherGpu >= 5;

        string decision;
        string summary;

        if (memoryPressure)
        {
            decision = ecoPids.Count > 0 ? "Intervenir de forma selectiva" : "Solo memoria si corresponde";
            summary = "Windows está bajo presión de RAM. Memory Priority EXPERIMENTAL puede actuar solo sobre la lista segura; EcoQoS se limita a PID(s) que demostraron competencia de ejecución CPU. El I/O se observa, pero no se limita.";
        }
        else if (gpuSaturated && ecoPids.Count == 0)
        {
            decision = "No tocar CPU";
            summary = "El motor GPU del juego está cerca de saturación y no aparece competencia segura relevante. EcoQoS no tiene una vía clara para aumentar FPS en esta muestra.";
        }
        else if (gpuCompetition && ecoPids.Count == 0)
        {
            decision = "Observar competencia GPU";
            summary = "Otro proceso usa GPU de forma visible. HyperBoost lo reporta, pero no modifica procesos gráficos porque Windows/NVIDIA/las apps conservan autoridad.";
        }
        else if (ecoPids.Count > 0)
        {
            decision = "EcoQoS selectivo permitido";
            summary = "Se detectó actividad CPU medible en PID(s) de la allowlist segura. EcoQoS puede reducir competencia de ejecución CPU; no limita directamente el tráfico de disco o red.";
        }
        else
        {
            decision = "No hacer cambios";
            summary = "No aparece presión de RAM ni competencia relevante en la allowlist segura. HyperBoost evita aplicar un tweak sin evidencia.";
        }

        return new(
            decision,
            summary,
            ecoPids,
            gameGpu,
            otherGpu,
            memory.MemoryLoad,
            memory.AvailableGb,
            interference,
            gpu,
            clock.Elapsed.TotalMilliseconds);
    }

    static MemorySnapshot ReadMemory()
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory)) return new(0, 0);
        return new(memory.MemoryLoad, memory.AvailablePhysical / 1073741824d);
    }

    readonly record struct MemorySnapshot(uint MemoryLoad, double AvailableGb);

    [StructLayout(LayoutKind.Sequential)]
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
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
