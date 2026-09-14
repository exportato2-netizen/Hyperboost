# HyperBoost

Beta abierta de un optimizador para Windows 11 enfocado en estabilidad, frame pacing y FPS sin reducir calidad gráfica ni debilitar la seguridad del equipo.

## Beta 0.2

- Detecta Windows, CPU, placa, BIOS, RAM, discos, plan de energía y herramientas/overlays comunes.
- La GPU es estrictamente de solo lectura.
- Diagnostica RAM con información WMI más prudente y aclara que el estado de disco WMI no sustituye SMART.
- Perfil reversible y transaccional: activa Game Mode y desactiva captura en segundo plano; verifica las escrituras y revierte si algo falla.
- Ya no fuerza el plan Alto rendimiento global.
- Boost de sesión por juego: usa APIs documentadas de Windows para solicitar HighQoS solo al proceso elegido, con restauración exacta del estado previo.
- Opcionales por sesión: prioridad Above Normal únicamente si el proceso estaba en Normal y respetar sus solicitudes de resolución de temporizador.
- Acceso directo a Configuración de gráficos para usar las optimizaciones de juegos en ventanas de Windows 11.
- Copia local antes de cambios persistentes y restauración compatible con copias de Beta 0.1.
- Registro local de cambios y errores en `%LOCALAPPDATA%\HyperBoost`.
- Interfaz en español; ejecutable autónomo para Windows 11 x64.

## Seguridad y límites deliberados

HyperBoost no desactiva Microsoft Defender, Integridad de memoria/VBS, Secure Boot, firewall, mitigaciones, servicios críticos ni anti-cheat. Tampoco usa ajustes BCD/HPET, core parking forzado, afinidad manual o limpieza agresiva de memoria.

No cambia gráficos, resolución, drivers, perfiles NVIDIA/AMD, clocks, voltaje, potencia, ventiladores, DLSS/FSR/XeSS, Frame Generation, sincronización ni FPS caps. No aplica BIOS, EXPO/XMP o Curve Optimizer.

El boost por proceso no inyecta DLL ni modifica archivos del juego. Si Windows o un anti-cheat niegan acceso al proceso, HyperBoost muestra el error y no intenta evadir esa protección.

## Por qué no se fuerza Alto rendimiento

Windows 11 dispone de un perfil de administración del procesador asociado a Game Mode y los procesadores modernos ajustan dinámicamente rendimiento/consumo. Un plan global Alto rendimiento puede aumentar consumo y temperatura sin asegurar una mejora de FPS, especialmente en portátiles y plataformas modernas. HyperBoost 0.2 prioriza cambios por proceso y reversibles.

## Compilar

Requiere .NET 10 SDK:

```powershell
dotnet publish src/HyperBoost/HyperBoost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

La compilación oficial de GitHub Actions genera `HyperBoost.exe`, un `SHA256SUMS.txt` y un ZIP. El proyecto usa .NET 10 LTS para evitar quedar sobre .NET 8 cerca de su fin de soporte.

> La beta aún no está firmada con un certificado Authenticode, por lo que SmartScreen puede mostrar una advertencia. Verifica el SHA-256 del artefacto antes de distribuirlo.
