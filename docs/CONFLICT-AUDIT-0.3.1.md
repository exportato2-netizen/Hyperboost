# HyperBoost 0.3.1 — Windows/NVIDIA conflict audit

Goal: HyperBoost must not duplicate Windows 11 or NVIDIA App controls. It should focus on reducing resource competition around a game, not override graphics, capture, Game Mode, or driver policy.

## Windows-native controls left to Windows
- Game Mode
- Xbox Game Bar / Game DVR capture
- Windowed game optimizations / Auto HDR / VRR
- Power plans and effective Game Mode power profile
- Foreground timer policy

## NVIDIA-native controls left to NVIDIA App/driver
- DLSS overrides and Frame Generation models
- Smooth Motion
- Low Latency / Reflex-related driver controls
- 3D profiles and game optimization
- G-SYNC / display controls
- Max Frame Rate / Background Application Max Frame Rate
- ShadowPlay / Instant Replay / recording
- GPU performance tuning

## HyperBoost-owned scope
- Read-only interference scan by process (CPU, RAM working set, I/O)
- Conservative EcoQoS for known non-critical background sync/desktop processes only
- Adaptive Memory Priority 5->4 for those same background processes only during real memory pressure
- Exact restoration on focus loss/stop
- No game-process priority, timer, GPU, driver, capture, Game Mode, power plan, affinity, CPU Set, security or graphics overrides

## Conflict guards
- Never touch processes outside the safe background allowlist automatically.
- Never override a process that already explicitly controls PROCESS_POWER_THROTTLING_EXECUTION_SPEED.
- Do not apply EcoQoS to browsers, launchers, Discord, audio, capture, overlays, anti-cheat or peripheral utilities.
- Keep legacy backup restoration so users of older betas can restore Game Mode/GameDVR/power-plan changes, but do not apply those settings in 0.3.1+.
