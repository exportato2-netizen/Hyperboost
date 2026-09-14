namespace HyperBoost;

public sealed class HardwareReport
{
    public string Windows { get; set; } = "No detectado";
    public string Cpu { get; set; } = "No detectado";
    public int PhysicalCores { get; set; }
    public int LogicalCores { get; set; }
    public string GpuReadOnly { get; set; } = "No detectada";
    public string Motherboard { get; set; } = "No detectada";
    public string Bios { get; set; } = "No detectado";
    public List<MemoryModule> Memory { get; set; } = [];
    public List<DiskInfo> Disks { get; set; } = [];
    public double TotalRamGb => Memory.Sum(x => x.CapacityGb);
}
public sealed record MemoryModule(string Name, double CapacityGb, uint SpeedMhz, uint ConfiguredMhz);
public sealed record DiskInfo(string Name, double SizeGb, string Status);
public sealed record Finding(string Severity, string Title, string Detail);
public sealed class BackupSnapshot
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? ActivePowerScheme { get; set; }
    public List<RegistryBackup> Registry { get; set; } = [];
}
public sealed record RegistryBackup(string Hive, string Path, string Name, bool Existed, object? Value, string? Kind);
