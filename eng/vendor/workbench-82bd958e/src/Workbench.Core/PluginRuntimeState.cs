using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Core;

internal sealed record SavedTaskIntent(bool ResumeOnStartup, bool DesiredRunning);
internal sealed record SavedRuntimePlugin(string Fingerprint, Dictionary<string, SavedTaskIntent> Tasks);
internal sealed record RuntimeStateDocument(int Version, Dictionary<string, SavedRuntimePlugin> Plugins);

/// <summary>Host-owned task intent, separate from plugin preferences and bound to package content.</summary>
internal sealed class PluginRuntimeState(string userRoot,
    Func<string, RuntimeStateDocument, CancellationToken, Task>? write = null)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(userRoot), "background-tasks.json");
    private readonly SemaphoreSlim _gate = PluginStateStore.GateForPath(Path.Combine(Path.GetFullPath(userRoot), "background-tasks.json"));
    private Dictionary<string, SavedRuntimePlugin>? _plugins;
    private bool _readFailed;
    private static readonly JsonSerializerOptions ReadOptions = new(AtomicJsonFile.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal async Task<IReadOnlyDictionary<string, SavedTaskIntent>> LoadPluginAsync(
        string pluginId, string fingerprint, Action<PluginDiagnostic> diagnostic, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(diagnostic, cancellationToken);
            if (_plugins!.TryGetValue(pluginId, out var plugin))
            {
                if (plugin.Fingerprint == fingerprint)
                    return new Dictionary<string, SavedTaskIntent>(plugin.Tasks, StringComparer.Ordinal);
                // Persist invalidation so reverting to an older package cannot revive old consent.
                var updated = new Dictionary<string, SavedRuntimePlugin>(_plugins, StringComparer.Ordinal);
                updated.Remove(pluginId);
                await WriteAsync(updated, cancellationToken);
            }
            return new Dictionary<string, SavedTaskIntent>(StringComparer.Ordinal);
        }
        finally { _gate.Release(); }
    }

    internal async Task SaveAsync(string pluginId, string fingerprint, string id, SavedTaskIntent intent,
        Action<PluginDiagnostic> diagnostic, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(diagnostic, cancellationToken);
            var tasks = _plugins!.TryGetValue(pluginId, out var plugin) && plugin.Fingerprint == fingerprint
                ? new Dictionary<string, SavedTaskIntent>(plugin.Tasks, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            tasks[id] = intent;
            var updated = new Dictionary<string, SavedRuntimePlugin>(_plugins, StringComparer.Ordinal)
            {
                [pluginId] = new(fingerprint, tasks)
            };
            await WriteAsync(updated, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task ClearAsync(string pluginId, bool removeConsent, Action<PluginDiagnostic> diagnostic, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(diagnostic, cancellationToken);
            if (!_plugins!.TryGetValue(pluginId, out var plugin) && !_readFailed) return;
            var updated = new Dictionary<string, SavedRuntimePlugin>(_plugins, StringComparer.Ordinal);
            if (removeConsent || plugin is null) updated.Remove(pluginId);
            else updated[pluginId] = plugin with
            {
                Tasks = plugin.Tasks.ToDictionary(pair => pair.Key, pair => pair.Value with { DesiredRunning = false }, StringComparer.Ordinal)
            };
            await WriteAsync(updated, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(Dictionary<string, SavedRuntimePlugin> updated, CancellationToken cancellationToken)
    {
        var document = new RuntimeStateDocument(1, updated);
        if (write is null) await AtomicJsonFile.WriteAsync(_path, document, cancellationToken);
        else await write(_path, document, cancellationToken);
        _plugins = updated;
    }

    private async Task LoadAsync(Action<PluginDiagnostic> diagnostic, CancellationToken cancellationToken)
    {
        _readFailed = false;
        try
        {
            FileSystemGuard.RejectLinks(_path);
            if (Directory.Exists(_path)) throw new IOException("Background task state is not a regular file.");
            if (!File.Exists(_path)) { _plugins = new(StringComparer.Ordinal); return; }
            FileSystemGuard.EnsureRegularFile(_path);
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var document = await JsonSerializer.DeserializeAsync<RuntimeStateDocument>(stream, ReadOptions, cancellationToken)
                ?? throw new JsonException();
            if (document.Version != 1 || document.Plugins is null) throw new JsonException();
            foreach (var (pluginId, plugin) in document.Plugins)
            {
                if (!PackageValidator.IsSafePluginId(pluginId) || plugin is null || plugin.Fingerprint is not { Length: 64 }
                    || !plugin.Fingerprint.All(Uri.IsHexDigit) || plugin.Tasks is null) throw new JsonException();
                foreach (var (id, intent) in plugin.Tasks)
                {
                    JsonPluginSettings.ValidateKey(id);
                    if (intent is null) throw new JsonException();
                }
            }
            _plugins = document.Plugins;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _readFailed = true;
            _plugins = new(StringComparer.Ordinal);
            diagnostic(new(PluginDiagnosticCode.RuntimeStateCorrupt, "Background task intent could not be read; automatic resume is disabled."));
        }
    }
}
