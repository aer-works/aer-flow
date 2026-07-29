namespace Aer.Flow.Dispatch;

/// <summary>
/// The environment variables a spawned worker inherits from the operator's shell — an allowlist,
/// because until #549 it inherited everything (<c>AerTask.WithClearEnv</c> was never called, and the
/// binding's default is inherit-all).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an allowlist and not a denylist.</b> The gate a worker runs under can be changed by
/// variables AER does not choose: <c>CLAUDE_CODE_SIMPLE=1</c> disables hooks exactly as <c>--bare</c>
/// does, and it needs only to be exported in the shell the daemon was started from. A denylist has to
/// enumerate every such variable, for every vendor, forever, and fails **open** on the one nobody
/// thought of. This fails **closed**: an unknown variable does not reach the worker. The direction is
/// the same argument <c>ClaudeWorkerAdapter</c> records for never passing <c>--bare</c> — an auth
/// failure is loud, and a missing gate is silent.
/// </para>
/// <para>
/// <b>The per-vendor minimum is measured, not assumed</b> — see <c>docs/vendor-doc-audit.md</c>
/// §"Environment starvation" for what each CLI survives and which of them can serve as a control.
/// An advisory pass proposed a much wider list on credential-discovery grounds that the measurement
/// contradicts.
/// </para>
/// <para>
/// Entries beyond that measured minimum are ordinary OS and toolchain plumbing, included to keep the
/// blast radius of clearing the environment small. None of them is known to influence a permission
/// gate; anything that does belongs in an adapter's own explicit <c>Environment</c>, which is applied
/// after this and therefore wins.
/// </para>
/// </remarks>
internal static class InheritedEnvironment
{
    /// <summary>Meaningful on every platform AER runs on.</summary>
    private static readonly string[] Common =
    [
        // Load-bearing: AER spawns "claude"/"agy"/"dotnet" by NAME, so without PATH the spawn itself
        // fails before any vendor question arises.
        "PATH",
        "LANG", "LC_ALL", "LC_CTYPE", "TZ",
        // The .NET host reads these; the dialogue worker is `dotnet exec`.
        "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT", "NUGET_PACKAGES",
    ];

    private static readonly string[] Windows =
    [
        // USERPROFILE is the measured one (agy). The rest are what a Windows process and cmd.exe
        // assume exist; SYSTEMROOT in particular is required for socket and crypto initialisation.
        "USERPROFILE", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)",
        "COMSPEC", "PATHEXT", "TEMP", "TMP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
    ];

    private static readonly string[] Unix =
    [
        "HOME", "SHELL", "USER", "LOGNAME", "TMPDIR",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME",
    ];

    /// <summary>The allowlisted names for the current platform, in a stable order.</summary>
    internal static IReadOnlyList<string> Names =>
        [.. Common, .. OperatingSystem.IsWindows() ? Windows : Unix];

    /// <summary>
    /// Each allowlisted variable that is actually set, with the value this process sees. Variables
    /// that are unset are skipped rather than passed as empty — an empty <c>USERPROFILE</c> is a
    /// different failure from an absent one, and neither is worth inventing.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> Resolve()
    {
        foreach (var name in Names)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                yield return (name, value);
            }
        }
    }
}
