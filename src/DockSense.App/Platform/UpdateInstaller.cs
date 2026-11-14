using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using DockSense.Core;

namespace DockSense.App.Platform;

internal enum InstallResult
{
    Installed,

    /// <summary>The user said no at the administrator prompt.</summary>
    Declined,

    Failed,
}

/// <summary>
/// Downloads a release, verifies it and puts it in place of the running executable.
/// <para>
/// DockSense is one self-contained file. Windows lets a running executable be renamed but not overwritten, so the
/// update renames it to <c>DockSense.exe.old</c> and copies the new file to the old name. The next start runs the
/// new version and removes the old file.
/// </para>
/// <para>
/// In a folder the user cannot write to (such as Program Files), the same step runs in a second, elevated
/// DockSense process (<see cref="ApplyArgument"/>) after one administrator prompt. Nothing else ever runs elevated.
/// </para>
/// </summary>
internal static class UpdateInstaller
{
    /// <summary><c>DockSense.exe --apply-update &lt;new exe&gt; &lt;sha256&gt;</c>: replace this executable, then exit.</summary>
    public const string ApplyArgument = "--apply-update";

    /// <summary>Passed to the new version: the old process is still exiting, so wait for it.</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>Passed when DockSense replaces a running instance: nothing about the mode changed, so start quietly.</summary>
    public const string RestartedArgument = "--restarted";

    private const string ExecutableName = "DockSense.exe";
    private const string ProductName = "DockSense";
    private const int ErrorCancelled = 1223;

    public static string UpdatesDirectory { get; } = Path.Combine(Log.DataDirectory, "updates");

    /// <summary>
    /// False for builds that are not a single file (a development build has DockSense.dll next to the executable):
    /// replacing only the executable would not update those.
    /// </summary>
    public static bool CanSelfUpdate => !File.Exists(Path.ChangeExtension(InstalledPath, ".dll"));

    private static string InstalledPath => Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");

    private static string OldPath => InstalledPath + ".old";

    /// <summary>True when the folder DockSense runs from can be written without administrator rights.</summary>
    public static bool CanWriteInstallFolder()
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(InstalledPath)!, $".docksense-write-test-{Environment.ProcessId}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads the offer's zip, checks its size and SHA-256 against the release, and extracts DockSense.exe into
    /// the updates folder. Throws <see cref="InvalidDataException"/> when anything does not match.
    /// </summary>
    /// <param name="progress">Receives whole percentages.</param>
    public static Task<StagedUpdate> DownloadAsync(HttpClient http, UpdateOffer offer, IProgress<int>? progress, CancellationToken cancel)
    {
        var asset = offer.Asset ?? throw new InvalidOperationException("This release has no download for this machine.");
        return Task.Run(() => DownloadCoreAsync(http, offer.Version, asset, progress, cancel), cancel);
    }

    private static async Task<StagedUpdate> DownloadCoreAsync(HttpClient http, Version version, ReleaseAsset asset, IProgress<int>? progress, CancellationToken cancel)
    {
        var name = ReleaseVersion.Format(version);
        Directory.CreateDirectory(UpdatesDirectory);
        var zipPath = Path.Combine(UpdatesDirectory, $"DockSense-{name}.zip.part");
        var exePath = Path.Combine(UpdatesDirectory, $"DockSense-{name}.exe");
        try
        {
            string digest;
            long total = 0;
            using (var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                var lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                    if (total > asset.Size)
                    {
                        throw new InvalidDataException("The download is larger than the release says.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    var percent = (int)(total * 100 / asset.Size);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }

                digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (total != asset.Size || !string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The download does not match the checksum of the release.");
            }

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"The release zip has no {ExecutableName}.");
                if (entry.Length > UpdatePlanner.MaxDownloadBytes)
                {
                    throw new InvalidDataException($"{ExecutableName} in the release zip is implausibly large.");
                }

                entry.ExtractToFile(exePath, overwrite: true);
            }

            // The file must say it is what the release said it is.
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (info.ProductName != ProductName || ReleaseVersion.Parse(info.ProductVersion) != version)
            {
                throw new InvalidDataException($"The downloaded file is {info.ProductName} {info.ProductVersion}, not DockSense {name}.");
            }

            return new StagedUpdate(name, exePath, HashFile(exePath));
        }
        catch
        {
            TryDelete(exePath);
            throw;
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    /// <summary>Returns <paramref name="staged"/> when its file exists and is newer than this build, else null.</summary>
    public static StagedUpdate? Validate(StagedUpdate? staged) =>
        staged is not null && File.Exists(staged.Path) && ReleaseVersion.Parse(staged.Version) is { } version && version > AppInfo.Version
            ? staged
            : null;

    /// <summary>
    /// Replaces the running executable, asking for administrator approval when the folder needs it. Throws
    /// <see cref="IOException"/> or <see cref="InvalidDataException"/> when the files cannot be swapped.
    /// </summary>
    public static InstallResult Install(StagedUpdate staged)
    {
        try
        {
            Swap(staged);
            return InstallResult.Installed;
        }
        catch (UnauthorizedAccessException)
        {
            return InstallElevated(staged);
        }
    }

    /// <summary>
    /// When updates are automatic and a verified download is waiting, installs it and starts the new version.
    /// Returns true when this process should exit. Never prompts: in a protected folder the update waits for the
    /// user to install it from the window.
    /// </summary>
    public static bool TryInstallAtStartup(string[] args)
    {
        if (!CanSelfUpdate || args.Contains(AfterUpdateArgument, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (Validate(StateStore.LoadUpdate().Staged) is not { } staged
                || new ConfigStore(ConfigStore.DefaultPath).Load(Log.Warn).Updates != UpdateMode.Automatic)
            {
                return false;
            }

            Swap(staged);
            Log.Info($"Installed DockSense {staged.Version} at startup; starting it.");
            return Relaunch([.. args, AfterUpdateArgument]);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Log.Warn($"Could not install the downloaded update at startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>Starts the executable that is now in place. Returns false when it could not be started.</summary>
    public static bool Relaunch(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(InstalledPath) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Win32Exception ex)
        {
            Log.Error("Could not start the updated DockSense", ex);
            return false;
        }
    }

    /// <summary>The elevated half of <see cref="Install"/>. Returns the process exit code (0 = installed).</summary>
    public static int ApplyFromCommandLine(string[] args)
    {
        try
        {
            if (args.Length != 3)
            {
                return 2;
            }

            // This runs with administrator rights, so it only ever replaces its own executable, and only with a
            // newer DockSense whose digest is the one the caller verified.
            var path = Path.GetFullPath(args[1]);
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.ProductName != ProductName || ReleaseVersion.Parse(info.ProductVersion) is not { } version || version <= AppInfo.Version)
            {
                Log.Warn($"Not installing {path}: it is not a newer DockSense.");
                return 2;
            }

            Swap(new StagedUpdate(ReleaseVersion.Format(version), path, args[2]));

            // The user's own process cannot delete the old file from a protected folder; let Windows do it.
            NativeMethods.MoveFileExW(OldPath, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            Log.Info($"Installed DockSense {ReleaseVersion.Format(version)} with administrator approval.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Log.Error("Could not install the update", ex);
            return 1;
        }
    }

    /// <summary>
    /// Removes what earlier updates left behind: the previous executable, and downloads that are no longer newer
    /// than this build. Returns the state without a stale download.
    /// </summary>
    public static UpdateState CleanUp(UpdateState state)
    {
        TryDelete(OldPath);
        var staged = Validate(state.Staged);
        try
        {
            if (Directory.Exists(UpdatesDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(UpdatesDirectory))
                {
                    if (!string.Equals(file, staged?.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(file);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not clean up the updates folder: {ex.Message}");
        }

        return staged == state.Staged ? state : state with { Staged = staged };
    }

    public static void Discard(StagedUpdate staged) => TryDelete(staged.Path);

    private static void Swap(StagedUpdate staged)
    {
        if (!string.Equals(HashFile(staged.Path), staged.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded update changed on disk after it was verified.");
        }

        var target = InstalledPath;
        var old = OldPath;
        File.Move(target, old, overwrite: true);
        try
        {
            // Copy, not move: a moved file would keep the permissions of the per-user updates folder.
            File.Copy(staged.Path, target);
        }
        catch
        {
            File.Move(old, target, overwrite: true);
            throw;
        }
    }

    private static InstallResult InstallElevated(StagedUpdate staged)
    {
        var start = new ProcessStartInfo(InstalledPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(ApplyArgument);
        start.ArgumentList.Add(staged.Path);
        start.ArgumentList.Add(staged.Sha256);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return InstallResult.Failed;
            }

            process.WaitForExit();
            return process.ExitCode == 0 ? InstallResult.Installed : InstallResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return InstallResult.Declined;
        }
        catch (Win32Exception ex)
        {
            Log.Error("Could not ask for administrator approval", ex);
            return InstallResult.Failed;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use, or in a protected folder: the next start or the next update tries again.
        }
    }
}
