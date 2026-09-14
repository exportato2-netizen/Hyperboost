using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace HyperBoost;

public partial class MainWindow : Window
{
    readonly HardwareScanner scanner = new();
    readonly OptimizationService optimizer = new();
    readonly GameBoostService gameBoost = new();
    bool busy;
    bool scanCompleted;

    public MainWindow()
    {
        InitializeComponent();
        RefreshProcesses();
        UpdateControls();
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Analizando Windows y hardware...");
        try
        {
            var r = await scanner.ScanAsync();
            var sb = new StringBuilder();
            sb.AppendLine($"WINDOWS  {r.Windows}")
              .AppendLine($"CPU      {r.Cpu}")
              .AppendLine($"NÚCLEOS  {r.PhysicalCores} físicos / {r.LogicalCores} lógicos")
              .AppendLine($"GPU      {r.GpuReadOnly} [SOLO LECTURA]")
              .AppendLine($"PLACA    {r.Motherboard}")
              .AppendLine($"BIOS     {r.Bios}")
              .AppendLine($"RAM      {r.TotalRamGb:0.#} GB");

            foreach (var m in r.Memory)
                sb.AppendLine($"         {m.Name} · {m.CapacityGb:0.#} GB · informada {m.SpeedMhz} MT/s · configurada {m.ConfiguredMhz} MT/s");

            sb.AppendLine("DISCOS");
            foreach (var d in r.Disks)
                sb.AppendLine($"         {d.Name} · {d.SizeGb:0} GB · estado WMI: {d.Status}");

            sb.AppendLine($"ENERGÍA  {r.ActivePowerScheme}");
            sb.AppendLine($"BATERÍA  {(r.HasBattery ? "detectada" : "no detectada")}");
            if (r.GamingBackgroundTools.Count > 0)
                sb.AppendLine($"TOOLS    {string.Join(", ", r.GamingBackgroundTools)}");

            HardwareText.Text = sb.ToString();
            Findings.ItemsSource = DiagnosticsEngine.Analyze(r);
            scanCompleted = true;
            StatusText.Text = "Análisis completado. Revisa las recomendaciones antes de aplicar.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo analizar", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "El análisis falló.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
            "Se guardará el estado actual y se aplicarán solo ajustes Windows de bajo riesgo: Game Mode y captura en segundo plano. No se cambiará el plan de energía. ¿Continuar?",
            "HyperBoost", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true, "Guardando y aplicando...");
        try
        {
            StatusText.Text = await optimizer.ApplySafeAsync();
            MessageBox.Show(StatusText.Text, "HyperBoost");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo aplicar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Restaurando...");
        try
        {
            StatusText.Text = await optimizer.RestoreAsync();
            MessageBox.Show(StatusText.Text, "Restauración");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo restaurar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    void RefreshProcesses()
    {
        var selectedPid = (GameProcessCombo.SelectedItem as GameProcessCandidate)?.Id;
        var candidates = gameBoost.GetCandidates();
        GameProcessCombo.ItemsSource = candidates;
        if (selectedPid is int pid)
            GameProcessCombo.SelectedItem = candidates.FirstOrDefault(x => x.Id == pid);
        if (GameProcessCombo.SelectedItem is null && candidates.Count > 0)
            GameProcessCombo.SelectedIndex = 0;
        UpdateControls();
    }

    void GameProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    void StartBoost_Click(object sender, RoutedEventArgs e)
    {
        if (GameProcessCombo.SelectedItem is not GameProcessCandidate game) return;
        try
        {
            BoostStatus.Text = gameBoost.Start(
                game.Id,
                BoostPriorityCheck.IsChecked == true,
                HonorTimersCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo iniciar el boost", MessageBoxButton.OK, MessageBoxImage.Warning);
            BoostStatus.Text = "No se aplicaron cambios al proceso.";
        }
        UpdateControls();
    }

    void StopBoost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BoostStatus.Text = gameBoost.Stop();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo detener el boost", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshProcesses();
    }

    void OpenGraphics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        }
    }

    void SetBusy(bool value, string? text = null)
    {
        busy = value;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) StatusText.Text = text;
        UpdateControls();
    }

    void UpdateControls()
    {
        ScanButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && scanCompleted;
        RestoreButton.IsEnabled = !busy && optimizer.HasBackup;
        RefreshProcessesButton.IsEnabled = !busy && !gameBoost.IsActive;
        GameProcessCombo.IsEnabled = !busy && !gameBoost.IsActive;
        BoostPriorityCheck.IsEnabled = !busy && !gameBoost.IsActive;
        HonorTimersCheck.IsEnabled = !busy && !gameBoost.IsActive;
        StartBoostButton.IsEnabled = !busy && !gameBoost.IsActive && GameProcessCombo.SelectedItem is GameProcessCandidate;
        StopBoostButton.IsEnabled = !busy && gameBoost.IsActive;
    }

    protected override void OnClosed(EventArgs e)
    {
        gameBoost.Dispose();
        base.OnClosed(e);
    }
}
