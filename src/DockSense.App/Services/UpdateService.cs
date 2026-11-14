using System.Net.Http;
using System.Net.Http.Headers;
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
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = CreateClient();

    private readonly DockSenseService _service;
    private readonly Action _restart;
    private readonly DispatcherTimer _timer = new() { Interval = FirstCheckDelay };
    private UpdateState _state = new();
    private StagedUpdate? _staged;
    private bool _busy;

    /// <param name="restart">Starts the installed version and exits this one.</param>
    public UpdateService(DockSenseService service, Action restart)
    {
        _service = service;
        _restart = restart;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = PollInterval;
            if (_service.Config.Updates != UpdateMode.Off && UpdatePlanner.IsCheckDue(_state.LastCheck, DateTimeOffset.Now))
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
    public bool InstallsAtNextStart =>
        _service.Config.Updates == UpdateMode.Automatic && UpdateInstaller.CanSelfUpdate && !NeedsElevation && Offer?.Asset is not null;

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

    /// <summary>Downloads the update if needed, installs it and restarts DockSense. Asked for by the user.</summary>
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
        try
        {
            switch (await Task.Run(() => UpdateInstaller.Install(staged)))
            {
                case InstallResult.Installed:
                    Log.Info($"Installed DockSense {staged.Version}; restarting.");
                    _restart();
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
            // The download is no longer what was verified: drop it and offer a fresh one.
            Log.Warn($"Discarding the downloaded update: {ex.Message}");
            Forget(staged);
            Error = Loc.F("Upd_InstallFailed", ex.Message);
            Stage = Offer?.Asset is not null ? UpdateStage.Available : UpdateStage.Idle;
        }
        catch (IOException ex)
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
            _staged = await UpdateInstaller.DownloadAsync(Http, offer, progress, timeout.Token);
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API rejects requests without a user agent. Nothing else identifies the machine or the user.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DockSense", AppInfo.VersionText));
        return client;
    }
}
