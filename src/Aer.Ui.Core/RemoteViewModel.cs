using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    private bool isRemoteEnabled;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHost))]
    private string? host;

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

    public bool HasHost => Host != null;
    public bool HasError => ErrorText != null;
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

            var lanAddress = LanAddress.TryGetPrimary();
            Host = lanAddress != null ? $"{lanAddress}:{status.Port}" : null;

            StatusText = status switch
            {
                { IsRemote: true, HasRunningTasks: true } => "Remote access is on. A task is running — pairing still works, but the toggle is locked until it finishes.",
                { IsRemote: true } => "Remote access is on — anyone on this network can reach it until you turn it off.",
                { HasRunningTasks: true } => "Remote access is off. A task is running — the toggle is locked until it finishes.",
                _ => "Remote access is off — only this computer can reach Aer.Daemon.",
            };

            if (Host != null)
            {
                await GeneratePairingCodeAsync(session, cancellationToken).ConfigureAwait(true);
            }
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
