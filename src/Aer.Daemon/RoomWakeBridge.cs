using Aer.Flow.Domain;
using Aer.Flow.Mutation;
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
    private volatile IReadOnlyList<LaneProbeFailure> _currentProbeFailures = [];

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

    /// <summary>
    /// Refs whose lane probe threw on the latest tick — surfaced rather than logged-and-lost,
    /// because a person asking "why is there no wake for that lane?" must be able to see the
    /// answer ("any state should be surfaced", operator, 2026-07-30). A failed probe asserts
    /// nothing about the lane; the next tick re-probes and self-heals once the write settles.
    /// </summary>
    public IReadOnlyList<LaneProbeFailure> CurrentProbeFailures
    {
        get => _currentProbeFailures;
        internal set => _currentProbeFailures = value;
    }
}

/// <summary>One lane whose probe threw this tick: the ref and the exception's message.</summary>
public sealed record LaneProbeFailure(HeldWorkRef Ref, string Error);

/// <summary>
/// One tick's full derivation output: the wake set plus every lane whose probe failed. Never
/// persisted, like the wakes themselves.
/// </summary>
public sealed record RoomWakeTick(IReadOnlyList<RoomWake> Wakes, IReadOnlyList<LaneProbeFailure> ProbeFailures);

/// <summary>
/// Daemon-hosted derivation of the room's wake set (#799): watches <c>room.jsonl</c> for appends
/// by length-poll (<c>Aer.Cli.StatusCommand</c>'s own precedent — filesystem change notifications
/// are unreliable cross-platform), recomputes which held-work refs are unresolved, and re-probes
/// each of those lanes' <c>flow.jsonl</c> every tick via <see cref="LaneTerminalProbe"/> — taking no
/// lane's <see cref="Aer.Flow.Concurrency.ConcurrencyGuard"/> for any of it.
/// <para>
/// #878: that used to read "never taking the room's or any lane's" guard, and the room half was
/// false. The same tick also sweeps for new memory proposals, and escalating one goes through
/// <c>RoomMutationInterface.DispatchHeldWorkAsync</c>, which <b>does</b> take the room's guard. It is
/// conditional — a capture file already in the projected state is skipped, so an idle tick locks
/// nothing — which is how the claim survived: it looks true in steady state and is wrong at exactly
/// the moment the lock is contended. #857's fix was written against the wrong belief this sentence
/// invited.
/// </para>
/// Holds no
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
                    // #833: sweep for new memory-proposal captures before deriving wakes, so one
                    // escalated this tick is already visible held work by the time
                    // DeriveCurrentWakesAsync projects state below. Attribution is structural, not a
                    // claim -- see MemoryProposalEscalation.EscalateNewProposalsForRoomAsync's own
                    // doc comment (canonical) for why.
                    await SweepMemoryProposalsAsync(roomDirectoryPath, stoppingToken).ConfigureAwait(false);

                    var tick = await DeriveCurrentWakesAsync(roomDirectoryPath, stoppingToken)
                        .ConfigureAwait(false);
                    state.CurrentWakes = tick.Wakes;
                    state.CurrentProbeFailures = tick.ProbeFailures;
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
    /// The daemon-hosted poller #801 shipped logic for but never wired (#833): escalates every new
    /// memory-proposal capture found under <paramref name="roomDirectoryPath"/>'s own execution
    /// directories into that same room's <c>room.jsonl</c>. Public for the same reason
    /// <see cref="DeriveCurrentWakesAsync"/> is — exercised directly by integration tests without a
    /// hosted service.
    /// </summary>
    public static async Task SweepMemoryProposalsAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
        var reader = new RoomEventLogReader(roomLogPath);
        await using var writer = new RoomEventLogWriter(roomLogPath);

        await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
            roomDirectoryPath, MemoryProposalEscalation.DefaultDeciderIdentity, reader, writer, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Public as the pure-plus-probe seam: room state and lane probes are computed here, but the
    /// actual set assembly is <see cref="RoomWakeDerivation.DeriveWakes"/> alone — exercised
    /// directly by the pure-derivation unit tests without spinning up a hosted service.
    /// </summary>
    public static async Task<RoomWakeTick> DeriveCurrentWakesAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
        var reader = new RoomEventLogReader(roomLogPath);
        var roomEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var roomState = RoomProjector.Project(roomEvents);

        var probes = new Dictionary<HeldWorkRef, LaneProbeResult>();
        var probeFailures = new List<LaneProbeFailure>();
        foreach (var (@ref, heldWork) in roomState.HeldWork)
        {
            if (heldWork.Status == HeldWorkStatus.Resolved)
            {
                continue;
            }

            // Never probed — the rationale is RoomWakeDerivation.DeriveWakes' matching guard
            // (#832); this skip just avoids a nonsense probe against a non-directory ref.
            if (heldWork.Shape == MemoryProposalEscalation.MemoryProposalShape)
            {
                continue;
            }

            // Per-ref isolation: one lane's transiently malformed or mid-write snapshot/journal
            // must suppress only that lane's wake for this tick, never the whole room's recompute.
            // The failed ref stays out of the probe dictionary -- DeriveWakes documents a missing
            // entry as "not (yet) probed, no wake" -- and is surfaced on the tick instead.
            try
            {
                probes[@ref] = await LaneTerminalProbe.ProbeAsync(@ref.LaneDirectoryPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                probeFailures.Add(new LaneProbeFailure(@ref, ex.Message));
            }
        }

        return new RoomWakeTick(RoomWakeDerivation.DeriveWakes(roomState, probes), probeFailures);
    }
}
