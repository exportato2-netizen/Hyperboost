using System.Text.Json;

namespace HyperBoost;

public enum RecoveryProperty
{
    ExecutionQos,
    MemoryPriority
}

public sealed record RecoveryJournalEntry(
    int Pid,
    ulong ProcessCreationTime,
    string ProcessName,
    RecoveryProperty Property,
    uint OriginalControlMask,
    uint OriginalStateMask,
    uint ExpectedControlMask,
    uint ExpectedStateMask,
    uint OriginalMemoryPriority,
    uint ExpectedMemoryPriority,
    DateTime AppliedAtUtc);

public sealed class RecoveryJournalDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<RecoveryJournalEntry> Entries { get; set; } = [];
}

public sealed class RecoveryJournal
{
    readonly object sync = new();
    readonly string path;

    public RecoveryJournal(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HyperBoost", "recovery-journal.json");
    }

    public string PathOnDisk => path;

    public IReadOnlyList<RecoveryJournalEntry> ReadEntries()
    {
        lock (sync) return Load().Entries.ToList();
    }

    public bool TryUpsert(RecoveryJournalEntry entry)
    {
        lock (sync)
        {
            try
            {
                var document = Load();
                document.Entries.RemoveAll(x => SameSlot(x, entry));
                document.Entries.Add(entry);
                Save(document);
                return true;
            }
            catch { return false; }
        }
    }

    public bool TryRemove(RecoveryJournalEntry entry)
    {
        lock (sync)
        {
            try
            {
                var document = Load();
                document.Entries.RemoveAll(x => SameSlot(x, entry));
                Save(document);
                return true;
            }
            catch { return false; }
        }
    }

    public bool TryRemove(int pid, ulong creation, RecoveryProperty property)
    {
        lock (sync)
        {
            try
            {
                var document = Load();
                document.Entries.RemoveAll(x => x.Pid == pid && x.ProcessCreationTime == creation && x.Property == property);
                Save(document);
                return true;
            }
            catch { return false; }
        }
    }

    public static bool IdentityMatches(RecoveryJournalEntry entry, int pid, ulong creation, string processName)
        => entry.Pid == pid && entry.ProcessCreationTime == creation &&
           string.Equals(entry.ProcessName, processName, StringComparison.OrdinalIgnoreCase);

    public static bool StillOwnedPower(RecoveryJournalEntry entry, uint currentControlMask, uint currentStateMask)
        => entry.Property == RecoveryProperty.ExecutionQos &&
           currentControlMask == entry.ExpectedControlMask && currentStateMask == entry.ExpectedStateMask;

    public static bool StillOwnedMemory(RecoveryJournalEntry entry, uint currentMemoryPriority)
        => entry.Property == RecoveryProperty.MemoryPriority && currentMemoryPriority == entry.ExpectedMemoryPriority;

    RecoveryJournalDocument Load()
    {
        if (!File.Exists(path)) return new();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new();
        var document = JsonSerializer.Deserialize<RecoveryJournalDocument>(text) ?? new();
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Recovery Journal schema {document.SchemaVersion} no compatible.");
        return document;
    }

    void Save(RecoveryJournalDocument document)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Recovery Journal sin directorio.");
        Directory.CreateDirectory(directory);
        if (document.Entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(
                   temporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    static bool SameSlot(RecoveryJournalEntry left, RecoveryJournalEntry right)
        => left.Pid == right.Pid && left.ProcessCreationTime == right.ProcessCreationTime && left.Property == right.Property;

    public static void RunSelfTest(string root)
    {
        var path = Path.Combine(root, "recovery-journal-test.json");
        var journal = new RecoveryJournal(path);
        var entry = new RecoveryJournalEntry(
            123, 456, "OneDrive", RecoveryProperty.ExecutionQos,
            0, 0, 1, 1, 0, 0, DateTime.UtcNow);

        if (!journal.TryUpsert(entry) || journal.ReadEntries().Count != 1)
            throw new InvalidOperationException("Recovery Journal no persistió una mutación.");
        if (!IdentityMatches(entry, 123, 456, "onedrive"))
            throw new InvalidOperationException("Recovery Journal rechazó una identidad válida.");
        if (IdentityMatches(entry, 123, 999, "OneDrive"))
            throw new InvalidOperationException("Recovery Journal aceptó reutilización de PID.");
        if (!StillOwnedPower(entry, 1, 1) || StillOwnedPower(entry, 3, 1))
            throw new InvalidOperationException("Recovery Journal no respeta ownership de Execution QoS.");

        var memory = entry with
        {
            Property = RecoveryProperty.MemoryPriority,
            OriginalMemoryPriority = 5,
            ExpectedMemoryPriority = 4
        };
        if (!StillOwnedMemory(memory, 4) || StillOwnedMemory(memory, 3))
            throw new InvalidOperationException("Recovery Journal no respeta ownership de Memory Priority.");
        journal.TryUpsert(memory);
        if (journal.ReadEntries().Count != 2)
            throw new InvalidOperationException("Recovery Journal perdió propiedades independientes.");
        journal.TryRemove(entry);
        journal.TryRemove(memory);
        journal.TryRemove(memory); // idempotencia
        if (journal.ReadEntries().Count != 0 || File.Exists(path))
            throw new InvalidOperationException("Recovery Journal no limpió una restauración idempotente.");
    }
}
