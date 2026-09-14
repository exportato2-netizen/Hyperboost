using System.Text;
using System.Windows;

namespace HyperBoost;

public partial class MainWindow : Window
{
    readonly HardwareScanner scanner=new();
    readonly OptimizationService optimizer=new();
    public MainWindow(){InitializeComponent();}
    async void Scan_Click(object sender,RoutedEventArgs e)
    {
        SetBusy(true,"Analizando Windows y hardware...");
        try
        {
            var r=await scanner.ScanAsync();
            var sb=new StringBuilder();
            sb.AppendLine($"WINDOWS  {r.Windows}").AppendLine($"CPU      {r.Cpu}").AppendLine($"NÚCLEOS  {r.PhysicalCores} físicos / {r.LogicalCores} lógicos")
              .AppendLine($"GPU      {r.GpuReadOnly} [SOLO LECTURA]").AppendLine($"PLACA    {r.Motherboard}").AppendLine($"BIOS     {r.Bios}")
              .AppendLine($"RAM      {r.TotalRamGb:0.#} GB");
            foreach(var m in r.Memory) sb.AppendLine($"         {m.Name} · {m.CapacityGb:0.#} GB · configurada {m.ConfiguredMhz} MT/s");
            sb.AppendLine("DISCOS");
            foreach(var d in r.Disks) sb.AppendLine($"         {d.Name} · {d.SizeGb:0} GB · {d.Status}");
            HardwareText.Text=sb.ToString();
            Findings.ItemsSource=DiagnosticsEngine.Analyze(r);
            ApplyButton.IsEnabled=true;
            StatusText.Text="Análisis completado. Revisa las recomendaciones antes de aplicar.";
        }catch(Exception ex){MessageBox.Show(ex.Message,"No se pudo analizar",MessageBoxButton.OK,MessageBoxImage.Error);StatusText.Text="El análisis falló.";}
        finally{SetBusy(false);}
    }
    async void Apply_Click(object sender,RoutedEventArgs e)
    {
        if(MessageBox.Show("Se guardará el estado actual y se aplicarán solo ajustes Windows seguros. ¿Continuar?","HyperBoost",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        SetBusy(true,"Guardando y aplicando...");
        try{StatusText.Text=await optimizer.ApplySafeAsync();MessageBox.Show(StatusText.Text,"Listo");}
        catch(Exception ex){MessageBox.Show(ex.Message,"No se pudo aplicar",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{SetBusy(false);}
    }
    async void Restore_Click(object sender,RoutedEventArgs e)
    {
        SetBusy(true,"Restaurando...");
        try{StatusText.Text=await optimizer.RestoreAsync();MessageBox.Show(StatusText.Text,"Restauración");}
        catch(Exception ex){MessageBox.Show(ex.Message,"No se pudo restaurar",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{SetBusy(false);}
    }
    void SetBusy(bool busy,string? text=null){Busy.Visibility=busy?Visibility.Visible:Visibility.Collapsed;ScanButton.IsEnabled=!busy;ApplyButton.IsEnabled=!busy&&ApplyButton.IsEnabled;RestoreButton.IsEnabled=!busy;if(text!=null)StatusText.Text=text;}
}
