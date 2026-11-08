# The Game Mode CPU throttle on AMD handhelds with an eGPU

## Symptom

- Device: ASUS ROG Xbox Ally X (RC74XA, Ryzen AI Z2 Extreme, BIOS 303). It is docked over USB4 to a ROG XG Mobile (GC34) with an RTX 5090 Laptop GPU and a 5120×1440 display.
- Forza Horizon 6 runs at ~50 FPS. Task Manager shows the CPU at "100%", but package power is only **10–12 W** and every core sits at about **2.0–2.2 GHz**. That includes the Zen 5 cores, which can boost to 5.0 GHz.
- The RTX 5090 sits at **~55% utilization** (91 W of 150 W). The game is CPU-bound.
- Armoury Crate reports Turbo mode at **35 W**, and its log shows that 35 W was applied successfully.
- **Alt-tabbing out of the game** brings power back to **~30 W** and the clock to **~3.7 GHz** within a second.

## Measurements

All values come from the built-in Windows counters `\Energy Meter(RAPL_Package0_PKG)\Power` and `\Processor Information(_Total)\% Processor Performance`. No admin rights are needed. A script logged them once per second together with the foreground process.

| Foreground window | Samples | CPU package | Effective clock | RTX 5090 util |
|---|---|---|---|---|
| `forzahorizon6` | 60 | **12.3 W** | **2.19 GHz** | **59%** |
| Windows Terminal (game still rendering in the background) | 64 | 27.4 W | 3.74 GHz | 88% |

During the run, the following were ruled out:
- **Power plan:** it stayed Turbo with the power mode at Best performance, and Windows' "% Performance Limit" stayed at 100%.
- **Thermals:** the ACPI thermal zone read 63 °C, with throttle reasons 0.
- **Vendor TDP:** Armoury Crate applied 35 W.
- **Third-party TDP tools:** none were running.

## Root cause

Windows has **processor power management (PPM) profiles**. The `GameMode` profile (`D4140C81-EBBA-4E60-8561-6918290359CD`) is active when the Game Mode toggle is on and the user is playing a game. Microsoft documents it as an **OEM opt-in that is deployed through provisioning packages**, and its settings are defined **per power scheme**:
https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configure-processor-power-management-options

On this device the AMD provisioning package (service `AmdPpkgSvc`) provisions these GameMode overrides:

| Setting | Normal (Armoury "Turbo" plan) | GameMode profile |
|---|---|---|
| Processor energy performance preference (EPP) | **20** (favor performance) | **100** (favor power saving) |
| EPP, efficiency class 1 | 20 | 100 |
| Heterogeneous thread scheduling policy | 2 (prefer performant cores) | **4 (prefer efficient cores)** |
| Heterogeneous short-running thread policy | 2 | 4 |

That explains everything observed:
- The CPU picks its most efficient clocks (~2 GHz) and stays fully busy.
- Game threads land on the Zen 5c cores. Zen 5c threads were 70–78% busy, Zen 5 threads only ~40%.
- The overrides drop away the moment the game loses focus.

In handheld play this is probably intentional. The CPU and the 890M iGPU share one 17–35 W budget, so a frugal CPU leaves more power for the GPU. With an eGPU, the CPU becomes the bottleneck and the policy works against you.

### Where the values live

```
HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\
  54533251-82be-4824-96c1-47b60b740d00\        (processor settings subgroup)
    36687f9e-e3a5-4dbf-b1dc-15eb381c6863\       (EPP)
      d4140c81-ebba-4e60-8561-6918290359cd\     (GameMode profile)
        381b4222-f694-41f0-9685-ff5bb260df2e    ProvAcSettingIndex = 100   (Balanced personality)
        961cc777-2547-4f9d-8174-7d86181b8a7a    ProvAcSettingIndex = 100   (Best power efficiency overlay)
        ded574b5-45a0-4f42-8737-46345c09c238    ProvAcSettingIndex = 100   (Best performance overlay)
```

`powercfg /q` and `powercfg /qh` only show the default profile, so these values are invisible to the usual tools.

## The way out: a High Performance-personality plan

The GameMode overrides are provisioned only for the **Balanced** personality and the power-mode overlays. A plan duplicated from **High Performance** (`8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`, personality 1) has no GameMode overrides. Its own settings stay in force (default EPP 0).

A/B test with Game Mode **on** and the game in the foreground, 60 s each:

| Active plan | CPU package | Effective clock |
|---|---|---|
| Duplicate of High Performance | **29–45 W** | **4.0–4.4 GHz** |
| Armoury Crate "Turbo" (Balanced personality) | 10–15 W | 2.2–2.6 GHz |

Notes:
- On Modern Standby devices, Windows hides High Performance-personality plans from `powercfg /list` and Control Panel. They still exist, and `powercfg /setactive` works.
- Vendor software can switch the plan back. Armoury Crate re-applies its own plan on AC/DC changes, mode changes and helper restarts. DockSense re-asserts the plan it wants.
- Vendor TDP (e.g. Armoury Crate's 35 W Turbo) is set in firmware and is independent of the Windows plan.

## Manual workarounds (without DockSense)

1. **Turn off Game Mode** while docked: Settings → Gaming → Game Mode. Turn it back on for handheld play.
2. **Use a High Performance-based plan** while docked:
   ```powershell
   powercfg /duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c   # prints the new GUID
   powercfg /changename <new-guid> "eGPU Docked"
   powercfg /setactive <new-guid>
   ```
