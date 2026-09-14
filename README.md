# HyperBoost

Beta abierta de un optimizador para Windows 11 enfocado en estabilidad, frame pacing y FPS sin reducir calidad gráfica ni debilitar la seguridad del equipo.

## Beta 0.3.1 · Gaming Persona sin duplicar Windows/NVIDIA

HyperBoost ya no intenta controlar funciones que Windows 11 o NVIDIA App administran mejor. El juego seleccionado sirve únicamente como referencia de foco; HyperBoost no cambia su prioridad, QoS, timers, gráficos ni driver.

### HyperBoost sí hace

- EcoQoS temporal únicamente para una allowlist pequeña de procesos secundarios no críticos conocidos (por ejemplo sincronizadores/servicios de escritorio de usuario).
- Si un proceso ya controla explícitamente su propio `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`, HyperBoost no lo pisa.
- Memory Priority adaptativa: solo bajo presión real de RAM (>=75% usada o <6 GB disponibles), y únicamente de 5 a 4 para la misma lista segura.
- Restauración automática al perder foco, detener la Persona o cerrar HyperBoost.
- Protección PID + tiempo de creación para evitar modificar procesos reutilizados.
- Escáner de interferencias de solo lectura con CPU, working set e I/O por proceso.

### HyperBoost deja a Windows 11

- Game Mode.
- Xbox Game Bar / Game DVR y captura.
- Optimizaciones para juegos en ventanas, Auto HDR y VRR.
- HAGS y preferencias gráficas por aplicación.
- Planes/modos de energía y decisiones de scheduling del juego.
- Política de resolución de temporizador del proceso foreground.

### HyperBoost deja a NVIDIA App/driver

- Optimización gráfica del juego y perfiles 3D.
- DLSS overrides y modelos de Frame Generation.
- Smooth Motion.
- Low Latency / Reflex y cola de render.
- G-SYNC, resolución, frecuencia y opciones de pantalla.
- Max Frame Rate y Background Application Max Frame Rate.
- ShadowPlay, Instant Replay, Highlights y overlay.
- Auto tuning de GPU, clocks y rendimiento.

Navegadores, launchers, Discord, audio, OBS, overlays, anti-cheat, NVIDIA y utilidades de periféricos no se modifican automáticamente. Pueden aparecer en el escáner solo para diagnóstico.

## Compatibilidad con betas anteriores

Beta 0.3.1 ya no escribe Game Mode, Game DVR ni planes de energía. Si existe un backup creado por Beta 0.1/0.2/0.3, la interfaz mantiene una opción de restauración para devolver esos valores al estado original.

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
