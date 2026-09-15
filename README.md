# HyperBoost

Beta abierta de un optimizador para Windows 11 enfocado en estabilidad, frame pacing y FPS sin reducir calidad gráfica ni debilitar la seguridad del equipo.

## Beta 0.6 · UI / UX Refresh

Beta 0.6 cambia **solo la interfaz y la experiencia de uso**. La lógica de rendimiento, Gaming Persona, Bottleneck Gate, PresentMon, benchmark A/B, restauración y políticas temporales permanecen iguales a Beta 0.5.

### Cambios visuales

- navegación lateral por secciones;
- nueva pantalla de Inicio con flujo Analiza → Juega → Comprueba;
- tema oscuro de alto contraste;
- tipografía y jerarquía visual más legibles;
- cards para separar acciones, estados y explicaciones;
- botones primarios/secundarios/peligro coherentes;
- textos técnicos reescritos para ser más claros sin perder precisión;
- ayuda contextual mediante paneles desplegables `ⓘ`, sin ventanas modales adicionales;
- Gaming Persona ordenada como flujo de tres pasos;
- Benchmark A/B presentado como captura guiada con plan, progreso y resultados separados;
- vista “Qué modifica” más transparente, separando lo que HyperBoost puede hacer de lo que deliberadamente no toca.

### Alcance congelado

Beta 0.6 **no añade optimizaciones nuevas**. No cambia umbrales, allowlists, EcoQoS, Memory Priority, eventos de foreground, lógica OFF/ON, métricas ni criterios estadísticos. El objetivo de esta versión es que la misma Beta 0.5 sea más fácil de entender, usar y auditar.

## Beta 0.5 · A/B Benchmark + Event-driven Bottleneck Gate

Beta 0.5 mantiene la arquitectura event-driven de 0.4 y añade una capa que faltaba: **medir si HyperBoost realmente mejora el rendimiento**. No se publica una ganancia de FPS por existir una optimización; se compara HyperBoost OFF vs ON sobre el mismo juego y se informa cuando la diferencia no supera el ruido de la propia prueba.

### Benchmark A/B integrado

- Usa PresentMon 2.5.1 oficial para capturar frametimes del PID del juego.
- El binario de PresentMon está fijado por versión y HyperBoost verifica su SHA-256 antes de ejecutarlo: `9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191`.
- PresentMon se distribuye con su licencia MIT y no se usa inyección de DLL en el juego.
- La captura empieza dentro del juego con `CTRL+SHIFT+F11` y termina automáticamente tras la duración configurada.
- Si el juego pierde foreground durante una pasada, HyperBoost cancela y descarta esa muestra.
- Los CSV originales de PresentMon se conservan para auditoría.

### Diseño de la comparación

La sesión usa pares OFF/ON contrabalanceados para reducir sesgo por calentamiento, boost y deriva térmica:

- Par 1: OFF → ON
- Par 2: ON → OFF
- Par 3: OFF → ON

Tres pares de 30 segundos son la configuración recomendada. También existen 2 y 5 pares, y capturas de 15/30/60 segundos.

Las políticas que forman la condición ON se congelan al crear la sesión. No pueden cambiarse entre pasadas. El reporte registra:

- versión de HyperBoost;
- versión y SHA-256 de PresentMon;
- juego/PID inicial;
- número de pares y duración;
- EcoQoS ON/OFF;
- Memory Priority ON/OFF;
- orden exacto de las pasadas.

### Métricas

Por cada pasada se calculan:

- FPS promedio;
- 1% Low = `1000 / p99 frametime`;
- 0.1% Low = `1000 / p99.9 frametime`;
- frametime mediano, p95, p99 y p99.9;
- stutters y tasa de stutter.

Un stutter se considera, de forma conservadora, un frame mayor que `max(33.333 ms, 2.5 × mediana)`.

El parser prefiere `DisplayedTime` cuando PresentMon lo entrega de forma suficiente; si no, usa una fuente de cadencia compatible y registra explícitamente cuál se utilizó en cada pasada.

### Señal vs ruido

HyperBoost no considera una diferencia pequeña como ganancia automáticamente. Calcula la variación relativa entre las propias pasadas OFF y exige que una mejora supere `1.5 ×` ese ruido, con umbrales mínimos conservadores. También exige consistencia de signo en al menos 2/3 de los pares.

El veredicto puede ser:

- `MEJORA MEDIBLE`;
- `SEÑAL POSITIVA, AÚN NO CONCLUYENTE`;
- `SIN MEJORA DEMOSTRABLE`;
- `REGRESIÓN MEDIBLE`.

Los resultados se guardan en `%LOCALAPPDATA%\HyperBoost\Benchmarks` como CSV crudos, resultados por pasada, JSON de sesión y reporte de texto.

## Beta 0.4/0.5 · motor por eventos

- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` sustituye el antiguo polling periódico de foco.
- La salida del juego se vigila mediante evento del proceso.
- `CreateMemoryResourceNotification` se usa cuando Windows expone notificaciones de memoria baja/alta.
- Una pérdida breve de foco usa únicamente un temporizador one-shot de 2 segundos para Gaming Persona normal.
- Si el hook foreground no está disponible, HyperBoost no usa polling como fallback.

Cuando el juego está foreground, HyperBoost intenta `PROCESS_MODE_BACKGROUND_BEGIN` únicamente sobre **su propio proceso** y vuelve a normal al perder foco.

## Bottleneck Gate

Antes de permitir EcoQoS en una pasada ON o en Gaming Persona, HyperBoost observa:

- CPU por proceso;
- I/O por proceso;
- RAM/presión de memoria;
- GPU por proceso mediante contadores WDDM de Windows, solo lectura.

EcoQoS solo queda autorizado para PID concretos de la allowlist segura que demostraron actividad CPU/I/O. Si no existe evidencia de competencia útil, el Gate puede decidir **no hacer cambios**.

Memory Priority sigue limitada a 5 → 4 bajo presión real de RAM y solo para la allowlist segura.

Navegadores, launchers, Discord, audio, OBS, overlays, anti-cheat, NVIDIA y utilidades de periféricos no se modifican automáticamente.

## HyperBoost deja a Windows 11

- Game Mode;
- Xbox Game Bar / Game DVR y captura;
- optimizaciones para juegos en ventanas, Auto HDR y VRR;
- HAGS y preferencias gráficas por aplicación;
- planes/modos de energía y scheduling del juego;
- política de temporizador del proceso foreground.

## HyperBoost deja a NVIDIA App/driver

- perfiles 3D y optimización gráfica;
- DLSS overrides y Frame Generation;
- Smooth Motion;
- Low Latency / Reflex;
- G-SYNC, resolución y frecuencia;
- límites de FPS;
- ShadowPlay / Instant Replay / Highlights;
- auto tuning, clocks, potencia y ventiladores.

## Seguridad y restauración

HyperBoost no desactiva Defender, VBS/Integridad de memoria, Secure Boot, firewall, mitigaciones, servicios críticos ni anti-cheat. No usa BCD/HPET, core parking forzado, afinidad manual, limpieza agresiva de memoria, inyección en juegos ni cambios globales de scheduler.

Las políticas temporales conservan PID + tiempo de creación, restauración ownership-aware y reintento si una restauración falla temporalmente.

Beta 0.6 no añade escrituras nuevas. La restauración legada de betas antiguas sigue validada en modo fail-closed.

## CI / verificación

El workflow oficial realiza:

1. Restore y Build;
2. Publish self-contained x64;
3. descarga PresentMon 2.5.1 desde el release oficial y verifica su SHA-256;
4. valida la superficie CLI requerida de PresentMon;
5. ejecuta un self-test sintético del parser y analizador A/B;
6. ejecuta un runtime smoke launch real de `HyperBoost.exe`;
7. genera checksums y ZIP verificable.

## Compilar

Requiere .NET 10 SDK:

```powershell
dotnet publish src/HyperBoost/HyperBoost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> La beta aún no está firmada con un certificado Authenticode, por lo que SmartScreen puede mostrar una advertencia. Verifica el SHA-256 del artefacto antes de distribuirlo.
