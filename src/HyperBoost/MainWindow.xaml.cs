using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace HyperBoost;

public partial class MainWindow : Window
{
    readonly HardwareScanner scanner = new();
    readonly OptimizationService optimizer = new();
    readonly GamingPersonaService persona = new();
    readonly DispatcherTimer personaTimer;
    bool busy;

    public MainWindow()
    {
        InitializeComponent();
        personaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        personaTimer.Tick += PersonaTimer_Tick;
        personaTimer.Start();
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
            StatusText.Text = "Análisis completado. HyperBoost no cambiará opciones que ya pertenecen a Windows o NVIDIA App.";
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

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Restaurando configuración de una beta anterior...");
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
        var candidates = persona.GetCandidates();
        GameProcessCombo.ItemsSource = candidates;
        if (selectedPid is int pid)
            GameProcessCombo.SelectedItem = candidates.FirstOrDefault(x => x.Id == pid);
        if (GameProcessCombo.SelectedItem is null && candidates.Count > 0)
            GameProcessCombo.SelectedIndex = 0;
        UpdateControls();
    }

    void GameProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    void StartPersona_Click(object sender, RoutedEventArgs e)
    {
        if (GameProcessCombo.SelectedItem is not GameProcessCandidate game) return;
        try
        {
            PersonaStatus.Text = persona.Start(
                game,
                PersonaEcoQosCheck.IsChecked == true,
                PersonaMemoryCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo armar Gaming Persona", MessageBoxButton.OK, MessageBoxImage.Warning);
            PersonaStatus.Text = "Gaming Persona no realizó cambios.";
        }
        UpdateControls();
    }

    void StopPersona_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersonaStatus.Text = persona.Stop();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo detener Gaming Persona", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshProcesses();
    }

    async void ScanInterference_Click(object sender, RoutedEventArgs e)
    {
        var excludedPid = (GameProcessCombo.SelectedItem as GameProcessCandidate)?.Id ?? 0;
        SetBusy(true, "Midiendo interferencias de procesos...");
        try
        {
            var samples = await persona.ScanInterferenceAsync(excludedPid);
            var sb = new StringBuilder();
            sb.AppendLine("PROCESO                     PID     CPU%     RAM MB    I/O MB/s");
            sb.AppendLine(new string('-', 69));
            foreach (var x in samples)
            {
                sb.AppendLine($"{TrimTo(x.Process, 26),-26} {x.Pid,7} {x.CpuPercent,7:0.00} {x.WorkingSetMb,10:0} {x.IoMbPerSec,10:0.00}");
                sb.AppendLine($"  ↳ {x.Recommendation}");
            }
            if (samples.Count == 0) sb.AppendLine("No se detectó actividad relevante durante la ventana de medición.");
            InterferenceText.Text = sb.ToString();
            StatusText.Text = "Medición completada. El escáner no modificó ningún proceso.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo medir interferencias", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "La medición de interferencias falló sin aplicar cambios.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    static string TrimTo(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(1, max - 1)] + "…";

    void PersonaTimer_Tick(object? sender, EventArgs e)
    {
        var wasEnabled = persona.IsEnabled;
        try
        {
            persona.Tick();
            PersonaStatus.Text = persona.Status;
        }
        catch (Exception ex)
        {
            PersonaStatus.Text = "Gaming Persona encontró un error y no forzó el cambio: " + ex.Message;
        }

        if (wasEnabled && !persona.IsEnabled)
            RefreshProcesses();
        else
            UpdateControls();
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
        var personaLocked = persona.IsEnabled;
        ScanButton.IsEnabled = !busy && !personaLocked;
        RestoreButton.IsEnabled = !busy && optimizer.HasBackup && !personaLocked;
        RefreshProcessesButton.IsEnabled = !busy && !personaLocked;
        GameProcessCombo.IsEnabled = !busy && !personaLocked;
        PersonaEcoQosCheck.IsEnabled = !busy && !personaLocked;
        PersonaMemoryCheck.IsEnabled = !busy && !personaLocked;
        StartPersonaButton.IsEnabled = !busy && !personaLocked && GameProcessCombo.SelectedItem is GameProcessCandidate;
        StopPersonaButton.IsEnabled = !busy && personaLocked;
        ScanInterferenceButton.IsEnabled = !busy;
    }

    protected override void OnClosed(EventArgs e)
    {
        personaTimer.Stop();
        persona.Dispose();
        base.OnClosed(e);
    }
}
