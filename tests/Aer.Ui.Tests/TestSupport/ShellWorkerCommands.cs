using Aer.Flow.Dispatch;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// The two tiny cross-platform shell <see cref="CoreDispatchTarget"/>s this project's fixture
/// needs, dispatched through the real aer-core M5 binding — no mocking of Aer.Core, matching every
/// other test project's convention (e.g. <c>Aer.Flow.Tests.TestSupport.ShellWorkerCommands</c>,
/// which this deliberately duplicates rather than shares: each test project owns its own minimal
/// shell-stub set, since none is a production dependency of another).
/// </summary>
internal static class ShellWorkerCommands
{
    public static CoreDispatchTarget WriteFile(string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\""]);

    public static CoreDispatchTarget CopyFirstInputTo(string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\""]);

    /// <summary>
    /// Fails its first invocation and succeeds every one after, keyed off a marker file at a fixed
    /// path outside <c>AER_OUTPUT_DIR</c> — each attempt's output directory is fresh by design (§16),
    /// so durable state across attempts has to live somewhere else. Duplicated from
    /// <c>Aer.Flow.Tests.TestSupport.ShellWorkerCommands</c> rather than shared, matching this file's
    /// own convention (M14 Phase 5, issue #122).
    /// </summary>
    /// <summary>
    /// Announces itself at <paramref name="startedMarkerPath"/>, then blocks until
    /// <paramref name="releaseFilePath"/> appears, and only then writes its output and
    /// <paramref name="finishedMarkerPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #335 needs two runs genuinely in flight at the same instant to prove the daemon can hold
    /// more than one, and it needs them held there long enough to cancel one and watch the other
    /// survive. A synchronous stub cannot do that: it is finished before a second request arrives,
    /// so a "concurrent" test with one would only ever observe two runs that happened to be quick.
    /// </para>
    /// <para>
    /// The three markers are what make the assertions unambiguous rather than timing-dependent.
    /// <b>Started</b> proves the run really is in flight (so "two at once" is observed, not assumed).
    /// <b>Finished</b> proves it ran to completion — a cancelled run's process is killed while
    /// blocked, so its finished marker never appears no matter how long the test waits, and absence
    /// therefore means cancelled rather than merely slow.
    /// </para>
    /// </remarks>
    public static CoreDispatchTarget BlockUntilReleased(
        string startedMarkerPath, string releaseFilePath, string finishedMarkerPath, string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget(
            "powershell",
            [
                "-NoProfile", "-Command",
                $"New-Item -ItemType File -Force '{startedMarkerPath}' | Out-Null; " +
                $"while (-not (Test-Path '{releaseFilePath}')) {{ Start-Sleep -Milliseconds 100 }}; " +
                $"Set-Content -Path (Join-Path $env:AER_OUTPUT_DIR '{outputName}') -Value 'stub-turn-response'; " +
                $"New-Item -ItemType File -Force '{finishedMarkerPath}' | Out-Null",
            ])
        : new CoreDispatchTarget(
            "sh",
            [
                "-c",
                $"touch \"{startedMarkerPath}\"; " +
                $"while [ ! -f \"{releaseFilePath}\" ]; do sleep 0.1; done; " +
                $"echo stub-turn-response > \"$AER_OUTPUT_DIR/{outputName}\"; " +
                $"touch \"{finishedMarkerPath}\"",
            ]);

    public static CoreDispatchTarget FailOnFirstAttemptThenSucceed(string markerFilePath, string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget(
            "cmd",
            ["/c", $"if exist {markerFilePath} (echo {content}>%AER_OUTPUT_DIR%\\{outputName}) else (echo marker>{markerFilePath} & exit 1)"])
        : new CoreDispatchTarget(
            "sh",
            ["-c", $"if [ -f \"{markerFilePath}\" ]; then echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"; else touch \"{markerFilePath}\"; exit 1; fi"]);
}
