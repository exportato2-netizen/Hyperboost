using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace HyperBoost;

public partial class MainWindow : Window
{
    readonly HardwareScanner scanner = new();
    readonly OptimizationService optimizer = new();
    readonly GamingPersonaService persona = new();
    readonly BottleneckGateService gate = new();
    readonly SystemEventCoordinator systemEvents = new();
    readonly SelfBackgroundMode selfBackground = new();

    CancellationTokenSource? gateCts;
    CancellationTokenSource? focusLossCts;
    bool gateRunning;
    bool busy;

    public MainWindow()
    {
        InitializeComponent();
        systemEvents.ForegroundProcessChanged += SystemEvents_ForegroundProcessChanged;
        systemEvents.MemoryPressureChanged += SystemEvents_MemoryPressureChanged;
        systemEvents.WatchedProcessExited += SystemEvents_WatchedProcessExited;
        RefreshProcesses();

        if (!systemEvents.ForegroundHookAvailable)
            StatusText.Text = "Windows no permitió registrar el evento de ventana foreground. Gaming Persona permanecerá deshabilitada para evitar polling continuo.";
        else
            StatusText.Text = systemEvents.MemoryNotificationsAvailable
                ? "Motor por eventos listo. HyperBoost está inactivo hasta que armes una Gaming Persona."
                : "Motor de foco listo; las notificaciones nativas de memoria no están disponibles y se usará la comprobación conservadora al aplicar políticas.";

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

    async void StartPersona_Click(object sender, RoutedEventArgs e)
    {
        if (GameProcessCombo.SelectedItem is not GameProcessCandidate game) return;
        if (!systemEvents.ForegroundHookAvailable)
        {
            MessageBox.Show("Windows no permitió registrar EVENT_SYSTEM_FOREGROUND. HyperBoost no usará polling como reemplazo, por lo que Gaming Persona no se puede armar en esta sesión.", "Motor por eventos no disponible", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            PersonaStatus.Text = persona.Start(
                game,
                PersonaEcoQosCheck.IsChecked == true,
                PersonaMemoryCheck.IsChecked == true);
            systemEvents.WatchProcessExit(game.Id);
            GateText.Text = "Esperando que el juego gane foco. El Gate no ha medido nada todavía.";
            await HandleForegroundChangedAsync(SystemEventCoordinator.GetForegroundProcessId());
        }
        catch (Exception ex)
        {
            systemEvents.StopWatchingProcess();
            MessageBox.Show(ex.Message, "No se pudo armar Gaming Persona", MessageBoxButton.OK, MessageBoxImage.Warning);
            PersonaStatus.Text = "Gaming Persona no realizó cambios.";
        }
        UpdateControls();
    }

    void StopPersona_Click(object sender, RoutedEventArgs e)
    {
        CancelTransientWork();
        selfBackground.SetActive(false);
        systemEvents.StopWatchingProcess();
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
        var excludedPid = (GameProcessCombo.SelectedItem as GameProcessCandidate)?.Id ?? persona.TargetGamePid;
        SetBusy(true, "Midiendo CPU, I/O y memoria de procesos...");
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
            StatusText.Text = "Medición CPU/I-O completada. El Bottleneck Gate GPU se ejecuta automáticamente con el juego en foco para evitar una muestra engañosa.";
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

    void SystemEvents_ForegroundProcessChanged(int pid)
        => Dispatcher.BeginInvoke(async () => await HandleForegroundChangedAsync(pid));

    void SystemEvents_MemoryPressureChanged(bool low)
        => Dispatcher.BeginInvoke(() =>
        {
            persona.NotifyMemoryPressure(low);
            PersonaStatus.Text = persona.Status;
            UpdateControls();
        });

    void SystemEvents_WatchedProcessExited()
        => Dispatcher.BeginInvoke(() =>
        {
            CancelTransientWork();
            selfBackground.SetActive(false);
            PersonaStatus.Text = persona.HandleGameExited();
            systemEvents.StopWatchingProcess();
            RefreshProcesses();
        });

    async Task HandleForegroundChangedAsync(int pid)
    {
        if (!persona.IsEnabled)
        {
            selfBackground.SetActive(false);
            return;
        }

        if (pid == persona.TargetGamePid)
        {
            focusLossCts?.Cancel();
            focusLossCts?.Dispose();
            focusLossCts = null;

            if (!selfBackground.SetActive(true))
                StatusText.Text = "El juego tiene foco, pero Windows no permitió poner HyperBoost en PROCESS_MODE_BACKGROUND. El Gate continuará sin ese ajuste propio.";

            if (!persona.IsEngaged && !gateRunning)
                await RunBottleneckGateAsync();
            return;
        }

        selfBackground.SetActive(false);
        gateCts?.Cancel();
        if (persona.IsEngaged)
            ScheduleFocusLossRestore();
    }

    async Task RunBottleneckGateAsync()
    {
        if (!persona.IsEnabled || persona.TargetGamePid <= 0) return;
        gateRunning = true;
        gateCts?.Cancel();
        gateCts?.Dispose();
        gateCts = new CancellationTokenSource();
        var token = gateCts.Token;
        var targetPid = persona.TargetGamePid;
        GateText.Text = "Bottleneck Gate midiendo ~1 s con el juego en foco. Todavía no se aplica EcoQoS.";
        PersonaStatus.Text = "Juego en foco · HyperBoost se puso a sí mismo en background · Gate evaluando CPU/GPU/RAM/I-O.";
        UpdateControls();

        try
        {
            var result = await gate.EvaluateAsync(targetPid, persona, token);
            GateText.Text = result.ToDisplayText();
            if (!token.IsCancellationRequested && persona.IsEnabled && persona.TargetGamePid == targetPid && SystemEventCoordinator.GetForegroundProcessId() == targetPid)
                PersonaStatus.Text = persona.EngageFromGate(result.AllowEcoQos);
        }
        catch (OperationCanceledException)
        {
            GateText.Text = "Gate cancelado porque el juego perdió foco o la Persona se detuvo. No se aplicó una decisión incompleta.";
        }
        catch (Exception ex)
        {
            GateText.Text = "Gate falló de forma segura: " + ex.Message;
            PersonaStatus.Text = "No se aplicó EcoQoS porque el Gate no pudo completar la medición.";
        }
        finally
        {
            gateRunning = false;
            UpdateControls();
        }
    }

    void ScheduleFocusLossRestore()
    {
        focusLossCts?.Cancel();
        focusLossCts?.Dispose();
        focusLossCts = new CancellationTokenSource();
        var token = focusLossCts.Token;
        PersonaStatus.Text = "Pérdida de foco detectada; se esperan 2 s antes de restaurar para ignorar overlays/Alt+Tab breve.";

        _ = RestoreAfterGraceAsync(token);
    }

    async Task RestoreAfterGraceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            if (!token.IsCancellationRequested && persona.IsEnabled && SystemEventCoordinator.GetForegroundProcessId() != persona.TargetGamePid)
                PersonaStatus.Text = persona.PauseAndRestore();
        }
        catch (OperationCanceledException) { }
        finally
        {
            UpdateControls();
        }
    }

    void CancelTransientWork()
    {
        gateCts?.Cancel();
        gateCts?.Dispose();
        gateCts = null;
        focusLossCts?.Cancel();
        focusLossCts?.Dispose();
        focusLossCts = null;
    }

    static string TrimTo(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(1, max - 1)] + "…";

    void SetBusy(bool value, string? text = null)
    {
        busy = value;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) StatusText.Text = text;
        UpdateControls();
    }

    void UpdateControls()
    {
        var personaActive = persona.IsEnabled;
        var pendingRestore = persona.HasPendingRestores;
        var personaLocked = personaActive || pendingRestore;

        ScanButton.IsEnabled = !busy && !personaActive && !gateRunning;
        RestoreButton.IsEnabled = !busy && optimizer.HasBackup && !personaActive && !gateRunning;
        RefreshProcessesButton.IsEnabled = !busy && !personaLocked && !gateRunning;
        GameProcessCombo.IsEnabled = !busy && !personaLocked && !gateRunning;
        PersonaEcoQosCheck.IsEnabled = !busy && !personaLocked && !gateRunning;
        PersonaMemoryCheck.IsEnabled = !busy && !personaLocked && !gateRunning;
        StartPersonaButton.IsEnabled = !busy && !personaLocked && !gateRunning && systemEvents.ForegroundHookAvailable && GameProcessCombo.SelectedItem is GameProcessCandidate;
        StopPersonaButton.IsEnabled = !busy && (personaActive || pendingRestore);
        ScanInterferenceButton.IsEnabled = !busy && !gateRunning;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelTransientWork();
        selfBackground.Dispose();
        systemEvents.Dispose();
        persona.Dispose();
        base.OnClosed(e);
    }
}
