# iGPU memory (UMA frame buffer)

Developer notes on the two ways DockSense sets the memory reserved for the built-in GPU, and the rules around
them. Either way a change applies only after a **restart**.

## ASUS: the ATKACPI interface

- `\\.\ATKACPI` opens without admin. `DeviceIoControl(0x0022240C)`, DSTS `0x53545344` (read) / DEVS `0x53564544`
  (write), device `0x000600C4` (VRAM_MEM). Source: G-Helper `AsusACPI.cs`.
- On the RC74XA (BIOS 303) the reply status is `0x00030012`: 512 MB units, Auto plus 0.5–16 GB (raw values 1, 2,
  4, 6 … 32), set to 8 GB (raw 0x10). Option `i` sits at reply offset `6 + 2i` (`IgpuMemoryOptions` decodes it).
- **Only write raw values the firmware lists.**
- **Never write the legacy `0x000600C1` (APU_MEM).** It is unsupported here and has unsafe raw values.

## AMD driver: Variable Graphics Memory through ADLX (experimental)

For devices without ATKACPI, such as the MSI Claw A8 (issue #10). `Platform/AmdAdlx`: `amdadlx64.dll` from
System32, `ADLXInitializeWithIncompatibleDriver`, `IADLXSystem3` slot 6, vtable slots from the SDK's C headers;
every use starts and ends ADLX on a thread of its own.

**What is known and what is not.** On the Ally X (driver 32.0.32015.7029, ADLX 1.5.0.124, read while at the eGPU
on 2026-11-26) the chain works up to `IsSupported`, which answers **false**: this driver does not offer VGM. So
ADLX is no fallback for ASUS devices. On an MSI Claw A8 (issue #10, 0.4.0, also ADLX 1.5.0.124) **reading works**:
current and default are Custom 8 GB, and the list has the presets Minimum / Medium / High (0.5, 8, 16 GB) plus
custom sizes 0.5 GB and 1 to 16 GB in 1 GB steps. **The write (`SetOption`, slot 7) has still never run on real
hardware**: 0.4.0 crashed before reaching it (next paragraph). AMD's docs and sample say `SetOption` restarts the
PC by itself, Claw Control's code assumes it does not, and whether it needs admin is unknown (DockSense has no
elevation for it; a refusal is reported and nothing changes).

**One ADLX start per load of the library.** Starting ADLX again after `ADLXTerminate` while `amdadlx64.dll` is
still in the process crashes inside `ADLXInitializeWithIncompatibleDriver` with an access violation (0xC0000005),
which .NET cannot catch: the process is gone without a log line. It does not matter whether the second start is
on the same thread, or goes through `ADLXInitializeWithIncompatibleDriver2`. It reproduces on the Ally X, where
VGM is not even offered. In 0.4.0 the library was bound with `DllImport` and so never unloaded: the read after the
start was the first session, and the first store or **Copy diagnostic report** was the second and took DockSense
down (issue #10: "hang and then crash" on **Change and restart**). AMD's `ADLXHelper` frees the library after
every terminate, and so does `AmdAdlx.WithMemoryInterface` now (`NativeLibrary.TryLoad` / `Free` around each
session, no `DllImport`). If the library is still loaded after the free, further sessions are refused instead of
risking the crash.

The user chose on 2026-11-26 to ship the write blind as an **experimental** feature, said so in the UI and the
READMEs with an invitation to report issues on GitHub. Keep that label until a report confirms it. The user does
not want to ask the reporter to run probe builds.

Hence the rules for this path (`DockSenseService.IgpuMemoryViaAmdDriver`):

- It is used only where the ASUS interface is absent.
- It is read in the background after the shell wait, never on the start path.
- Every write needs the user's go-ahead for a restart (`IgpuMemoryPlanner` `alwaysAsk`; only **Change and
  restart** is offered).
- The size DockSense stored is remembered per boot (`IgpuMemoryBootState.StoredRaw`), because the driver is not
  known to report a pending size. The log says whether `SetOption` came back.
- Only the driver's custom sizes are offered (no Auto). A driver preset that comes to the size a profile asks
  for does not count as that size stored (`IgpuMemoryOptions.CurrentIsFixed`).
- A store has three outcomes: stored, refused, or **no answer within 10 s** (the wait is on the UI thread).
  Without an answer nothing prepared for the restart is taken back, DockSense does not restart the PC, and
  nothing else is stored or prepared until the driver answers (`_amdStoreUnanswered`). The outcome is picked up
  then (`FollowLateAmdStoreAsync`: stored means the restart toast, refused means the restart target and the
  sign-in setting go back).
- A read gives up after 15 s and is tried once more 30 s later. Only one ADLX session runs at a time: while a
  call that timed out is still inside the library, further calls fail at once.

Debug builds: `DOCKSENSE_SIMULATE_AMD_VGM=1` simulates such a driver (it reads a made-up list, storing always
fails, the real state file is left alone) to look at the flow on other hardware.

## When a size is stored

`IgpuMemoryPlanner`, per profile `igpuMemoryApply`:

- `Automatic` stores the size when the mode starts. It stays the default on ASUS (the user's decision,
  2026-11-26).
- `Ask` only offers it. Putting back the size in effect is stored without asking, except on the AMD driver path,
  where `alwaysAsk` makes every store ask.

The question is a toast with **Change** / **Change and restart** (same tag as the restart toast, so one replaces
the other) plus a Status card. Its buttons act only while `IgpuMemoryOffer` is still the offer the toast was
shown for. It is shown even when mode change notifications are off.

## Restarting

Restarting Windows is always the user's click (`Platform/SystemPower`).

- With a confirmation dialog: the Status card's **Restart now** and **Change and restart**, and the tray's
  "Restart for `<mode>`".
- Without one, because the button is the go-ahead: **Restart now** on the restart toast and **Change and
  restart** on the question toast.
- Windows starts DockSense for a click on an old toast, so a toast button acts only while what it offered still
  stands (a size still pending, the same offer still open).
- While an eGPU is connected, the restart dialogs (the tray's "Restart for `<mode>`" included) and toasts add a
  neutral sentence for PCs that do not start with one attached: an MSI Claw A8 with an AOOSTAR AG02 hangs at
  boot. "Restart for eGPU" without an eGPU connected says to connect it after sign-in instead
  (`RestartFor_EgpuHint`). **Never tell everyone to unplug**: the Ally X boots fine at the XG Mobile.

## "Restart for `<mode>`"

`IgpuRestartPlanner` lists the other modes whose iGPU memory size differs from the one in effect. The tray offers
"Restart for `<mode>`" for them, which stores that size and the mode's FSE sign-in setting first, so one restart
is enough.

`AwaitedMode`: after such a restart the PC may come back up in another mode, because the eGPU gets connected only
after sign-in. That mode's size then stays for up to 5 minutes instead of being written back at once.

- The record of that restart, `igpu-restart.json`, is written before the size is stored. It carries the time the
  restart was asked for and, once the wait runs, the time the wait ends, so a restart of DockSense does not end
  the wait.
- A boot counts as coming from the restart when it began within 2 hours of it, without counting boots, because a
  restart can pass through several.

## State files (`%LOCALAPPDATA%\DockSense`)

- `igpu-memory.json`: the size in effect in this boot session, and what DockSense stored through the driver.
- `igpu-restart.json`: the restart asked for through "Restart for `<mode>`" (see `AwaitedMode`).
