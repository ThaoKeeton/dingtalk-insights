using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Core;

internal sealed record PackageJournalRecord(
    int Version, string PluginId, string OperationId, PackageOperationKind Kind, PackageOperationPhase Phase,
    string TargetDirectory, string? OldFingerprint, string? NewFingerprint,
    string? OldVersion, string? NewVersion, string? DataFingerprint, DateTimeOffset CreatedAt);

/// <summary>Only validated identifiers and one relative target name may select transaction paths.</summary>
internal sealed class PackageJournal(string pluginsRoot)
{
    internal const string DirectoryName = ".workbench-packages";
    internal string Root { get; } = FileSystemGuard.ContainedPath(pluginsRoot, DirectoryName);
    private static readonly JsonSerializerOptions ReadOptions = new(AtomicJsonFile.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal string PendingRoot => FileSystemGuard.ContainedPath(Root, "pending");
    internal string HistoryRoot => FileSystemGuard.ContainedPath(Root, "history");
    internal string PendingPath(string id) => FileSystemGuard.ContainedPath(PendingRoot, id + ".json");
    internal string OperationRoot(PackageJournalRecord record) => FileSystemGuard.ContainedPath(Root, $"operations/{record.PluginId}/{record.OperationId}");
    internal string StagePath(PackageJournalRecord record) => FileSystemGuard.ContainedPath(OperationRoot(record), "new");
    internal string HistoryDirectory(PackageJournalRecord record) => FileSystemGuard.ContainedPath(HistoryRoot, $"{record.PluginId}/{record.OperationId}");
    internal string CodePath(PackageJournalRecord record) => FileSystemGuard.ContainedPath(HistoryDirectory(record), "code");
    internal string QuarantinePath(PackageJournalRecord record) => FileSystemGuard.ContainedPath(OperationRoot(record), "quarantine");
    internal string DataPath(PackageJournalRecord record) => FileSystemGuard.ContainedPath(HistoryDirectory(record), "data");
    internal string TargetPath(PackageJournalRecord record) => FileSystemGuard.ContainedPath(pluginsRoot, record.TargetDirectory);
    internal Task WriteAsync(PackageJournalRecord record, CancellationToken token) => AtomicJsonFile.WriteAsync(PendingPath(record.PluginId), record, token);
    internal Task WriteHistoryAsync(PackageJournalRecord record, CancellationToken token)
        => AtomicJsonFile.WriteAsync(FileSystemGuard.ContainedPath(HistoryDirectory(record), "record.json"), record, token);

    internal static async Task<PackageJournalRecord> ReadAsync(string path, CancellationToken token)
    {
        FileSystemGuard.EnsureRegularFile(path);
        var text = await File.ReadAllTextAsync(path, token);
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            json.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != json.RootElement.EnumerateObject().Count())
            throw new JsonException("Invalid journal object.");
        var record = JsonSerializer.Deserialize<PackageJournalRecord>(text, ReadOptions) ?? throw new JsonException();
        if (record.Version != 1 || !PackageValidator.IsSafePluginId(record.PluginId) ||
            !Guid.TryParseExact(record.OperationId, "N", out _) || !Enum.IsDefined(record.Kind) || !Enum.IsDefined(record.Phase) ||
            !PackageValidator.IsSafeRelativePath(record.TargetDirectory) || record.TargetDirectory.Contains('/') ||
            record.TargetDirectory.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase) ||
            !Fingerprint(record.OldFingerprint) || !Fingerprint(record.NewFingerprint) || !Fingerprint(record.DataFingerprint) ||
            (record.OldFingerprint is null) != (record.OldVersion is null) ||
            (record.NewFingerprint is null) != (record.NewVersion is null) ||
            (record.Kind == PackageOperationKind.Remove ? record.OldFingerprint is null || record.NewFingerprint is not null : record.NewFingerprint is null) ||
            (record.Kind == PackageOperationKind.Update && record.OldFingerprint is null) ||
            !IsVersion(record.OldVersion) || !IsVersion(record.NewVersion) ||
            (record.Phase != PackageOperationPhase.Prepared && record.DataFingerprint is null))
            throw new JsonException("Unsupported or invalid package journal.");
        return record;
    }

    internal static async Task<bool> BlocksActivationAsync(string pluginsRoot, string id, CancellationToken token)
    {
        try
        {
            var journal = new PackageJournal(pluginsRoot);
            if (File.Exists(journal.Root) || File.Exists(journal.PendingRoot))
                throw new IOException("Package maintenance metadata is not a directory.");
            var path = journal.PendingPath(id);
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            var record = await ReadAsync(path, token);
            return record.PluginId != id || record.Phase != PackageOperationPhase.Prepared;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return true;
        }
    }

    private static bool Fingerprint(string? value) => value is null || value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsVersion(string? value) => value is null || System.Version.TryParse(value, out var version)
        && version.Revision == -1 && version.Build >= 0;
}
