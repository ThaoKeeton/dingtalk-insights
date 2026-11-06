using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Workbench.Core;
using Workbench.Sdk;
using static Workbench.App.NativeRuntimeProbe;

namespace Workbench.App;

internal static class NativeRestartProbe
{
    public static async Task RunAsync(MainWindow window, RunOptions options, Action<string> record)
    {
        var manager = window.SmokeManager;
        var packages = window.SmokeRuntime.Packages;
        var data = Path.Combine(options.UserRoot, "data", Id, "persistent-synthetic-data.txt");
        if (options.RuntimeProbe == "remove-verify")
        {
            Require(manager.Plugins.Count == 0 && packages.PendingOperations.Count == 0 && packages.History.Count >= 3, "Removal left active code or lost history.");
            Require(await File.ReadAllTextAsync(data) == "synthetic-current-data-never-rolled-back", "Removal changed current data.");
            Require(Directory.EnumerateFiles(Path.Combine(options.UserRoot, "secrets", Id)).Any(), "Removal deleted current ciphertext.");
            // Removed plugins deliberately have no manager settings API; inspect only this known synthetic document.
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.UserRoot, "settings", Id, "settings.json")));
            Require(saved.RootElement.GetProperty("worker-runs").GetInt32() == 2 && saved.RootElement.GetProperty("confirmed-worker-runs").GetInt32() == 1, "Package maintenance revived background authority.");
            record("Sixth actual process applied removal while retaining host-managed data/settings/ciphertext/code history and never reviving background authority");
            return;
        }
        var settings = manager.GetSettings(Id);
        switch (options.RuntimeProbe)
        {
            case "restart-seed":
                Require(manager.Plugins is [{ IsLoaded: false, IsTrusted: false, DesiredEnabled: false }], "Fresh probe unexpectedly activated code.");
                await settings.SetAsync("saved-note", "synthetic-settings-survive-processes");
                Require((await manager.EnableAsync(Id, confirmTrust: true)).Succeeded, "Seed fixture enable failed.");
                await File.WriteAllTextAsync(data, "synthetic-current-data-never-rolled-back");
                await manager.WriteSecretAsync(Id, SecretId, SyntheticSecret);
                Require((await manager.SetBackgroundTaskResumeOnStartupAsync(Id, "worker", true)).Succeeded, "Generic fixture resume opt-in failed.");
                Require((await manager.StartBackgroundTaskAsync(Id, "worker")).Succeeded, "Seed worker did not start.");
                Require(!(await manager.StartBackgroundTaskAsync(Id, "confirmed-worker")).Succeeded, "Host bypassed one-use confirmation.");
                Require(!(await manager.SetBackgroundTaskResumeOnStartupAsync(Id, "confirmed-worker", true)).Succeeded, "One-use confirmation task accepted restore.");
                Require((await manager.ExecuteCommandAsync(Id, "confirm-start")).Succeeded, "Synthetic plugin confirmation did not start its task.");
                await WaitAsync(async () => await settings.GetAsync("worker-runs", 0) == 1 && await settings.GetAsync("confirmed-worker-runs", 0) == 1);
                await window.SmokeNavigateAsync("view", Id, "fixture-page");
                await window.SmokeNavigateAsync("view", Id, "fixture-settings");
                record("First actual process explicitly trusted the fixture, saved synthetic data/settings/DPAPI, opted in the generic worker and confirmed one guarded run");
                break;
            case "restart-verify":
                await WaitAsync(async () => await settings.GetAsync("worker-runs", 0) == 2);
                Require(await settings.GetAsync("worker-start", "") == "Restore", "Generic fixture did not restore in the next process.");
                var guarded = manager.GetBackgroundTaskSnapshots(Id).Single(task => task.Id == "confirmed-worker");
                Require(guarded is { State: PluginBackgroundTaskState.Stopped, ResumeOnStartup: false, DesiredRunning: false }
                    && await settings.GetAsync("confirmed-worker-runs", 0) == 1, "Restart revived one-use live authority.");
                await VerifyPersistenceAsync(window, data);
                record("Second actual process restored only the opted-in generic worker; guarded task stayed stopped while page/settings/data/DPAPI persisted");
                Require((await manager.DisableAsync(Id)).Succeeded, "Explicit fixture disable failed.");
                Require(await settings.GetAsync("worker-stop", "") == "PluginDisabled", "Explicit disable did not revoke worker intent.");
                record("Explicit disable stopped the worker and revoked its running intent before the next process");
                break;
            case "restart-disabled":
                Require(manager.Plugins is [{ IsLoaded: false, DesiredEnabled: false }], "Disabled fixture was restored.");
                Require(await settings.GetAsync("worker-runs", 0) == 2 && await settings.GetAsync("confirmed-worker-runs", 0) == 1, "Disable/restart executed another task.");
                var source = await CreateUpdateSourceAsync(options.UserRoot);
                Require((await packages.StageUpdateAsync(Id, source)).Succeeded, "Synthetic update staging failed.");
                Require(manager.Plugins.Single().Manifest!.Version == "1.0.0" && packages.PendingOperations is [{ Phase: PackageOperationPhase.Prepared }], "Stage changed current code before process restart.");
                record("Third actual process kept disabled tasks stopped and staged version 2 without changing current version 1");
                break;
            case "update-verify":
                VerifyReplacedPackage(window, "2.0.0");
                Require((await manager.EnableAsync(Id, confirmTrust: true)).Succeeded, "Explicit trust of updated fixture failed.");
                await VerifyPersistenceAsync(window, data);
                var history = packages.History.Single(snapshot => snapshot.Version == "1.0.0");
                Require(history.DataBackupPath is not null && Directory.Exists(history.DataBackupPath), "Update did not retain host-managed state backup.");
                Require((await packages.StageRollbackAsync(Id, history.HistoryId)).Succeeded, "Synthetic rollback staging failed.");
                record("Fourth actual process applied update before discovery, revoked trust/resume, retained current state/DPAPI and staged code-only rollback using HistoryId");
                break;
            case "rollback-verify":
                VerifyReplacedPackage(window, "1.0.0");
                Require((await manager.EnableAsync(Id, confirmTrust: true)).Succeeded, "Explicit trust of rolled-back fixture failed.");
                await VerifyPersistenceAsync(window, data);
                Require((await packages.StageRemoveAsync(Id)).Succeeded, "Synthetic remove staging failed.");
                record("Fifth actual process applied code rollback without restoring old business data, preserved current credentials and staged remove");
                break;
            default: throw new InvalidOperationException("Unknown isolated restart phase.");
        }
    }

    private static void VerifyReplacedPackage(MainWindow window, string version)
    {
        var package = window.SmokeManager.Plugins.Single();
        Require(package.Manifest!.Version == version && !package.IsTrusted && !package.DesiredEnabled && !package.IsLoaded, "Restart replacement inherited trust or activation.");
        Require(window.SmokeRuntime.MaintenanceResults is [{ Succeeded: true }] && window.SmokeRuntime.Packages.PendingOperations.Count == 0, "Startup maintenance did not commit before discovery.");
    }

    private static async Task VerifyPersistenceAsync(MainWindow window, string data)
    {
        var manager = window.SmokeManager;
        Require(await File.ReadAllTextAsync(data) == "synthetic-current-data-never-rolled-back"
            && await manager.GetSettings(Id).GetAsync("saved-note", "") == "synthetic-settings-survive-processes", "Current synthetic data/settings were not preserved.");
        Require(await manager.ReadSecretAsync(Id, SecretId) == SyntheticSecret, "Synthetic credential did not survive process restart.");
        var page = await window.SmokeNavigateAsync("credentials");
        Require(Find<PasswordBox>(page, $"新凭据值 {Id}/{SecretId}").Password.Length == 0, "Process restart prefilled the credential editor.");
        Require(manager.GetSecretSnapshots(Id).Single().IsConfigured == true, "Ciphertext metadata did not show saved state.");
        await window.SmokeNavigateAsync("view", Id, "fixture-page");
        await window.SmokeNavigateAsync("view", Id, "fixture-settings");
    }

    private static async Task<string> CreateUpdateSourceAsync(string userRoot)
    {
        var destination = Path.Combine(userRoot, "synthetic-update-source");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(FixtureSource)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        var manifestPath = Path.Combine(destination, "plugin.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["version"] = "2.0.0";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        return destination;
    }
}
