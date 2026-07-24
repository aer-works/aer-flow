namespace Aer.Adapters;

/// <summary>
/// The single seam through which every reference to AER's per-machine storage root — the
/// <c>~/.aer</c> directory that holds tasks, sessions, profiles, the projects list, the daemon
/// token and paired-client records — is resolved. Before this type those seventeen sites each
/// re-derived <c>Path.Combine(UserProfile, ".aer", …)</c> inline, so nothing could point the
/// whole tree somewhere else at once; routing them here makes redirection a one-line change and
/// per-run test isolation (#318) possible.
/// </summary>
/// <remarks>
/// <para>
/// The root honours the <see cref="HomeEnvironmentVariable"/> override when set to a non-blank
/// value, and otherwise defaults to <c>%USERPROFILE%\.aer</c> on Windows / <c>$HOME/.aer</c> on
/// Unix. A blank (empty or whitespace) value is treated as unset, so a stray empty variable can
/// never silently redirect storage to a bare relative <c>.aer</c>.
/// </para>
/// <para>
/// <b>Resolve, never capture.</b> <see cref="Root"/> reads the environment on every access on
/// purpose: a single process (the test suite above all) can change
/// <see cref="HomeEnvironmentVariable"/> between runs and must be honoured immediately. Assigning
/// any member of this type to a <c>static readonly</c> field re-introduces the one-shot,
/// captured-at-type-load resolution this seam exists to remove — expose a re-resolving property
/// instead.
/// </para>
/// <para>
/// The vendor CLIs' own configuration directories (e.g. Claude Code's <c>~/.claude</c>) are
/// deliberately <b>not</b> routed through here: they belong to those tools, not to AER, and
/// redirecting them via <see cref="HomeEnvironmentVariable"/> would point the vendor CLI at a
/// throwaway directory and break its discovery/auth.
/// </para>
/// </remarks>
public static class AerPaths
{
    /// <summary>
    /// Environment variable that overrides the storage root. A blank value (empty or whitespace)
    /// is treated as unset.
    /// </summary>
    public const string HomeEnvironmentVariable = "AER_HOME";

    private const string DefaultDirectoryName = ".aer";

    /// <summary>
    /// The AER storage root, resolved fresh on every access — <see cref="HomeEnvironmentVariable"/>
    /// when set to a non-blank value, otherwise <c>{UserProfile}/.aer</c>. Never cache this in a
    /// <c>static readonly</c> field (see the type remarks).
    /// </summary>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultDirectoryName)
                : overridden;
        }
    }

    /// <summary>
    /// <c>{Root}/sessions</c> — <b>the</b> record root. Decision 0001 deletes "task" from the product
    /// and defines a session as a running instance of a workflow, so this holds every record: a DAG
    /// run is a session whose workflow is an authored pipeline, exactly as a chat is a session whose
    /// workflow is the conversation shape.
    /// </summary>
    public static string Sessions => Path.Combine(Root, SessionsDirectoryName);

    /// <summary>Directory name of <see cref="Sessions"/> relative to a root.</summary>
    public const string SessionsDirectoryName = "sessions";

    /// <summary>Directory name of <see cref="LegacyTasks"/> relative to a root.</summary>
    public const string LegacyTasksDirectoryName = "tasks";

    /// <summary>
    /// The canonical key for a record directory: absolute, with any trailing separator removed, so
    /// <c>C:\x\run</c>, <c>C:\x\run\</c> and <c>C:\x\..\x\run</c> all resolve to one entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every per-record concurrency primitive keys on a directory path, and each one that derives
    /// its own key is a chance for two of them to disagree about whether two paths are the same
    /// record. #393's per-session turn lock was the first (a mismatch there loses turns); #335's
    /// per-session host state is the second (a mismatch there stops the wrong session). One
    /// normaliser, used by both, is what stops the third from drifting from the first two.
    /// </para>
    /// <para>
    /// Pair this with <see cref="RecordKeyComparer"/>. Path case-sensitivity is per-filesystem, not
    /// per-OS, so neither comparer is universally correct: an ordinal one under-locks on Windows
    /// (two casings of one directory become two records — the actual bug), an ignore-case one
    /// over-locks on a case-sensitive filesystem (two genuinely distinct records serialise against
    /// each other). Over-locking costs throughput; under-locking costs correctness, so the choice
    /// is not close.
    /// </para>
    /// </remarks>
    public static string RecordKey(string directoryPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

    /// <summary>
    /// The comparer every dictionary keyed by <see cref="RecordKey"/> must use. See that method's
    /// remarks for why this errs toward treating two paths as one record.
    /// </summary>
    public static StringComparer RecordKeyComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// <c>{Root}/tasks</c> — the pre-#333 second root, <b>read by the migration only</b>. Nothing
    /// else may write here or enumerate it: doing so would recreate the parallel-root split that
    /// scattered <c>isSession</c> special-casing through the daemon and UI. New records always go to
    /// <see cref="Sessions"/>. Retained (not deleted) after migration so the fold stays reversible.
    /// </summary>
    public static string LegacyTasks => Path.Combine(Root, LegacyTasksDirectoryName);
}
