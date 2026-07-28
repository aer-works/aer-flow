namespace Aer.Cli;

/// <summary>
/// Whether a write a worker is attempting lands in its own outbox — the <c>AER_OUTPUT_DIR</c> AER
/// allocated for this execution — rather than in the workspace (#649).
/// </summary>
/// <remarks>
/// <para>
/// The outbox is not the workspace. <c>AER_OUTPUT_DIR</c> is a directory AER owns under the task
/// directory's <c>artifacts/execution_&lt;id&gt;/</c>, and a grant that withholds "modify the
/// workspace" was never meant to withhold "write your report". Conflating them is why a read-only
/// reviewer cannot produce a deliverable, and why every reviewing template grants a write it does
/// not need.
/// </para>
/// <para>
/// <b>What this proves is "inside the outbox", never "outside the workspace"</b> — those are not the
/// same claim, and the task directory is not required to sit outside the repo: the repo's own
/// dispatcher defaults it to a gitignored scratch subtree <em>within</em> the checkout. Containment
/// in the allocated, per-execution outbox is the whole of the guarantee.
/// </para>
/// <para>
/// <b>Containment, not prefix matching.</b> Both paths are fully resolved before comparison — <c>..</c>
/// normalised and <b>every path component's links followed</b> — so <c>&lt;outbox&gt;/../../repo/src/x.cs</c>
/// does not pass by sharing a leading substring, a sibling directory whose name merely starts with
/// the outbox's (<c>artifacts/execution_1-evil</c> beside <c>artifacts/execution_1</c>) is not inside
/// it, and a symlink planted at <c>&lt;outbox&gt;/escape</c> pointing at the repo does not launder a
/// workspace write into an outbox one. Getting this wrong turns a permission boundary into a
/// formality, which is why each case is tested with a legitimate in-outbox control beside it.
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

        // A relative outbox is not a location. This process is spawned by the vendor CLI and inherits
        // *its* working directory, so resolving one here answers "inside a directory of that name
        // under the worker's cwd" — and the worker's cwd is the workspace. Measured: a run with a
        // relative --task-dir emitted AER_OUTPUT_DIR as `task2\artifacts\execution_<id>`, the worker
        // created that path inside its workspace and wrote there, and this check called it contained.
        // The exemption would have laundered a workspace write. AER always has an absolute path to
        // give; anything else is a question this cannot answer, so it denies.
        if (!Path.IsPathRooted(outboxDirectory))
        {
            return false;
        }

        string resolvedCandidate;
        string resolvedOutbox;
        try
        {
            resolvedCandidate = ResolveLinks(Path.GetFullPath(candidate));
            resolvedOutbox = ResolveLinks(Path.GetFullPath(outboxDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or PathTooLongException or IOException or UnauthorizedAccessException)
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

    /// <summary>
    /// <paramref name="fullPath"/> with every existing path component's link target followed, so a
    /// link cannot make a path outside the outbox look like one inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolves component by component from the root rather than resolving only the deepest existing
    /// entry: the dangerous shape is a link <em>partway along</em> the path
    /// (<c>&lt;outbox&gt;/link-to-repo/src/x.cs</c>), where the final component is an ordinary file
    /// and resolving it alone answers the wrong question.
    /// </para>
    /// <para>
    /// <b>Each component is read with <see cref="FileSystemInfo.LinkTarget"/>, never gated on
    /// <see cref="Directory.Exists"/>.</b> Those checks stat <em>through</em> a link, so a link whose
    /// target does not exist yet answers false to both — and an earlier version of this method took
    /// that as "not a link", appended the component literally, and reported a path resolving into the
    /// workspace as contained. "Does not resolve to an existing entry" is not "has no link to follow":
    /// a dangling link satisfies the first while still being a link. A component that is genuinely
    /// absent has a null <see cref="FileSystemInfo.LinkTarget"/> and is appended unresolved, which is
    /// correct — that is the file a worker is about to create, and its parents were resolved on the
    /// way down.
    /// </para>
    /// </remarks>
    private static string ResolveLinks(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            // Deliberately not Path.Combine: on Windows a segment such as "C:" is itself rooted, and
            // Combine discards everything accumulated so far and returns a drive-relative path. That
            // only ever produced a denial, but it silently stopped being the path this method claims
            // to return.
            current = current.EndsWith(Path.DirectorySeparatorChar)
                ? current + segment
                : current + Path.DirectorySeparatorChar + segment;

            current = FollowLink(current);
        }

        return current;
    }

    /// <summary>
    /// <paramref name="path"/> with its own link chain followed, or unchanged when it is not a link.
    /// </summary>
    /// <remarks>
    /// Hop-limited rather than recursive: a link cycle would otherwise spin here, and this runs on
    /// every write a withheld-write worker attempts. Exhausting the limit returns whatever was
    /// reached, which cannot match the outbox prefix and therefore denies.
    /// </remarks>
    private static string FollowLink(string path)
    {
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            FileSystemInfo probe = new DirectoryInfo(path);
            if (probe.LinkTarget is null)
            {
                probe = new FileInfo(path);
            }

            if (probe.LinkTarget is not { } target)
            {
                return path;
            }

            // A link target may be relative, and is relative to the link's own directory.
            path = Path.GetFullPath(target, Path.GetDirectoryName(path) ?? path);
        }

        return path;
    }

    private const int MaxLinkHops = 16;
}
