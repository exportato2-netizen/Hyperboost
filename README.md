# HyperBoost

HyperBoost es una beta abierta para Windows 11 centrada en estabilidad, frame pacing y FPS. Su regla es simple:

> Detecta → Explica → Actúa → Mide → Aprende

Si una intervención no puede demostrar beneficio, HyperBoost no afirma que lo haya. **No hacer cambios es un resultado correcto.**

## Beta 0.7 · Game Intelligence

Beta 0.7 cierra el ciclo `Aprende` sobre la metodología endurecida de 0.6.1, sin añadir políticas de optimización:

- Game Hub crea perfiles locales por ejecutable/juego;
- Evidence Library conserva sesiones, capturas, huellas, políticas y resultados completos;
- solo agrega sesiones compatibles y separa cualquier cambio de contexto;
- clasifica evidencia como `SIN DATOS`, `PRELIMINAR`, `EVIDENCIA BAJA`, `EVIDENCIA MODERADA`, `EVIDENCIA ALTA`, `RESULTADOS CONTRADICTORIOS` o `CONTEXTO CAMBIÓ`;
- genera recomendaciones para el esquema **exacto** medido: EcoQoS, Memory Priority experimental o la combinación; una combinación nunca se atribuye a una política individual;
- Gaming Readiness usa `LISTO`, `ATENCIÓN` o `INTERFERENCIA DETECTADA`, sin score arbitrario;
- reutiliza el Bottleneck Gate para una lectura puntual de CPU, GPU read-only, RAM, background, captura y sincronizadores; no añade polling permanente;
- conserva las métricas de stalls/spikes como metadata, pero **no** implementa Stutter Attribution estable en 0.7.

Todo permanece local en `%LOCALAPPDATA%\HyperBoost`. No se descargan perfiles universales de Internet y no se usa machine learning opaco.

Los resultados nuevos usan `schemaVersion: 4` para añadir una etiqueta opcional de ajustes/versión del juego. La biblioteca usa `schemaVersion: 1`, escritura atómica y backup de la versión anterior. Los JSON anteriores se importan sin borrarlos: schema 3 se conserva con la limitación de contexto que corresponda; esquemas más antiguos sin huella suficiente quedan como histórico no agregable.

## Evidence Library Methodology

### Compatibilidad histórica

La referencia es la sesión completa más reciente del mismo juego y esquema de políticas. Para sumar una sesión deben coincidir:

- familia compatible de metodología A/B y build de Windows (la versión completa de HyperBoost siempre se registra);
- CPU, GPU, driver y RAM total (tolerancia de 0,5 GB para lectura WMI);
- ruta, tamaño y timestamp del ejecutable;
- PresentMon y su hash;
- fuente de frametime;
- tipo de benchmark y duración por pasada;
- resolución declarada;
- esquema exacto de políticas ON;
- etiqueta de ajustes/versión del juego, cuando se declaró.

Una diferencia conocida produce `CONTEXTO CAMBIÓ`; la sesión se conserva pero no suma. Metadata crítica ausente produce `metadata insuficiente`. Si ambas sesiones omiten únicamente la etiqueta de ajustes, pueden orientar dentro de la misma huella técnica, pero la evidencia se limita a `EVIDENCIA BAJA`. Si solo una declara resolución o ajustes, no se mezclan.

### Regla de niveles

Una sesión es elegible para evidencia persistente normal cuando tiene al menos 6 pares, intervalos pareados estables y no está marcada preliminar, ruidosa o inválida.

| Nivel | Regla implementada |
|---|---|
| SIN DATOS | No existen sesiones para esa política exacta |
| PRELIMINAR | Existen datos, pero ninguna sesión elegible; incluye todos los resultados de 3 pares |
| EVIDENCIA BAJA | Al menos 1 sesión elegible |
| EVIDENCIA MODERADA | Al menos 2 sesiones elegibles, 12 pares compatibles, al menos 1 escenario integrado/ruta repetible, consistencia direccional ≥ 2/3 y contexto conocido exacto |
| EVIDENCIA ALTA | Al menos 4 sesiones elegibles, 24 pares compatibles, al menos 3 escenarios integrados/ruta repetible, consistencia ≥ 75%, cero sesiones ruidosas y contexto conocido exacto |
| RESULTADOS CONTRADICTORIOS | Hay al menos una mejora medible y una regresión medible compatibles |
| CONTEXTO CAMBIÓ | La huella actual no tiene sesiones agregables, aunque existe histórico |

La consistencia usa cuatro clases legibles: mejora medible, regresión medible, señal positiva y beneficio no demostrado. El nivel expresa calidad/repetición de evidencia; el resultado expresa dirección. Por eso `SIN BENEFICIO DEMOSTRABLE` puede tener varias sesiones sin convertirse en la afirmación “el efecto es cero”.

### Gaming Readiness

Readiness es una foto puntual y reutiliza la misma muestra del Gate:

- `INTERFERENCIA DETECTADA`: RAM ≥ 85% o menos de 3 GB libres, CPU secundaria agregada ≥ 5%/un proceso ≥ 3%, u otro proceso ≥ 15% en su motor GPU más ocupado;
- `ATENCIÓN`: RAM ≥ 75% o menos de 6 GB, actividad CPU/GPU secundaria menor pero visible, I/O ≥ 5 MB/s, herramienta de captura/sincronizador observado o GPU WDDM indeterminada;
- `LISTO`: ninguna de esas señales apareció en la ventana.

La GPU permanece en solo lectura. Un hallazgo dice “observado” o “coincidió con”; no atribuye causalidad de stutter. EcoQoS puede reducir competencia de ejecución CPU, pero no limita directamente I/O ni red.

## Beta 0.6.1 · Benchmark Integrity (base estadística)

Beta 0.6.1 endurece el benchmark A/B de Beta 0.6 sin añadir políticas de optimización nuevas.

- `p99 frametime` es la métrica primaria de cola.
- `1% Low = 1000 / p99`; se muestra por legibilidad, pero no cuenta como evidencia independiente.
- niveles Rápido (3 pares), Estándar (6) y Extendido (9); 6 es el recomendado.
- 3 pares siempre producen `RESULTADO PRELIMINAR`.
- intervalos de confianza del 95% mediante bootstrap de deltas OFF/ON pareados, nunca de frames individuales como si fueran IID.
- una única fuente de frametime queda bloqueada al aceptar la primera pasada de la sesión.
- separación entre `Severe Stall`, `Relative Frame Spike` y `Extreme Stall`.
- huella del entorno, tipo de escenario y resolución informada por el usuario.
- medición ligera de overhead de HyperBoost, Bottleneck Gate, escáner GPU WMI y PresentMon.
- Recovery Journal write-ahead, idempotente y ownership-aware para las políticas temporales.
- Memory Priority aparece como `EXPERIMENTAL · beneficio aún no demostrado`.

Los resultados de 0.6.1 usaban `schemaVersion: 3`. Los CSV y JSON de betas anteriores no se modifican ni eliminan.

## Benchmark Methodology

### Comparación A/B y contrabalanceo

Cada par compara HyperBoost `OFF` y `ON` dentro de un mismo escenario. El orden alterna para reducir sesgo de calentamiento, boost o deriva temporal:

- par impar: OFF → ON;
- par par: ON → OFF.

La condición ON congela EcoQoS y Memory Priority al crear la sesión. La inferencia usa **un delta por par completo**. Los miles de frames consecutivos no se tratan como miles de observaciones independientes porque presentan autocorrelación.

| Nivel | Pares | Interpretación |
|---|---:|---|
| Rápido | 3 | Exploración; siempre preliminar |
| Estándar | 6 | Configuración recomendada para una conclusión normal |
| Extendido | 9 | Confirmación o escenarios variables |

### Escenario y comparabilidad

Se recomienda el benchmark integrado del juego. Si no existe, debe repetirse el mismo save, punto de inicio y ruta. Durante una sesión se conservan resolución, ajustes, escalado, Frame Generation, límite de FPS y driver. El usuario declara uno de estos tipos:

- benchmark integrado;
- ruta repetible;
- escena manual;
- stress test.

La sesión registra versión de HyperBoost, build de Windows, CPU, GPU/driver, RAM, juego, PID inicial, metadata barata del ejecutable, PresentMon 2.5.1 y SHA-256, políticas ON, fuente de frametime, resolución informada, fecha, duración, pares y tipo de prueba. Esta metadata se captura fuera de la ventana crítica.

### Fuente de frametime

La primera captura válida elige, por orden, `DisplayedTime`, `MsBetweenDisplayChange`, `MsBetweenPresents`, `FrameTime` o `MsBetweenAppStart`. Debe tener valores válidos en al menos el 80% de las filas. Todas las pasadas posteriores deben conservar esa fuente; si deja de cumplir el mínimo, la pasada queda `PASADA INVÁLIDA` y debe repetirse. HyperBoost nunca sustituye silenciosamente la fuente dentro de una sesión.

### Métricas

Primarias:

- FPS promedio;
- p99 frametime, donde menos es mejor.

Representación amigable:

- 1% Low, calculado como `1000 / p99`. Es exactamente la misma familia estadística que p99 y no añade un segundo voto.

Secundarias:

- p95 frametime;
- Severe Stall rate;
- Relative Frame Spike rate.

Indicativas cuando la cola es corta:

- p99.9 frametime;
- 0.1% Low = `1000 / p99.9`.

El reporte registra frames totales y el número aproximado de muestras de la cola p99.9. Si hay menos de 20, la métrica aparece como `INDICATIVA` y no participa en el veredicto principal.

### Stalls, spikes y freezes

`Severe Stall` conserva el umbral robusto `max(33.333 ms, 2.5 × mediana)`.

`Relative Frame Spike` compara cada frame con la mediana de hasta 31 frames anteriores, después de un mínimo de 15. El umbral es `max(1.8 × mediana local, mediana local + 5 ms)`. Una excursión continua cuenta una vez, lo que evita convertir una transición sostenida de carga en decenas de spikes.

Los valores no finitos, menores de 0.1 ms o mayores de 10 segundos se registran como inválidos. Los frames reales de 200/500/1000 ms se conservan: cuentan en duración, promedio, Severe Stall y `ExtremeStallCount`. Los eventos de 200 ms o más se separan de percentiles cuando quedan al menos 30 frames no extremos, y el reporte registra explícitamente cuántos se excluyeron. Esto evita que un único freeze domine una cola corta sin ocultarlo ni winsorizarlo.

### Intervalos, ruido y decisión

HyperBoost calcula un bootstrap percentil determinista de 20.000 remuestras sobre los **deltas de pares**. Con menos de 6 pares muestra `INTERVALO NO ESTABLE / EVIDENCIA PRELIMINAR`.

Para una conclusión fuerte deben concordar:

- magnitud práctica mínima;
- consistencia de signo en al menos dos tercios de los pares;
- intervalo pareado fuera de cero;
- ausencia de una métrica primaria contradictoria;
- variabilidad razonable de la línea base y de los deltas.

La variabilidad OFF combina una estimación robusta basada en MAD con una fracción conservadora del CV clásico. Con 3 OFF solo sirve como contexto y nunca habilita evidencia fuerte.

La regla implementada es auditable: `ruido = max(1.4826 × MAD / mediana, 0.5 × CV muestral)`. La magnitud mínima es `max(1.0%, ruido)` para FPS promedio y `max(1.5%, ruido)` para p99. Se exige signo favorable en `ceil(2/3 × pares)` y un intervalo 95% que excluya cero. La sesión se marca demasiado ruidosa antes de emitir una conclusión fuerte si el ruido OFF supera 5% en FPS, 12% en p99, o si la desviación estándar de los deltas supera 8/15 puntos porcentuales respectivamente. Si FPS y p99 entregan señales decisivas opuestas, el resultado es `SIN MEJORA DEMOSTRABLE`, no una afirmación global.

Veredictos posibles:

- `RESULTADO PRELIMINAR`;
- `MEJORA MEDIBLE`;
- `SEÑAL POSITIVA, AÚN NO CONCLUYENTE`;
- `SIN MEJORA DEMOSTRABLE`;
- `REGRESIÓN MEDIBLE`;
- `SESIÓN DEMASIADO RUIDOSA`;
- `PASADA INVÁLIDA`.

`SIN MEJORA DEMOSTRABLE` significa que la sesión no separó el efecto del ruido; no significa que el efecto sea exactamente cero.

## Gaming Persona y Bottleneck Gate

El motor es event-driven: usa eventos de foreground, salida de proceso y notificaciones nativas de memoria. No añade un monitor periódico permanente.

HyperBoost puede:

- observar CPU, RAM, I/O y GPU mediante contadores WDDM de solo lectura;
- poner su propio proceso en modo background mientras el juego tiene foco;
- aplicar EcoQoS temporal a PID concretos de una allowlist conservadora cuando existe actividad CPU medible;
- aplicar Memory Priority `5 → 4` únicamente bajo presión real de RAM, sobre la misma allowlist y como función experimental.

EcoQoS afecta scheduling, execution QoS y comportamiento energético/CPU. **No limita directamente disco ni red.** Un proceso con I/O alto puede reportarse, pero eso no convierte a EcoQoS en un limitador de I/O.

No se incluyen automáticamente servicios Windows, procesos dentro de `svchost`, navegadores, launchers, Discord, audio, OBS, overlays, anti-cheat, NVIDIA ni utilidades de periféricos.

## Recovery Journal

Antes de cada escritura temporal, HyperBoost persiste PID, tiempo de creación, proceso, propiedad, valor original, valor esperado y timestamp. En el siguiente inicio:

1. verifica PID + tiempo de creación + nombre;
2. lee el valor actual;
3. restaura solo si el valor completo sigue siendo exactamente el esperado por HyperBoost;
4. si el PID fue reutilizado o otro software cambió el valor, no sobrescribe nada;
5. limpia la entrada de forma idempotente.

No usa Task Scheduler. La restauración normal sigue ocurriendo al perder foco, detener la Persona o cerrar la aplicación.

## Límites deliberados

HyperBoost no implementa overclock, clocks, voltajes, power limits, ventiladores, BIOS/EXPO/Curve Optimizer, BCD/HPET, timer hacks, core parking agresivo, afinidad forzada, prioridad High/Realtime, debloat, standby-list cleaners, desactivación de servicios críticos/Defender/VBS, registry packs, network tweaks genéricos, perfiles NVIDIA, DLSS/Reflex/Frame Generation/HAGS/Game Mode, DLL injection, hooks internos ni anti-cheat bypass.

Windows, NVIDIA y el juego conservan autoridad sobre sus respectivas opciones. El lector GPU es exclusivamente diagnóstico.

`OptimizationService` existe solo para restaurar de forma fail-closed backups creados por betas antiguas; Beta 0.6.1 no crea esos backups ni aplica esas configuraciones.

## CI y artifact

El workflow oficial ejecuta:

1. Restore y Build en .NET 10;
2. Publish self-contained `win-x64`;
3. descarga PresentMon 2.5.1 oficial y verifica SHA-256;
4. valida su CLI;
5. ejecuta los self-tests sintéticos de Benchmark Integrity, Recovery Journal y Game Intelligence;
6. realiza un runtime smoke launch;
7. genera `SHA256SUMS.txt` y `HyperBoost-Beta-0.7-win-x64.zip`;
8. publica el artifact `HyperBoost-Beta-0.7-win-x64` y partes de transporte verificables para evitar truncamientos en clientes móviles.

## Compilar

Requiere .NET 10 SDK:

```powershell
dotnet publish src/HyperBoost/HyperBoost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

La beta aún no está firmada con Authenticode; SmartScreen puede mostrar una advertencia. Verifica `SHA256SUMS.txt` antes de ejecutarla.
