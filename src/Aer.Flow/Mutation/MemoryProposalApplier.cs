using System.Text.Json;

namespace Aer.Flow.Mutation;

/// <summary>
/// Applies one captured <see cref="MemoryProposalCapture"/> to a room's <c>memory/</c> directory
/// (decision 0044 point 3, #672 item 2). Called ONLY on operator approval — see
/// <see cref="MemoryProposalResolution"/>, the sole caller. Mirrors
/// <c>Aer.Mcp.Host.MemoryProposalTool</c>'s capture shape as a duplicated record for the same
/// cross-project-boundary reason <see cref="MemoryProposalEscalation.CaptureDirectoryName"/>
/// documents: <c>Aer.Flow</c> cannot reference <c>Aer.Mcp.Host</c>. Both
/// <c>MemoryProposalApplierTests</c> (this project) and <c>MemoryProposalToolTests</c>
/// (<c>Aer.Mcp.Host</c>'s own) exercise the identical JSON shape so the two sides cannot drift
/// unnoticed.
/// </summary>
public static class MemoryProposalApplier
{
    public const string MemoryDirectoryName = "memory";

    /// <summary>
    /// Mechanically regenerated on every apply (never hand-edited): one line per fact file
    /// currently under <c>memory/</c>, sorted, so the orchestrator's turn-start read (0044 point 2)
    /// is always in sync with what is actually on disk rather than a record that can drift from a
    /// hand-maintained one.
    /// </summary>
    public const string IndexFileName = "INDEX.md";

    /// <summary>
    /// Reads <paramref name="captureFilePath"/> and applies its proposed operation to
    /// <c>{roomDirectoryPath}/memory/</c>, then regenerates <see cref="IndexFileName"/>.
    /// <paramref name="captureFilePath"/> must resolve strictly inside <c>memory/</c> after joining
    /// with the memory root — a traversal attempt (a rooted path, or a <c>../</c> segment that
    /// escapes the root) is refused loudly via <see cref="InvalidRoomMutationException"/>, never
    /// silently clamped or ignored. Deleting a target that does not exist is likewise a loud
    /// failure, not a silent success, per #672's explicit requirement.
    /// </summary>
    public static async Task ApplyAsync(
        string roomDirectoryPath, string captureFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(captureFilePath);

        if (!File.Exists(captureFilePath))
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal capture file '{captureFilePath}' was not found; cannot apply.");
        }

        var json = await File.ReadAllTextAsync(captureFilePath, cancellationToken).ConfigureAwait(false);
        MemoryProposalCapture capture;
        try
        {
            capture = JsonSerializer.Deserialize<MemoryProposalCapture>(json)
                ?? throw new InvalidRoomMutationException(
                    $"Memory-proposal capture file '{captureFilePath}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal capture file '{captureFilePath}' is not valid JSON: {ex.Message}", ex);
        }

        var memoryRoot = Path.GetFullPath(Path.Combine(roomDirectoryPath, MemoryDirectoryName));
        var resolvedTargetPath = ResolveTargetPathStrictlyInsideMemory(memoryRoot, capture.TargetPath);

        switch (capture.Operation)
        {
            case "add" or "edit":
                if (capture.Content is null)
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal capture file '{captureFilePath}' has operation '{capture.Operation}' " +
                        "but no content.");
                }

                // 0044 point 3: nothing writes memory but an approved decision -- an 'add' that
                // silently overwrote an existing fact, or an 'edit' that silently created a new
                // one, would each contain a write nobody actually approved (the operator approved
                // the proposal they read, not whatever collided with it by the time this ran).
                // Loud refusal, same posture as the delete-of-a-missing-target guard below.
                var targetExists = File.Exists(resolvedTargetPath);
                if (capture.Operation == "add" && targetExists)
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal 'add' target '{capture.TargetPath}' already exists under " +
                        $"'{memoryRoot}'; refusing to silently overwrite it (use 'edit' instead).");
                }

                if (capture.Operation == "edit" && !targetExists)
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal 'edit' target '{capture.TargetPath}' does not exist under " +
                        $"'{memoryRoot}'; refusing to silently create it (use 'add' instead).");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(resolvedTargetPath)!);

                // Temp-then-move, matching MemoryProposalTool's own convention: a reader of memory/
                // never observes a partial write.
                var tempTargetPath = resolvedTargetPath + ".tmp";
                await File.WriteAllTextAsync(tempTargetPath, capture.Content, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(tempTargetPath, resolvedTargetPath, overwrite: true);
                break;

            case "delete":
                if (!File.Exists(resolvedTargetPath))
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal delete target '{capture.TargetPath}' does not exist under " +
                        $"'{memoryRoot}'; refusing to report a silent success.");
                }

                File.Delete(resolvedTargetPath);
                break;

            default:
                throw new InvalidRoomMutationException(
                    $"Memory-proposal capture file '{captureFilePath}' has unknown operation " +
                    $"'{capture.Operation}'.");
        }

        RegenerateIndex(memoryRoot);
    }

    /// <summary>
    /// Joins <paramref name="targetPath"/> onto <paramref name="memoryRoot"/> and canonicalizes,
    /// then requires the result to sit strictly inside the root. <see cref="Path.Combine"/> returns
    /// a rooted second argument verbatim (ignoring the first), so a rooted <paramref
    /// name="targetPath"/> (an absolute Windows or Unix path) surfaces here as a canonical path
    /// outside <paramref name="memoryRoot"/> exactly like a <c>../</c> escape does — one guard
    /// catches both shapes, non-negotiable per #672.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFullPath(string)"/> is purely lexical: it collapses <c>..</c> segments
    /// textually but never asks the filesystem whether a directory along the way is a junction or
    /// symlink. #856: a reparse point already sitting under <paramref name="memoryRoot"/> (a
    /// junction is creatable by anything with plain write access to the room directory, no admin
    /// needed on Windows -- see <see cref="ResolveReparsePointsIgnoringMissingTail"/>) passes the
    /// lexical check above and would let the actual disk write land wherever the link points,
    /// because the OS follows reparse points transparently for every normal file API. This is
    /// defense-in-depth for the engine's own promise that an approved apply writes strictly inside
    /// <c>memory/</c> -- not a privilege boundary: an attacker who can already place a junction
    /// under <c>memory/</c> already has write access to the room directory and could edit
    /// <c>memory/</c> directly.
    /// </remarks>
    internal static string ResolveTargetPathStrictlyInsideMemory(string memoryRoot, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new InvalidRoomMutationException("Memory-proposal targetPath must not be empty.");
        }

        var combined = Path.GetFullPath(Path.Combine(memoryRoot, targetPath));

        var rootWithSeparator = memoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? memoryRoot
            : memoryRoot + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal targetPath '{targetPath}' resolves outside memory/ (to '{combined}'); refused.");
        }

        // The lexical check above passed. Re-check containment against the reparse-resolved path:
        // a junction/symlink for an ancestor directory (or the leaf itself) that exists today under
        // memoryRoot can redirect the write outside it even though the string-only check above is
        // satisfied. A link that resolves back inside memoryRoot is left alone (item 2, #856) --
        // this only refuses the case where resolution actually escapes.
        //
        // Both sides of this comparison MUST go through the identical resolution walk. An earlier
        // version of this fix resolved memoryRoot only if memoryRoot itself was a reparse point,
        // while resolving combined by walking every segment beneath it -- so if the room directory
        // is itself reached through a junction (memoryRoot is an ordinary directory, but one of
        // its own ancestors is a link), realCombined came back fully resolved past that ancestor
        // while realRoot stayed lexical, and a legitimate in-tree alias was wrongly refused. Proven
        // by reproduction: rooting the temp room directory itself behind a junction and re-running
        // the allow-arm reproduced exactly this false positive before this fix.
        var realRoot = ResolveReparsePointsIgnoringMissingTail(memoryRoot);
        var realCombined = ResolveReparsePointsIgnoringMissingTail(combined);

        var caseComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var realRootWithSeparator = realRoot.EndsWith(Path.DirectorySeparatorChar)
            ? realRoot
            : realRoot + Path.DirectorySeparatorChar;

        if (!string.Equals(realCombined, realRoot, caseComparison)
            && !realCombined.StartsWith(realRootWithSeparator, caseComparison))
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal targetPath '{targetPath}' resolves outside memory/ through a reparse point " +
                $"(to '{realCombined}'); refused.");
        }

        return combined;
    }

    /// <summary>
    /// Fully resolves <paramref name="path"/> by walking every segment from its filesystem root
    /// down, resolving each existing ancestor that is itself a reparse point (following chained
    /// links via <c>returnFinalTarget: true</c>) before appending the next segment. A segment that
    /// does not exist yet (the common case for an 'add' whose parent directories get created later
    /// by <see cref="ApplyAsync"/>) is appended literally with no resolution attempted -- there is
    /// nothing on disk yet for it to redirect through. Starting from the root rather than from
    /// <c>memoryRoot</c> is what lets <c>memoryRoot</c> itself and a target beneath it be resolved
    /// symmetrically, even when an ancestor of <c>memoryRoot</c> (not memoryRoot itself) is the
    /// reparse point.
    /// </summary>
    private static string ResolveReparsePointsIgnoringMissingTail(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return ResolveIfReparsePoint(root);
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                current = ResolveIfReparsePoint(current);
            }
        }

        return current;
    }

    /// <summary>
    /// Returns <paramref name="path"/> unchanged unless it exists and is itself a reparse point, in
    /// which case returns the fully-resolved final target (chained junctions/symlinks included).
    /// </summary>
    private static string ResolveIfReparsePoint(string path)
    {
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            return path;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return path;
        }

        var resolved = isDirectory
            ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
            : File.ResolveLinkTarget(path, returnFinalTarget: true);

        return resolved?.FullName ?? path;
    }

    private static void RegenerateIndex(string memoryRoot)
    {
        Directory.CreateDirectory(memoryRoot);

        var factFiles = Directory.GetFiles(memoryRoot, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(IndexFileName, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(memoryRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>
        {
            "# Memory index",
            "",
            "Mechanically regenerated on every applied memory proposal -- do not edit by hand.",
            "",
        };
        lines.AddRange(factFiles.Select(f => $"- {f}"));

        var indexPath = Path.Combine(memoryRoot, IndexFileName);
        var tempIndexPath = indexPath + ".tmp";
        File.WriteAllLines(tempIndexPath, lines);
        File.Move(tempIndexPath, indexPath, overwrite: true);
    }
}

/// <summary>The structured shape a capture file holds, mirroring <c>Aer.Mcp.Host.MemoryProposalTool</c>'s own record of the same name.</summary>
public sealed record MemoryProposalCapture(string Operation, string TargetPath, string? Content, string Rationale);
