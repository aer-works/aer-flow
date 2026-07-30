namespace Aer.Flow.Domain;

/// <summary>
/// Cites an event in a lane's journal (<c>flow.jsonl</c>) without copying its content.
/// </summary>
public sealed record LaneJournalCitation(
    string LaneDirectoryPath,
    ExecutionId ExecutionId,
    string EventType,
    int? LineIndex = null);
