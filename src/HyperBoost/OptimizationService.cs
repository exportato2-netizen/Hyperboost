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

    static readonly HashSet<string> AllowedRegistryTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        Key("Software\\Microsoft\\GameBar", "AutoGameModeEnabled"),
        Key("Software\\Microsoft\\GameBar", "AllowAutoGameMode"),
        Key("System\\GameConfigStore", "GameDVR_Enabled"),
        Key("Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled")
    };

    // Desde 0.3.1 HyperBoost no aplica Game Mode, Game DVR ni planes de energía.
    // Este servicio se conserva únicamente para restaurar cambios creados por betas anteriores.
    public async Task<string> RestoreAsync()
    {
        if (!File.Exists(LastBackupPath)) return "No existe una copia de seguridad previa.";
        var b = await LoadBackupAsync();
        if (b is null) return $"La copia no se pudo validar y se dejó intacta en {LastBackupPath}.";

        await RestoreSnapshotAsync(b);
        File.Delete(LastBackupPath);
        Log("Restaurado backup legado: " + b.CreatedUtc.ToString("O"));
        return "Configuración de una beta anterior restaurada. HyperBoost 0.3.2 ya no modifica Game Mode, Game DVR ni el plan de energía.";
    }

    async Task RestoreSnapshotAsync(BackupSnapshot b)
    {
        foreach (var x in b.Registry.AsEnumerable().Reverse()) RestoreRegistry(x);

        // Compatibilidad con Beta 0.1, que podía forzar Alto rendimiento.
        if ((b.SchemaVersion == 0 || b.PowerSchemeChanged) && !string.IsNullOrWhiteSpace(b.ActivePowerScheme))
        {
            if (!Guid.TryParse(b.ActivePowerScheme, out var scheme))
                throw new InvalidDataException("El backup contiene un GUID de plan de energía inválido.");
            await RunAsync("powercfg.exe", $"/setactive {scheme:D}");
        }
    }

    async Task<BackupSnapshot?> LoadBackupAsync()
    {
        try
        {
            var b = JsonSerializer.Deserialize<BackupSnapshot>(await File.ReadAllTextAsync(LastBackupPath));
            if (b is null || !ValidateBackup(b, out var reason))
            {
                Log("Backup rechazado: " + (b is null ? "contenido vacío" : reason));
                return null;
            }
            return b;
        }
        catch (JsonException ex)
        {
            Log("Copia JSON inválida: " + ex.Message);
            return null;
        }
        catch (NotSupportedException ex)
        {
            Log("Formato de copia no soportado: " + ex.Message);
            return null;
        }
        catch (IOException ex)
        {
            Log("No se pudo leer la copia: " + ex.Message);
            return null;
        }
    }

    static bool ValidateBackup(BackupSnapshot b, out string reason)
    {
        reason = "";
        if (b.SchemaVersion is < 0 or > 2)
        {
            reason = $"SchemaVersion {b.SchemaVersion} no compatible.";
            return false;
        }
        if (b.Registry is null || b.Registry.Count > 16)
        {
            reason = "Lista de registro ausente o fuera de rango.";
            return false;
        }

        foreach (var x in b.Registry)
        {
            if (x is null || string.IsNullOrWhiteSpace(x.Hive) || string.IsNullOrWhiteSpace(x.Path) || string.IsNullOrWhiteSpace(x.Name))
            {
                reason = "Entrada de registro incompleta.";
                return false;
            }
            if (!x.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) || !AllowedRegistryTargets.Contains(Key(x.Path, x.Name)))
            {
                reason = $"Destino de registro no permitido: {x.Hive}\\{x.Path}\\{x.Name}.";
                return false;
            }

            if (!x.Existed) continue;
            if (x.Value is null || string.IsNullOrWhiteSpace(x.Kind) || !Enum.TryParse<RegistryValueKind>(x.Kind, out var kind) || !Enum.IsDefined(kind))
            {
                reason = $"Valor previo inválido para {x.Name}.";
                return false;
            }
            if (kind is RegistryValueKind.None or RegistryValueKind.Unknown)
            {
                reason = $"Tipo de registro no soportado para {x.Name}.";
                return false;
            }
        }

        if ((b.SchemaVersion == 0 || b.PowerSchemeChanged) && !string.IsNullOrWhiteSpace(b.ActivePowerScheme) && !Guid.TryParse(b.ActivePowerScheme, out _))
        {
            reason = "GUID de plan de energía inválido.";
            return false;
        }

        return true;
    }

    static string Key(string path, string name) => path + "\0" + name;

    static void RestoreRegistry(RegistryBackup x)
    {
        if (!x.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) || !AllowedRegistryTargets.Contains(Key(x.Path, x.Name)))
            throw new InvalidDataException("El backup intentó restaurar un destino no permitido.");

        using var k = Registry.CurrentUser.CreateSubKey(x.Path, true)
            ?? throw new InvalidOperationException($"No se pudo abrir HKCU\\{x.Path}.");

        if (!x.Existed)
        {
            k.DeleteValue(x.Name, false);
            return;
        }

        if (!Enum.TryParse<RegistryValueKind>(x.Kind, out var kind) || !Enum.IsDefined(kind) || kind is RegistryValueKind.None or RegistryValueKind.Unknown)
            throw new InvalidDataException($"Tipo de registro inválido para {x.Name}.");

        k.SetValue(x.Name, ConvertJsonValue(x.Value, kind), kind);
    }

    static object ConvertJsonValue(object? value, RegistryValueKind kind)
    {
        if (value is not JsonElement e)
            return value ?? throw new InvalidDataException("Valor de registro ausente.");

        try
        {
            return kind switch
            {
                RegistryValueKind.DWord => e.GetInt32(),
                RegistryValueKind.QWord => e.GetInt64(),
                RegistryValueKind.Binary => e.GetBytesFromBase64(),
                RegistryValueKind.MultiString => e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
                RegistryValueKind.String or RegistryValueKind.ExpandString => e.GetString() ?? "",
                _ => throw new InvalidDataException("Tipo de registro no soportado.")
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("El valor guardado no coincide con su tipo de registro.", ex);
        }
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{file} no respondió dentro del tiempo esperado.");
        }

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
