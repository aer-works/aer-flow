using Aer.Flow;

namespace Aer.Ui;

/// <summary>
/// Raised when a directory the UI is pointed at is not a valid task directory. UI spec §3.1: a
/// task directory is self-describing, confirmed only by its actual contents (a persisted
/// <c>snapshot.json</c>) — never assumed from a path alone. Derives from
/// <see cref="AerFlowException"/>, mirroring <c>Aer.Cli.CliArgumentException</c>'s convention, so
/// <c>Program.cs</c> can catch every domain-level failure — this one alongside
/// <c>SnapshotLoadException</c>/<c>FlowEventLogReadException</c> — at a single boundary.
/// </summary>
public sealed class InvalidTaskDirectoryException : AerFlowException
{
    public InvalidTaskDirectoryException(string message)
        : base(message)
    {
    }
}
