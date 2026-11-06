namespace Workbench.Core;

/// <summary>Bounded copies of host-owned state only. These snapshots are never restored automatically.</summary>
internal static class PackageDataBackup
{
    private const int MaximumFiles = 10000;
    private const long MaximumBytes = 1024L * 1024 * 1024;

    internal static async Task<string> CreateAsync(string userRoot, string pluginId, string destination, CancellationToken token)
    {
        var files = Inventory(userRoot, pluginId);
        var fingerprint = await PackageValidator.FingerprintAsync(files, token);
        if (Directory.Exists(destination))
        {
            if (await FingerprintAsync(destination, token) != fingerprint)
                throw new IOException("An incomplete state backup differs from current host-managed state.");
        }
        else
        {
            var temporary = destination + ".preparing-" + Guid.NewGuid().ToString("N");
            FileSystemGuard.RejectLinks(temporary);
            Directory.CreateDirectory(temporary);
            try
            {
                await PackageArchive.CopyFilesAsync(files, temporary, MaximumFiles, MaximumBytes, MaximumBytes, token);
                if (await FingerprintAsync(temporary, token) != fingerprint ||
                    await PackageValidator.FingerprintAsync(Inventory(userRoot, pluginId), token) != fingerprint)
                    throw new IOException("Host-managed state changed during backup.");
                FileSystemGuard.RejectLinks(destination);
                Directory.Move(temporary, destination);
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
        }
        return fingerprint;
    }

    internal static async Task<string> FingerprintAsync(string path, CancellationToken token)
    {
        var files = PackageValidator.Inventory(path);
        CheckBounds(files);
        return await PackageValidator.FingerprintAsync(files, token);
    }

    private static IReadOnlyList<PackageFile> Inventory(string userRoot, string pluginId)
    {
        FileSystemGuard.RejectLinks(userRoot);
        var files = new List<PackageFile>();
        foreach (var name in new[] { "data", "settings", "secrets" })
        {
            var root = FileSystemGuard.ContainedPath(userRoot, $"{name}/{pluginId}");
            if (File.Exists(root)) throw new IOException("A host state directory is not a directory.");
            if (!Directory.Exists(root)) continue;
            files.AddRange(PackageValidator.Inventory(root).Select(file => new PackageFile($"{name}/{pluginId}/{file.RelativePath}", file.FullPath)));
        }
        var background = FileSystemGuard.ContainedPath(userRoot, "background-tasks.json");
        if (Directory.Exists(background)) throw new IOException("Background task state is not a regular file.");
        if (File.Exists(background))
        {
            FileSystemGuard.EnsureRegularFile(background);
            files.Add(new("background-tasks.json", background));
        }
        CheckBounds(files);
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void CheckBounds(IReadOnlyList<PackageFile> files)
    {
        if (files.Count > MaximumFiles) throw new IOException("Host state backup exceeds 10000 files.");
        long total = 0;
        foreach (var file in files)
        {
            FileSystemGuard.EnsureRegularFile(file.FullPath);
            if ((total += new FileInfo(file.FullPath).Length) > MaximumBytes)
                throw new IOException("Host state backup exceeds 1 GiB.");
        }
    }
}
