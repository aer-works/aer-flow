using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Workers.Dialogue;

namespace Aer.Adapters.Tests;

/// <summary>
/// M17 Phase 4's deliverable (#167): the constructed command/args a <see cref="DialogueWorkerAdapter"/>
/// produces to invoke the <c>Aer.Workers.Dialogue</c> executable, asserted without spawning any real
/// process — a real dispatch through <c>WorkerAdapterRegistry.Default</c> is covered by
/// <c>Aer.Cli.Tests.DialogueDispatchEndToEndTests</c>. Mirrors <see cref="ClaudeWorkerAdapterTests"/>'s
/// shape, minus everything that adapter needs and this one deliberately does not (stdin redirection,
/// multi-line prompt collapsing, an inputs/outputs section) — see <see cref="DialogueWorkerAdapter"/>'s
/// remarks for why.
/// </summary>
public class DialogueWorkerAdapterTests
{
    private static readonly WorkerContract DebateContract = new(
        "debate", [], [new ProducedOutput("verdict.md")], []);

    [Fact]
    public void Resolves_to_a_shell_wrapper_so_AER_OUTPUT_DIR_can_be_expanded()
    {
        var target = new DialogueWorkerAdapter().Resolve(new WorkerInvocation("/configs/debate.json"), DebateContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd", target.Program);
            Assert.Equal("/c", target.Args[0]);
        }
        else
        {
            Assert.Equal("sh", target.Program);
            Assert.Equal("-c", target.Args[0]);
        }
    }

    [Fact]
    public void The_command_invokes_dotnet_exec_against_the_dialogue_worker_dll()
    {
        var target = new DialogueWorkerAdapter().Resolve(new WorkerInvocation("/configs/debate.json"), DebateContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("dotnet", target.Args[1]);
            Assert.Equal("exec", target.Args[2]);
            Assert.Contains("Aer.Workers.Dialogue.dll", target.Args[3]);
        }
        else
        {
            var commandLine = target.Args[1];
            Assert.StartsWith("dotnet exec ", commandLine);
            Assert.Contains("Aer.Workers.Dialogue.dll", commandLine);
        }
    }

    [Fact]
    public void The_resolved_dll_path_is_the_same_assembly_that_defines_DialogueWorkerConfig()
    {
        var target = new DialogueWorkerAdapter().Resolve(new WorkerInvocation("/configs/debate.json"), DebateContract);
        var expectedDllPath = typeof(DialogueWorkerConfig).Assembly.Location;

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expectedDllPath, target.Args[3]);
        }
        else
        {
            Assert.Contains($"\"{expectedDllPath}\"", target.Args[1]);
        }
    }

    [Fact]
    public void The_prompt_template_is_forwarded_as_the_config_file_path_argument()
    {
        var target = new DialogueWorkerAdapter().Resolve(new WorkerInvocation("/configs/debate.json"), DebateContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("/configs/debate.json", target.Args[4]);
        }
        else
        {
            Assert.Contains("\"/configs/debate.json\"", target.Args[1]);
        }
    }

    [Fact]
    public void The_output_directory_argument_is_an_unexpanded_env_var_reference()
    {
        var target = new DialogueWorkerAdapter().Resolve(new WorkerInvocation("/configs/debate.json"), DebateContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("%AER_OUTPUT_DIR%", target.Args[^1]);
        }
        else
        {
            Assert.EndsWith("\"$AER_OUTPUT_DIR\"", target.Args[1]);
        }
    }

    [Fact]
    public void A_config_path_containing_shell_metacharacters_is_defused_not_expanded()
    {
        var invocation = new WorkerInvocation("/configs/$HOME/\"debate\".json");

        var target = new DialogueWorkerAdapter().Resolve(invocation, DebateContract);

        if (OperatingSystem.IsWindows())
        {
            // No POSIX metacharacters need defusing on Windows -- only '%' does (see the '%' test
            // below) -- so the literal path passes straight through as its own argv token.
            Assert.Equal("/configs/$HOME/\"debate\".json", target.Args[4]);
        }
        else
        {
            var commandLine = target.Args[1];
            Assert.Contains("/configs/\\$HOME/\\\"debate\\\".json", commandLine);
        }
    }

    [Fact]
    public void A_percent_sign_in_the_config_path_is_defused_on_windows_so_cmd_cannot_expand_it()
    {
        var invocation = new WorkerInvocation("/configs/100%/debate.json");

        var target = new DialogueWorkerAdapter().Resolve(invocation, DebateContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("/configs/100%%/debate.json", target.Args[4]);
        }
        else
        {
            Assert.Contains("/configs/100%/debate.json", target.Args[1]);
        }
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new DialogueWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, DebateContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("/configs/debate.json"), null!));
    }
}
