using System.Runtime.InteropServices;
using System.Text;

namespace HyperBoost;

public sealed record BottleneckGateResult(
    string Decision,
    string Summary,
    bool AllowEcoQos,
    double GameGpuPercent,
    double HighestOtherGpuPercent,
    uint MemoryLoad,
    double AvailableMemoryGb,
    IReadOnlyList<InterferenceSample> Interference,
    GpuScanResult Gpu)
{
    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DECISIÓN   {Decision}")
          .AppendLine($"RAZÓN      {Summary}")
          .AppendLine($"GPU JUEGO  {(Gpu.Available ? $"{GameGpuPercent:0.0}%" : "no disponible")}")
          .AppendLine($"GPU OTROS  {(Gpu.Available ? $"{HighestOtherGpuPercent:0.0}%" : "no disponible")}")
          .AppendLine($"RAM        {MemoryLoad}% usada · {AvailableMemoryGb:0.0} GB libres")
          .AppendLine($"EcoQoS     {(AllowEcoQos ? "permitido por Gate" : "bloqueado por Gate")}");

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
            .Where(x => x.CpuPercent >= 0.25 || x.IoMbPerSec >= 0.50 || x.WorkingSetMb >= 300)
            .ToList();

        var memoryPressure = memory.MemoryLoad >= 75 || memory.AvailableGb < 6;
        var gpuSaturated = gpu.Available && gameGpu >= 90;
        var gpuCompetition = gpu.Available && otherGpu >= 5;
        var allowEco = strongSafeBackground.Count > 0;

        string decision;
        string summary;

        if (memoryPressure)
        {
            decision = allowEco ? "Intervenir de forma selectiva" : "Solo memoria si corresponde";
            summary = "Windows está bajo presión de RAM. La prioridad de memoria adaptativa puede actuar solo sobre la lista segura; EcoQoS requiere además competencia real de CPU/I-O.";
        }
        else if (gpuSaturated && strongSafeBackground.Count == 0)
        {
            decision = "No tocar CPU";
            summary = "El motor GPU del juego está cerca de saturación y no aparece competencia segura relevante. EcoQoS no tiene una vía clara para aumentar FPS en esta muestra.";
            allowEco = false;
        }
        else if (gpuCompetition && strongSafeBackground.Count == 0)
        {
            decision = "Observar competencia GPU";
            summary = "Otro proceso usa GPU de forma visible. HyperBoost lo reporta, pero no modifica procesos gráficos porque Windows/NVIDIA/las apps conservan autoridad.";
            allowEco = false;
        }
        else if (strongSafeBackground.Count > 0)
        {
            decision = "EcoQoS selectivo permitido";
            summary = "Se detectó actividad medible en un proceso de la allowlist segura. HyperBoost puede reducir esa competencia mientras el juego mantenga foco.";
        }
        else
        {
            decision = "No hacer cambios";
            summary = "No aparece presión de RAM ni competencia relevante en la allowlist segura. HyperBoost evita aplicar un tweak sin evidencia.";
            allowEco = false;
        }

        return new(
            decision,
            summary,
            allowEco,
            gameGpu,
            otherGpu,
            memory.MemoryLoad,
            memory.AvailableGb,
            interference,
            gpu);
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
