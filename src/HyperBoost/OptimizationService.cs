using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyperBoost;

public sealed class OptimizationService
{
    readonly string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HyperBoost");
    public string LastBackupPath => Path.Combine(root,"last-backup.json");

    public async Task<string> ApplySafeAsync()
    {
        Directory.CreateDirectory(root);
        var b=new BackupSnapshot { ActivePowerScheme=await ActiveSchemeAsync() };
        Backup(b, Registry.CurrentUser, "Software\\Microsoft\\GameBar","AutoGameModeEnabled");
        Backup(b, Registry.CurrentUser, "Software\\Microsoft\\GameBar","AllowAutoGameMode");
        Backup(b, Registry.CurrentUser, "System\\GameConfigStore","GameDVR_Enabled");
        Backup(b, Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR","AppCaptureEnabled");
        await File.WriteAllTextAsync(LastBackupPath,JsonSerializer.Serialize(b,new JsonSerializerOptions{WriteIndented=true}));
        SetDword("Software\\Microsoft\\GameBar","AutoGameModeEnabled",1);
        SetDword("Software\\Microsoft\\GameBar","AllowAutoGameMode",1);
        SetDword("System\\GameConfigStore","GameDVR_Enabled",0);
        SetDword("Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR","AppCaptureEnabled",0);
        var power=await RunAsync("powercfg.exe","/setactive SCHEME_MIN");
        Log("Aplicado: Game Mode activo, captura en segundo plano desactivada, plan Alto rendimiento. "+power);
        return "Perfil seguro aplicado. Reinicia el juego para que todos los cambios surtan efecto.";
    }

    public async Task<string> RestoreAsync()
    {
        if(!File.Exists(LastBackupPath)) return "No existe una copia de seguridad previa.";
        var b=JsonSerializer.Deserialize<BackupSnapshot>(await File.ReadAllTextAsync(LastBackupPath));
        if(b is null) return "La copia no se pudo leer.";
        foreach(var x in b.Registry) RestoreRegistry(x);
        if(!string.IsNullOrWhiteSpace(b.ActivePowerScheme)) await RunAsync("powercfg.exe",$"/setactive {b.ActivePowerScheme}");
        Log("Restaurado: "+b.CreatedUtc.ToString("O"));
        return "Configuración anterior restaurada.";
    }

    static void Backup(BackupSnapshot b, RegistryKey hive, string path, string name)
    {
        using var k=hive.OpenSubKey(path);
        var names=k?.GetValueNames()??[];
        var existed=names.Contains(name,StringComparer.OrdinalIgnoreCase);
        var value=existed?k!.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames):null;
        var kind=existed?k!.GetValueKind(name).ToString():null;
        b.Registry.Add(new("HKCU",path,name,existed,value,kind));
    }
    static void SetDword(string path,string name,int value){using var k=Registry.CurrentUser.CreateSubKey(path,true);k.SetValue(name,value,RegistryValueKind.DWord);}
    static void RestoreRegistry(RegistryBackup x)
    {
        using var k=Registry.CurrentUser.CreateSubKey(x.Path,true);
        if(!x.Existed){k.DeleteValue(x.Name,false);return;}
        var kind=Enum.TryParse<RegistryValueKind>(x.Kind,out var parsed)?parsed:RegistryValueKind.String;
        object value=x.Value is JsonElement e ? kind==RegistryValueKind.DWord?e.GetInt32():e.ToString() : x.Value??"";
        k.SetValue(x.Name,value,kind);
    }
    async Task<string?> ActiveSchemeAsync()
    {
        var s=await RunAsync("powercfg.exe","/getactivescheme");
        return Regex.Match(s,@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}").Value;
    }
    static async Task<string> RunAsync(string file,string args)
    {
        using var p=new Process{StartInfo=new(file,args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true}};
        p.Start(); var output=await p.StandardOutput.ReadToEndAsync(); var error=await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        if(p.ExitCode!=0) throw new InvalidOperationException(error.Length>0?error:$"{file} terminó con código {p.ExitCode}.");
        return output.Trim();
    }
    void Log(string text){Directory.CreateDirectory(root);File.AppendAllText(Path.Combine(root,"hyperboost.log"),$"{DateTime.Now:O} {text}{Environment.NewLine}");}
}
