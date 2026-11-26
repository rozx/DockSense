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
  (`DockSense.exe --install-tdp-helper <SID>`). Removing it, from Settings, takes another, for Windows' own
  `schtasks.exe`: removal must also work from a copy that is not in a protected place (the task may be a
  leftover), and such a copy must not be what gets administrator rights.
  The task counts as ready only while it is enabled, starts this copy and is still set to run with the highest
  privileges. Settings offers **Remove** for any task under that name, also a disabled or outdated one.
- DockSense starts the task with the watts as its parameter (`$(Arg0)`). The task runs
  `DockSense.exe --set-tdp <watts>` (`Platform/TdpWriter`): it trusts nothing it is handed (a known board, a known
  processor, the value clamped to the device's range), shows no UI and writes no files. Its exit code is the
  result, read back from the task: a `TdpWriteResult`, or after a write that went through, 100 plus the watts
  it sent (`TdpHelperTask.OutcomeOf`). So what DockSense records is what the helper sent, clamped for the power
  source at that moment.
- Neither elevated command writes to the log: it is in the user's folders.

### Where the helper may run from

**The task runs this executable elevated, so it is only registered when nobody without administrator rights can
replace that file** (Debug builds skip the check; they are not distributed). Never weaken this.

The check reads the permissions (`TdpHelper.LocationIsProtected`, decided by `Core/ElevatedLocation`, unit tested):

- The installation must be in a folder of its own under Program Files (either one). The task keeps the path
  for as long as it exists, and those are the folders that stay administrator-managed whatever is installed or
  removed inside them.
- The file and its folder must be owned by, and changeable only for, administrators, SYSTEM or TrustedInstaller.
- The folder directly above must not let anyone else create folders: the task keeps the path, so a path that
  could be brought back after the installation is moved or removed is not acceptable. Directly under Program
  Files passes; directly under the root of a drive does not.
- No folder further up may let anyone else delete or rename it, change its permissions or take ownership.
- The path must be a plain one on a fixed local drive, without a junction or link anywhere in it.

It runs twice: before DockSense asks for approval, and again in the elevated `--install-tdp-helper` itself. A
write test would not do there: elevated, it always succeeds.

### What the helper sends

- It takes the mutex hardware tools share for SMU access (`Global\Access_PCI`). If another tool holds it for more
  than 2 s, or the mutex cannot be opened at all, nothing is sent (`TdpWriteResult.Busy`), and DockSense tries
  again, up to three times.
- The SMU's test message must answer correctly (argument plus one) before any limit is sent. A wrong answer means
  these are not the registers of this processor, and nothing is set.
- The value is clamped for the power source right before it is sent, after the wait for the mutex.
- One request: wait until the mailbox is idle, write the arguments, clear the response, write the command, wait
  for the response. The response is cleared only once the arguments are in place, so a failure before the
  command leaves the mailbox as it was found.
- The results tell apart what DockSense has to do afterwards: `Rejected` and `Failed` changed nothing;
  `PartlyApplied` means a limit may be in force (an earlier one went in, or one was handed to the SMU and no
  answer came back).

## Keeping it in force, and giving it back

- `DockSenseService` sends the value when the mode's profile is applied, and again 3 s after anything that may
  have replaced it: a power source change, resume, a power plan change, Armoury Crate writing its throttle
  settings (`Platform/ArmouryCrate` watches that registry key and reopens it if it is replaced). One write runs
  at a time.
- `tdp.json` remembers the value in force for this boot; a restart of the PC clears every limit. The record is
  written before a value is sent, marked as pending, and nothing is sent when it cannot be written. If DockSense
  is closed while the helper runs, the next start counts that value as possibly in force and can give it back.
- A mode without an override gets the TDP handed back: DockSense re-sends the performance mode Armoury Crate has
  selected through ATKACPI (`0x00120075`), and the firmware loads that mode's own limits again. Only Silent,
  Performance and Turbo can be re-sent; for anything else (Manual, Windows, no Armoury Crate) it is Turbo on AC
  and Performance on battery, and the READMEs say so. If the hand-back fails, the value stays on record, Status
  keeps showing it, and the next apply tries again, also at a start after an update.
- Removing the helper task hands back as well, also while switching is paused; when a write is still on its way,
  once that write has returned.
- DockSense waits for the helper longer than the task may run (75 s against the task's limit of one minute), so
  that giving up means the task is no longer there to set anything late. A helper that did not report back, or
  ended with a code it does not use, is `TdpWriteResult.Unconfirmed`: the value stays on record as possibly in
  force, and Status shows it as unconfirmed.
- After `PartlyApplied` the mix is not left behind: DockSense puts it on record and hands the TDP back at once.
- What an override needs (the driver, the task) is looked at again whenever the user acts on it, so installing
  PawnIO while DockSense is open is noticed without a restart. A value whose last attempt failed is sent again
  then.

## The Mode profiles row

A switch (off = the vendor software decides) and, while it is on, `Views/OptionSlider`: a slider with an input
box for the watts. It passes the choice on only when it is final (slider let go, 400 ms after a key or a click
on the track, Enter or leaving the box, leaving the page), because a choice can open a dialog or start the
helper. Typed text has to be one plain number, with a unit after it at most.

- A profile that never had a value starts at the highest one the manufacturer allows on either power source, so
  switching the override on never asks about exceeding the maximum.
- The question about going above the maximum names the watts and covers that value and lower ones, in every
  profile (the dialog says so). The answer is kept with the settings, per board model (`tdpConfirmedBoard` /
  `tdpConfirmedWatts`) and never lowered for the same model. If the edit also prompts for the helper setup,
  the answer is saved only after that setup succeeds. A higher value asks again. A value above the maximum that was not confirmed for this model, for example one that arrived with a
  settings file from another model, is not sent: `TdpPlanner.Plan` treats it as no value, a warning says so,
  and the note under the setting has a Confirm button (choosing the same value again changes nothing, so the
  slider cannot ask). The manufacturer's maximum belongs to the model, so the agreement does too: a settings
  file copied to another unit of the same model carries it along.
- A value saved for another device stays in the settings, whatever this device supports.

The user wants a slider plus an input box for the TDP, a dropdown for iGPU memory, and no "Don't change" entry
mixed in with the watt values (2026-11-26).

## Known limits

Raised in review of PR #12 and not solved; decide before treating the feature as more than experimental.

- **The helper trusts more than its own file.** The location check covers the executable and its path. The
  release is a single-file build that unpacks native libraries into the user's temporary folder at start, and a
  process started in the user's session inherits the user's environment. The helper path itself loads no
  unpacked library, but nothing enforces that. A small separate helper with nothing but administrator-controlled
  files behind it would be the robust form.
- **The task stays when the installation goes.** Remove the helper under Settings before moving or deleting
  DockSense; the uninstall steps in the READMEs say so. Requiring Program Files keeps the old path from being
  made again without administrator rights.
- **One record per user.** The limit applies to the whole PC, the record of it is in one user's profile. A second
  user signing in during the same boot does not know of the first user's limit.
- **What DockSense records is what the helper says it sent.** It is not read back from the hardware.
- **The result is read from the task, not from the run.** Task Scheduler reports the last run of a task. Runs
  from one DockSense are kept apart in time; a second program starting the same task at the same moment could
  still be mistaken for it.

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
