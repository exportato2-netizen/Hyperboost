using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyperBoost;

public sealed class OptimizationService
{
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperBoost");
    public string LastBackupPath => Path.Combine(root, "last-backup.json");
    public bool HasBackup => File.Exists(LastBackupPath);

    public async Task<string> ApplySafeAsync()
    {
        Directory.CreateDirectory(root);

        if (File.Exists(LastBackupPath))
        {
            var stale = await LoadBackupAsync();
            if (stale?.ApplyCompleted == true)
                return "Ya existe un perfil aplicado. Restaura los cambios antes de volver a aplicar para no perder el estado original.";

            if (stale is not null)
            {
                await RestoreSnapshotAsync(stale);
                File.Delete(LastBackupPath);
                Log("Se recuperó una copia incompleta antes de volver a aplicar.");
            }
        }

        var b = new BackupSnapshot
        {
            ActivePowerScheme = await ActiveSchemeAsync(),
            PowerSchemeChanged = false,
            ApplyCompleted = false
        };

        Backup(b, Registry.CurrentUser, "Software\\Microsoft\\GameBar", "AutoGameModeEnabled");
        Backup(b, Registry.CurrentUser, "Software\\Microsoft\\GameBar", "AllowAutoGameMode");
        Backup(b, Registry.CurrentUser, "System\\GameConfigStore", "GameDVR_Enabled");
        Backup(b, Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled");
        await SaveBackupAsync(b);

        try
        {
            SetDwordVerified("Software\\Microsoft\\GameBar", "AutoGameModeEnabled", 1);
            SetDwordVerified("Software\\Microsoft\\GameBar", "AllowAutoGameMode", 1);
            SetDwordVerified("System\\GameConfigStore", "GameDVR_Enabled", 0);
            SetDwordVerified("Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled", 0);

            b.ApplyCompleted = true;
            await SaveBackupAsync(b);
            Log("Aplicado: Game Mode activo y captura de juegos en segundo plano desactivada. No se modificó el plan de energía.");
            return "Perfil seguro aplicado. No se cambió el plan de energía, la GPU ni funciones de seguridad. Reinicia el juego para asegurar que tome la configuración.";
        }
        catch
        {
            try
            {
                await RestoreSnapshotAsync(b);
                if (File.Exists(LastBackupPath)) File.Delete(LastBackupPath);
                Log("Aplicación fallida; se revirtió automáticamente el estado previo.");
            }
            catch (Exception rollbackEx)
            {
                Log("Falló la reversión automática: " + rollbackEx.Message);
            }
            throw;
        }
    }

    public async Task<string> RestoreAsync()
    {
        if (!File.Exists(LastBackupPath)) return "No existe una copia de seguridad previa.";
        var b = await LoadBackupAsync();
        if (b is null) return "La copia no se pudo leer.";

        await RestoreSnapshotAsync(b);
        File.Delete(LastBackupPath);
        Log("Restaurado: " + b.CreatedUtc.ToString("O"));
        return "Configuración anterior restaurada y copia activa cerrada.";
    }

    async Task RestoreSnapshotAsync(BackupSnapshot b)
    {
        foreach (var x in b.Registry.AsEnumerable().Reverse()) RestoreRegistry(x);

        // Compatibilidad con copias de Beta 0.1, que sí forzaba Alto rendimiento.
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
    }

    async Task SaveBackupAsync(BackupSnapshot b)
    {
        Directory.CreateDirectory(root);
        var temp = LastBackupPath + ".tmp";
        var json = JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temp, json);
        File.Move(temp, LastBackupPath, true);
    }

    static void Backup(BackupSnapshot b, RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        var names = k?.GetValueNames() ?? [];
        var existed = names.Contains(name, StringComparer.OrdinalIgnoreCase);
        var value = existed ? k!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        var kind = existed ? k!.GetValueKind(name).ToString() : null;
        b.Registry.Add(new("HKCU", path, name, existed, value, kind));
    }

    static void SetDwordVerified(string path, string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(path, true)
            ?? throw new InvalidOperationException($"No se pudo abrir HKCU\\{path}.");
        k.SetValue(name, value, RegistryValueKind.DWord);
        var readBack = k.GetValue(name);
        if (readBack is not int v || v != value)
            throw new InvalidOperationException($"Windows no confirmó el cambio {name}.");
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

    async Task<string?> ActiveSchemeAsync()
    {
        var s = await RunAsync("powercfg.exe", "/getactivescheme");
        var match = Regex.Match(s, @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}");
        return match.Success ? match.Value : null;
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
