# HyperBoost

Beta abierta de un optimizador para Windows 11 enfocado en estabilidad, latencia y FPS sin modificar la GPU.

## Beta 0.1

- Detecta Windows, CPU, placa, BIOS, RAM, discos y GPU.
- La GPU es estrictamente de solo lectura.
- Diagnostica RAM a velocidad base, módulo único, memoria insuficiente y estado de discos.
- Perfil reversible: Game Mode, captura en segundo plano y plan Alto rendimiento.
- Copia local antes de cualquier cambio y botón de restauración.
- Interfaz en español; ejecutable autónomo para Windows 11 x64.

## Límites deliberados

No cambia gráficos, resolución, drivers, perfiles NVIDIA/AMD, clocks, voltaje, potencia, ventiladores, DLSS/FSR/XeSS, Frame Generation, sincronización ni FPS caps. No aplica BIOS, EXPO/XMP o Curve Optimizer.

## Compilar

```powershell
dotnet publish src/HyperBoost/HyperBoost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Las copias y el registro quedan en `%LOCALAPPDATA%\HyperBoost`.
