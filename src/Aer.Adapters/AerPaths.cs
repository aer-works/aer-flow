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

    /// <summary><c>{Root}/tasks</c> — the workflow (task) directories.</summary>
    public static string Tasks => Path.Combine(Root, "tasks");

    /// <summary><c>{Root}/sessions</c> — the interactive-session directories.</summary>
    public static string Sessions => Path.Combine(Root, "sessions");
}
