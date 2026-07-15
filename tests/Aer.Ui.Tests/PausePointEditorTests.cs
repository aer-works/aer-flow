using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M16 Phase 3 (issue #152), driven through the real <see cref="MainWindow"/>: toggle a step's
/// <c>PausePoint</c>, choose its <c>SupersedeTargets</c> from a checkbox per that step's actual
/// transitive ancestor in the current in-edit graph — never a free-form entry — proving the
/// <c>architect-critic-supersede-workflow.json</c> fixture is rebuildable from blank entirely in the
/// UI, and that a graph edit orphaning an already-selected target surfaces as a live violation
/// immediately rather than being silently dropped.
/// </summary>
public class PausePointEditorTests
{
    private static string FixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string TempTemplatePath()
        => Path.Combine(Path.GetTempPath(), $"ui-pause-editor-{Guid.NewGuid():N}.json");

    private static string CopyFixtureToTemp(string fileName)
    {
        var path = TempTemplatePath();
        File.Copy(FixturePath(fileName), path);
        return path;
    }

    [AvaloniaFact]
    public async Task The_architect_critic_supersede_fixture_is_rebuilt_from_blank_entirely_in_the_UI()
    {
        var path = TempTemplatePath();
        try
        {
            var window = new MainWindow();
            var editor = window.ViewModel.TemplateEditor;

            window.NewTemplate();
            editor.TemplateId = "architect-critic-supersede";

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
            critic.OutputsText = "feedback";
            critic.DependsOnOptions.Single(o => o.StepId == "architect").IsSelected = true;

            critic.HasPausePoint = true;
            Assert.Equal(["architect"], critic.SupersedeTargetOptions.Select(o => o.StepId));
            critic.SupersedeTargetOptions.Single(o => o.StepId == "architect").IsSelected = true;

            Assert.Empty(editor.ValidationErrors);

            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("Saved", editor.StatusText);

            var expected = await WorkflowDefinitionParser.LoadFromFileAsync(
                FixturePath("architect-critic-supersede-workflow.json"), TestContext.Current.CancellationToken);
            var actual = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected.Steps.Count, actual.Steps.Count);
            var actualCritic = actual.Steps.Single(s => s.StepId.Value == "critic");
            Assert.NotNull(actualCritic.PausePoint);
            Assert.Equal([new StepId("architect")], actualCritic.PausePoint!.SupersedeTargets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void SupersedeTargetOptions_offer_only_actual_transitive_ancestors()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "a";
        editor.AddStep();
        editor.Steps[1].StepId = "b";
        editor.AddStep();
        editor.Steps[2].StepId = "c";
        editor.Steps[2].DependsOnOptions.Single(o => o.StepId == "b").IsSelected = true;

        editor.Steps[2].HasPausePoint = true;

        // c depends on b only — a is not c's ancestor and must never be offered (§17.1; §8's
        // "reflect, don't invent").
        Assert.Equal(["b"], editor.Steps[2].SupersedeTargetOptions.Select(o => o.StepId));
    }

    [AvaloniaFact]
    public void Orphaning_an_already_selected_target_surfaces_a_live_violation_instead_of_being_dropped()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "a";
        editor.AddStep();
        editor.Steps[1].StepId = "b";
        var b = editor.Steps[1];
        b.DependsOnOptions.Single(o => o.StepId == "a").IsSelected = true;
        b.HasPausePoint = true;
        b.SupersedeTargetOptions.Single(o => o.StepId == "a").IsSelected = true;

        Assert.Empty(editor.ValidationErrors);

        // Remove the DependsOn edge that made "a" an ancestor of "b" — the selection itself is not
        // silently cleared (SelectedSupersedeTargets still holds "a"), so the validator's own
        // ancestry rule must catch it live.
        b.DependsOnOptions.Single(o => o.StepId == "a").IsSelected = false;

        Assert.Contains(editor.ValidationErrors, e => e.Contains("not a transitive ancestor"));
        Assert.Contains("a", b.SelectedSupersedeTargets);
    }

    [AvaloniaFact]
    public async Task A_loaded_steps_PausePoint_and_SupersedeTargets_are_seeded_correctly_and_a_no_op_save_writes_nothing()
    {
        var path = CopyFixtureToTemp("diamond-workflow-with-pause.json");
        try
        {
            var window = new MainWindow();
            var editor = window.ViewModel.TemplateEditor;
            var contentBefore = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            await window.OpenTemplateInEditorAsync(path, TestContext.Current.CancellationToken);

            var stepC = editor.Steps.Single(s => s.StepId == "c");
            Assert.True(stepC.HasPausePoint);
            Assert.Contains("a", stepC.SelectedSupersedeTargets);
            Assert.Equal(["a", "b"], stepC.SupersedeTargetOptions.Select(o => o.StepId).OrderBy(id => id));

            var stepD = editor.Steps.Single(s => s.StepId == "d");
            Assert.False(stepD.HasPausePoint);

            Assert.False(editor.IsDirty);

            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal("No changes to save.", editor.StatusText);
            Assert.Equal(contentBefore, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Toggling_a_pause_point_off_produces_a_null_PausePoint_regardless_of_prior_selections()
    {
        var window = new MainWindow();
        var editor = window.ViewModel.TemplateEditor;

        window.NewTemplate();
        editor.AddStep();
        editor.Steps[0].StepId = "a";
        editor.AddStep();
        editor.Steps[1].StepId = "b";
        var b = editor.Steps[1];
        b.DependsOnOptions.Single(o => o.StepId == "a").IsSelected = true;
        b.HasPausePoint = true;
        b.SupersedeTargetOptions.Single(o => o.StepId == "a").IsSelected = true;

        b.HasPausePoint = false;

        var (candidate, errors) = editor.BuildCandidate();
        Assert.Empty(errors);
        Assert.Null(candidate!.Steps.Single(s => s.StepId.Value == "b").PausePoint);
    }
}
