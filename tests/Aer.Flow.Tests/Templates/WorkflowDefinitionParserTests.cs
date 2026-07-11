using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Flow.Tests.Templates;

public class WorkflowDefinitionParserTests
{
    private static WorkflowDefinition ThreeStepLinearDefinition() => new(
        new WorkflowTemplateId("architect-critic-synth"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(
                new StepId("architect"),
                "architect",
                Inputs: ["goal"],
                Outputs: ["plan"],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(MaxAttempts: 3)),
            new WorkflowStepDefinition(
                new StepId("critic"),
                "critic",
                Inputs: ["plan"],
                Outputs: ["review"],
                DependsOn: [new StepId("architect")],
                RetryPolicy: new RetryPolicy(MaxAttempts: 1),
                PausePoint: new PausePoint(SupersedeTargets: [new StepId("architect")])),
            new WorkflowStepDefinition(
                new StepId("synth"),
                "synth",
                Inputs: ["review"],
                Outputs: ["result"],
                DependsOn: [new StepId("critic")],
                RetryPolicy: new RetryPolicy(MaxAttempts: 1)),
        ]);

    [Fact]
    public void A_valid_three_step_definition_parses_successfully()
    {
        var json = JsonSerializer.Serialize(ThreeStepLinearDefinition());

        var parsed = WorkflowDefinitionParser.Parse(json);

        Assert.Equal(3, parsed.Steps.Count);
        Assert.Equal("architect-critic-synth", parsed.WorkflowTemplateId.Value);
    }

    [Fact]
    public async Task LoadFromFileAsync_reads_and_parses_a_template_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"template-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ThreeStepLinearDefinition()));
        try
        {
            var parsed = await WorkflowDefinitionParser.LoadFromFileAsync(path);

            Assert.Equal(3, parsed.Steps.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Malformed_json_is_rejected_with_a_clear_error()
    {
        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse("{ not valid json"));

        Assert.Contains(ex.Errors, e => e.Contains("Malformed"));
    }

    [Fact]
    public void A_null_json_document_is_rejected()
    {
        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse("null"));

        Assert.Contains(ex.Errors, e => e.Contains("did not contain a WorkflowDefinition"));
    }

    [Fact]
    public void A_template_missing_the_steps_array_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        // System.Text.Json does not enforce non-nullable reference-typed record parameters by
        // default, so a template that simply omits "Steps" deserializes with Steps == null.
        var json = """{"WorkflowTemplateId":"x","WorkflowTemplateVersion":1}""";

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("Steps is missing"));
    }

    [Fact]
    public void A_step_missing_its_dependsOn_array_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        var json = """
            {
              "WorkflowTemplateId": "x",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                { "StepId": "a", "Worker": "worker", "Inputs": [], "Outputs": [], "RetryPolicy": { "MaxAttempts": 1 } }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("missing DependsOn"));
    }

    [Fact]
    public void A_pausePoint_missing_its_supersedeTargets_array_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        var json = """
            {
              "WorkflowTemplateId": "x",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                {
                  "StepId": "a",
                  "Worker": "worker",
                  "Inputs": [],
                  "Outputs": [],
                  "DependsOn": [],
                  "RetryPolicy": { "MaxAttempts": 1 },
                  "PausePoint": {}
                }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("missing SupersedeTargets"));
    }

    [Fact]
    public void A_step_missing_its_RetryPolicy_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        var json = """
            {
              "WorkflowTemplateId": "x",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                { "StepId": "a", "Worker": "worker", "Inputs": [], "Outputs": [], "DependsOn": [] }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("missing RetryPolicy"));
    }

    [Fact]
    public void A_RetryPolicy_with_MaxAttempts_less_than_one_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("bad-retry"),
            1,
            Steps: [new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(MaxAttempts: 0))]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("MaxAttempts '0'"));
    }

    [Fact]
    public void Duplicate_StepIds_are_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("dup"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("Duplicate StepId 'a'"));
    }

    [Fact]
    public void An_undeclared_DependsOn_reference_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("bad-dep"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [new StepId("ghost")], new RetryPolicy(1)),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("'ghost'"));
    }

    [Fact]
    public void A_cyclic_DependsOn_graph_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("cycle"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [new StepId("b")], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "worker", [], [], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("Cyclic"));
    }

    [Fact]
    public void A_SupersedeTarget_that_is_not_a_transitive_ancestor_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("bad-supersede"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("c"),
                    "worker",
                    [],
                    [],
                    [new StepId("a")],
                    new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [new StepId("b")])),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("SupersedeTarget 'b'"));
    }

    [Fact]
    public void A_SupersedeTarget_that_is_a_transitive_but_not_direct_ancestor_is_accepted()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("transitive-supersede"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "worker", [], [], [new StepId("a")], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("c"),
                    "worker",
                    [],
                    [],
                    [new StepId("b")],
                    new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [new StepId("a")])),
            ]);

        var parsed = WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition));

        Assert.Equal(3, parsed.Steps.Count);
    }
}
