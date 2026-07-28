namespace Aer.Cli;

/// <summary>
/// Whether a write a worker is attempting lands in its own outbox — the <c>AER_OUTPUT_DIR</c> AER
/// allocated for this execution — rather than in the workspace (#649).
/// </summary>
/// <remarks>
/// <para>
/// The outbox is not the workspace. <c>AER_OUTPUT_DIR</c> is a directory AER owns under the task
/// directory's <c>artifacts/execution_&lt;id&gt;/</c>, outside the repo entirely, and a grant that
/// withholds "modify the workspace" was never meant to withhold "write your report". Conflating them
/// is why a read-only reviewer cannot produce a deliverable, and why every reviewing template grants
/// a write it does not need.
/// </para>
/// <para>
/// <b>Containment, not prefix matching.</b> Both paths are resolved before comparison, so
/// <c>&lt;outbox&gt;/../../repo/src/x.cs</c> does not pass by sharing a leading substring, and a
/// sibling directory whose name merely starts with the outbox's — <c>artifacts/execution_1-evil</c>
/// beside <c>artifacts/execution_1</c> — is not inside it. Getting this wrong turns a permission
/// boundary into a formality, which is why the traversal and sibling-prefix cases are tested with a
/// legitimate in-outbox control beside them.
/// </para>
/// </remarks>
public static class OutboxPath
{
    /// <summary>
    /// True when <paramref name="candidate"/> resolves to a location inside
    /// <paramref name="outboxDirectory"/>. Fails closed: an unset outbox, an empty candidate, or a
    /// path the OS refuses to resolve all answer false, so an unanswerable question denies rather
    /// than allows.
    /// </summary>
    public static bool IsInsideOutbox(string? candidate, string? outboxDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(outboxDirectory))
        {
            return false;
        }

        string resolvedCandidate;
        string resolvedOutbox;
        try
        {
            resolvedCandidate = Path.GetFullPath(candidate);
            resolvedOutbox = Path.GetFullPath(outboxDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // The trailing separator is what stops `execution_1-evil` counting as inside `execution_1`.
        var outboxWithSeparator = resolvedOutbox.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedOutbox
            : resolvedOutbox + Path.DirectorySeparatorChar;

        // Case-insensitive on Windows only: on Linux `Report.md` and `report.md` are different files,
        // and treating them as one would let a denied path through under a different case.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return resolvedCandidate.StartsWith(outboxWithSeparator, comparison);
    }
}
