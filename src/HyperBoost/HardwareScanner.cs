using System.Diagnostics;
using System.Management;

namespace HyperBoost;

public sealed class HardwareScanner
{
    static readonly Dictionary<string, string> GamingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Discord"] = "Discord",
        ["NVIDIA Share"] = "NVIDIA Overlay",
        ["GameBar"] = "Xbox Game Bar",
        ["GameBarFTServer"] = "Xbox Game Bar",
        ["Overwolf"] = "Overwolf",
        ["RTSS"] = "RivaTuner Statistics Server",
        ["MSIAfterburner"] = "MSI Afterburner",
        ["Medal"] = "Medal",
        ["obs64"] = "OBS Studio"
    };

    static string S(ManagementBaseObject o, string p) => o[p]?.ToString()?.Trim() ?? "";
    static uint U(ManagementBaseObject o, string p) => uint.TryParse(S(o, p), out var v) ? v : 0;
    static ulong UL(ManagementBaseObject o, string p) => ulong.TryParse(S(o, p), out var v) ? v : 0;

    public Task<HardwareReport> ScanAsync() => Task.Run(() =>
    {
        var r = new HardwareReport();

        using (var searcher = new ManagementObjectSearcher("SELECT Caption,Version,BuildNumber FROM Win32_OperatingSystem"))
        foreach (ManagementObject o in searcher.Get())
        {
            using (o) r.Windows = $"{S(o, "Caption")} · {S(o, "Version")} (build {S(o, "BuildNumber")})";
        }

        using (var searcher = new ManagementObjectSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor"))
        foreach (ManagementObject o in searcher.Get())
        {
            using (o)
            {
                r.Cpu = S(o, "Name");
                r.PhysicalCores = (int)U(o, "NumberOfCores");
                r.LogicalCores = (int)U(o, "NumberOfLogicalProcessors");
            }
        }

        var gpus = new List<string>();
        using (var searcher = new ManagementObjectSearcher("SELECT Name,DriverVersion FROM Win32_VideoController"))
        foreach (ManagementObject o in searcher.Get())
        {
            using (o) gpus.Add($"{S(o, "Name")} (driver {S(o, "DriverVersion")})");
        }
        r.GpuReadOnly = string.Join(" · ", gpus);

        using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer,Product FROM Win32_BaseBoard"))
        foreach (ManagementObject o in searcher.Get())
        {
            using (o) r.Motherboard = $"{S(o, "Manufacturer")} {S(o, "Product")}".Trim();
        }

        using (var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS"))
        foreach (ManagementObject o in searcher.Get())
        {
            using (o) r.Bios = $"{S(o, "SMBIOSBIOSVersion")} · {S(o, "ReleaseDate")}";
        }

        using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
        foreach (ManagementObject o in searcher.Get())
        {
            using (o)
            {
                r.Memory.Add(new(
                    $"{S(o, "Manufacturer")} {S(o, "PartNumber")}".Trim(),
                    UL(o, "Capacity") / 1073741824d,
                    U(o, "Speed"),
                    U(o, "ConfiguredClockSpeed")));
            }
        }

        using (var searcher = new ManagementObjectSearcher("SELECT Model,Size,Status FROM Win32_DiskDrive"))
        foreach (ManagementObject o in searcher.Get())
        {
            using (o) r.Disks.Add(new(S(o, "Model"), UL(o, "Size") / 1_000_000_000d, S(o, "Status")));
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o) { r.HasBattery = true; break; }
            }
        }
        catch { /* Algunos desktops/firmwares no exponen Win32_Battery. */ }

        r.ActivePowerScheme = ReadActivePowerScheme();
        r.GamingBackgroundTools = DetectGamingTools();
        return r;
    });

    static string ReadActivePowerScheme()
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new("powercfg.exe", "/getactivescheme")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1500);
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : "No detectado";
        }
        catch { return "No detectado"; }
    }

    static List<string> DetectGamingTools()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (GamingTools.TryGetValue(p.ProcessName, out var label)) found.Add(label);
                }
                catch { /* Procesos protegidos pueden negar lectura. */ }
            }
        }
        return found.OrderBy(x => x).ToList();
    }
}

public static class DiagnosticsEngine
{
    const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public static List<Finding> Analyze(HardwareReport r)
    {
        var f = new List<Finding>();

        if (!r.Windows.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
            f.Add(new("Aviso", "Sistema operativo", "Esta beta se valida para Windows 11."));

        if (r.Memory.Count == 1)
            f.Add(new("Media", "Un solo módulo de RAM detectado", "Según la plataforma puede reducir el ancho de banda de memoria. HyperBoost no cambia BIOS ni perfiles EXPO/XMP."));

        foreach (var m in r.Memory.Where(x => x.SpeedMhz > 0 && x.ConfiguredMhz > 0 && x.ConfiguredMhz + 200 < x.SpeedMhz))
            f.Add(new("Media", "RAM por debajo de la velocidad informada", $"{m.Name}: WMI informa {m.SpeedMhz} MT/s y configuración actual {m.ConfiguredMhz} MT/s. Verifica compatibilidad y estabilidad antes de cambiar BIOS."));

        if (r.TotalRamGb < 16)
            f.Add(new("Alta", "Memoria limitada", $"{r.TotalRamGb:0} GB detectados; juegos modernos pueden sufrir paginación."));

        foreach (var d in r.Disks.Where(x => !string.IsNullOrWhiteSpace(x.Status) && !x.Status.Equals("OK", StringComparison.OrdinalIgnoreCase)))
            f.Add(new("Alta", "Estado de almacenamiento", $"{d.Name}: Windows reporta {d.Status}. Este dato WMI no sustituye una lectura SMART del fabricante."));

        if (r.ActivePowerScheme.Contains(HighPerformanceGuid, StringComparison.OrdinalIgnoreCase))
        {
            var extra = r.HasBattery ? " En un portátil también puede elevar consumo y temperatura." : "";
            f.Add(new("Info", "Plan Alto rendimiento detectado", "HyperBoost 0.3 no lo fuerza: en CPUs modernas Windows ya adapta el perfil de procesador durante Game Mode y un plan global puede no mejorar FPS." + extra));
        }

        if (r.GamingBackgroundTools.Count > 0)
            f.Add(new("Info", "Overlays/herramientas detectadas", $"Activas: {string.Join(", ", r.GamingBackgroundTools)}. Si hay stutter, prueba A/B desactivando solo su overlay; HyperBoost no cerrará procesos ni deshabilitará servicios."));

        if (r.Cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase))
            f.Add(new("Info", "Perfil Ryzen X3D", "Se evitan afinidad manual, voltajes, overclock y cambios de parking. Mantén chipset AMD y BIOS al día; Curve Optimizer queda fuera de esta beta."));

        f.Add(new("Info", "GPU protegida", "Solo se leyó nombre y controlador. HyperBoost no cambia gráficos, drivers, perfiles, clocks, voltaje, ventiladores, DLSS ni Frame Generation."));
        f.Add(new("Info", "Seguridad de Windows intacta", "No se desactiva Defender, Integridad de memoria, VBS, Secure Boot, firewall ni mitigaciones del sistema para ganar FPS."));

        if (!f.Any(x => x.Severity is "Alta" or "Media" or "Aviso"))
            f.Insert(0, new("Bien", "Base saludable", "No se detectaron problemas evidentes en el escaneo inicial."));

        return f;
    }
}
