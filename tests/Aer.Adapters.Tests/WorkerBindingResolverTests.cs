using Aer.Adapters.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Adapters.Tests;

/// <summary>
/// M11 Phase 1's deliverable: the canonical → <c>CoreDispatchTarget</c> mapping under a fake/echo
/// adapter, and the worker-binding config parsed and resolved into <see cref="WorkerBinding"/>s —
/// no real vendor, no live process.
/// </summary>
public class WorkerBindingResolverTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan")], []);

    [Fact]
    public void An_entry_resolves_to_a_Process_binding_via_its_named_adapter()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), "claude-opus-4", "write-only"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
        Assert.Same(ArchitectContract, binding.Contract);
        Assert.Equal(TimeSpan.FromMinutes(5), binding.Timeout);
    }

    [Fact]
    public void The_resolved_target_carries_the_invocation_and_contract_fields_the_adapter_received()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), "claude-opus-4", "write-only"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal("echo", binding.Target.Program);
        Assert.Equal(
            ["Draft a plan.", "claude-opus-4", "write-only", "architect", "goal", "plan"],
            binding.Target.Args);
    }

    [Fact]
    public void An_entry_with_no_model_or_permission_scope_still_resolves()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal(["Draft a plan.", "(no-model)", "(no-permission-scope)", "architect", "goal", "plan"], binding.Target.Args);
    }

    [Fact]
    public void An_entry_naming_an_unregistered_adapter_throws()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("claude", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var ex = Assert.Throws<UnknownWorkerAdapterException>(() => WorkerBindingResolver.Resolve(config, adapters));
        Assert.Equal("claude", ex.AdapterName);
    }

    [Fact]
    public void Multiple_entries_resolve_independently()
    {
        var criticContract = new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
            ["critic"] = new WorkerBindingConfigEntry("echo", criticContract, "Review the plan.", TimeSpan.FromMinutes(2)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        Assert.Equal(2, bindings.Count);
        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
        Assert.IsType<WorkerBinding.Process>(bindings["critic"]);
    }

    [Fact]
    public void An_empty_config_resolves_to_an_empty_binding_set()
    {
        var bindings = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry>(), new Dictionary<string, IWorkerAdapter>());

        Assert.Empty(bindings);
    }

    // M24 Phase 1 (#262): the live in-turn streaming seam.

    [Fact]
    public void OnWorkerStdoutLine_null_leaves_the_resolved_target_with_no_OnStdoutLine_callback()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Null(binding.Target.OnStdoutLine);
    }

    [Fact]
    public void OnWorkerStdoutLine_when_supplied_is_wrapped_onto_the_target_with_the_workers_own_name()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var received = new List<(string WorkerName, string Line)>();

        var bindings = WorkerBindingResolver.Resolve(
            config, adapters, onWorkerStdoutLine: (workerName, line) => received.Add((workerName, line)));

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.NotNull(binding.Target.OnStdoutLine);
        binding.Target.OnStdoutLine!("a raw stdout line");
        Assert.Equal(("architect", "a raw stdout line"), Assert.Single(received));
    }

    [Fact]
    public void OnWorkerStdoutLine_reports_each_entrys_own_worker_name_independently()
    {
        var criticContract = new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
            ["critic"] = new WorkerBindingConfigEntry("echo", criticContract, "Review the plan.", TimeSpan.FromMinutes(2)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var received = new List<(string WorkerName, string Line)>();

        var bindings = WorkerBindingResolver.Resolve(
            config, adapters, onWorkerStdoutLine: (workerName, line) => received.Add((workerName, line)));

        ((WorkerBinding.Process)bindings["architect"]).Target.OnStdoutLine!("line from architect");
        ((WorkerBinding.Process)bindings["critic"]).Target.OnStdoutLine!("line from critic");

        Assert.Contains(("architect", "line from architect"), received);
        Assert.Contains(("critic", "line from critic"), received);
    }

    // M23 Phase 3 (#272): WorkingDirectory profile resolution and the dialogue PromptTemplate
    // portability fix.

    [Fact]
    public void A_rooted_WorkingDirectory_passes_through_unchanged_with_no_profiles_needed()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "/home/user/my-project"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal("/home/user/my-project", binding.Target.WorkingDirectory);
    }

    [Fact]
    public void A_profile_named_WorkingDirectory_resolves_via_the_supplied_profile_map()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "myproject"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var profiles = new Dictionary<string, string> { ["myproject"] = "/real/machine/path" };

        var bindings = WorkerBindingResolver.Resolve(config, adapters, profiles);

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Equal("/real/machine/path", binding.Target.WorkingDirectory);
    }

    [Fact]
    public void A_profile_named_WorkingDirectory_with_no_matching_profile_throws()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "myproject"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var ex = Assert.Throws<UnknownWorkingDirectoryProfileException>(() =>
            WorkerBindingResolver.Resolve(config, adapters, profiles: null));
        Assert.Equal("architect", ex.WorkerName);
        Assert.Equal("myproject", ex.ProfileName);
    }

    [Fact]
    public void A_profile_named_WorkingDirectory_absent_from_a_non_empty_profile_map_still_throws()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5), WorkingDirectory: "myproject"),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };
        var profiles = new Dictionary<string, string> { ["some-other-project"] = "/real/path" };

        Assert.Throws<UnknownWorkingDirectoryProfileException>(() => WorkerBindingResolver.Resolve(config, adapters, profiles));
    }

    [Fact]
    public void No_WorkingDirectory_at_all_resolves_to_null_regardless_of_profiles()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry("echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["echo"] = new FakeEchoWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters, profiles: new Dictionary<string, string>());

        var binding = (WorkerBinding.Process)bindings["architect"];
        Assert.Null(binding.Target.WorkingDirectory);
    }

    /// <summary>
    /// The portability fix proven through a real adapter, not just the echo fake: a relative
    /// dialogue-sidecar PromptTemplate resolves against the supplied bindingsFileDirectory, the same
    /// end-to-end path <c>DialogueWorkerAdapterTests</c> proves at the adapter level alone.
    /// </summary>
    [Fact]
    public void BindingsFileDirectory_is_forwarded_so_a_relative_dialogue_PromptTemplate_resolves_portably()
    {
        var debateContract = new WorkerContract("debate", [], [new ProducedOutput("verdict.md")], []);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["debate"] = new WorkerBindingConfigEntry(
                "dialogue", debateContract, "dialogue-debate.json", TimeSpan.FromMinutes(5)),
        };
        var adapters = new Dictionary<string, IWorkerAdapter> { ["dialogue"] = new DialogueWorkerAdapter() };

        var bindings = WorkerBindingResolver.Resolve(config, adapters, bindingsFileDirectory: "/configs");

        var binding = (WorkerBinding.Process)bindings["debate"];
        var expected = Path.GetFullPath(Path.Combine("/configs", "dialogue-debate.json"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, binding.Target.Args[4]);
        }
        else
        {
            Assert.Contains($"\"{expected}\"", binding.Target.Args[1]);
        }
    }
}
