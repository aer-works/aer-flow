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
}
