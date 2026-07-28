using Aer.Adapters;
using Aer.Flow.Domain;

namespace Aer.Cli.Tests;

/// <summary>
/// The one test that sees both sides of #649's write-tool split. <c>Aer.Adapters</c> cannot reference
/// <c>Aer.Cli</c>, so the adapter decides which tools leave <c>--disallowedTools</c> for the hook and
/// the hook decides which tools the outbox exemption covers, with nothing holding the two in
/// agreement.
/// </summary>
/// <remarks>
/// The adapter's side is <b>derived from a real <c>Resolve</c></b>, never restated: the names that
/// appear on the hook channel but not on the deny flag *are* the tools #649 moved. Writing the list
/// out again here would be a second copy that agrees with the first until someone edits one.
/// </remarks>
public class WriteFamilyContractTests
{
    [Fact]
    public void The_tools_the_adapter_moves_onto_the_hook_are_exactly_the_ones_the_exemption_covers()
    {
        // Writes withheld, shell withheld -- the reviewer grant, and the one shape where the two
        // channels differ. Shell must stay withheld or #529's refusal rejects the binding before it
        // can be resolved.
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Review this.",
                PermissionGrant: new PermissionGrant(
                    ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false)),
            new WorkerContract("reviewer", [], [], []));

        var flag = Split(Arg(target, "--disallowedTools"));
        var hook = Split(
            target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable)
                .Value.Split(':', 2)[1]);

        var movedToTheHook = hook.Except(flag).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(HookCheckCommand.WriteFamilyTools.OrderBy(t => t), movedToTheHook.OrderBy(t => t));
    }

    [Fact]
    public void A_tool_the_exemption_does_not_cover_cannot_reach_the_outbox()
    {
        // Polarity, and the reason the equality above matters. A name on the hook channel that
        // WriteFamilyTools does not carry gets no write target extracted, so IsInsideOutbox is asked
        // about null and denies -- a worker unable to write its own declared output, which is #629's
        // pay-then-fail rather than a permission hole.
        var outbox = Path.Combine(Path.GetTempPath(), "aer-task", "artifacts", "execution_1");
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            tool_name = "MultiEdit",
            tool_input = new { file_path = Path.Combine(outbox, "review.md") },
        });

        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(payload), stderr, "claude:MultiEdit", outbox);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.DoesNotContain("MultiEdit", string.Join(',', HookCheckCommand.WriteFamilyTools));
    }

    private static string? Arg(Aer.Flow.Dispatch.CoreDispatchTarget target, string flag)
    {
        for (var i = 0; i < target.Args.Count - 1; i++)
        {
            if (target.Args[i] == flag)
            {
                return target.Args[i + 1];
            }
        }

        return null;
    }

    private static HashSet<string> Split(string? commaJoined) =>
        string.IsNullOrEmpty(commaJoined)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(commaJoined.Split(','), StringComparer.Ordinal);
}
