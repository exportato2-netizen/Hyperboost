using System.Management;

namespace HyperBoost;

public sealed class HardwareScanner
{
    static string S(ManagementBaseObject o, string p) => o[p]?.ToString()?.Trim() ?? "";
    static uint U(ManagementBaseObject o, string p) => uint.TryParse(S(o,p), out var v) ? v : 0;
    static ulong UL(ManagementBaseObject o, string p) => ulong.TryParse(S(o,p), out var v) ? v : 0;

    public Task<HardwareReport> ScanAsync() => Task.Run(() =>
    {
        var r = new HardwareReport();
        foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Caption,Version,BuildNumber FROM Win32_OperatingSystem").Get())
            r.Windows = $"{S(o,"Caption")} · {S(o,"Version")} (build {S(o,"BuildNumber")})";
        foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor").Get())
        { r.Cpu=S(o,"Name"); r.PhysicalCores=(int)U(o,"NumberOfCores"); r.LogicalCores=(int)U(o,"NumberOfLogicalProcessors"); }
        var gpus = new List<string>();
        foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Name,DriverVersion FROM Win32_VideoController").Get())
            gpus.Add($"{S(o,"Name")} (driver {S(o,"DriverVersion")})");
        r.GpuReadOnly = string.Join(" · ", gpus);
        foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Manufacturer,Product FROM Win32_BaseBoard").Get())
            r.Motherboard=$"{S(o,"Manufacturer")} {S(o,"Product")}".Trim();
        foreach (ManagementObject o in new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS").Get())
            r.Bios=$"{S(o,"SMBIOSBIOSVersion")} · {S(o,"ReleaseDate")}";
        foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed FROM Win32_PhysicalMemory").Get())
            r.Memory.Add(new($"{S(o,"Manufacturer")} {S(o,"PartNumber")}".Trim(), UL(o,"Capacity")/1073741824d, U(o,"Speed"), U(o,"ConfiguredClockSpeed")));
        foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Model,Size,Status FROM Win32_DiskDrive").Get())
            r.Disks.Add(new(S(o,"Model"), UL(o,"Size")/1_000_000_000d, S(o,"Status")));
        return r;
    });
}

public static class DiagnosticsEngine
{
    public static List<Finding> Analyze(HardwareReport r)
    {
        var f = new List<Finding>();
        if (!r.Windows.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
            f.Add(new("Aviso","Sistema operativo","Esta beta se valida para Windows 11."));
        if (r.Memory.Count == 1) f.Add(new("Alta","RAM en un solo módulo","Dos módulos compatibles suelen mejorar el ancho de banda y los mínimos de FPS."));
        var configured = r.Memory.Where(x=>x.ConfiguredMhz>0).Select(x=>x.ConfiguredMhz).DefaultIfEmpty(0u).Min();
        if (configured > 0 && configured <= 4800)
            f.Add(new("Media","RAM a velocidad base",$"Detectada a {configured} MT/s. Revisa EXPO/XMP y estabilidad en BIOS; HyperBoost no cambiará la BIOS."));
        if (r.TotalRamGb < 16) f.Add(new("Alta","Memoria limitada",$"{r.TotalRamGb:0} GB detectados; juegos modernos pueden sufrir paginación."));
        foreach(var d in r.Disks.Where(x=>!x.Status.Equals("OK",StringComparison.OrdinalIgnoreCase)))
            f.Add(new("Alta","Estado de almacenamiento",$"{d.Name}: {d.Status}"));
        if (r.Cpu.Contains("7800X3D",StringComparison.OrdinalIgnoreCase))
            f.Add(new("Info","Perfil Ryzen X3D","Se evitarán voltajes y overclock automático. Mantén chipset AMD y BIOS al día; Curve Optimizer queda fuera de esta beta."));
        f.Add(new("Info","GPU protegida","Solo se leyó nombre y controlador. HyperBoost no cambia gráficos, drivers, perfiles, clocks, voltaje, ventiladores, DLSS ni Frame Generation."));
        if (f.Count==1) f.Insert(0,new("Bien","Base saludable","No se detectaron problemas evidentes en el escaneo inicial."));
        return f;
    }
}
