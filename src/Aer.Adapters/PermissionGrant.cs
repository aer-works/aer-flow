namespace Aer.Adapters;

/// <summary>
/// A structured, vendor-neutral permission grant (M21 Phase 1) — the builder-UI alternative to a
/// hand-typed <see cref="WorkerInvocation.PermissionScope"/> string. Composable categories; each
/// <see cref="IPermissionGrantTranslator"/> translates the requested set into its own vendor's
/// actual flag syntax, refusing rather than approximating whenever it cannot express the request
/// exactly in either direction — granting more than asked is exactly as wrong as granting less.
/// <para>
/// Precedence when both are set on the same <see cref="WorkerInvocation"/>/<see cref="WorkerBindingConfigEntry"/>:
/// a non-null <see cref="WorkerInvocation.PermissionGrant"/> always wins over
/// <see cref="WorkerInvocation.PermissionScope"/> — see those types' own docs. The bindings editor
/// UI never authors both on the same entry; this only matters for a hand-edited config file.
/// </para>
/// </summary>
/// <param name="ReadFiles">Grants reading files beyond the worker's declared contract inputs.</param>
/// <param name="WriteFiles">Grants creating and editing files.</param>
/// <param name="RunShellCommands">
/// Grants shell/tool command execution. When <paramref name="ShellCommandPatterns"/> is non-empty,
/// vendors that support pattern-scoped shell grants (e.g. Claude's <c>Bash(git:*)</c>) restrict to
/// those patterns; an empty list means "any command" — not every vendor can express the
/// pattern-scoped form (see each <see cref="IPermissionGrantTranslator"/>'s own notes).
/// </param>
/// <param name="ShellCommandPatterns">Command-pattern allowlist (e.g. <c>"git:*"</c>) — only meaningful when <see cref="RunShellCommands"/> is set.</param>
/// <param name="NetworkAccess">Grants outbound network access (web fetch/search tools).</param>
public sealed record PermissionGrant(
    bool ReadFiles = false,
    bool WriteFiles = false,
    bool RunShellCommands = false,
    IReadOnlyList<string>? ShellCommandPatterns = null,
    bool NetworkAccess = false)
{
    /// <summary>
    /// True when every category is unset — the structured equivalent of a blank
    /// <see cref="WorkerInvocation.PermissionScope"/> string, which callers collapse to
    /// <see langword="null"/> rather than persisting an explicit "nothing" record (see
    /// <c>WorkerBindingEntryViewModel.TryBuildEntry</c>'s decision of record).
    /// </summary>
    public bool IsEmpty => !ReadFiles && !WriteFiles && !RunShellCommands && !NetworkAccess
        && (ShellCommandPatterns is null || ShellCommandPatterns.Count == 0);
}
