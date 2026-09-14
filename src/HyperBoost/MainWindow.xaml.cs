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
    readonly PresentMonBenchmarkService benchmarkService = new();

    CancellationTokenSource? gateCts;
    CancellationTokenSource? focusLossCts;
    CancellationTokenSource? benchmarkCaptureCts;
    AbBenchmarkSession? benchmarkSession;
    BenchmarkPlanItem? benchmarkPendingPlan;
    bool benchmarkPassArmed;
    bool benchmarkCaptureRunning;
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
            StatusText.Text = "Windows no permitió registrar el evento de ventana foreground. Gaming Persona y Benchmark A/B permanecerán deshabilitados para evitar polling continuo.";
        else
            StatusText.Text = systemEvents.MemoryNotificationsAvailable
                ? "Motor por eventos listo. HyperBoost está inactivo hasta que armes una Gaming Persona o una sesión A/B."
                : "Motor de foco listo; las notificaciones nativas de memoria no están disponibles y se usará la comprobación conservadora al aplicar políticas.";

        try
        {
            var hash = benchmarkService.ValidatePresentMon();
            BenchmarkStatusText.Text = $"PresentMon 2.5.1 verificado · SHA-256 {hash[..12]}… · listo para crear una sesión A/B.";
        }
        catch (Exception ex)
        {
            BenchmarkStatusText.Text = "Benchmark no disponible: " + ex.Message;
        }

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
    void BenchmarkRefresh_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    void RefreshProcesses()
    {
        var selectedPid = (GameProcessCombo.SelectedItem as GameProcessCandidate)?.Id;
        var benchmarkSelectedPid = (BenchmarkGameProcessCombo.SelectedItem as GameProcessCandidate)?.Id;
        var candidates = persona.GetCandidates();

        GameProcessCombo.ItemsSource = candidates;
        if (selectedPid is int pid)
            GameProcessCombo.SelectedItem = candidates.FirstOrDefault(x => x.Id == pid);
        if (GameProcessCombo.SelectedItem is null && candidates.Count > 0)
            GameProcessCombo.SelectedIndex = 0;

        BenchmarkGameProcessCombo.ItemsSource = candidates;
        if (benchmarkSelectedPid is int benchmarkPid)
            BenchmarkGameProcessCombo.SelectedItem = candidates.FirstOrDefault(x => x.Id == benchmarkPid);
        if (BenchmarkGameProcessCombo.SelectedItem is null && candidates.Count > 0)
            BenchmarkGameProcessCombo.SelectedIndex = 0;

        UpdateControls();
    }

    void GameProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();
    void BenchmarkGameProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

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
            CancelTransientWork();
            selfBackground.SetActive(false);
            systemEvents.StopWatchingProcess();
            if (persona.IsEnabled || persona.HasPendingRestores)
            {
                try { persona.Stop(); } catch { }
            }
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
            StatusText.Text = "Medición CPU/I-O completada. El Bottleneck Gate GPU se ejecuta automáticamente con el juego en foco para evitar una lectura engañosa.";
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

    void BenchmarkCreate_Click(object sender, RoutedEventArgs e)
    {
        if (BenchmarkGameProcessCombo.SelectedItem is not GameProcessCandidate game) return;
        if (!systemEvents.ForegroundHookAvailable)
        {
            MessageBox.Show("El benchmark requiere EVENT_SYSTEM_FOREGROUND para cancelar muestras contaminadas si el juego pierde foco.", "Motor por eventos no disponible", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (persona.IsEnabled || persona.HasPendingRestores)
        {
            MessageBox.Show("Detén primero Gaming Persona. Una sesión A/B necesita empezar desde un estado OFF conocido.", "Gaming Persona activa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            benchmarkService.ValidatePresentMon();
            var pairs = SelectedTagInt(BenchmarkPairsCombo, 3);
            var seconds = SelectedTagInt(BenchmarkDurationCombo, 30);
            benchmarkSession = new AbBenchmarkSession(game, pairs, seconds);
            benchmarkPendingPlan = null;
            benchmarkPassArmed = false;
            benchmarkCaptureRunning = false;
            systemEvents.WatchProcessExit(game.Id);
            BenchmarkStatusText.Text = $"Sesión creada para {game.Name} · {pairs} pares · {seconds}s por pasada. Pulsa «Preparar siguiente pasada».";
            BenchmarkResultsText.Text = "Aún no hay capturas. El resultado se calcula por pares y se compara con el ruido de las pasadas OFF.";
            UpdateBenchmarkPlanText();
        }
        catch (Exception ex)
        {
            benchmarkSession = null;
            systemEvents.StopWatchingProcess();
            MessageBox.Show(ex.Message, "No se pudo crear la sesión A/B", MessageBoxButton.OK, MessageBoxImage.Warning);
            BenchmarkStatusText.Text = "La sesión A/B no se inició.";
        }
        UpdateControls();
    }

    void BenchmarkNext_Click(object sender, RoutedEventArgs e)
    {
        if (benchmarkSession is null || benchmarkSession.IsComplete || benchmarkPassArmed || benchmarkCaptureRunning) return;
        var plan = benchmarkSession.Next;
        if (plan is null) return;

        try
        {
            if (plan.Mode == BenchmarkMode.On)
            {
                PersonaStatus.Text = persona.Start(
                    benchmarkSession.Game,
                    PersonaEcoQosCheck.IsChecked == true,
                    PersonaMemoryCheck.IsChecked == true);
            }
            else
            {
                if (persona.IsEnabled || persona.HasPendingRestores)
                    PersonaStatus.Text = persona.Stop();
                selfBackground.SetActive(false);
            }

            benchmarkPendingPlan = plan;
            benchmarkPassArmed = true;
            BenchmarkStatusText.Text = $"{plan.Label} preparada. Vuelve al juego. Al detectar su foco HyperBoost dejará PresentMon listo; luego pulsa {PresentMonBenchmarkService.CaptureHotkey} en el punto exacto de inicio.";
            UpdateBenchmarkPlanText();
        }
        catch (Exception ex)
        {
            benchmarkPendingPlan = null;
            benchmarkPassArmed = false;
            if (persona.IsEnabled || persona.HasPendingRestores)
            {
                try { persona.Stop(); } catch { }
            }
            MessageBox.Show(ex.Message, "No se pudo preparar la pasada", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateControls();
    }

    void BenchmarkCancel_Click(object sender, RoutedEventArgs e)
    {
        if (benchmarkSession is null) return;
        var path = benchmarkSession.RootDirectory;
        benchmarkCaptureCts?.Cancel();
        benchmarkPassArmed = false;
        benchmarkPendingPlan = null;
        selfBackground.SetActive(false);
        try
        {
            if (persona.IsEnabled || persona.HasPendingRestores)
                PersonaStatus.Text = persona.Stop();
        }
        catch { }
        systemEvents.StopWatchingProcess();
        benchmarkSession = null;
        BenchmarkStatusText.Text = $"Sesión cancelada. Las capturas ya completadas, si existen, permanecen en {path}";
        UpdateControls();
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
            if (benchmarkSession is not null)
            {
                var path = benchmarkSession.RootDirectory;
                benchmarkCaptureCts?.Cancel();
                benchmarkPassArmed = false;
                benchmarkPendingPlan = null;
                selfBackground.SetActive(false);
                try
                {
                    if (persona.IsEnabled || persona.HasPendingRestores)
                        PersonaStatus.Text = persona.HandleGameExited();
                }
                catch { }
                benchmarkSession = null;
                BenchmarkStatusText.Text = $"El juego terminó. La sesión A/B se cerró; las capturas válidas permanecen en {path}";
                systemEvents.StopWatchingProcess();
                RefreshProcesses();
                return;
            }

            CancelTransientWork();
            selfBackground.SetActive(false);
            PersonaStatus.Text = persona.HandleGameExited();
            systemEvents.StopWatchingProcess();
            RefreshProcesses();
        });

    async Task HandleForegroundChangedAsync(int pid)
    {
        if (benchmarkSession is not null)
        {
            var target = benchmarkSession.Game.Id;
            if (benchmarkCaptureRunning && pid != target)
            {
                BenchmarkStatusText.Text = "El juego perdió foco durante la pasada. La captura se cancela y se descarta para no contaminar el A/B.";
                benchmarkCaptureCts?.Cancel();
                selfBackground.SetActive(false);
                return;
            }

            if (benchmarkPassArmed && !benchmarkCaptureRunning && pid == target)
            {
                await RunBenchmarkPassAsync();
                return;
            }

            // Una sesión A/B es dueña de la lógica de foco mientras está abierta.
            // Así evitamos que Gaming Persona se active fuera de una pasada ON preparada.
            return;
        }

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

    async Task RunBenchmarkPassAsync()
    {
        var session = benchmarkSession;
        var plan = benchmarkPendingPlan;
        if (session is null || plan is null || benchmarkCaptureRunning) return;

        benchmarkPassArmed = false;
        benchmarkCaptureRunning = true;
        benchmarkCaptureCts?.Dispose();
        benchmarkCaptureCts = new CancellationTokenSource();
        var token = benchmarkCaptureCts.Token;
        UpdateControls();

        try
        {
            if (plan.Mode == BenchmarkMode.On)
            {
                if (!persona.IsEnabled)
                    throw new InvalidOperationException("La pasada ON perdió el estado armado de Gaming Persona.");

                if (!selfBackground.SetActive(true))
                    StatusText.Text = "Windows no permitió poner HyperBoost en background; la pasada ON continuará y el reporte reflejará el comportamiento real de esta sesión.";

                BenchmarkStatusText.Text = $"{plan.Label}: Bottleneck Gate midiendo interferencias antes de la captura. Todavía no pulses la hotkey.";
                var gateResult = await gate.EvaluateAsync(session.Game.Id, persona, token);
                GateText.Text = gateResult.ToDisplayText();
                if (SystemEventCoordinator.GetForegroundProcessId() != session.Game.Id)
                    throw new OperationCanceledException("El juego perdió foco durante el Gate.", token);
                PersonaStatus.Text = persona.EngageFromGate(gateResult.EcoQosPids);
            }
            else
            {
                selfBackground.SetActive(false);
                if (persona.IsEnabled || persona.HasPendingRestores)
                    throw new InvalidOperationException("La pasada OFF detectó Gaming Persona activa; se cancela para preservar la línea base.");
            }

            if (SystemEventCoordinator.GetForegroundProcessId() != session.Game.Id)
                throw new OperationCanceledException("El juego ya no está en foreground.", token);

            BenchmarkStatusText.Text = $"{plan.Label}: iniciando PresentMon verificado…";
            var capture = await benchmarkService.CaptureWithHotkeyAsync(
                plan,
                session.Game.Id,
                session.CaptureSeconds,
                session.RootDirectory,
                () => Dispatcher.BeginInvoke(() => BenchmarkStatusText.Text = $"{plan.Label}: LISTA. Mantén el juego en foco y pulsa {PresentMonBenchmarkService.CaptureHotkey}. Después de la hotkey se grabarán {session.CaptureSeconds}s automáticamente."),
                token);

            if (SystemEventCoordinator.GetForegroundProcessId() != session.Game.Id)
                throw new OperationCanceledException("El juego perdió foco antes de cerrar la captura.", token);

            session.Add(capture);
            UpdateBenchmarkPlanText();
            UpdateBenchmarkResultsText();

            if (session.IsComplete)
            {
                var analysis = session.Analyze();
                await session.SaveReportAsync(analysis, token);
                BenchmarkResultsText.Text = analysis.ReportText;
                BenchmarkStatusText.Text = $"SESIÓN COMPLETA · {analysis.Verdict} Reporte y CSV crudos: {session.RootDirectory}";
                systemEvents.StopWatchingProcess();
            }
            else
            {
                BenchmarkStatusText.Text = $"Pasada válida guardada: {capture.AverageFps:0.00} FPS · 1% Low {capture.Low1Fps:0.00} · p99 {capture.P99FrameTimeMs:0.00} ms. Vuelve a HyperBoost y prepara la siguiente.";
            }
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatusText.Text = $"{plan.Label} descartada. No se añadió al resultado; puedes preparar la misma pasada nuevamente.";
        }
        catch (Exception ex)
        {
            BenchmarkStatusText.Text = $"{plan.Label} falló y se descartó: {ex.Message}";
        }
        finally
        {
            selfBackground.SetActive(false);
            try
            {
                if (persona.IsEnabled || persona.HasPendingRestores)
                    PersonaStatus.Text = persona.Stop();
            }
            catch (Exception ex)
            {
                PersonaStatus.Text = "Advertencia al restaurar tras benchmark: " + ex.Message;
            }

            benchmarkPendingPlan = null;
            benchmarkPassArmed = false;
            benchmarkCaptureRunning = false;
            benchmarkCaptureCts?.Dispose();
            benchmarkCaptureCts = null;
            UpdateControls();
        }
    }

    void UpdateBenchmarkPlanText()
    {
        if (benchmarkSession is null)
        {
            BenchmarkPlanText.Text = "Crea una sesión para generar el plan contrabalanceado.";
            return;
        }

        var done = benchmarkSession.Results.Select(x => x.Sequence).ToHashSet();
        var next = benchmarkSession.Next?.Sequence;
        var sb = new StringBuilder();
        foreach (var item in benchmarkSession.Plan)
        {
            var marker = done.Contains(item.Sequence) ? "✓" : item.Sequence == next ? "→" : "·";
            sb.AppendLine($"{marker} #{item.Sequence:00} · Par {item.Pair} · {item.Mode.ToString().ToUpperInvariant()}");
        }
        sb.AppendLine().AppendLine("Orden contrabalanceado: pares impares OFF→ON; pares pares ON→OFF.");
        BenchmarkPlanText.Text = sb.ToString();
    }

    void UpdateBenchmarkResultsText()
    {
        if (benchmarkSession is null || benchmarkSession.Results.Count == 0)
        {
            BenchmarkResultsText.Text = "Aún no hay capturas válidas.";
            return;
        }

        try
        {
            var analysis = benchmarkSession.Analyze();
            BenchmarkResultsText.Text = analysis.ReportText;
        }
        catch
        {
            var sb = new StringBuilder();
            sb.AppendLine("PASADAS VÁLIDAS");
            foreach (var x in benchmarkSession.Results.OrderBy(x => x.Sequence))
                sb.AppendLine($"#{x.Sequence:00} P{x.Pair} {x.Mode,-3} · {x.AverageFps:0.00} FPS · 1% {x.Low1Fps:0.00} · 0.1% {x.Low01Fps:0.00} · p99 {x.P99FrameTimeMs:0.00} ms · stutter {x.StutterRatePercent:0.000}%");
            sb.AppendLine().AppendLine("Falta completar al menos un par OFF/ON para calcular diferencias.");
            BenchmarkResultsText.Text = sb.ToString();
        }
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
                PersonaStatus.Text = persona.EngageFromGate(result.EcoQosPids);
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

    static int SelectedTagInt(ComboBox box, int fallback)
    {
        if (box.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var value)) return value;
        return fallback;
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
        var benchmarkInProgress = benchmarkSession is { IsComplete: false } || benchmarkPassArmed || benchmarkCaptureRunning;
        var benchmarkExists = benchmarkSession is not null;

        ScanButton.IsEnabled = !busy && !personaActive && !gateRunning && !benchmarkInProgress;
        RestoreButton.IsEnabled = !busy && optimizer.HasBackup && !personaActive && !gateRunning && !benchmarkInProgress;
        RefreshProcessesButton.IsEnabled = !busy && !personaLocked && !gateRunning && !benchmarkInProgress;
        GameProcessCombo.IsEnabled = !busy && !personaLocked && !gateRunning && !benchmarkInProgress;
        PersonaEcoQosCheck.IsEnabled = !busy && !personaLocked && !gateRunning && !benchmarkPassArmed && !benchmarkCaptureRunning;
        PersonaMemoryCheck.IsEnabled = !busy && !personaLocked && !gateRunning && !benchmarkPassArmed && !benchmarkCaptureRunning;
        StartPersonaButton.IsEnabled = !busy && !personaLocked && !gateRunning && !benchmarkInProgress && systemEvents.ForegroundHookAvailable && GameProcessCombo.SelectedItem is GameProcessCandidate;
        StopPersonaButton.IsEnabled = !busy && !benchmarkInProgress && (personaActive || pendingRestore);
        ScanInterferenceButton.IsEnabled = !busy && !gateRunning && !benchmarkCaptureRunning;

        BenchmarkRefreshButton.IsEnabled = !busy && !personaLocked && !benchmarkInProgress;
        BenchmarkGameProcessCombo.IsEnabled = !busy && !personaLocked && !benchmarkInProgress;
        BenchmarkPairsCombo.IsEnabled = !busy && !personaLocked && !benchmarkInProgress;
        BenchmarkDurationCombo.IsEnabled = !busy && !personaLocked && !benchmarkInProgress;
        BenchmarkCreateButton.IsEnabled = !busy && !personaLocked && !gateRunning && !benchmarkCaptureRunning && systemEvents.ForegroundHookAvailable && BenchmarkGameProcessCombo.SelectedItem is GameProcessCandidate && (benchmarkSession is null || benchmarkSession.IsComplete);
        BenchmarkNextButton.IsEnabled = !busy && benchmarkSession is { IsComplete: false } && !benchmarkPassArmed && !benchmarkCaptureRunning && !gateRunning;
        BenchmarkCancelButton.IsEnabled = !busy && benchmarkExists;
    }

    protected override void OnClosed(EventArgs e)
    {
        benchmarkCaptureCts?.Cancel();
        CancelTransientWork();
        selfBackground.Dispose();
        systemEvents.Dispose();
        persona.Dispose();
        base.OnClosed(e);
    }
}
