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
    public string ActivePowerScheme { get; set; } = "No detectado";
    public bool HasBattery { get; set; }
    public List<MemoryModule> Memory { get; set; } = [];
    public List<DiskInfo> Disks { get; set; } = [];
    public List<string> GamingBackgroundTools { get; set; } = [];
    public double TotalRamGb => Memory.Sum(x => x.CapacityGb);
}

public sealed record MemoryModule(string Name, double CapacityGb, uint SpeedMhz, uint ConfiguredMhz);
public sealed record DiskInfo(string Name, double SizeGb, string Status);
public sealed record Finding(string Severity, string Title, string Detail);

public sealed class BackupSnapshot
{
    // 0 significa formato legado (Beta 0.1, que no incluía este campo).
    public int SchemaVersion { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool ApplyCompleted { get; set; }
    public bool PowerSchemeChanged { get; set; }
    public string? ActivePowerScheme { get; set; }
    public List<RegistryBackup> Registry { get; set; } = [];
}

public sealed record RegistryBackup(string Hive, string Path, string Name, bool Existed, object? Value, string? Kind);
