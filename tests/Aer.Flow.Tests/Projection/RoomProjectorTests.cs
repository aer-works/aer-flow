using Aer.Flow.Domain;
using Aer.Flow.Projection;

namespace Aer.Flow.Tests.Projection;

public class RoomProjectorTests
{
    private static readonly HeldWorkRef LaneRefA = new("lanes/lane-a");
    private static readonly HeldWorkRef LaneRefB = new("lanes/lane-b");
    private static readonly ExecutionId ExecId = new("exec-lane-a");

    [Fact]
    public void Projects_held_work_lifecycle_purely_and_deterministically()
    {
        var events = new List<RoomEvent>
        {
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkEscalated(LaneRefA, "supervisor-bob"),
            new RoomEvent.HeldWorkResolved(LaneRefA, new LaneJournalCitation("lanes/lane-a", ExecId, "executionSucceeded", 2)),
            new RoomEvent.HeldWorkDispatched(LaneRefB, "shape-2", TimeSpan.FromMinutes(5), "decider-2"),
        };

        var state = RoomProjector.Project(events);

        Assert.Equal(2, state.HeldWork.Count);

        var itemA = state.HeldWork[LaneRefA];
        Assert.Equal(HeldWorkStatus.Resolved, itemA.Status);
        Assert.Equal("supervisor-bob", itemA.EscalatedTo);
        Assert.NotNull(itemA.Citation);
        Assert.Equal(ExecId, itemA.Citation.ExecutionId);

        var itemB = state.HeldWork[LaneRefB];
        Assert.Equal(HeldWorkStatus.Dispatched, itemB.Status);
        Assert.Null(itemB.EscalatedTo);
        Assert.Null(itemB.Citation);
    }

    [Fact]
    public void Replay_determinism_room_projection_output_is_byte_identical_regardless_of_probe()
    {
        var events = new List<RoomEvent>
        {
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        };

        var state1 = RoomProjector.Project(events);
        var state2 = RoomProjector.Project(events);

        Assert.Equal(state1, state2);
        Assert.Equal(state1.HeldWork[LaneRefA], state2.HeldWork[LaneRefA]);
    }

    [Fact]
    public void Polarity_arm_1_ref_with_no_lane_journal_renders_loud_orphan_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1")
        ]);

        var item = state.HeldWork[LaneRefA];
        var rendered = HeldWorkReconciler.RenderStatus(item, laneJournalExistsProbe: _ => false);

        Assert.Contains("dispatch recorded; lane never started", rendered);
    }

    [Fact]
    public void An_escalation_and_resolution_for_an_unknown_ref_surface_as_unmatched_entries()
    {
        var events = new List<RoomEvent>
        {
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkEscalated(LaneRefB, "supervisor-bob"),
            new RoomEvent.HeldWorkResolved(LaneRefB, new LaneJournalCitation("lanes/lane-b", ExecId, "executionSucceeded", 1)),
        };

        var state = RoomProjector.Project(events);

        // The tracked ref is untouched, and the orphans are named in append order -- the why
        // lives on RoomState.UnmatchedEntries' doc.
        Assert.Equal(HeldWorkStatus.Dispatched, state.HeldWork[LaneRefA].Status);
        Assert.Equal(2, state.UnmatchedEntries.Count);
        Assert.Contains("heldWorkEscalated", state.UnmatchedEntries[0]);
        Assert.Contains("lanes/lane-b", state.UnmatchedEntries[0]);
        Assert.Contains("heldWorkResolved", state.UnmatchedEntries[1]);
    }

    [Fact]
    public void A_journal_whose_every_entry_matches_has_no_unmatched_entries()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkEscalated(LaneRefA, "supervisor-bob"),
        ]);

        Assert.Empty(state.UnmatchedEntries);
    }

    [Fact]
    public void Polarity_arm_2_lane_directory_with_no_ref_in_room_journal_is_invisible_to_room()
    {
        // Projection has only LaneRefA; non-referenced lane directory 'lanes/lane-b' is invisible
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1")
        ]);

        Assert.True(state.HeldWork.ContainsKey(LaneRefA));
        Assert.False(state.HeldWork.ContainsKey(LaneRefB));
    }
}
