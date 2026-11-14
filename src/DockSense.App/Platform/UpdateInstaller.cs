using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using DockSense.Core;

namespace DockSense.App.Platform;

internal enum InstallResult
{
    /// <summary>The installer has the update; this process must exit now so it can go on.</summary>
    Started,

    /// <summary>The user said no at the administrator prompt.</summary>
    Declined,

    Failed,
}

/// <summary>
/// Downloads a release, verifies it and puts it in place of the installed executable.
/// <para>
/// DockSense is one self-contained file, and a running single-file app must not be replaced: it reads assemblies
/// from its own file on demand, so after a swap the next assembly it needs comes out of the wrong file and fails
/// to load. The running DockSense therefore only starts the update and exits. The work is done by the downloaded
/// executable itself, started from the updates folder (<see cref="InstallArgument"/>): it waits for DockSense to
/// exit, renames the installed file to <c>DockSense.exe.old</c>, writes itself under the old name and starts it.
/// </para>
/// <para>
/// In a folder the user cannot write to (such as Program Files), the file swap alone runs in an elevated process
/// after one administrator prompt (<see cref="ApplyArgument"/>). That process is the installed executable, which
/// only administrators can change, never the download. It takes nothing on trust: it hashes the release zip and
/// asks GitHub itself whether that is a newer release. It replaces its own file, so everything it does afterwards
/// has already run once before the swap.
/// </para>
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>
    /// <c>&lt;download&gt; --install-update &lt;installed exe&gt; &lt;pid&gt; [arguments...]</c>: wait for that DockSense
    /// process to exit, install this file over the installed one, and start it with the arguments.
    /// </summary>
    public const string InstallArgument = "--install-update";

    /// <summary>
    /// <c>DockSense.exe --apply-update &lt;release zip&gt; &lt;pid&gt;</c>, elevated: check the zip against GitHub, wait
    /// for that DockSense process to exit, then replace this executable with the one in the zip.
    /// </summary>
    public const string ApplyArgument = "--apply-update";

    /// <summary>Passed to the new version by the installer. Marks the first start after an update.</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>Passed by the installer when the update did not go in and the old version is started again.</summary>
    public const string FailedArgument = "--update-failed";

    /// <summary>
    /// <c>--restarted=&lt;mode&gt;</c>, passed when DockSense replaces a running instance: the mode whose profile that
    /// instance had applied. The new instance carries on from there and applies nothing again.
    /// </summary>
    public const string RestartedArgument = "--restarted";

    private const string ExecutableName = "DockSense.exe";
    private const string ProductName = "DockSense";
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Longer than the elevated process can take (30 s for GitHub, <see cref="ExitTimeout"/>, then the copy). The
    /// wait normally ends earlier, when that process is gone.
    /// </summary>
    private static readonly TimeSpan ElevatedSwapTimeout = TimeSpan.FromMinutes(5);

    public static string UpdatesDirectory { get; } = Path.Combine(Log.DataDirectory, "updates");

    /// <summary>
    /// False for builds that are not a single file (a development build has DockSense.dll next to the executable):
    /// replacing only the executable would not update those.
    /// </summary>
    public static bool CanSelfUpdate => !File.Exists(Path.ChangeExtension(InstalledPath, ".dll"));

    private static string InstalledPath => Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");

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
    /// <param name="keepZip">Keep the zip for the elevated step (see <see cref="StagedUpdate.ZipPath"/>).</param>
    public static Task<StagedUpdate> DownloadAsync(HttpClient http, UpdateOffer offer, bool keepZip, IProgress<int>? progress, CancellationToken cancel)
    {
        var asset = offer.Asset ?? throw new InvalidOperationException("This release has no download for this machine.");
        return Task.Run(() => DownloadCoreAsync(http, offer.Version, asset, keepZip, progress, cancel), cancel);
    }

    /// <summary>A client for GitHub. The user agent is all it says about this machine: GitHub's API requires one.</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue(ProductName, AppInfo.VersionText));
        return client;
    }

    private static async Task<StagedUpdate> DownloadCoreAsync(HttpClient http, Version version, ReleaseAsset asset, bool keepZip, IProgress<int>? progress, CancellationToken cancel)
    {
        var name = ReleaseVersion.Format(version);
        Directory.CreateDirectory(UpdatesDirectory);
        var zipPath = Path.Combine(UpdatesDirectory, $"DockSense-{name}.zip.part");
        var keptZipPath = Path.Combine(UpdatesDirectory, $"DockSense-{name}.zip");
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
            if (DockSenseVersion(exePath) != version)
            {
                throw new InvalidDataException($"The downloaded file is not DockSense {name}.");
            }

            if (keepZip)
            {
                File.Move(zipPath, keptZipPath, overwrite: true);
            }

            return new StagedUpdate(name, exePath, HashFile(exePath), keepZip ? keptZipPath : null);
        }
        catch
        {
            TryDelete(exePath);
            TryDelete(keptZipPath);
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
    /// Hands the verified download to the installer, asking for administrator approval first when the folder needs
    /// it. On <see cref="InstallResult.Started"/> the caller must exit: the installer waits for that, installs, and
    /// starts DockSense again with <paramref name="relaunchArguments"/>. Throws <see cref="InvalidDataException"/>
    /// when the download is no longer the file that was verified.
    /// </summary>
    public static InstallResult Begin(StagedUpdate staged, IEnumerable<string> relaunchArguments)
    {
        // Gone (deleted, or quarantined by antivirus) counts as "not what was verified": the caller drops the
        // download and fetches it again, where a plain I/O error would be retried with the same missing file.
        if (!File.Exists(staged.Path))
        {
            throw new InvalidDataException("The downloaded update is no longer on disk.");
        }

        if (!string.Equals(HashFile(staged.Path), staged.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded update changed on disk after it was verified.");
        }

        var processes = Environment.ProcessId.ToString();
        if (!CanWriteInstallFolder())
        {
            if (staged.ZipPath is not { } zip || !File.Exists(zip))
            {
                throw new InvalidDataException("The release zip is needed for a protected folder and is gone; download the update again.");
            }

            var elevated = StartElevatedSwap(zip, out var elevatedProcessId);
            if (elevated != InstallResult.Started)
            {
                return elevated;
            }

            processes += "," + elevatedProcessId;
        }

        // The download installs itself: it is the only DockSense that is not the file being replaced.
        string[] arguments = [InstallArgument, InstalledPath, processes, .. relaunchArguments];
        return Start(staged.Path, arguments) ? InstallResult.Started : InstallResult.Failed;
    }

    /// <summary>
    /// When updates are automatic and a verified download is waiting, starts the installer. Returns true when this
    /// process should exit. Never prompts: in a protected folder the update waits for the user to install it from
    /// the window.
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
                || new ConfigStore(ConfigStore.DefaultPath).Load(Log.Warn).Updates != UpdateMode.Automatic
                || !CanWriteInstallFolder())
            {
                return false;
            }

            if (Begin(staged, args) != InstallResult.Started)
            {
                return false;
            }

            Log.Info($"Installing DockSense {staged.Version} at startup.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("Could not install the downloaded update at startup", ex);
            return false;
        }
    }

    /// <summary>
    /// The installer (<see cref="InstallArgument"/>), running as the downloaded file. Always starts DockSense again
    /// once the old process is gone, whether or not the update went in.
    /// </summary>
    public static int InstallFromCommandLine(string[] args)
    {
        // args[2] is "<DockSense pid>" or "<DockSense pid>,<pid of the elevated swap>".
        var ids = args.Length < 3 ? [] : args[2].Split(',');
        var elevatedProcessId = 0;
        if (ids.Length is < 1 or > 2 || !int.TryParse(ids[0], out var processId) || (ids.Length == 2 && !int.TryParse(ids[1], out elevatedProcessId)))
        {
            return 2;
        }

        try
        {
            var target = Path.GetFullPath(args[1]);
            var relaunch = args.Skip(3).Append(AfterUpdateArgument).ToList();

            // Only ever install over a DockSense, and never over this file itself.
            var self = InstalledPath;
            if (!Path.GetFileName(target).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, self, StringComparison.OrdinalIgnoreCase)
                || DockSenseVersion(target) is null)
            {
                Log.Warn($"Not installing over {target}: it is not an installed DockSense.");
                return 2;
            }

            if (!WaitForExit(processId))
            {
                // Replacing the file under a running DockSense would break it. Leave everything as it is.
                Log.Warn("DockSense did not exit; the update was not installed.");
                return 3;
            }

            var installed = false;
            try
            {
                using var source = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read);
                Replace(target, source);
                installed = true;
                Log.Info($"Installed DockSense {AppInfo.VersionText}.");
            }
            catch (UnauthorizedAccessException)
            {
                // A protected folder: the elevated process swaps the file. Wait until it is done.
                installed = WaitForVersion(target, AppInfo.Version, elevatedProcessId);
                Log.Info(installed
                    ? $"DockSense {AppInfo.VersionText} was installed with administrator approval."
                    : "The update was not installed; starting the installed version again.");
            }
            catch (IOException ex)
            {
                Log.Error("Could not install the update", ex);
            }

            if (!installed)
            {
                // A swap that stopped between its two renames left the old file under its .old name: put it back.
                if (!File.Exists(target) && File.Exists(target + ".old"))
                {
                    File.Move(target + ".old", target);
                }

                // The version that comes back says so, instead of quietly offering the same update again.
                relaunch.Add(FailedArgument);
            }

            return Start(target, relaunch) ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("Could not install the update", ex);
            return 1;
        }
    }

    /// <summary>The elevated file swap (<see cref="ApplyArgument"/>). Returns the process exit code (0 = installed).</summary>
    public static int ApplyFromCommandLine(string[] args)
    {
        try
        {
            if (args.Length != 3 || !int.TryParse(args[2], out var processId))
            {
                return 2;
            }

            // This runs with administrator rights and anyone can start it, so it trusts nothing it is handed. It
            // only replaces its own executable, and only with the DockSense.exe from a zip that GitHub itself lists
            // as a newer release of this repository.
            var path = Path.GetFullPath(args[1]);
            var target = InstalledPath;
            var old = target + ".old";

            // One handle from the checksum to the last extracted byte, and it denies writing, deleting and
            // renaming: the updates folder is writable by the user, so nothing may swap the zip in between.
            using var zipFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexStringLower(SHA256.HashData(zipFile));
            if (!IsNewerRelease(digest, out var version))
            {
                Log.Warn($"Not installing {path}: GitHub does not list it as a newer DockSense release.");
                return 2;
            }

            if (!WaitForExit(processId))
            {
                Log.Warn("DockSense did not exit; the update was not installed.");
                return 3;
            }

            // This process replaces its own file. Afterwards it must not need anything it has not used yet (see the
            // class remarks), so the two calls that follow the swap run once here first.
            NativeMethods.MoveFileExW(old + ".none", null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            Log.Info($"Installing DockSense {version} with administrator approval.");

            zipFile.Position = 0;
            using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Read, leaveOpen: true))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"The release zip has no {ExecutableName}.");
                using var source = entry.Open();
                Replace(target, source);
            }

            // The user's own process cannot delete the old file from a protected folder; Windows does it at the
            // next restart.
            NativeMethods.MoveFileExW(old, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            Log.Info("Installed.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or HttpRequestException or OperationCanceledException)
        {
            Log.Error("Could not install the update", ex);
            return 1;
        }
    }

    /// <summary>
    /// Asks GitHub whether a zip with this SHA-256 is the download of a release newer than this build, for this
    /// build's architecture. It need not be the latest release: a newer one may have come out since the download.
    /// </summary>
    private static bool IsNewerRelease(string zipSha256, out string version)
    {
        version = string.Empty;
        using var http = CreateHttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var json = http.GetStringAsync(UpdatePlanner.ReleaseListUrl, timeout.Token).GetAwaiter().GetResult();
        if (UpdatePlanner.FindRelease(ReleaseFeed.ParseAll(json), AppInfo.Version, AppInfo.Architecture, zipSha256) is not { } release)
        {
            return false;
        }

        version = ReleaseVersion.Format(release.Version);
        return true;
    }

    /// <summary>
    /// Removes what earlier updates left behind: the previous executable, and downloads that are no longer newer
    /// than this build. Returns the state without a stale download.
    /// </summary>
    public static UpdateState CleanUp(UpdateState state)
    {
        TryDelete(InstalledPath + ".old");
        TryDelete(InstalledPath + ".new");
        var staged = Validate(state.Staged);
        try
        {
            if (Directory.Exists(UpdatesDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(UpdatesDirectory))
                {
                    if (!string.Equals(file, staged?.Path, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(file, staged?.ZipPath, StringComparison.OrdinalIgnoreCase))
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

    public static void Discard(StagedUpdate staged)
    {
        TryDelete(staged.Path);
        if (staged.ZipPath is { } zip)
        {
            TryDelete(zip);
        }
    }

    /// <summary>
    /// Writes <paramref name="source"/> next to <paramref name="target"/>, then swaps the two with renames: the
    /// installed file becomes <c>.old</c> and the new one takes its name. The complete new file is on disk before
    /// anything is renamed, so a crash or power loss cannot leave a half-written executable under the real name.
    /// </summary>
    private static void Replace(string target, Stream source)
    {
        var incoming = target + ".new";
        var old = target + ".old";
        try
        {
            // A new file, not a moved one: it gets the permissions of the install folder, where a moved file
            // would keep those of the per-user updates folder.
            using (var destination = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            File.Move(target, old, overwrite: true);
            try
            {
                File.Move(incoming, target);
            }
            catch
            {
                File.Move(old, target, overwrite: true);
                throw;
            }
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }
    }

    private static InstallResult StartElevatedSwap(string zipPath, out int processId)
    {
        processId = 0;
        var start = new ProcessStartInfo(InstalledPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { ApplyArgument, zipPath, Environment.ProcessId.ToString() })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return InstallResult.Failed;
            }

            processId = process.Id;
            return InstallResult.Started;
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

    private static bool Start(string path, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Win32Exception ex)
        {
            Log.Error($"Could not start {path}", ex);
            return false;
        }
    }

    /// <summary>True once the process is gone (or never existed); false when it is still running after the timeout.</summary>
    private static bool WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(ExitTimeout);
        }
        catch (ArgumentException)
        {
            return true; // Already gone.
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Cannot watch it (for example from the unelevated installer once the process is someone else's):
            // fall back to its lifetime as seen in the process list.
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < ExitTimeout)
            {
                if (!IsRunning(processId))
                {
                    return true;
                }

                Thread.Sleep(200);
            }

            return false;
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until the file is a DockSense of exactly that version: the elevated swap finished. Gives up once the
    /// elevated process is gone without having done it, or after the timeout.
    /// </summary>
    private static bool WaitForVersion(string path, Version version, int elevatedProcessId)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < ElevatedSwapTimeout)
        {
            // Read this first: a swap that completes right after it is still seen by the check below.
            var swapping = elevatedProcessId != 0 && IsRunning(elevatedProcessId);
            try
            {
                if (DockSenseVersion(path) == version)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Between the two renames.
            }

            if (!swapping)
            {
                return false;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    /// <summary>The release version of a DockSense executable, or null when the file is not one.</summary>
    private static Version? DockSenseVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == ProductName ? ReleaseVersion.Parse(info.ProductVersion) : null;
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
