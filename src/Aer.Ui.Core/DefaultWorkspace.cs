namespace Aer.Ui.Core;

/// <summary>
/// Single definition of the guided-flow's default Documents workspace root (#823's
/// <c>record-once</c> fix — this literal previously lived independently in
/// <see cref="NewWorkflowViewModel"/> and <c>HomeView.axaml.cs</c>). Lives here rather than on a
/// ViewModel because <see cref="Views.HomeView"/>-equivalent Avalonia-side code (which has no
/// business referencing a ViewModel just to read a folder name) needs it too, and this project is
/// the shared, Avalonia-free layer both already depend on.
/// </summary>
public static class DefaultWorkspace
{
    /// <summary>The product's current name — see decision 0045.</summary>
    public const string FolderName = "Baton";

    /// <summary>The pre-#823 folder name. Real installs already have data under this path, so it
    /// stays a named constant for the migration in <see cref="WorkspaceMigration"/> rather than
    /// disappearing once <see cref="FolderName"/> changed.</summary>
    public const string LegacyFolderName = "AER Flow";

    public static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FolderName);

    public static string LegacyRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), LegacyFolderName);
}
