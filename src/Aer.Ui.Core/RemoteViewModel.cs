using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;

namespace Aer.Ui.Core;

/// <summary>
/// The Enable Remote Access view's state (M21 Phase 3, issue #234): shows a pairing code and QR,
/// and toggles the daemon between loopback-only and <c>--remote</c>. There's no live Kestrel
/// rebind (<see cref="TaskSession.SetRemoteEnabledAsync"/>'s remarks), so every toggle is a real
/// shutdown-and-respawn — <see cref="IsBusy"/> covers that multi-second gap.
/// <para>
/// <b>QR payload decision of record:</b> a plain <c>aer://pair?host=&lt;host&gt;&amp;code=&lt;code&gt;</c>
/// URI, not JSON — simpler to encode and parse on both ends (<c>Aer.Mobile</c>'s scanner just reads
/// query parameters off a <see cref="Uri"/>), and reads unambiguously as "this QR is for pairing"
/// if a phone's general-purpose camera app ever scans it outside the app.
/// </para>
/// </summary>
public sealed partial class RemoteViewModel : ObservableObject
{
    public RemoteViewModel()
    {
        PairedClients.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPairedClients));
    }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    [NotifyPropertyChangedFor(nameof(ShouldPollSidecarStatus))]
    private bool isRemoteEnabled;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHost))]
    private string? host;

    [ObservableProperty]
    private int? port;

    [ObservableProperty]
    private string? pairingCode;

    [ObservableProperty]
    private int pairingCodeExpiresInSeconds;

    [ObservableProperty]
    private byte[]? qrPngBytes;

    [ObservableProperty]
    private string statusText = "Checking...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorText;

    /// <summary>Paired devices management list (Phase 6, #243) — populated on every <see cref="RefreshAsync"/>, independent of the current toggle state, since revocation is meaningful whether or not remote access happens to be on right now.</summary>
    public ObservableCollection<PairedClientItemViewModel> PairedClients { get; } = new();

    // M21 Phase 5 (#242): the Go tsnet sidecar's state, polled while remote access is on and not
    // yet Ready (MainWindow's existing 1s pairing-countdown timer drives RefreshSidecarStatusAsync
    // too — see ShouldPollSidecarStatus). Not proven live end to end (no cross-network run has
    // exercised the resulting tailnet address yet), but this is what makes that runnable at all
    // instead of reading ~/.aer/sidecar-spawn.log by hand.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldPollSidecarStatus))]
    private bool sidecarReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidecarAuthUrl))]
    private string? sidecarAuthUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidecarTailnetHost))]
    private string? sidecarTailscaleIp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidecarError))]
    private string? sidecarError;

    [ObservableProperty]
    private byte[]? tailnetQrPngBytes;

    public bool HasHost => Host != null;
    public bool HasError => ErrorText != null;
    public bool HasPairedClients => PairedClients.Count > 0;
    public bool HasSidecarAuthUrl => SidecarAuthUrl != null;
    public bool HasSidecarError => SidecarError != null;
    public string? SidecarTailnetHost => SidecarTailscaleIp != null && Port != null ? $"{SidecarTailscaleIp}:{Port}" : null;
    public bool HasSidecarTailnetHost => SidecarTailnetHost != null;
    public bool ShouldPollSidecarStatus => IsRemoteEnabled && !SidecarReady;
    public string ToggleButtonText => IsRemoteEnabled ? "Turn off remote access" : "Turn on remote access";

    /// <summary>
    /// Refreshes the daemon's current bind mode/port, this machine's LAN address, and a fresh
    /// pairing code — everything the view needs to render, in one call (activation, and after a
    /// successful toggle).
    /// </summary>
    public async Task RefreshAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            var status = await session.GetRemoteAccessStatusAsync(cancellationToken).ConfigureAwait(true);
            if (status == null)
            {
                StatusText = "Could not reach Aer.Daemon.";
                return;
            }

            IsRemoteEnabled = status.IsRemote;
            Port = status.Port;

            var lanAddress = LanAddress.TryGetPrimary();
            Host = lanAddress != null ? $"{lanAddress}:{status.Port}" : null;

            StatusText = status switch
            {
                { IsRemote: true, HasRunningTasks: true } => "Remote access is on. A task is running — pairing still works, but the toggle is locked until it finishes.",
                { IsRemote: true } => "Remote access is on — anyone on this network can reach it until you turn it off.",
                { HasRunningTasks: true } => "Remote access is off. A task is running — the toggle is locked until it finishes.",
                _ => "Remote access is off — only this computer can reach Aer.Daemon.",
            };

            if (IsRemoteEnabled)
            {
                await RefreshSidecarStatusAsync(session, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                SidecarReady = false;
                SidecarAuthUrl = null;
                SidecarTailscaleIp = null;
                SidecarError = null;
                TailnetQrPngBytes = null;
            }

            if (Host != null)
            {
                await GeneratePairingCodeAsync(session, cancellationToken).ConfigureAwait(true);
            }

            await RefreshPairedClientsAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-reads the paired-devices list. Called from <see cref="RefreshAsync"/> and again after a successful <see cref="RemovePairedClientAsync"/> so the list reflects the revocation immediately.</summary>
    public async Task RefreshPairedClientsAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        var clients = await session.GetPairedClientsAsync(cancellationToken).ConfigureAwait(true);
        if (clients == null) return;

        PairedClients.Clear();
        foreach (var client in clients)
        {
            PairedClients.Add(new PairedClientItemViewModel(
                client.ClientId, client.Name, client.PairedAt,
                clientId => RemovePairedClientAsync(session, clientId)));
        }
    }

    /// <summary>
    /// Re-reads the tsnet sidecar's state (Phase 5, #242) — called once from <see cref="RefreshAsync"/>
    /// and then repeatedly from <c>MainWindow</c>'s existing 1s pairing-countdown timer while
    /// <see cref="ShouldPollSidecarStatus"/> is true, so the one-time Tailscale sign-in link (and,
    /// once enrollment finishes, the tailnet address) show up without a manual refresh. Doesn't
    /// touch <see cref="IsBusy"/> — unlike <see cref="RefreshAsync"/>, this runs silently in the
    /// background every second and shouldn't flicker the toggle button's enabled state.
    /// </summary>
    public async Task RefreshSidecarStatusAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        var status = await session.GetSidecarStatusAsync(cancellationToken).ConfigureAwait(true);
        if (status == null)
        {
            SidecarReady = false;
            SidecarAuthUrl = null;
            SidecarTailscaleIp = null;
            SidecarError = "Could not reach the zero-config (Tailscale) sidecar.";
            return;
        }

        var justBecameReady = status.Ready && !SidecarReady;

        SidecarReady = status.Ready;
        SidecarAuthUrl = status.AuthUrl;
        SidecarTailscaleIp = status.TailscaleIp;
        SidecarError = status.Error;

        // The tailnet QR/host only exists once SidecarTailscaleIp is known -- rebuild it the
        // moment enrollment completes rather than waiting for the next unrelated code refresh.
        if (justBecameReady && Host != null)
        {
            await GeneratePairingCodeAsync(session, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Revokes a paired device's token. Not a <c>[RelayCommand]</c> for the same reason <see cref="ToggleRemoteAsync"/> isn't — the view's code-behind supplies <paramref name="session"/> at call time.</summary>
    public async Task RemovePairedClientAsync(TaskSession session, string clientId, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            var removed = await session.RevokePairedClientAsync(clientId, cancellationToken).ConfigureAwait(true);
            if (!removed)
            {
                ErrorText = "Could not revoke that device — it may have already been removed.";
                return;
            }

            await RefreshPairedClientsAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One second of the pairing code's countdown (driven by <c>MainWindow</c>'s
    /// <c>DispatcherTimer</c>, the same pattern as its live-refresh timer — <c>Aer.Ui.Core</c>
    /// stays Avalonia-free, so the timer itself lives in the view layer). Floors at 0 rather than
    /// going negative; the caller is expected to fetch a fresh code once it does, since the daemon
    /// actually invalidates the code server-side at 60s (<c>PairingCodeManager.ValidateAndConsume</c>)
    /// — before this existed, the label was set once from the fetch response and never updated, so
    /// it always read "Expires in 60s" even long after the code had gone stale.
    /// </summary>
    public void TickPairingCodeCountdown()
    {
        if (PairingCodeExpiresInSeconds > 0)
        {
            PairingCodeExpiresInSeconds--;
        }
    }

    /// <summary>
    /// Fetches a fresh 60-second pairing code and rebuilds the QR against it — a separate,
    /// explicitly re-callable step since a code expires well before most other state does. On
    /// failure, keeps whatever code/QR was already showing and surfaces an error instead of
    /// blanking the screen — the countdown's own auto-refresh calls this the instant it hits 0, and
    /// a single transient failure right at that moment used to silently wipe a still-legible QR the
    /// user might be mid-scan against.
    /// </summary>
    public async Task GeneratePairingCodeAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        if (Host == null)
        {
            PairingCode = null;
            QrPngBytes = null;
            return;
        }

        var code = await session.GetPairingCodeAsync(cancellationToken).ConfigureAwait(true);
        if (code == null)
        {
            ErrorText = "Could not reach Aer.Daemon for a new pairing code — will keep retrying.";
            return;
        }

        ErrorText = null;
        PairingCode = code.Code;
        PairingCodeExpiresInSeconds = code.ExpiresInSeconds;
        QrPngBytes = BuildQrPng($"aer://pair?host={Uri.EscapeDataString(Host)}&code={Uri.EscapeDataString(code.Code)}");

        // Same code, second QR against the sidecar's tailnet address (Phase 5, #242) — only once
        // tsnet enrollment is actually done; the code is a shared secret, not host-bound, so
        // whichever of the two a phone scans first just consumes it, same as re-scanning the LAN
        // QR twice already would.
        TailnetQrPngBytes = SidecarTailnetHost is { } tailnetHost
            ? BuildQrPng($"aer://pair?host={Uri.EscapeDataString(tailnetHost)}&code={Uri.EscapeDataString(code.Code)}")
            : null;
    }

    /// <summary>The toggle button's action — public and directly callable (not a <c>[RelayCommand]</c>), the same reason <see cref="HomeViewModel.RefreshAsync"/> takes a <see cref="TaskSession"/> parameter rather than capturing one: this ViewModel is constructed before the session exists (<see cref="MainWindowViewModel"/>'s property-initializer <c>new()</c>), so the view's code-behind supplies it at call time instead.</summary>
    public async Task ToggleRemoteAsync(TaskSession session)
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            var outcome = await session.SetRemoteEnabledAsync(!IsRemoteEnabled).ConfigureAwait(true);
            if (outcome.ErrorMessage != null)
            {
                ErrorText = outcome.ErrorMessage;
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"Toggle failed unexpectedly: {ex.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(session).ConfigureAwait(true);
    }

    private static byte[] BuildQrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(data);
        return pngQrCode.GetGraphic(10);
    }
}

/// <summary>
/// One row in the "Paired Devices" list (Phase 6, #243) — same shape as <c>HomeViewModel</c>'s
/// <c>InboxItemViewModel</c>: a small, purpose-built item ViewModel whose <see cref="RemoveCommand"/>
/// closes over the parent's already-available <c>TaskSession</c>, so the XAML can bind
/// <c>Command="{Binding RemoveCommand}"</c> directly per row instead of the shell wiring a single
/// static button (there's no way to know which row's button was clicked from a static handler).
/// </summary>
public sealed partial class PairedClientItemViewModel(
    string clientId, string name, DateTime pairedAt, Func<string, Task> removeAsync)
{
    public string ClientId { get; } = clientId;
    public string Name { get; } = name;
    public DateTime PairedAt { get; } = pairedAt;

    [RelayCommand]
    private Task Remove() => removeAsync(ClientId);
}
