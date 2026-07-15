using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M16 Phase 2 (issue #151), driven through the real <see cref="MainWindow"/>: add/remove steps, edit
/// their fields, choose <c>DependsOn</c> from declared steps only, live-validate on every edit through
/// <see cref="WorkflowDefinitionValidator"/>, and save — proving the
/// <c>three-step-linear-workflow.json</c> fixture is rebuildable from blank entirely in the UI, and
/// that Save stays blocked until the in-progress graph is valid (Phase 2's save-validity decision of
/// record).
/// </summary>
public class StepEditorTests
{
    private static string FixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string TempTemplatePath()
        => Path.Combine(Path.GetTempPath(), $"ui-step-editor-{Guid.NewGuid():N}.json");

    private static string CopyFixtureToTemp(string fileName)
    {
        var path = TempTemplatePath();
        File.Copy(FixturePath(fileName), path);
        return path;
    }

    [AvaloniaFact]
    public async Task The_three_step_linear_fixture_is_rebuilt_from_blank_entirely_in_the_UI()
    {
        var path = TempTemplatePath();
        try
        {
            var window = new MainWindow();
            var editor = window.ViewModel.TemplateEditor;

            window.NewTemplate();
            editor.TemplateId = "architect-critic-publisher";

            editor.AddStep();
            var architect = editor.Steps[0];
            architect.StepId = "architect";
            architect.Worker = "architect";
            architect.OutputsText = "plan";

            editor.AddStep();
            var critic = editor.Steps[1];
            critic.StepId = "critic";
            critic.Worker = "critic";
            critic.InputsText = "plan";
            critic.OutputsText = "review";
            var architectOption = critic.DependsOnOptions.Single(o => o.StepId == "architect");
            architectOption.IsSelected = true;

            editor.AddStep();
            var publisher = editor.Steps[2];
            publisher.StepId = "publisher";
            publisher.Worker = "publisher";
            publisher.InputsText = "review";
            publisher.OutputsText = "summary";
            var criticOption = publisher.DependsOnOptions.Single(o => o.StepId == "critic");
            criticOption.IsSelected = true;

            Assert.Empty(editor.ValidationErrors);
            Assert.NotNull(editor.PreviewLayout);

            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("Saved", editor.StatusText);

            var expected = await WorkflowDefinitionParser.LoadFromFileAsync(
                FixturePath("three-step-linear-workflow.json"), TestContext.Current.CancellationToken);
            var actual = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected.Steps.Count, actual.Steps.Count);
            for (var i = 0; i < expected.Steps.Count; i++)
            {
                Assert.Equal(expected.Steps[i].StepId, actual.Steps[i].StepId);
                Assert.Equal(expected.Steps[i].Worker, actual.Steps[i].Worker);
                Assert.Equal(expected.Steps[i].Inputs, actual.Steps[i].Inputs);
                Assert.Equal(expected.Steps[i].Outputs, actual.Steps[i].Outputs);
                Assert.Equal(expected.Steps[i].DependsOn, actual.Steps[i].DependsOn);
                Assert.Equal(expected.Steps[i].RetryPolicy, actual.Steps[i].RetryPolicy);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void A_duplicate_StepId_surfaces_as_a_live_violation_and_clears_the_preview()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "same";
        editor.Steps[0].Worker = "architect";

        editor.AddStep();
        editor.Steps[1].StepId = "same";
        editor.Steps[1].Worker = "critic";

        Assert.Contains(editor.ValidationErrors, e => e.Contains("Duplicate StepId"));
        Assert.Null(editor.PreviewLayout);
    }

    [AvaloniaFact]
    public async Task Save_is_blocked_while_the_in_progress_graph_is_invalid()
    {
        var path = TempTemplatePath();
        try
        {
            var window = new MainWindow();
            var editor = window.ViewModel.TemplateEditor;

            window.NewTemplate();
            editor.TemplateId = "invalid-in-progress";
            editor.AddStep();
            editor.Steps[0].StepId = "same";
            editor.AddStep();
            editor.Steps[1].StepId = "same";

            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("Duplicate StepId", editor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void DependsOn_candidates_are_offered_from_declared_steps_only_and_never_include_self()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "a";
        editor.AddStep();
        editor.Steps[1].StepId = "b";

        Assert.Equal(["b"], editor.Steps[0].DependsOnOptions.Select(o => o.StepId));
        Assert.Equal(["a"], editor.Steps[1].DependsOnOptions.Select(o => o.StepId));
    }

    [AvaloniaFact]
    public void A_cyclic_DependsOn_graph_is_reported_without_hanging_or_crashing_layout()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "a";
        editor.AddStep();
        editor.Steps[1].StepId = "b";

        editor.Steps[0].DependsOnOptions.Single(o => o.StepId == "b").IsSelected = true;
        editor.Steps[1].DependsOnOptions.Single(o => o.StepId == "a").IsSelected = true;

        Assert.Contains(editor.ValidationErrors, e => e.Contains("Cyclic DependsOn graph"));
        Assert.Null(editor.PreviewLayout);
    }

    [AvaloniaFact]
    public void Removing_a_step_that_others_depend_on_surfaces_a_live_unresolved_reference_violation()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "a";
        editor.AddStep();
        editor.Steps[1].StepId = "b";
        editor.Steps[1].DependsOnOptions.Single(o => o.StepId == "a").IsSelected = true;

        Assert.Empty(editor.ValidationErrors);

        editor.Steps[0].RemoveCommand.Execute(null);

        Assert.Single(editor.Steps);
        Assert.Contains(editor.ValidationErrors, e => e.Contains("not a declared StepId"));
    }

    [AvaloniaFact]
    public async Task A_loaded_steps_PausePoint_rides_through_a_field_edit_untouched()
    {
        var path = CopyFixtureToTemp("diamond-workflow-with-pause.json");
        try
        {
            var window = new MainWindow();
            var editor = window.ViewModel.TemplateEditor;

            await window.OpenTemplateInEditorAsync(path, TestContext.Current.CancellationToken);

            var stepD = editor.Steps.Single(s => s.StepId == "d");
            stepD.Worker = "publisher-v2";

            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            var saved = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            var stepC = saved.Steps.Single(s => s.StepId.Value == "c");
            Assert.NotNull(stepC.PausePoint);
            Assert.Equal([new StepId("a")], stepC.PausePoint!.SupersedeTargets);

            var savedStepD = saved.Steps.Single(s => s.StepId.Value == "d");
            Assert.Equal("publisher-v2", savedStepD.Worker);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void A_non_numeric_MaxAttempts_surfaces_as_a_violation_and_clears_the_preview()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "a";
        editor.Steps[0].MaxAttemptsText = "not-a-number";

        Assert.Contains(editor.ValidationErrors, e => e.Contains("non-numeric RetryPolicy.MaxAttempts"));
        Assert.Null(editor.PreviewLayout);
    }
}
