namespace Workbench.Core;

/// <summary>Conservative, irreversible process ownership; disposing a manager cannot unload its code.</summary>
internal static class LoadedPackageRegistry
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> LoadedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DiscoveredRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static SemaphoreSlim Gate(string root)
    {
        lock (Sync)
        {
            root = Normalize(root);
            if (!Gates.TryGetValue(root, out var gate)) Gates[root] = gate = new(1, 1);
            return gate;
        }
    }

    internal static void SealForDiscovery(string root)
    {
        lock (Sync) DiscoveredRoots.Add(Normalize(root));
    }

    internal static void RecordLoaded(string path)
    {
        lock (Sync) LoadedPaths.Add(Normalize(path));
    }

    internal static void MoveUnloadedDirectory(string source, string destination)
    {
        lock (Sync)
        {
            if (OverlapsLoaded(Normalize(source)) || OverlapsLoaded(Normalize(destination)))
                throw new IOException("A package path was loaded in this process; restart is required.");
            // Registration cannot race the final rename after an earlier check.
            Directory.Move(source, destination);
        }
    }

    internal static bool WasLoaded(string path)
    {
        lock (Sync) return OverlapsLoaded(Normalize(path));
    }

    private static bool OverlapsLoaded(string path) => LoadedPaths.Any(loaded =>
        loaded.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        loaded.StartsWith(WithSeparator(path), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(WithSeparator(loaded), StringComparison.OrdinalIgnoreCase));

    private static string WithSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    internal static bool HasDiscovered(string root)
    {
        lock (Sync) return DiscoveredRoots.Contains(Normalize(root));
    }
}
