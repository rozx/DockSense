using System.Net.Http;
using System.Windows.Threading;
using DockSense.App.Localization;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App.Services;

public enum UpdateStage
{
    /// <summary>No newer release is known.</summary>
    Idle,
    Checking,

    /// <summary>A newer release exists and is not downloaded yet.</summary>
    Available,
    Downloading,

    /// <summary>Downloaded and verified; installing needs a restart of DockSense.</summary>
    Ready,

    /// <summary>Handed to the installer; DockSense is about to exit.</summary>
    Installing,
}

/// <summary>
/// Looks for new releases on GitHub and installs them (see <see cref="UpdateInstaller"/>). Lives on the UI thread.
/// <para>
/// The only request DockSense ever makes on its own is the daily "latest release" query, and only when updates
/// are not turned off. DockSense never restarts itself unasked: an automatic update is installed when DockSense
/// starts, a manual one when the user clicks.
/// </para>
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    /// <summary>A failed background check (offline, rate limit) is not repeated sooner than this.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(3);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = UpdateInstaller.CreateHttpClient();

    private readonly DockSenseService _service;
    private readonly Func<IReadOnlyList<string>> _relaunchArguments;
    private readonly Action _exit;
    private readonly DispatcherTimer _timer = new() { Interval = FirstCheckDelay };
    private UpdateState _state = new();
    private StagedUpdate? _staged;
    private DateTimeOffset? _lastFailure;
    private bool _busy;

    /// <param name="relaunchArguments">The arguments that bring DockSense back the way it is now.</param>
    /// <param name="exit">Exits DockSense, so the installer can replace it.</param>
    public UpdateService(DockSenseService service, Func<IReadOnlyList<string>> relaunchArguments, Action exit)
    {
        _service = service;
        _relaunchArguments = relaunchArguments;
        _exit = exit;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = PollInterval;
            var now = DateTimeOffset.Now;
            if (_service.Config.Updates != UpdateMode.Off
                && UpdatePlanner.IsCheckDue(_state.LastCheck, now)
                && (_lastFailure is not { } failed || failed > now || now - failed >= RetryAfterFailure))
            {
                _ = CheckAsync(manual: false);
            }
        };
    }

    /// <summary>Raised when anything shown about updates may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once per version, when a background check finds a release the user was not told about yet.</summary>
    public event Action<UpdateOffer>? UpdateFound;

    public UpdateStage Stage { get; private set; }

    /// <summary>The newer release, once one is known.</summary>
    public UpdateOffer? Offer { get; private set; }

    public int DownloadPercent { get; private set; }

    /// <summary>The last failure, ready to show; cleared when the next action starts.</summary>
    public string? Error { get; private set; }

    public DateTimeOffset? LastCheck => _state.LastCheck;

    /// <summary>True when installing needs one administrator prompt, because the folder is protected.</summary>
    public bool NeedsElevation { get; private set; }

    /// <summary>False when DockSense can only point at the release page (no verifiable download, or a development build).</summary>
    public bool CanInstall => UpdateInstaller.CanSelfUpdate && (_staged is not null || Offer?.Asset is not null);

    /// <summary>True when the update goes in at the next start without any click.</summary>
    public bool InstallsAtNextStart => _service.Config.Updates == UpdateMode.Automatic && !NeedsElevation && CanInstall;

    public void Start()
    {
        NeedsElevation = !UpdateInstaller.CanWriteInstallFolder();
        var loaded = StateStore.LoadUpdate();
        _state = UpdateInstaller.CleanUp(loaded);
        if (_state != loaded)
        {
            StateStore.SaveUpdate(_state);
        }

        _staged = _state.Staged;
        if (_staged is not null && ReleaseVersion.Parse(_staged.Version) is { } version)
        {
            // Downloaded earlier and still waiting, e.g. because the folder needs administrator approval.
            Offer = new UpdateOffer(version, UpdatePlanner.ReleasePageUrl(version), null);
            Stage = UpdateStage.Ready;
        }

        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    /// <summary>Asks GitHub for the latest release. A manual check reports failures; a background one only logs them.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Error = null;
        var before = Stage;
        Stage = UpdateStage.Checking;
        RaiseChanged();
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);
            var json = await Http.GetStringAsync(UpdatePlanner.FeedUrl, timeout.Token);
            var offer = UpdatePlanner.Offer(ReleaseFeed.Parse(json), AppInfo.Version, AppInfo.Architecture);
            _lastFailure = null;
            _state = _state with { LastCheck = DateTimeOffset.Now };
            Stage = Adopt(offer);
            if (offer is not null && _state.NotifiedVersion != ReleaseVersion.Format(offer.Version))
            {
                _state = _state with { NotifiedVersion = ReleaseVersion.Format(offer.Version) };
                Log.Info($"DockSense {ReleaseVersion.Format(offer.Version)} is available (this is {AppInfo.VersionText}).");
                if (!manual)
                {
                    UpdateFound?.Invoke(offer);
                }
            }

            StateStore.SaveUpdate(_state);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            var reason = ex is OperationCanceledException ? "the request timed out" : ex.Message;
            Log.Warn($"Could not check for updates: {reason}");
            _lastFailure = DateTimeOffset.Now;
            Stage = before;
            if (manual)
            {
                Error = Loc.F("Upd_CheckFailed", reason);
            }
        }
        finally
        {
            _busy = false;
            RaiseChanged();
        }

        if (Stage == UpdateStage.Available && CanInstall && _service.Config.Updates == UpdateMode.Automatic)
        {
            await DownloadAsync();
        }
    }

    /// <summary>Downloads the update if needed, hands it to the installer and exits DockSense. Asked for by the user.</summary>
    public async Task InstallAsync()
    {
        if (_busy || !CanInstall)
        {
            return;
        }

        if (_staged is null)
        {
            await DownloadAsync();
        }

        if (_staged is not { } staged || _busy)
        {
            return;
        }

        _busy = true;
        Error = null;
        Stage = UpdateStage.Installing;
        RaiseChanged();
        var checkAgain = false;
        try
        {
            var arguments = _relaunchArguments();
            switch (await Task.Run(() => UpdateInstaller.Begin(staged, arguments)))
            {
                case InstallResult.Started:
                    // The installer waits for this process to exit, installs, and starts DockSense again.
                    Log.Info($"Installing DockSense {staged.Version}; exiting so the installer can replace this version.");
                    _exit();
                    return;

                case InstallResult.Declined:
                    Error = Loc.T("Upd_Declined");
                    break;

                default:
                    Error = Loc.F("Upd_InstallFailed", Loc.T("Upd_SeeLog"));
                    break;
            }

            Stage = UpdateStage.Ready;
        }
        catch (InvalidDataException ex)
        {
            // The download is no longer what was verified, or part of it is gone: drop it and offer a fresh one.
            Log.Warn($"Discarding the downloaded update: {ex.Message}");
            Forget(staged);
            Error = Loc.F("Upd_InstallFailed", ex.Message);
            Stage = Offer?.Asset is not null ? UpdateStage.Available : UpdateStage.Idle;

            // An offer restored from an earlier run has no download link; ask GitHub for it again.
            checkAgain = Stage == UpdateStage.Idle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not install the update: {ex.Message}");
            Error = Loc.F("Upd_InstallFailed", ex.Message);
            Stage = UpdateStage.Ready;
        }
        finally
        {
            _busy = false;
            RaiseChanged();
        }

        if (checkAgain)
        {
            Offer = null;
            await CheckAsync(manual: true);
        }
    }

    /// <summary>Takes over the result of a check and returns the stage that follows from it.</summary>
    private UpdateStage Adopt(UpdateOffer? offer)
    {
        if (offer is null)
        {
            // Nothing newer on GitHub. A download that is still waiting stays valid (it is newer than this build).
            if (_staged is null)
            {
                Offer = null;
            }

            return _staged is null ? UpdateStage.Idle : UpdateStage.Ready;
        }

        if (_staged is { } staged && ReleaseVersion.Parse(staged.Version) != offer.Version)
        {
            Forget(staged); // A newer release replaced the one that was downloaded.
        }

        Offer = offer;
        return _staged is null ? UpdateStage.Available : UpdateStage.Ready;
    }

    private async Task DownloadAsync()
    {
        if (_busy || Offer is not { Asset: not null } offer)
        {
            return;
        }

        _busy = true;
        Error = null;
        DownloadPercent = 0;
        Stage = UpdateStage.Downloading;
        RaiseChanged();
        try
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout);
            var progress = new Progress<int>(percent =>
            {
                DownloadPercent = percent;
                RaiseChanged();
            });
            _staged = await UpdateInstaller.DownloadAsync(Http, offer, keepZip: NeedsElevation, progress, timeout.Token);
            _state = _state with { Staged = _staged };
            StateStore.SaveUpdate(_state);
            Log.Info($"Downloaded and verified DockSense {_staged.Version}.");
            Stage = UpdateStage.Ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            var reason = ex is OperationCanceledException ? "the download timed out" : ex.Message;
            Log.Warn($"Could not download DockSense {ReleaseVersion.Format(offer.Version)}: {reason}");
            Error = Loc.F("Upd_DownloadFailed", reason);
            Stage = UpdateStage.Available;
        }
        finally
        {
            _busy = false;
            RaiseChanged();
        }
    }

    private void Forget(StagedUpdate staged)
    {
        UpdateInstaller.Discard(staged);
        _staged = null;
        _state = _state with { Staged = null };
        StateStore.SaveUpdate(_state);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
