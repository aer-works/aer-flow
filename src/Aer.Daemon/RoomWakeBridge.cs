using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Microsoft.Extensions.Hosting;

namespace Aer.Daemon;

/// <summary>
/// Thin, settable pointer to the room directory <see cref="RoomWakeBridge"/> watches — the same
/// "holder" shape as <c>BindingsPathHolder</c> in Program.cs. Nothing here is worth
/// crash-proofing (#799's contract): on restart a fresh <see cref="RoomWakeBridge"/> starts
/// dormant until re-pointed, and the wake set it then produces is identical because it is derived
/// fresh from the room and lane journals, never carried over.
/// </summary>
public sealed class RoomWakeBridgeState
{
    private volatile string? _roomDirectoryPath;
    private volatile IReadOnlyList<RoomWake> _currentWakes = [];

    public string? RoomDirectoryPath
    {
        get => _roomDirectoryPath;
        set => _roomDirectoryPath = value;
    }

    public IReadOnlyList<RoomWake> CurrentWakes
    {
        get => _currentWakes;
        internal set => _currentWakes = value;
    }
}

/// <summary>
/// Daemon-hosted derivation of the room's wake set (#799): watches <c>room.jsonl</c> for appends
/// by length-poll (<c>Aer.Cli.StatusCommand</c>'s own precedent — filesystem change notifications
/// are unreliable cross-platform), recomputes which held-work refs are unresolved, and re-probes
/// each of those lanes' <c>flow.jsonl</c> every tick via <see cref="LaneTerminalProbe"/> — never
/// taking the room's or any lane's <see cref="Aer.Flow.Concurrency.ConcurrencyGuard"/>. Holds no
/// state worth crash-proofing: <see cref="RoomWakeBridgeState.CurrentWakes"/> is fully
/// recomputed, never incrementally updated, so a restarted bridge reproduces the identical set.
/// </summary>
public sealed class RoomWakeBridge(RoomWakeBridgeState state) : BackgroundService
{
    private const string RoomLogFileName = "room.jsonl";

    /// <summary>Host config, not state — matches <c>StatusCommand.PollIntervalMs</c>'s own reasoning.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (state.RoomDirectoryPath is { } roomDirectoryPath)
                {
                    state.CurrentWakes = await DeriveCurrentWakesAsync(roomDirectoryPath, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A malformed journal or a lane mid-write must not kill the bridge's own loop --
                // the next tick re-reads from scratch and self-heals the moment the write settles.
                Console.Error.WriteLine($"RoomWakeBridge: derivation failed, will retry next tick: {ex}");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Public as the pure-plus-probe seam: room state and lane probes are computed here, but the
    /// actual set assembly is <see cref="RoomWakeDerivation.DeriveWakes"/> alone — exercised
    /// directly by the pure-derivation unit tests without spinning up a hosted service.
    /// </summary>
    public static async Task<IReadOnlyList<RoomWake>> DeriveCurrentWakesAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
        var reader = new RoomEventLogReader(roomLogPath);
        var roomEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var roomState = RoomProjector.Project(roomEvents);

        var probes = new Dictionary<HeldWorkRef, LaneProbeResult>();
        foreach (var (@ref, heldWork) in roomState.HeldWork)
        {
            if (heldWork.Status == HeldWorkStatus.Resolved)
            {
                continue;
            }

            probes[@ref] = await LaneTerminalProbe.ProbeAsync(@ref.LaneDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
        }

        return RoomWakeDerivation.DeriveWakes(roomState, probes);
    }
}
