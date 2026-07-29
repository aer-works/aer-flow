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
/// Entries beyond that measured minimum are ordinary OS, toolchain and network-reachability
/// plumbing, included to keep the blast radius of clearing the environment small. None of them is
/// known to influence a permission gate; anything that does belongs in an adapter's own explicit
/// <c>Environment</c>, which is applied after this and therefore wins.
/// </para>
/// <para>
/// <b>The measurement is Windows-only, and the Unix list is reasoned rather than measured.</b> Said
/// here because the first version of this file read as if the whole allowlist were evidence-backed:
/// only <c>USERPROFILE</c> carries a measurement, and it is a Windows entry.
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
        // The .NET host reads these; the dialogue worker is `dotnet exec`, which then spawns its own
        // participant children, so any per-spawn setup cost is paid three times per run. Omitting the
        // first-run suppressors and DOTNET_CLI_HOME timed the dialogue e2e tests out at their 30s
        // binding limit on Windows CI while they still passed locally in 2s -- a cleared environment
        // is not free, and the margin that absorbed it locally did not exist on a cold runner.
        "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_CLI_HOME", "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_MULTILEVEL_LOOKUP",
        "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH",

        // REACHABILITY. Both vendor CLIs are network clients, and on a corporate network these are
        // the whole of how they reach anything. Omitting them was a regression this file introduced
        // and did not notice: the allowlist was measured on a host that needs none of them, so every
        // arm passed while an operator behind a proxy would have had every vendor call fail with no
        // network and no TLS trust. Measured-on-one-machine is not measured (`claim-scope`).
        // Lowercase forms are separate variables on POSIX and several clients read only those.
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        "http_proxy", "https_proxy", "no_proxy", "all_proxy",
        "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE",
    ];

    private static readonly string[] Windows =
    [
        // USERPROFILE is the measured one (agy). The rest are what a Windows process and cmd.exe
        // assume exist; SYSTEMROOT in particular is required for socket and crypto initialisation.
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "ALLUSERSPROFILE",
        "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432",
        "COMSPEC", "PATHEXT", "TEMP", "TMP",
        // powershell.exe resolves its own modules through this; the dialogue worker's participants
        // are powershell on Windows.
        "PSMODULEPATH",
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
