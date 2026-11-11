using System.Text.Json.Serialization;

namespace Workbench.Core;

[JsonConverter(typeof(JsonStringEnumConverter<PackageOperationKind>))]
public enum PackageOperationKind { Update, Rollback, Remove }

[JsonConverter(typeof(JsonStringEnumConverter<PackageOperationPhase>))]
public enum PackageOperationPhase { Prepared, BackedUp, OldMoved, NewMoved, Committed, RecoveryPrepared, Quarantined, Recovered }

public sealed record PendingPackageOperation(
    string PluginId, string OperationId, PackageOperationKind Kind, PackageOperationPhase Phase,
    string CurrentPath, string? StagedPath, string? OldVersion, string? NewVersion);

/// <summary>Code history and host-managed state snapshots; never an automatic data restore.</summary>
public sealed record PackageHistorySnapshot(
    string PluginId, string HistoryId, PackageOperationKind Kind, string Version, string Fingerprint,
    string CodePath, string? DataBackupPath, DateTimeOffset CreatedAt);
