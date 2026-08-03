using System.Text;

namespace Aer.Journeys.Tests;

/// <summary>
/// Reads journey ids, titles and status lines out of <c>spec/journeys.md</c> — the single place
/// those three facts are written. <see cref="Journeys.All"/> joins on this at load (#952), so the
/// registry cannot re-declare a title or status that drifts from the spec; before #952 both were
/// hand-copied here and two <see cref="ReconcileTests"/> drift tests policed the copies.
/// </summary>
internal static class SpecJourneys
{
    internal sealed record Entry(string Id, string Title, string Status);

    internal static IReadOnlyList<Entry> Parse()
    {
        // Journey headers are exactly "## J{n} — {title}"; the next "**Status:** {x}" line under
        // one is its status. Plain string parsing (not regex) to sidestep the em-dash / middot the
        // status lines carry.
        const string headerPrefix = "## J";
        const string separator = " — ";
        const string statusPrefix = "**Status:**";

        var journeys = new List<Entry>();
        var lines = File.ReadAllLines(SpecPath(), Encoding.UTF8);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(headerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var sep = line.IndexOf(separator, StringComparison.Ordinal);
            if (sep < 0)
            {
                continue;
            }

            var id = line["## ".Length..sep].Trim();
            var title = line[(sep + separator.Length)..].Trim();

            var status = lines.Skip(i + 1)
                .TakeWhile(l => !l.StartsWith(headerPrefix, StringComparison.Ordinal))
                .FirstOrDefault(l => l.StartsWith(statusPrefix, StringComparison.Ordinal))
                ?[statusPrefix.Length..].Trim();

            if (string.IsNullOrEmpty(status))
            {
                throw new InvalidOperationException(
                    $"Journey {id} in spec/journeys.md has no **Status:** line.");
            }

            journeys.Add(new Entry(id, title, status));
        }

        if (journeys.Count == 0)
        {
            throw new InvalidOperationException(
                "No journeys parsed from spec/journeys.md — has its format changed?");
        }

        return journeys;
    }

    private static string SpecPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "spec", "journeys.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate spec/journeys.md by walking up from " + AppContext.BaseDirectory);
    }
}
