using Aer.DesignTokens;

// Regenerates both toolkits' theme resources from design/tokens.json (#345).
//
// The CI gate (Aer.Architecture.Tests) runs the same generator in memory and compares, so this and
// the gate can never disagree about what "correct output" is -- which is the point of sharing
// TokenGenerator rather than reimplementing the comparison.

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
if (repositoryRoot is null)
{
    Console.Error.WriteLine(
        $"Could not locate the repository root (no ancestor directory contains '{TokenGenerator.TokensPath}').");
    return 1;
}

var tokensPath = Path.Combine(repositoryRoot, TokenGenerator.TokensPath);
var generated = TokenGenerator.Generate(await File.ReadAllTextAsync(tokensPath));

foreach (var (relativePath, content) in generated)
{
    var destination = Path.Combine(repositoryRoot, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

    // Byte-identical output must not touch the file: a no-op regeneration that restamped mtimes
    // would make "did anything change?" unanswerable from the filesystem.
    var existing = File.Exists(destination) ? await File.ReadAllTextAsync(destination) : null;
    if (string.Equals(existing, content, StringComparison.Ordinal))
    {
        Console.WriteLine($"  unchanged  {relativePath}");
        continue;
    }

    await File.WriteAllTextAsync(destination, content);
    Console.WriteLine($"  written    {relativePath}");
}

return 0;

static string? FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, TokenGenerator.TokensPath)))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return null;
}
