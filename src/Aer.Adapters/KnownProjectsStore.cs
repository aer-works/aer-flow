using System.Text.Json;

namespace Aer.Adapters;

public sealed record KnownProject(
    string FriendlyName,
    string Path,
    DateTimeOffset LastOpenedAt);

public static class KnownProjectsStore
{
    // Re-resolving property, not a static field: a captured value would defeat AER_HOME isolation.
    private static string ProjectsFilePath => System.IO.Path.Combine(AerPaths.Root, "projects.json");

    public static async Task<IReadOnlyList<KnownProject>> LoadProjectsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ProjectsFilePath))
        {
            return Array.Empty<KnownProject>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(ProjectsFilePath, cancellationToken).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<KnownProject>>(json);
            return items?.OrderByDescending(p => p.LastOpenedAt).ToList() ?? (IReadOnlyList<KnownProject>)Array.Empty<KnownProject>();
        }
        catch
        {
            return Array.Empty<KnownProject>();
        }
    }

    public static async Task AddOrUpdateProjectAsync(string projectPath, string? friendlyName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;

        var fullPath = System.IO.Path.GetFullPath(projectPath);
        var projects = (await LoadProjectsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        var existing = projects.FirstOrDefault(p => string.Equals(p.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        var name = !string.IsNullOrWhiteSpace(friendlyName)
            ? friendlyName
            : existing?.FriendlyName ?? System.IO.Path.GetFileName(fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(name)) name = fullPath;

        if (existing != null)
        {
            projects.Remove(existing);
        }

        projects.Insert(0, new KnownProject(name, fullPath, DateTimeOffset.UtcNow));

        if (projects.Count > 50)
        {
            projects = projects.Take(50).ToList();
        }

        var json = JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true });
        var dir = System.IO.Path.GetDirectoryName(ProjectsFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(ProjectsFilePath, json, cancellationToken).ConfigureAwait(false);
    }
}
