# Starting at sign-in

Developer notes: why DockSense starts from a task, what the start path may and may not do, and the measurements
behind it.

## How it starts

- The Run entry passes `--autostart`, the sign-in task `--autostart --logon-task`. Both fire at sign-in; the
  second one finds the mutex and exits without showing the window.
- The task is per user: `DockSense-<SID>` in the task root, the same shape as OneDrive's per-user task, no admin
  (`LogonTaskDefinition` for the name and XML, `LogonTaskRegistration` for Task Scheduler through late-bound COM).
- A task start first asks `AutoStart.AllowsLogonTaskStart`: it exits when Windows would not start the Run entry,
  and a task without a Run entry for this copy is removed. `StartupEntry.AllowsLogonTask`: the task may only
  start DockSense when the entry is on and not held back, so Windows' switches stay in charge.
- The Run entry stays as the setting and the fallback: Settings > Apps > Startup and Task Manager keep working,
  and DockSense still starts if the task fails.
- Development builds never register the task unless "Start with Windows" is turned on in them.
- Task defaults that would hurt: no start on battery, a 72 h execution limit, priority 7 (below normal, inherited
  by the programs a profile starts). `LogonTaskDefinition` sets all three.
  `ITaskService.NewTask(0).XmlText = xml` validates task XML without registering anything.

## The fast path

Windows reads `StartupToGamingHome` once, when the shell starts. `SignInFastPath.Run` therefore writes the
current mode's value before anything else is loaded.

- **Keep that path free of WPF, of the gaming experience API and of anything else that asks the shell.** It uses
  only the registry, the settings file and the hardware probe. The task-start check reads the registry only
  (`ReadStatus(askWindows: false)`), and `RemoveMistakenXboxModeKey` runs only in the normal startup, not ahead of
  the write.
- Whatever needs the shell waits until 20 s after sign-in: startup programs and toasts through
  `SignInSession.ShellWait` (no wait when the sign-in time is unknown), the live FSE switch through
  `FsePlanner.StartupDelay` (10 s when it is unknown, as before 0.3.3).
- Sign-in time comes from `WTSQuerySessionInformation(WTSSessionInfo)`, `LogonTime` at offset 200 of `WTSINFOW`.
- The log line `Sign-in start in <mode> mode` lists each step (`runtime`, `startup check`, `settings`, `adapters`,
  `displays`, `write`); a slow gaming API call logs `Windows took N s to ...`.

## The tray icon before the shell

Before the shell is up, `Shell_NotifyIcon` fails and H.NotifyIcon's `Create` throws `InvalidOperationException`
("TryCreate failed."); it can be called again. `TrayIconService` retries every 2 s.

## Measurements

**Windows runs the HKCU Run key late** (event logs, 2026-11-18):

| Boot | Sign-in | Shell | Run key |
|---|---|---|---|
| Into FSE | 19:47:54.7 | logon tasks 19:47:58 | 19:48:31 (33 s after the shell) |
| Desktop | 19:51:30 | other apps' logon tasks 19:51:31.9, `explorer.exe` 19:51:36.3 | 19:52:53 (83 s) |

With that late start, a PC shut down as a handheld and switched on at the eGPU booted into FSE, and DockSense
left it 50 s after sign-in. That is why 0.3.3 added the task and the fast path (114–227 ms after process start
in a Debug build).

**0.3.3 at real sign-ins: the write came far too late** (log, 2026-11-19 to 11-22). The task did start DockSense
0.5–3.5 s after sign-in, but the write followed 15.7, 16.6, 17.2, 23.4 and 52.4 s after process start (one eGPU
boot left FSE again 60 s after sign-in). With the shell up the same code takes 0.1–0.3 s. The cause is **not
proven**; the suspects were the two `FullScreenExperience.IsSupported` calls ahead of the write (`gamemode.dll`
activates WinRT classes from `twinui.pcshell.dll` and `SystemSettings.DataModel.dll`; on the handheld boot the
write came 2 s after the Xbox app started). 0.3.4 took them off the path. Warm, in a Debug build, the path takes
170 ms in total, of which runtime 70 and settings (JSON) 85.

**The margin over the shell is small and varies.** On the two boots of 2026-11-18 a logon task ran 3–4 s before
the shell. Don't budget with that: on 2026-11-22 (handheld, FSE boot) the shell's logon tasks (Shell-Core 62170)
began 0.8 s after sign-in and DockSense's process 0.5 s after it, a lead of 0.3 s; on 2026-11-21 (eGPU, desktop
boot) 3.5 s and 0.7 s, a lead of 2.8 s.

**Still open:** the step times at a real sign-in on a build after 0.3.3, and whether the write now beats the
shell. The release is a compressed single-file build, whose start-up time at sign-in is not measured either.
