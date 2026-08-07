using Aer.Flow;

namespace Aer.Adapters;

/// <summary>
/// Raised when room materialization is asked to write into a directory that already holds
/// a materialized room (a <c>workflow.json</c> already exists there). Without this guard, a
/// duplicate room name silently overwrote the prior room's <c>workflow.json</c>,
/// <c>bindings.json</c>, and (for interactive sessions) <c>.aer/room.json</c> — destroying its
/// turn history and vendor session id with no error at all, since <c>Directory.CreateDirectory</c>
/// is a no-op on an existing directory and every writer downstream truncates unconditionally.
/// </summary>
public sealed class RoomDirectoryAlreadyExistsException(string message) : AerFlowException(message)
{
}
