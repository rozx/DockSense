# Self-update

Developer notes on how DockSense replaces itself, and the parts of it that are a contract with older versions.

Code: `Core/Updates.cs` (`UpdateMode`: Off / Notify / Automatic, config key `updates`; `ReleaseVersion`: 0.2
equals 0.2.0; `ReleaseFeed`: parses GitHub's latest-release JSON; `UpdatePlanner`: is it newer, which asset, which
URLs are trusted, when the daily check is due), `Services/UpdateService`, `Platform/UpdateInstaller`. State:
`%LOCALAPPDATA%\DockSense\update.json` and `updates\`.

## Download

A daily query of the GitHub latest release, download of the `-win-<arch>.zip` asset, a size and SHA-256 check
against the digest GitHub publishes, and extraction of `DockSense.exe` into `updates\`.

## Why the running DockSense does not swap its own file

**Never replace the file of a running single-file DockSense that still has work to do.** The runtime reads
assemblies from the exe on demand, so after a swap the next assembly load reads the wrong file and fails (seen as
`FileNotFoundException: System.Diagnostics.Process`). It only shows when the two builds differ.

So the running DockSense only starts the installer and exits.

## The installer (a contract)

The installer is the **downloaded exe itself**:

```
<download> --install-update <installed exe> <pid>[,<elevated pid>] [args...]
```

It waits for the pid to exit, writes itself to `DockSense.exe.new`, renames the installed file to `.old` and
`.new` to the real name, then starts it with the args plus `--after-update`.

**Older versions call this on newer ones, so its arguments are a contract: never change their meaning.**

## Protected folders

When the install folder cannot be written without admin (the user's own install is in
`C:\Program Files (x86)\DockSense`), the installed exe runs elevated as `--apply-update <release zip> <pid>`.
This is the one place DockSense asks for UAC: once per update.

- It trusts nothing it is handed: it hashes the zip (one handle, held to the last extracted byte), asks GitHub's
  release **list** whether that digest is a release newer than itself (not only the latest: a newer one may have
  come out since the download), waits for the pid, then replaces its own file from the zip.
- Everything it does after the swap already ran once before it.
- The unelevated installer waits for the version to change and relaunches.

## Around the swap

- When the update did not go in, the installer starts the old version with `--update-failed`: the card says so,
  and the next check runs at the first tick, whatever the last check says.
- Automatic mode starts the installer from `Program.Main` at the next start, before any UI (writable folders
  only). The Status card installs on click. DockSense never restarts itself unasked.
- `--restarted=<mode>`: the mode whose profile the previous instance had applied. If it is still the mode, the
  new instance applies nothing (the saved plan state, owned or handed over, carries on); if it changed during the
  restart, that is a normal mode change.
- `--paused`: start paused (passed on when DockSense restarts itself while paused).

## Testing

- Development builds (a `DockSense.dll` next to the exe) cannot self-update; they link to the release page.
- Test with two single-file publishes of different `-p:Version` (see the release workflow's publish command),
  and with builds of **different code**, not two publishes of the same commit: the failure above only shows when
  the builds differ.
- The real UAC path can only be exercised from 0.3 on, because the download must know `--install-update`.
