using Aer.Adapters.Tests.TestSupport;
using Aer.Flow.Dispatch;
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
            ["Draft a plan.", "claude-opus-4", "write-only", "(no-permission-grant)", "architect", "goal", "plan"],
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
        Assert.Equal(
            ["Draft a plan.", "(no-model)", "(no-permission-scope)", "(no-permission-grant)", "architect", "goal", "plan"],
            binding.Target.Args);
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

    /// <summary>
    /// #588: the binding entry's <c>Timeout</c> must reach the adapter, not just
    /// <c>WorkerBinding.Process</c>. <c>agy -p</c> applies its own hardcoded 5-minute print-mode wait
    /// unless told otherwise, so an adapter that cannot see AER's timeout silently caps every long
    /// task at 5 minutes regardless of what the operator configured.
    /// </summary>
    [Fact]
    public void Resolve_hands_the_entrys_Timeout_to_the_adapter_as_well_as_to_the_binding()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "capture", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(20)),
        };
        var adapter = new CapturingWorkerAdapter();
        var adapters = new Dictionary<string, IWorkerAdapter> { ["capture"] = adapter };

        var bindings = WorkerBindingResolver.Resolve(config, adapters);

        // Both halves, because they are separately wrong-able: the binding carrying it while the
        // adapter does not is exactly the pre-#588 state, and that state looked entirely correct
        // from Aer.Flow's side.
        Assert.Equal(TimeSpan.FromMinutes(20), adapter.LastInvocation!.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(20), Assert.IsType<WorkerBinding.Process>(bindings["architect"]).Timeout);
    }

    /// <summary>Records the <see cref="WorkerInvocation"/> it was handed, and nothing else.</summary>
    private sealed class CapturingWorkerAdapter : IWorkerAdapter
    {
        public WorkerInvocation? LastInvocation { get; private set; }

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
        {
            LastInvocation = invocation;
            return new CoreDispatchTarget("echo", []);
        }
    }
    // ---------------------------------------------------------------------------------------
    // #529 — a granted shell reaches three of the four categories, so withholding one while
    // granting the shell does not withhold it. These assert the bind-time refusal.
    // ---------------------------------------------------------------------------------------

    private static Dictionary<string, IWorkerAdapter> EchoAdapter() =>
        new() { ["echo"] = new FakeEchoWorkerAdapter() };

    private static Dictionary<string, WorkerBindingConfigEntry> ConfigWithGrant(PermissionGrant grant) =>
        new()
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5),
                PermissionGrant: grant),
        };

    [Theory]
    // Each withholds exactly one category a granted Bash reaches. #529 measured the write arm
    // directly: --disallowedTools Edit,Write,NotebookEdit removed those tools and the model
    // created the file with Bash instead.
    [InlineData(false, true, true, "WriteFiles")]
    [InlineData(true, false, true, "ReadFiles")]
    [InlineData(true, true, false, "NetworkAccess")]
    public void A_grant_that_withholds_a_category_a_granted_shell_reaches_is_refused(
        bool writeFiles, bool readFiles, bool networkAccess, string expectedCategoryInMessage)
    {
        var grant = new PermissionGrant(
            ReadFiles: readFiles, WriteFiles: writeFiles,
            RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: networkAccess);

        var thrown = Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));

        // EXACTLY the withheld one. `Assert.Contains` on the message would pass on a resolver that
        // named all three every time, and the sibling test below cannot see that either — its grant
        // withholds all three, so an over-broad list is indistinguishable from a correct one there.
        // The message is the operator-facing artifact: naming a category they already granted tells
        // them to grant it again.
        Assert.Equal([expectedCategoryInMessage], thrown.WithheldCategories);
        Assert.Contains(expectedCategoryInMessage, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("architect", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_with_the_shell_and_every_reachable_category_granted_resolves()
    {
        // The control arm. Without it the check above passes on a resolver that refuses every
        // grant carrying a shell, which would be a different and much worse defect.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true,
            RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: true);

        var bindings = WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_grant_that_withholds_categories_without_the_shell_resolves()
    {
        // The second control. Withholding writes is perfectly coherent when no shell is granted —
        // #529 is about a shell that defeats a withhold, and there is no shell here to defeat one.
        //
        // Deliberately on a contract with no declared outputs. It used to use one that declares
        // "plan", described as "the ordinary read-only reviewer", and that shape is now refused by
        // #629's separate rule — a worker that must produce an artifact and cannot write is
        // unsatisfiable whether or not a shell is involved. Keeping the old contract here would make
        // this control fail for a reason that has nothing to do with what it controls for.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false,
            RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

        var bindings = WorkerBindingResolver.Resolve(ConfigWith(NoOutputsContract, grant), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_shell_command_pattern_allowlist_does_not_exempt_the_grant_from_the_refusal()
    {
        // The tempting exemption, and the reason it is wrong: a pattern list reaches only
        // --allowedTools, which gate.allowedtools-is-preapproval-not-ceiling measured to be
        // pre-approval rather than a ceiling. --disallowedTools has no narrowed Bash(…) form at all,
        // so patterns change what is pre-approved and never what is reachable.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false,
            RunShellCommands: true, ShellCommandPatterns: ["git:*"], NetworkAccess: true);

        Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));
    }

    [Fact]
    public void Every_category_a_shell_defeats_is_named_at_once_rather_than_one_per_run()
    {
        // An operator fixing these one at a time would hit the refusal three times over.
        var grant = new PermissionGrant(
            ReadFiles: false, WriteFiles: false,
            RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: false);

        var thrown = Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWithGrant(grant), EchoAdapter()));

        Assert.Equal(["ReadFiles", "WriteFiles", "NetworkAccess"], thrown.WithheldCategories);
    }

    [Fact]
    public void An_entry_with_no_structured_grant_at_all_resolves()
    {
        // Third control: the coherence check must not fire on the many entries that carry no
        // PermissionGrant, which is still the common case.
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "echo", ArchitectContract, "Draft a plan.", TimeSpan.FromMinutes(5)),
        };

        var bindings = WorkerBindingResolver.Resolve(config, EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    // ---------------------------------------------------------------------------------------
    // #629 — a step that must produce an artifact, bound to a worker that cannot write one.
    // ---------------------------------------------------------------------------------------

    private static readonly WorkerContract NoOutputsContract = new("architect", ["goal"], [], []);

    private static Dictionary<string, WorkerBindingConfigEntry> ConfigWith(
        WorkerContract contract, PermissionGrant? grant, string adapter = "echo") =>
        new()
        {
            ["architect"] = new WorkerBindingConfigEntry(
                adapter, contract, "Draft a plan.", TimeSpan.FromMinutes(5), PermissionGrant: grant),
        };

    [Fact]
    public void A_contract_with_outputs_bound_to_a_grant_that_cannot_write_is_refused()
    {
        // The only way a vendor worker satisfies ProducedOutputs is by writing the artifact into
        // AER_OUTPUT_DIR. Withholding the write tools makes the contract unsatisfiable on its face,
        // and before this the run was dispatched, paid for in full, and only then failed the check.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);

        var thrown = Assert.Throws<UnsatisfiableOutputContractException>(
            () => WorkerBindingResolver.Resolve(ConfigWith(ArchitectContract, grant), EchoAdapter()));

        Assert.Equal("architect", thrown.WorkerName);
        Assert.Equal(["plan"], thrown.UnwritableOutputs);
    }

    [Fact]
    public void The_refusal_names_every_output_the_worker_cannot_write()
    {
        var contract = new WorkerContract(
            "architect", ["goal"], [new ProducedOutput("plan"), new ProducedOutput("notes.md")], []);

        var thrown = Assert.Throws<UnsatisfiableOutputContractException>(
            () => WorkerBindingResolver.Resolve(
                ConfigWith(contract, new PermissionGrant(ReadFiles: true, WriteFiles: false)), EchoAdapter()));

        Assert.Equal(["plan", "notes.md"], thrown.UnwritableOutputs);
    }

    [Fact]
    public void An_adapter_that_does_not_consume_a_grant_is_not_refused_for_one()
    {
        // The control that killed the first version of this rule. NoOpWorkerAdapter never reads
        // PermissionGrant — AER builds its dispatch itself and it writes its output regardless — so a
        // withheld write there withholds nothing and refusing would reject a binding that works.
        // Unscoped, this rule refused every interactive session, whose anchor is exactly this shape.
        // WorkerAdapterRegistryTests (#651) is what keeps IPermissionGrantTranslator honest as the
        // marker for "this adapter's grant is load-bearing".
        var adapters = new Dictionary<string, IWorkerAdapter>
        {
            [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        };
        var grant = new PermissionGrant(ReadFiles: false, WriteFiles: false);

        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(ArchitectContract, grant, NoOpWorkerAdapter.AdapterName), adapters);

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_contract_with_outputs_and_a_grant_that_can_write_resolves()
    {
        // Without this the checks above pass on a resolver that refuses every contract carrying an
        // output, which would make every real workflow undispatchable.
        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(ArchitectContract, new PermissionGrant(ReadFiles: true, WriteFiles: true)), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void A_contract_with_no_outputs_at_all_resolves_under_a_read_only_grant()
    {
        // The rule kept honest in the other direction: a worker that declares no outputs has nothing
        // to write, so withholding writes is coherent. This is the shape #650 gave the chat step.
        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(NoOutputsContract, new PermissionGrant(ReadFiles: true, WriteFiles: false)), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void An_entry_with_outputs_and_no_structured_grant_resolves()
    {
        // An entry using the raw PermissionScope escape hatch carries no grant, so there is nothing
        // to reconcile against the contract. This is also the interactive anchor's shape today (#651).
        var bindings = WorkerBindingResolver.Resolve(
            ConfigWith(ArchitectContract, grant: null), EchoAdapter());

        Assert.IsType<WorkerBinding.Process>(bindings["architect"]);
    }

    [Fact]
    public void The_shell_refusal_wins_when_a_grant_is_both_incoherent_and_unsatisfiable()
    {
        // Both faults at once. The shell one is reported, because it names the mistake the operator
        // actually made: they reached for the shell believing it escaped the write withhold. Told only
        // that the contract is unsatisfiable, they would grant more shell.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true);

        Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(ConfigWith(ArchitectContract, grant), EchoAdapter()));
    }
}
