# TDP override

Developer notes on setting the processor's power limits per mode: what works on the hardware, how DockSense gets
the rights for it, and what was measured.

## How it works

Per profile `tdpWatts` (null = the vendor software decides). The user's decisions of 2026-11-26: custom watts
only, no Silent / Performance / Turbo presets, and an elevated helper task instead of running DockSense elevated.

- The value goes to the processor's SMU through the [PawnIO](https://pawnio.eu/) driver, the way RyzenAdj,
  Universal x86 Tuning Utility and G-Helper's fallback do it. All three limits (sustained, fast, slow) get the
  same value.
- Only on boards in `TdpDevice` with a processor in `AmdSmuPowerCommands` (both in `Core/Tdp.cs`). The range runs
  from the manufacturer's minimum to the firmware's own burst limit for the power source; never more.
- Above the manufacturer's maximum the UI asks first and keeps a warning under the setting. The user asked for
  that warning; keep it.
- PawnIO is not shipped or installed by DockSense. Its AMD module, `Assets/PawnIO/RyzenSMU.bin`, is embedded
  unchanged (LGPL-2.1; provenance in the README next to it). PawnIO only loads modules signed by its author.

## The elevated helper task

PawnIO refuses callers without administrator rights (opening it fails with error 5).

- `Platform/TdpHelper` registers a task without triggers, `DockSense-TDP-<SID>`: run level highest, and a security
  descriptor that lets the user read and run it but not change it. Registering takes one UAC prompt
  (`DockSense.exe --install-tdp-helper <SID>`); removing it another (`--remove-tdp-helper <SID>`, from Settings).
- DockSense starts the task with the watts as its parameter (`$(Arg0)`). The task runs
  `DockSense.exe --set-tdp <watts>` (`Platform/TdpWriter`): it trusts nothing it is handed (a known board, a known
  processor, the value clamped to the device's range), shows no UI and writes no files. Its exit code
  (`TdpWriteResult`) is the result, read back from the task.
- Before any limit is sent, the SMU's test message must answer correctly (argument plus one). A wrong answer
  means these are not the registers of this processor, and nothing is set.
- **The task runs this executable elevated, so it is only registered when nobody without administrator rights
  can replace that file** (Debug builds skip the check). Never weaken this: from anywhere else a program could
  put its own code in its place and get administrator rights without a prompt.
  - The check reads the permissions (`TdpHelper.LocationIsProtected`, decided by `Core/ElevatedLocation`): the
    file and its folder must be owned by, and writable only for, administrators, SYSTEM or TrustedInstaller, and
    no folder above may let anyone else delete or rename it or what is inside it, change permissions or take
    ownership (a folder that can be renamed can have another one put in its place).
    Creating new folders further up does not count: Windows lets everyone do that at the root of a drive.
  - It runs twice: before DockSense asks for approval, and again in the elevated `--install-tdp-helper` itself,
    because that command can be started by anyone. A write test would not do there: elevated, it always succeeds.
- The helper takes the mutex hardware tools share for SMU access (`Global\Access_PCI`). If another tool holds it
  for more than 2 s, nothing is sent (`TdpWriteResult.Busy`) and DockSense tries again, up to three times.
- Registering the task from an agent shell is blocked; the user does that step in the UI.

## Keeping it in force, and giving it back

- `DockSenseService` sends the value when the mode's profile is applied, and again 3 s after anything that may
  have replaced it: a power source change, resume, a power plan change, Armoury Crate writing its throttle
  settings (`Platform/ArmouryCrate` watches that registry key). One write runs at a time.
- `tdp.json` remembers the value in force for this boot. A restart of the PC clears every limit.
- A mode without an override then gets the TDP handed back: DockSense re-sends the performance mode Armoury Crate
  has selected through ATKACPI (`0x00120075`), and the firmware loads that mode's own limits again. If that
  fails, the value stays on record, so Status keeps showing it and the next apply tries again. Removing the
  helper task hands back as well; when a write is still on its way, once that write has returned.
- What an override needs (the driver, the task) is looked at again whenever the user acts on it, so installing
  PawnIO while DockSense is open is noticed without a restart.

## The Mode profiles row

A switch (off = the vendor software decides) and, while it is on, `Views/OptionSlider`: a slider with an input
box for the watts. It passes the choice on only when it is final (slider let go, 400 ms after a key or a click
on the track, Enter or leaving the box), because a choice can open a dialog or start the helper.

The user wants a slider plus an input box for the TDP, a dropdown for iGPU memory, and no "Don't change" entry
mixed in with the watt values (2026-11-26).

## Measurements

On the RC74XA (BIOS 303, Ryzen AI Z2 Extreme = Strix Point, CPUID family 1Ah model 24h) on 2026-11-26, at the eGPU
on AC with Armoury Crate SE and `amdpmf` running. Package power was read from
`\Energy Meter(RAPL_Package0_PKG)\Power` (no admin) under an all-core load.

**The ASUS limits are ignored.** DEVS to `0x001200A3` (SPL), `0x001200A0` (sPPT), `0x001200C1` (fPPT) returns 1 and
changes nothing: not in Turbo, not after re-sending the mode first, not when rewritten every 250 ms, not below
the mode's own cap in Silent. DSTS never reads a value back (`0x00010000`). This is the route of G-Helper,
Handheld Companion's "OEM" mode and the Linux `asus-armoury` driver; the cause here is unknown.

**The performance mode works**, unelevated, with Armoury Crate running. DEVS `0x00120075`:

| Value | Mode | Package power under load |
|---|---|---|
| 2 | Silent | holds 13 W |
| 0 | Performance | 26 W, then 21 W, then settles at 17 W (the 17 / 21 / 26 defaults) |
| 1 | Turbo | about 45 W (35 / 45 / 55) |
| 4 | **not "Manual" here** | behaves as Performance |

Armoury Crate did not undo a mode within 30 s, and its registry did not follow
(`HKLM\SOFTWARE\ASUS\ARMOURY CRATE Service\ThrottlePlugin\ROG ATKStatus`: `ThrottleModeOnAC` 3, `ThrottleModeOnDC`
2; 0 Windows, 1 Silent, 2 Performance, 3 Turbo, 4 Manual). That key saw no writes in 4 idle minutes.

**The SMU works.** Through PawnIO 2.2.0 and `RyzenSMU.bin` of PawnIO.Modules 0.2.12: MP1 mailbox `0x3B10928` /
`0x3B10978` / `0x3B10998`, then `0x14` (sustained), `0x15` (fast), `0x16` (slow) in milliwatts. 20 W held the
package at exactly 20 W for 30 s and again after 25 s idle. The whole path ran from a Debug build: one UAC
prompt registered the helper task, the task then set 20 W and 25 W without a prompt (each held flat under load),
and switching the override off handed back to about 43 W.

**Above 35 W it does not sustain on this device.** With 45 W set (twice, 90 and 110 s of load): 44 W until the CPU
reached 93 °C (after 58 s, and 41 s from a warmer start), then 35 W flat at 80 °C, the same curve as stock Turbo.
Having the helper send 45 W again while it sat at 35 W changed nothing, so the limits were not replaced:
something else holds sustained power at 35 W when hot (probably the skin-temperature limiter; not identified).

- What a Universal x86 Tuning Utility preset adds made no difference (all accepted with status 1): `stapm-time`
  64 s (`0x18`) and `slow-time` 128 s (`0x17`), then also `tctl-temp` and `cHTC-temp` 95 (`0x19`, `0x63`). The drop
  to 35 W came after 68 s and, from a warm start, 35 s. UXTU's `apu-skin-temp` is not offered for Strix Point, and
  its VRM current limits were deliberately not sent.
- Not tried: the STT power limit (`skin-temp-limit`, `0x4a`), which no UXTU preset sends. **Do not go after that
  limiter unasked: it is a thermal protection.**
- The user chose to keep the range up to the ceiling anyway ("it might work on other devices") and to say so in
  the warning and the READMEs.

**Not measured:** a real power source change, sleep, a mode switch in Armoury Crate, battery, and any device but
the RC74XA. Phoenix and Hawk Point (the Z1 series: RC71, RC72) use the commands RyzenAdj and UXTU list for them.

## Manufacturer ranges

From the Linux `asus-armoury.h` (SPL / sPPT / fPPT):

| Board | On AC | On battery |
|---|---|---|
| RC74XA, RC73XA (ROG Xbox Ally X) | 7–35 / 13–45 / 19–55 W | the same |
| RC71, RC72 (ROG Ally, Ally X) | 7–30 / 15–43 / 15–53 W | 7–25 / 15–30 / 15–35 W |
