using Aer.Flow.Dispatch;

namespace Aer.Flow.Tests.TestSupport;

/// <summary>
/// Tiny cross-platform shell <see cref="CoreDispatchTarget"/>s standing in for real workers in
/// integration tests that dispatch through the real aer-core M5 binding (no mocking of Aer.Core
/// itself, per M7 Phase 7's acceptance criteria).
/// </summary>
internal static class ShellWorkerCommands
{
    public static CoreDispatchTarget WriteFile(string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\""]);

    public static CoreDispatchTarget CopyFirstInputTo(string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\""]);

    public static CoreDispatchTarget ExitCleanlyWithoutWriting() => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
        : new CoreDispatchTarget("sh", ["-c", "exit 0"]);
}
