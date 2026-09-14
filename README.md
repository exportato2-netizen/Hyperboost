# HyperBoost

Beta abierta de un optimizador para Windows 11 enfocado en estabilidad, frame pacing y FPS sin reducir calidad gráfica ni debilitar la seguridad del equipo.

## Beta 0.4 · Event-driven Bottleneck Gate

Beta 0.4 cambia la arquitectura: HyperBoost deja de consultar periódicamente qué ventana tiene foco y pasa a reaccionar a eventos nativos de Windows. Además, antes de aplicar EcoQoS mide CPU, GPU, RAM e I/O y puede decidir explícitamente **no hacer ningún cambio**.

> HyperBoost todavía no afirma una ganancia porcentual de FPS. La arquitectura 0.4 reduce intervenciones innecesarias y crea la base para medir A/B de forma controlada en una versión posterior.

### Motor por eventos

- Usa `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` para recibir cambios de ventana foreground sin un timer periódico de 1,2 s.
- Vigila la salida del juego mediante eventos del proceso, no mediante polling.
- Usa `CreateMemoryResourceNotification` cuando Windows expone notificaciones de memoria baja/alta.
- Una pérdida breve de foco usa únicamente un temporizador one-shot de 2 segundos para evitar restaurar/reaplicar por overlays o Alt+Tab corto.
- Si el hook de foreground no está disponible, Gaming Persona no usa un fallback de polling: queda deshabilitada para esa sesión.

### HyperBoost se pone a sí mismo en background

Cuando el juego seleccionado obtiene foco, HyperBoost intenta aplicar `PROCESS_MODE_BACKGROUND_BEGIN` únicamente a **su propio proceso**. Al perder foco lo revierte inmediatamente con `PROCESS_MODE_BACKGROUND_END`.

No cambia prioridad, QoS, timers ni scheduling del proceso del juego.

### Bottleneck Gate

Con el juego realmente en foreground, el Gate realiza una muestra corta antes de permitir EcoQoS:

- CPU por proceso.
- I/O por proceso.
- RAM y presión de memoria.
- GPU por proceso mediante contadores WDDM de Windows, en modo solo lectura.

El porcentaje GPU mostrado usa el motor GPU más ocupado por proceso. Si Windows no expone esos contadores, el Gate continúa de forma segura con CPU/RAM/I/O.

EcoQoS solo queda autorizado para **PID concretos** de la allowlist segura que hayan demostrado actividad CPU o I/O durante la muestra. No se autoriza una aplicación solo por consumir memoria.

Ejemplos de decisión del Gate:

- Juego cerca de saturación GPU y sin competencia segura: **no tocar CPU**.
- Otro proceso usa GPU: **informar**, sin modificar procesos gráficos.
- RAM bajo presión: permitir Memory Priority adaptativa únicamente sobre la allowlist segura.
- Proceso seguro con CPU/I/O medible: permitir EcoQoS solo para ese PID.
- Sin evidencia de cuello/interferencia: **no hacer cambios**.

### Políticas temporales que siguen disponibles

- EcoQoS temporal únicamente para una allowlist pequeña de procesos secundarios no críticos conocidos y solo si el Gate aprueba su PID.
- Si un proceso ya controla explícitamente su propio `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`, HyperBoost no lo pisa.
- Al restaurar EcoQoS, solo revierte el bit que todavía reconoce como propio; cambios posteriores de Windows u otra aplicación se conservan.
- Memory Priority adaptativa: solo bajo presión real de RAM y únicamente de 5 a 4 para la misma lista segura.
- Memory Priority solo vuelve de 4 a 5 si el valor actual sigue siendo el aplicado por HyperBoost.
- Restauraciones que fallen temporalmente conservan su snapshot para reintento.
- PID + tiempo de creación evita modificar procesos reutilizados.

Navegadores, launchers, Discord, audio, OBS, overlays, anti-cheat, NVIDIA y utilidades de periféricos no se modifican automáticamente. Pueden aparecer en los diagnósticos solo para observación.

### HyperBoost deja a Windows 11

- Game Mode.
- Xbox Game Bar / Game DVR y captura.
- Optimizaciones para juegos en ventanas, Auto HDR y VRR.
- HAGS y preferencias gráficas por aplicación.
- Planes/modos de energía y decisiones de scheduling del juego.
- Política de resolución de temporizador del proceso foreground.

### HyperBoost deja a NVIDIA App/driver

- Optimización gráfica del juego y perfiles 3D.
- DLSS overrides y Frame Generation.
- Smooth Motion.
- Low Latency / Reflex.
- G-SYNC, resolución, frecuencia y opciones de pantalla.
- Max Frame Rate y Background Application Max Frame Rate.
- ShadowPlay, Instant Replay, Highlights y overlay.
- Auto tuning de GPU, clocks y rendimiento.

## Compatibilidad con betas anteriores

Beta 0.4 no escribe Game Mode, Game DVR ni planes de energía. Si existe un backup creado por Beta 0.1/0.2/0.3, la interfaz mantiene la restauración legada validada en modo fail-closed.

## Verificación de ejecución

El workflow oficial hace Restore, Build, Publish y un **runtime smoke launch** que inicia realmente `HyperBoost.exe` en Windows, comprueba que siga vivo y falla si aparece `crash.log`. Después genera checksum y ZIP.

## Seguridad y límites deliberados

HyperBoost no desactiva Microsoft Defender, Integridad de memoria/VBS, Secure Boot, firewall, mitigaciones, servicios críticos ni anti-cheat. Tampoco usa BCD/HPET, core parking forzado, afinidad manual, limpieza agresiva de memoria o cambios globales de scheduler.

No cambia gráficos, resolución, drivers, perfiles NVIDIA/AMD, clocks, voltaje, potencia, ventiladores, DLSS/FSR/XeSS, Frame Generation, sincronización ni FPS caps. No aplica BIOS, EXPO/XMP o Curve Optimizer.

## Compilar

Requiere .NET 10 SDK:

```powershell
dotnet publish src/HyperBoost/HyperBoost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

La compilación oficial de GitHub Actions genera `HyperBoost.exe`, `SHA256SUMS.txt` y un ZIP verificable.

> La beta aún no está firmada con un certificado Authenticode, por lo que SmartScreen puede mostrar una advertencia. Verifica el SHA-256 del artefacto antes de distribuirlo.
