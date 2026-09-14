using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HyperBoost;

public sealed class OptimizationService
{
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperBoost");
    public string LastBackupPath => Path.Combine(root, "last-backup.json");
    public bool HasBackup => File.Exists(LastBackupPath);

    // Desde 0.3.1 HyperBoost no aplica Game Mode, Game DVR ni planes de energía.
    // Este servicio se conserva únicamente para restaurar cambios creados por betas anteriores.
    public async Task<string> RestoreAsync()
    {
        if (!File.Exists(LastBackupPath)) return "No existe una copia de seguridad previa.";
        var b = await LoadBackupAsync();
        if (b is null) return $"La copia no se pudo leer y se dejó intacta en {LastBackupPath}.";

        await RestoreSnapshotAsync(b);
        File.Delete(LastBackupPath);
        Log("Restaurado backup legado: " + b.CreatedUtc.ToString("O"));
        return "Configuración de una beta anterior restaurada. HyperBoost 0.3.1 ya no modifica Game Mode, Game DVR ni el plan de energía.";
    }

    async Task RestoreSnapshotAsync(BackupSnapshot b)
    {
        foreach (var x in b.Registry.AsEnumerable().Reverse()) RestoreRegistry(x);

        // Compatibilidad con Beta 0.1, que podía forzar Alto rendimiento.
        if ((b.SchemaVersion == 0 || b.PowerSchemeChanged) && !string.IsNullOrWhiteSpace(b.ActivePowerScheme))
            await RunAsync("powercfg.exe", $"/setactive {b.ActivePowerScheme}");
    }

    async Task<BackupSnapshot?> LoadBackupAsync()
    {
        try
        {
            return JsonSerializer.Deserialize<BackupSnapshot>(await File.ReadAllTextAsync(LastBackupPath));
        }
        catch (JsonException ex)
        {
            Log("Copia JSON inválida: " + ex.Message);
            return null;
        }
        catch (IOException ex)
        {
            Log("No se pudo leer la copia: " + ex.Message);
            return null;
        }
    }

    static void RestoreRegistry(RegistryBackup x)
    {
        if (!x.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La copia contiene un hive no permitido.");

        using var k = Registry.CurrentUser.CreateSubKey(x.Path, true)
            ?? throw new InvalidOperationException($"No se pudo abrir HKCU\\{x.Path}.");

        if (!x.Existed)
        {
            k.DeleteValue(x.Name, false);
            return;
        }

        var kind = Enum.TryParse<RegistryValueKind>(x.Kind, out var parsed) ? parsed : RegistryValueKind.String;
        k.SetValue(x.Name, ConvertJsonValue(x.Value, kind), kind);
    }

    static object ConvertJsonValue(object? value, RegistryValueKind kind)
    {
        if (value is not JsonElement e) return value ?? "";

        return kind switch
        {
            RegistryValueKind.DWord => e.GetInt32(),
            RegistryValueKind.QWord => e.GetInt64(),
            RegistryValueKind.Binary => e.GetBytesFromBase64(),
            RegistryValueKind.MultiString => e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
            RegistryValueKind.String or RegistryValueKind.ExpandString => e.GetString() ?? "",
            _ => e.ToString()
        };
    }

    static async Task<string> RunAsync(string file, string args)
    {
        using var p = new Process
        {
            StartInfo = new(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        p.Start();
        var outputTask = p.StandardOutput.ReadToEndAsync();
        var errorTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (p.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? error : $"{file} terminó con código {p.ExitCode}.");
        return output.Trim();
    }

    void Log(string text)
    {
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "hyperboost.log"), $"{DateTime.Now:O} {text}{Environment.NewLine}");
    }
}
