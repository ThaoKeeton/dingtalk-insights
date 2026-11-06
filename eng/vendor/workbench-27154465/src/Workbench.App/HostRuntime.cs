using Workbench.Core;

namespace Workbench.App;

/// <summary>Owns the single-writer store lease for the whole manager lifetime.</summary>
internal sealed class HostRuntime : IAsyncDisposable
{
    private readonly FileStream _lease;
    public PluginManager Manager { get; }
    public PluginPackageStore Packages { get; }
    public IReadOnlyList<PluginOperationResult> MaintenanceResults { get; private set; } = [];
    private bool _initializationStarted;

    public HostRuntime(PluginManagerOptions options)
    {
        _lease = AcquireLease(options.UserRoot);
        try
        {
            Packages = new PluginPackageStore(options);
            Manager = new PluginManager(options);
        }
        catch { _lease.Dispose(); throw; }
    }

    public async Task InitializeAsync(Action<string>? trace = null)
    {
        if (_initializationStarted) throw new InvalidOperationException("Host initialization has already started.");
        _initializationStarted = true;
        trace?.Invoke("applying-pending-package-maintenance");
        MaintenanceResults = await Packages.ApplyPendingAsync();
        trace?.Invoke("discovering-packages");
        await Manager.DiscoverAsync();
        trace?.Invoke("restoring-enabled-packages");
        await Manager.RestoreEnabledAsync();
    }

    internal static FileStream AcquireLease(string userRoot)
    {
        RejectLinks(userRoot);
        Directory.CreateDirectory(userRoot);
        var lockPath = Path.Combine(userRoot, "host.lock");
        RejectLinks(lockPath);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xFFFF) is 32 or 33)
        {
            throw new UserStoreInUseException(error);
        }
    }

    internal static void RejectLinks(string path)
    {
        for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse points are not allowed for the user store or smoke packages: {part}");
    }

    public async ValueTask DisposeAsync()
    {
        // A failed dispose is retryable. Known work and its user-root lease remain owned.
        await Manager.DisposeAsync();
        _lease.Dispose();
    }
}

internal sealed class UserStoreInUseException(IOException inner)
    : IOException("此用户目录已由另一个工作台实例占用。请关闭该实例后重试。", inner);
