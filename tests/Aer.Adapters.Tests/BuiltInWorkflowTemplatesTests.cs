using Aer.Flow.Templates;

namespace Aer.Adapters.Tests;

public class BuiltInWorkflowTemplatesTests
{
    [Fact]
    public void Catalog_ContainsSoloAndReviewRunTemplates()
    {
        var catalog = BuiltInWorkflowTemplates.Catalog;
        Assert.Equal(2, catalog.Count);
        Assert.Contains(catalog, t => t.Id == "solo-run");
        Assert.Contains(catalog, t => t.Id == "review-run");
    }

    [Fact]
    public void Materialize_SoloRun_ProducesValidDefinitionAndBindings()
    {
        var (definition, bindings) = BuiltInWorkflowTemplates.Materialize("solo-run", "claude", null, "Custom solo prompt");

        Assert.Equal("solo-run-template", definition.WorkflowTemplateId.Value);
        Assert.Single(definition.Steps);
        Assert.Equal("solo-step", definition.Steps[0].StepId.Value);

        Assert.Single(bindings);
        var entry = bindings["solo-worker"];
        Assert.Equal("claude", entry.Adapter);
        Assert.Equal("Custom solo prompt", entry.PromptTemplate);
    }

    [Fact]
    public void Materialize_ReviewRun_ProducesValidTwoStepDefinitionAndBindings()
    {
        var (definition, bindings) = BuiltInWorkflowTemplates.Materialize("review-run", "claude", "gemini");

        Assert.Equal("review-run-template", definition.WorkflowTemplateId.Value);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal("draft", definition.Steps[0].StepId.Value);
        Assert.Equal("review", definition.Steps[1].StepId.Value);
        Assert.NotNull(definition.Steps[1].PausePoint);
        Assert.Contains(definition.Steps[1].PausePoint!.SupersedeTargets, target => target.Value == "draft");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("claude", bindings["draft-worker"].Adapter);
        Assert.Equal("gemini", bindings["review-worker"].Adapter);
    }

    [Fact]
    public void Materialize_ReviewRun_DefaultsReviewerPromptWhenNoSecondaryCustomPromptGiven()
    {
        var (_, bindings) = BuiltInWorkflowTemplates.Materialize("review-run", "claude", "gemini", "Write a roast");

        Assert.Equal(
            "Review draft.md carefully, provide feedback and recommendations, and write to review.md.",
            bindings["review-worker"].PromptTemplate);
    }

    [Fact]
    public void Materialize_ReviewRun_UsesSecondaryCustomPromptForReviewerWhenGiven()
    {
        // Review follow-up (issue #255): the reviewer's prompt used to be hardcoded no matter what
        // the drafter was asked to do -- e.g. asking the drafter for a roast still got the reviewer
        // told to "review draft.md carefully" as a document, not respond to it.
        var (_, bindings) = BuiltInWorkflowTemplates.Materialize(
            "review-run", "claude", "gemini", "Write a roast", "Write your own roast back");

        Assert.Equal("Write a roast", bindings["draft-worker"].PromptTemplate);
        Assert.Equal("Write your own roast back", bindings["review-worker"].PromptTemplate);
    }

    [Fact]
    public async Task MaterializeToDirectoryAsync_PersistsValidFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_template_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync("review-run", "claude", "gemini", tempDir, cancellationToken: TestContext.Current.CancellationToken);

            var workflowPath = Path.Combine(tempDir, "workflow.json");
            var bindingsPath = Path.Combine(tempDir, "bindings.json");
            var metaWorkflow = Path.Combine(tempDir, ".aer", "workflow-path");
            var metaBindings = Path.Combine(tempDir, ".aer", "bindings-path");

            Assert.True(File.Exists(workflowPath));
            Assert.True(File.Exists(bindingsPath));
            Assert.True(File.Exists(metaWorkflow));
            Assert.True(File.Exists(metaBindings));

            var loadedDef = await WorkflowDefinitionParser.LoadFromFileAsync(workflowPath, TestContext.Current.CancellationToken);
            var loadedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken);

            Assert.Equal("review-run-template", loadedDef.WorkflowTemplateId.Value);
            Assert.Equal(2, loadedBindings.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
