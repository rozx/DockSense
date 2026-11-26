# Xbox full screen experience (FSE, "Xbox mode")

Developer notes on what Windows stores, what its API does, and how startup apps behave in FSE.

## Settings

`HKCU\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration`:

- `StartupToGamingHome` (0 / 1): whether Windows boots into FSE. Windows reads it **once, when the shell starts**
  (see [`sign-in-start.md`](sign-in-start.md) for what that means for timing).
- `GamingHomeApp`: the home app (the user's machine has AnyFSE set).
- `SystemDialogResults\EnterGamingPostureConfirmation`: 0 = ask, **1 = restart the PC**, 2 = start now.
  DockSense never enters live when it is 1, unless `EnterGamingPostureConfirmation_NoReboot` = 1. The exit value
  is `ExitGamingPostureConfirmation` (1 = no prompt).
- `FseSettings.IsAvailable` means "the `GamingConfiguration` key exists". DockSense therefore **never creates that
  key**: 0.2.1 did, which made FSE look available on PCs without it (`AutoStart.RemoveMistakenXboxModeKey` undoes
  that).

## Live state and switching

The public gaming experience API (`gamingexperience.h`, API set `api-ms-win-gaming-experience-l1-1-0`):
`IsGamingFullScreenExperienceSupported`, `IsGamingFullScreenExperienceActive` and
`CanSetGamingFullScreenExperience` return BOOL; `SetGamingFullScreenExperience(BOOL)` returns an HRESULT. It can
block on Windows' confirmation dialog, and `E_ABORT` means the user stayed. There is also a change notification.

- The calls work unelevated. Don't use `Win+F11`.
- Right after sign-in these calls can take many seconds (see the sign-in notes), so nothing on the start path
  may use them.
- `FsePlanner` decides the live target: only the entered mode's `FseApplyImmediately` and target count. The
  per-mode "exit when leaving" option was removed after 0.3.3 as redundant; an old `fseExitOnLeave` key in a
  config is ignored.

## Startup apps in FSE

- When `StartupToGamingHome` = 1, Windows holds Run-key apps back until the first switch to the desktop.
  Settings > Apps > Startup then shows a per-app dropdown instead of the On/Off switch.
- "Start at log in" is stored as DWORD 1, named after the Run value, in `GamingConfiguration\Startup\Run` (HKCU).
  Verified on 2026-11-13: with it, DockSense started 34 s after sign-in in FSE; without it, never.
- "Off" disables the entry through `Explorer\StartupApproved\Run`.
- The Settings dropdown does not read the value back on build 26300 (it always shows "Start when exiting to
  desktop"), so don't trust that UI.
- `AutoStart` writes the value for DockSense (and, as said above, never creates the `GamingConfiguration` key).

## Windows' own switch for a Run entry

`Explorer\StartupApproved\Run\<name>` (12 bytes). An odd first byte means disabled: this build writes `01` plus a
FILETIME for off and all zeros for on, older builds `03` / `02`. No value means enabled.

`StartupEntry` (Core) combines the Run value, this switch and the FSE value: on / off / disabled by Windows /
another copy, and "waits for the desktop" in Xbox mode. The Settings switch shows the result and a note explains
a mismatch.
