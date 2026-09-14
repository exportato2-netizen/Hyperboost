# HyperBoost

Beta abierta de un optimizador para Windows 11 enfocado en estabilidad, frame pacing y FPS sin reducir calidad gráfica ni debilitar la seguridad del equipo.

## Beta 0.3 · Gaming Persona

La Beta 0.3 cambia el enfoque desde una colección de tweaks hacia un **estado gaming temporal y reversible**. El usuario selecciona un juego ya abierto y HyperBoost arma una Gaming Persona que solo entra en acción mientras ese PID está realmente en primer plano.

- Mantiene el boost HighQoS por proceso de Beta 0.2, pero ahora se activa con el foco del juego y se restaura automáticamente al hacer Alt+Tab.
- Puede aplicar EcoQoS a una lista conservadora de procesos secundarios conocidos. Por defecto solo incluye sincronizadores/procesos de escritorio no críticos; navegadores y helpers de launchers son un grupo opcional.
- Discord, audio, OBS, overlays, anti-cheat y herramientas de periféricos/RGB quedan excluidos explícitamente de la política automática.
- Memory Priority adaptativa: únicamente bajo presión real de RAM (≥75% usada o <6 GB disponibles), procesos secundarios elegibles pueden bajar de prioridad Normal (5) a Below Normal (4). Al desaparecer la presión se restaura el valor original.
- Cada modificación temporal se asocia a PID + tiempo de creación para evitar tocar un PID reutilizado.
- No mantiene handles abiertos a juegos ni procesos secundarios durante la sesión.
- Añade un escáner de interferencias de solo lectura que toma dos muestras y ordena procesos por CPU, working set e I/O. Su objetivo es encontrar competencia real antes de ampliar la política automática.
- La Persona no mata, suspende, desinstala ni detiene servicios.
- El perfil persistente de Beta 0.2 sigue siendo transaccional: Game Mode + captura en segundo plano, con backup y rollback.
- GPU estrictamente de solo lectura.
- Interfaz en español y ejecutable autónomo para Windows 11 x64.

## Seguridad y límites deliberados

HyperBoost no desactiva Microsoft Defender, Integridad de memoria/VBS, Secure Boot, firewall, mitigaciones, servicios críticos ni anti-cheat. Tampoco usa BCD/HPET, core parking forzado, afinidad manual, limpieza agresiva de memoria o cambios globales de scheduler.

No cambia gráficos, resolución, drivers, perfiles NVIDIA/AMD, clocks, voltaje, potencia, ventiladores, DLSS/FSR/XeSS, Frame Generation, sincronización ni FPS caps. No aplica BIOS, EXPO/XMP o Curve Optimizer.

Gaming Persona usa APIs públicas de Windows por proceso. Si Windows o un anti-cheat niegan acceso, ese proceso se omite; HyperBoost no intenta elevar privilegios ni evadir protecciones.

## Por qué el enfoque es adaptativo

Un tweak global puede ayudar a un PC y empeorar otro. Beta 0.3 solo aplica políticas temporales cuando el juego está en primer plano y utiliza umbrales conservadores para memoria. El escáner de interferencias permite observar qué procesos realmente compiten por CPU/I/O/RAM antes de añadir nuevas reglas.

## Compilar

Requiere .NET 10 SDK:

```powershell
dotnet publish src/HyperBoost/HyperBoost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

La compilación oficial de GitHub Actions genera `HyperBoost.exe`, `SHA256SUMS.txt` y un ZIP verificable.

> La beta aún no está firmada con un certificado Authenticode, por lo que SmartScreen puede mostrar una advertencia. Verifica el SHA-256 del artefacto antes de distribuirlo.
