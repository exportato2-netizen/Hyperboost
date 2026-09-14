# HyperBoost

Beta abierta de un optimizador para Windows 11 enfocado en estabilidad, frame pacing y FPS sin reducir calidad gráfica ni debilitar la seguridad del equipo.

## Beta 0.3.2 · Gaming Persona reversible y conflict-aware

HyperBoost no intenta controlar funciones que Windows 11 o NVIDIA App administran mejor. El juego seleccionado sirve únicamente como referencia de foco; HyperBoost no cambia su prioridad, QoS, timers, gráficos ni driver.

### HyperBoost sí hace

- EcoQoS temporal únicamente para una allowlist pequeña de procesos secundarios no críticos conocidos.
- Si un proceso ya controla explícitamente su propio `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`, HyperBoost no lo pisa.
- Al restaurar EcoQoS, solo revierte el bit que todavía reconoce como propio; cambios posteriores de Windows u otra aplicación se conservan.
- Memory Priority adaptativa: solo bajo presión real de RAM (>=75% usada o <6 GB disponibles), y únicamente de 5 a 4 para la misma lista segura.
- Memory Priority solo vuelve de 4 a 5 si el valor actual sigue siendo el aplicado por HyperBoost; si otra autoridad lo cambió, se respeta.
- Tolerancia de 2 segundos ante pérdidas breves de foco para evitar ciclos de restauración/reaplicación por overlays o Alt+Tab corto.
- Las restauraciones que fallen temporalmente no pierden su snapshot y pueden reintentarse.
- Protección PID + tiempo de creación para evitar modificar procesos reutilizados.
- Escáner de interferencias de solo lectura con CPU, working set e I/O por proceso usando tiempo monotónico real.
- El escáner verifica también el tiempo de creación del proceso entre muestras.
- El análisis de hardware tolera fallos WMI parciales: un sensor/clase inaccesible no invalida el resto del informe.

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

Beta 0.3.2 ya no escribe Game Mode, Game DVR ni planes de energía. Si existe un backup creado por Beta 0.1/0.2/0.3, la interfaz mantiene una opción de restauración. Antes de escribir, el backup se valida contra una allowlist de los cuatro valores de registro históricos y contra un GUID de energía válido. Un backup malformado se conserva intacto y no se aplica.

## Verificación de ejecución

El workflow oficial ahora hace Restore, Build, Publish y además un **runtime smoke launch**: inicia realmente `HyperBoost.exe` en Windows, comprueba que siga vivo y falla la build si aparece `crash.log`. Después genera checksum y ZIP.

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
