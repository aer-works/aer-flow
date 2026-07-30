using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

public sealed record HeldWorkState(
    HeldWorkRef Ref,
    string Shape,
    TimeSpan Budget,
    string DeciderIdentity,
    HeldWorkStatus Status,
    string? EscalatedTo = null,
    LaneJournalCitation? Citation = null);
