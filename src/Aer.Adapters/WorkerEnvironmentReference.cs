namespace Aer.Adapters;

/// <summary>
/// How an AER-computed environment variable is written into prompt text for a worker to expand
/// (<c>%AER_OUTPUT_DIR%</c> on Windows, <c>$AER_OUTPUT_DIR</c> elsewhere).
/// </summary>
/// <remarks>
/// One copy, because there were three: both vendor adapters carried an identical private helper, and
/// #650 needed a third when the interactive session's write instruction moved out of its contract and
/// into its prompt template. The syntax is a property of the shell the worker runs in rather than of
/// any one vendor, so sharing it does not cross Adapter Isolation.
/// </remarks>
internal static class WorkerEnvironmentReference
{
    internal static string For(string name, bool isWindows) => isWindows ? $"%{name}%" : $"${name}";

    internal static string For(string name) => For(name, OperatingSystem.IsWindows());
}
