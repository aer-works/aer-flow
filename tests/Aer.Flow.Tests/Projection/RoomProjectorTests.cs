using Aer.Flow.Domain;
using Aer.Flow.Mutation;
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

    // #832: a memory-proposal held-work item's Ref is a capture FILE, not a lane directory. Before
    // #832, RenderStatus had no shape concept and joined every ref against "<ref>/flow.jsonl" --
    // for a file ref that join can never exist, so every memory-proposal item rendered the loud
    // "lane never started" line regardless of whether its capture file was actually still there.

    private static readonly HeldWorkRef MemoryProposalRef = new("captures/proposal-1.json");

    [Fact]
    public void Reconciler_status_arm_lane_shaped_item_with_journal_renders_todays_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(state.HeldWork[LaneRefA], laneJournalExistsProbe: _ => true);

        Assert.Equal("dispatched", rendered);
    }

    [Fact]
    public void Reconciler_status_arm_lane_shaped_item_without_journal_renders_todays_orphan_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(state.HeldWork[LaneRefA], laneJournalExistsProbe: _ => false);

        Assert.Equal(
            $"dispatch recorded; lane never started (no ledger found at {LaneRefA.LaneDirectoryPath})",
            rendered);
    }

    [Fact]
    public void Reconciler_status_arm_memory_proposal_with_capture_file_renders_operator_wait_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(
                MemoryProposalRef, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget, "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(
            state.HeldWork[MemoryProposalRef], memoryProposalFileExistsProbe: _ => true);

        Assert.Equal("awaiting operator decision (memory proposal)", rendered);
    }

    [Fact]
    public void Reconciler_status_arm_escalated_memory_proposal_renders_the_generic_escalation_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(
                MemoryProposalRef, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget, "decider-1"),
            new RoomEvent.HeldWorkEscalated(MemoryProposalRef, "operator"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(
            state.HeldWork[MemoryProposalRef], memoryProposalFileExistsProbe: _ => true);

        Assert.Equal("escalated to operator", rendered);
    }

    [Fact]
    public void Reconciler_status_arm_memory_proposal_without_capture_file_renders_missing_file_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(
                MemoryProposalRef, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget, "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(
            state.HeldWork[MemoryProposalRef], memoryProposalFileExistsProbe: _ => false);

        Assert.Equal(
            $"proposal file missing (memory proposal; no capture file found at {MemoryProposalRef.Value})",
            rendered);
    }

    [Fact]
    public void Reconciler_status_arm_unknown_future_shape_deliberately_keeps_the_lane_probe()
    {
        // No shape other than memory-proposal is distinguished today (#832) -- an unrecognised
        // shape falls through to the lane probe deliberately, not silently; this test pins that
        // choice so a future shape added without its own case is caught here, not in production.
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "some-future-shape", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(state.HeldWork[LaneRefA], laneJournalExistsProbe: _ => false);

        Assert.Equal(
            $"dispatch recorded; lane never started (no ledger found at {LaneRefA.LaneDirectoryPath})",
            rendered);
    }
}
