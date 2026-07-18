using System.Text;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// Direct shell-less <see cref="IWorkerAdapter"/> (M20 Phase 4): resolves a
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a direct <c>agy</c>
/// (Google Gemini CLI) invocation without shell wrappers. Bypasses cmd.exe and sh, eliminating quoting and
/// command injection risks. Stdin redirection to null is handled natively by the process host.
/// </summary>
public sealed class GeminiWorkerAdapter : IWorkerAdapter
{
    private const string DefaultPermissionScope = "accept-edits";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = invocation.PermissionScope ?? DefaultPermissionScope;
        var artifactsRoot = EnvironmentReference("AER_ARTIFACTS_ROOT", isWindows);

        List<string> args =
        [
            "-p", prompt,
            "--mode", permissionScope,
            "--add-dir", artifactsRoot,
        ];

        if (invocation.Model is not null)
        {
            args.Add("--model");
            args.Add(invocation.Model);
        }

        return new CoreDispatchTarget("agy", [.. args]);
    }

    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(promptTemplate);

        if (contract.RequiredInputs.Count > 0)
        {
            prompt.Append("\n\nInputs, in the order listed, are available at these absolute paths:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {EnvironmentReference($"AER_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each of the following outputs to the exact absolute path shown, creating parent directories as needed:\n");
            foreach (var output in contract.ProducedOutputs)
            {
                var outputDir = EnvironmentReference("AER_OUTPUT_DIR", isWindows);
                var separator = isWindows ? '\\' : '/';
                prompt.Append($"- {output.Name}: {outputDir}{separator}{output.Name}\n");
            }
        }

        return prompt.ToString();
    }

    private static string EnvironmentReference(string name, bool isWindows) => isWindows ? $"%{name}%" : $"${name}";
}
