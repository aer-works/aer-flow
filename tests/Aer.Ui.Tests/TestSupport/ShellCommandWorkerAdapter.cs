using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// The deterministic, CI-safe <see cref="IWorkerAdapter"/> <see cref="MainWindow.RunAsync"/>'s own
/// tests need (M15 Phase 1, issue #137): resolves a worker-binding config entry's
/// <c>PromptTemplate</c> as a literal shell command line, exactly like
/// <c>Aer.Cli.Tests.TestSupport.ShellCommandWorkerAdapter</c> — duplicated rather than shared,
/// matching this project's established convention of owning its own minimal shell-stub set (see
/// <see cref="ShellWorkerCommands"/>'s own remarks).
/// </summary>
internal sealed class ShellCommandWorkerAdapter : IWorkerAdapter
{
    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", invocation.PromptTemplate])
        : new CoreDispatchTarget("sh", ["-c", invocation.PromptTemplate]);
}
