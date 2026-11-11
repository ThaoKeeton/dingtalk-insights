using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Workbench.Core;
using Workbench.Sdk;
using static Workbench.App.NativeRuntimeProbe;

namespace Workbench.App;

internal static class NativeManagementProbe
{
    public static async Task RunAsync(MainWindow window, RunOptions options, Action<string> record)
    {
        var manager = window.SmokeManager;
        Require(window.SmokeManagementNavigationAvailable, "Initialized native navigation was not enabled and attached.");
        Require(manager.Plugins is [{ IsTrusted: false, IsLoaded: false }], "Native management probe must start with only the untrusted synthetic fixture.");
        foreach (var route in new[] { "home", "background", "credentials", "packages", "settings" }) await window.SmokeNavigateAsync(route);
        record("Initialized host navigation attached Home, Background, Credentials, Package maintenance and Settings");
        await manager.GetSettings(Id).SetAsync("mode", "synchronous");
        Require((await manager.EnableAsync(Id, confirmTrust: true)).Succeeded, "Synthetic fixture enable failed.");
        await VerifyCredentialsAsync(window);
        record("Actual native empty PasswordBox saved synthetic DPAPI ciphertext, read roundtrip, reopened empty and deleted via native controls");
        var background = await window.SmokeNavigateAsync("background");
        Require(!Find<Button>(background, $"启动后台任务 {Id}/confirmed-worker").IsEnabled
            && !Find<ToggleSwitch>(background, $"恢复后台任务 {Id}/confirmed-worker").IsEnabled, "Business-confirmed task exposed generic Start/Resume.");
        Invoke(Find<Button>(background, $"启动后台任务 {Id}/worker"));
        var data = Path.Combine(options.UserRoot, "data", Id);
        await WaitAsync(() => Task.FromResult(File.Exists(Path.Combine(data, "sync-started"))));
        var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Require(window.DispatcherQueue.TryEnqueue(() => pulse.TrySetResult()), "Dispatcher pulse could not be queued.");
        await pulse.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await window.SmokeNavigateAsync("home");
        await window.SmokeNavigateAsync("settings");
        background = await window.SmokeNavigateAsync("background");
        Require(manager.GetBackgroundTaskSnapshots(Id).Single(task => task.Id == "worker").State == PluginBackgroundTaskState.Running, "Navigation ended the supervised worker.");
        await File.WriteAllTextAsync(Path.Combine(data, "release-sync"), "synthetic");
        Invoke(Find<Button>(background, $"停止后台任务 {Id}/worker"));
        await WaitAsync(() => Task.FromResult(manager.GetBackgroundTaskSnapshots(Id).Single(task => task.Id == "worker").State == PluginBackgroundTaskState.Stopped && !window.SmokeActionBusy));
        await WaitAsync(() => Task.FromResult(Find<TextBlock>(background, $"后台任务状态 {Id}/worker").Text.Contains("Stopped", StringComparison.Ordinal)));
        Invoke(Find<Button>(background, $"启动后台任务 {Id}/fault"));
        await WaitAsync(() => Task.FromResult(manager.GetBackgroundTaskSnapshots(Id).Single(task => task.Id == "fault").State == PluginBackgroundTaskState.Faulted));
        await WaitAsync(() => Task.FromResult(Find<TextBlock>(background, $"后台任务状态 {Id}/fault").Text.Contains("BackgroundTaskFailed", StringComparison.Ordinal)));
        Require(manager.Plugins.SelectMany(plugin => plugin.Diagnostics).All(diagnostic => !diagnostic.Message.Contains("synthetic-private-exception", StringComparison.Ordinal)), "A provider exception escaped safe task diagnostics.");
        record("Actual Background controls kept dispatcher/navigation responsive during a synchronous worker prefix and displayed Stopped/Faulted with safe codes");
        Invoke(Find<Button>(background, $"打开后台任务所属插件页面 {Id}/confirmed-worker"));
        await WaitAsync(() => Task.FromResult(window.SmokeCurrentPage is { } page && Descendants<Button>(page).Any(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "确认本次合成任务")));
        Invoke(Find<Button>(window.SmokeCurrentPage!, "确认本次合成任务"));
        await WaitAsync(() => Task.FromResult(manager.GetBackgroundTaskSnapshots(Id).Single(task => task.Id == "confirmed-worker").State == PluginBackgroundTaskState.Running));
        Require(!(await manager.SetBackgroundTaskResumeOnStartupAsync(Id, "confirmed-worker", true)).Succeeded, "One-use confirmation task accepted generic resume.");
        background = await window.SmokeNavigateAsync("background");
        Invoke(Find<Button>(background, $"停止后台任务 {Id}/confirmed-worker"));
        await WaitAsync(() => Task.FromResult(manager.GetBackgroundTaskSnapshots(Id).Single(task => task.Id == "confirmed-worker").State == PluginBackgroundTaskState.Stopped && !window.SmokeActionBusy));
        record("RequiresPluginStart disabled generic controls, opened the actual plugin confirmation page and allowed host Stop afterward");
        await VerifyFailedInitializationAsync(options);
        record("Failed initialization revoked task/secret/page contributions while native management navigation stayed usable");
        foreach (var mode in new[] { "hold-stop", "hold-callback", "hold-task" })
        {
            await VerifyPendingCloseAsync(options, mode);
            record($"Actual native close/retry retained the lease and one runtime/Stop across {mode}, then released only after known completion");
        }
        await VerifyOwnedActionAsync(options);
        record("Close declined during an owned asynchronous package action, blocked second ownership, then safely retried after the actual write settled");
        await VerifyMaintenanceFailureAsync(options);
        record("Local managed maintenance failure remained visible without blocking navigation or an unrelated bundled sample");
    }

    private static async Task VerifyCredentialsAsync(MainWindow window)
    {
        var page = await window.SmokeNavigateAsync("credentials");
        var editor = Find<PasswordBox>(page, $"新凭据值 {Id}/{SecretId}");
        Require(editor.Password.Length == 0, "A fresh credential editor was prefilled.");
        editor.Password = SyntheticSecret;
        Invoke(Find<Button>(page, $"保存凭据 {Id}/{SecretId}"));
        await WaitAsync(() => Task.FromResult(Find<TextBlock>(page, $"凭据状态 {Id}/{SecretId}").Text.StartsWith("已保存", StringComparison.Ordinal) && !window.SmokeActionBusy));
        Require(editor.Password.Length == 0 && await window.SmokeManager.ReadSecretAsync(Id, SecretId) == SyntheticSecret, "Synthetic native DPAPI roundtrip failed.");
        await window.SmokeNavigateAsync("home");
        page = await window.SmokeNavigateAsync("credentials");
        Require(Find<PasswordBox>(page, $"新凭据值 {Id}/{SecretId}").Password.Length == 0, "Reopened credential editor disclosed saved data.");
        Require(Descendants<TextBlock>(page).All(text => !text.Text.Contains(SyntheticSecret, StringComparison.Ordinal)), "Credential plaintext entered page metadata.");
        Invoke(Find<Button>(page, $"删除凭据 {Id}/{SecretId}"));
        await WaitAsync(() => Task.FromResult(Find<TextBlock>(page, $"凭据状态 {Id}/{SecretId}").Text == "未保存" && !window.SmokeActionBusy));
        Require(await window.SmokeManager.ReadSecretAsync(Id, SecretId) is null, "Native Delete left the credential stored.");
    }

    private static async Task VerifyFailedInitializationAsync(RunOptions options)
    {
        var extra = await OpenFixtureWindowAsync(options, "failed-initialization");
        try
        {
            await extra.SmokeManager.GetSettings(Id).SetAsync("mode", "init-fail");
            Require(!(await extra.SmokeManager.EnableAsync(Id, confirmTrust: true)).Succeeded, "Expected fixture initialization failure.");
            Require(extra.SmokeManager.GetBackgroundTaskSnapshots(Id).Count == 0 && extra.SmokeManager.GetSecretSnapshots(Id).Count == 0, "Failed initialization committed runtime metadata.");
            var page = await extra.SmokeNavigateAsync("credentials");
            Require(!Descendants<PasswordBox>(page).Any(), "Failed initialization exposed a credential editor.");
            await extra.SmokeNavigateAsync("background");
            Require(extra.SmokeManagementNavigationAvailable, "Failed plugin initialization disabled host navigation.");
        }
        finally { await extra.FinishSmokeAsync(); }
    }

    private static async Task VerifyPendingCloseAsync(RunOptions options, string mode)
    {
        var extra = await OpenFixtureWindowAsync(options, mode);
        var root = Path.Combine(options.UserRoot, mode);
        var data = Path.Combine(root, "data", Id);
        var manager = extra.SmokeManager;
        var settings = manager.GetSettings(Id);
        try
        {
            await settings.SetAsync("mode", mode);
            Require((await manager.EnableAsync(Id, confirmTrust: true)).Succeeded, "Held fixture enable failed.");
            if (mode == "hold-task")
            {
                Require((await manager.StartBackgroundTaskAsync(Id, "worker")).Succeeded, "Held worker start failed.");
                await WaitAsync(async () => await settings.GetAsync("worker-runs", 0) == 1);
            }
            Require(!await extra.SmokeCloseAttemptAsync().WaitAsync(TimeSpan.FromSeconds(10)), "Unfinished known work allowed native close.");
            Require(extra.SmokeShutdownPending && extra.SmokeManagementNavigationAvailable && manager.HasPendingStops, "Pending-close UI or manager was not queryable.");
            AssertLeaseHeld(root);
            var page = await extra.SmokeNavigateAsync("background");
            Require(Descendants<Button>(page).All(button => !button.IsEnabled), "Shutdown-pending task page exposed new work.");
            Require(!(await manager.EnableAsync(Id)).Succeeded, "Pending close created another instance.");
            Invoke(extra.SmokeCloseRetryButton ?? throw new InvalidOperationException("Pending native close did not expose its retry button."));
            await WaitAsync(() => Task.FromResult(extra.SmokeCloseInProgress));
            Require(!await extra.SmokeCloseCompletion.WaitAsync(TimeSpan.FromSeconds(10)), "Native retry button escaped pending ownership.");
            Require(await settings.GetAsync("stop-calls", 0) == 1 && await settings.GetAsync("instances", 0) == 1, "Retry spawned a second Stop/runtime.");
            AssertLeaseHeld(root);
        }
        finally
        {
            Directory.CreateDirectory(data);
            foreach (var release in new[] { "release-stop", "release-callback", "release-work" }) await File.WriteAllTextAsync(Path.Combine(data, release), "synthetic");
            await WaitAsync(() => Task.FromResult(!manager.HasPendingStops));
            Require(await extra.SmokeCloseAttemptAsync(), "Native close retry failed after true completion.");
        }
        using var nextOwner = HostRuntime.AcquireLease(root);
    }

    private static async Task VerifyOwnedActionAsync(RunOptions options)
    {
        var extra = await OpenFixtureWindowAsync(options, "owned-action");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = extra.SmokeRunOwnedActionAsync(async () =>
        {
            await release.Task;
            Require((await extra.SmokeRuntime.Packages.StageRemoveAsync(Id)).Succeeded, "Owned synthetic package stage failed.");
        });
        try
        {
            Require(extra.SmokeActionBusy && !await extra.SmokeCloseAttemptAsync(), "Close disposed an unfinished host operation.");
            AssertLeaseHeld(Path.Combine(options.UserRoot, "owned-action"));
            release.TrySetResult();
            await action;
            Require(extra.SmokeRuntime.Packages.PendingOperations is [{ Phase: PackageOperationPhase.Prepared }], "Owned host operation did not finish under the lease.");
            AssertLeaseHeld(Path.Combine(options.UserRoot, "owned-action"));
        }
        finally { release.TrySetResult(); await action; await extra.FinishSmokeAsync(); }
    }

    private static async Task VerifyMaintenanceFailureAsync(RunOptions options)
    {
        var root = Path.Combine(options.UserRoot, "maintenance-failure");
        using (HostRuntime.AcquireLease(root))
        {
            var metadata = Path.Combine(root, "Plugins", ".workbench-packages");
            Directory.CreateDirectory(metadata);
            await File.WriteAllTextAsync(Path.Combine(metadata, "pending"), "synthetic occupied metadata");
        }
        var extra = new MainWindow(new(false, root, null));
        extra.Activate();
        try
        {
            await extra.InitializeAsync();
            Require(extra.SmokeStartupFailure is null && extra.SmokeManagementNavigationAvailable, "Local maintenance failure blocked host startup.");
            var page = await extra.SmokeNavigateAsync("packages");
            Require(Descendants<TextBlock>(page).Any(text => text.Text.Contains("PackageMaintenanceFailed", StringComparison.Ordinal)), "Maintenance diagnostics were not visible.");
            Require((await extra.SmokeManager.EnableAsync(RunOptions.SampleIds[0], confirmTrust: true)).Succeeded, "Unrelated bundled sample could not activate.");
        }
        finally { await extra.FinishSmokeAsync(); }
    }

    private static async Task<MainWindow> OpenFixtureWindowAsync(RunOptions options, string name)
    {
        var root = Path.Combine(options.UserRoot, name);
        using (HostRuntime.AcquireLease(root))
            Require((await new PluginPackageStore(new() { PluginsRoot = Path.Combine(root, "Plugins"), UserRoot = root }).InstallAsync(FixtureSource)).Succeeded, "Extra native fixture installation failed.");
        var extra = new MainWindow(new(false, root, null));
        extra.Activate();
        await extra.InitializeAsync();
        Require(extra.SmokeStartupFailure is null, "Extra isolated native host failed to initialize.");
        return extra;
    }

    private static void AssertLeaseHeld(string root)
    {
        try { using var duplicate = HostRuntime.AcquireLease(root); }
        catch (UserStoreInUseException) { return; }
        throw new InvalidOperationException("Known native work lost its user-root lease.");
    }

    private static void Invoke(Button button) => new ButtonAutomationPeer(button).Invoke();
}

internal sealed partial class MainWindow
{
    internal FrameworkElement? SmokeCurrentPage => _content.Content as FrameworkElement;
    internal bool SmokeManagementNavigationAvailable => _navigation.IsEnabled && _navigation.IsSettingsVisible && ReferenceEquals(_body.Content, _navigation);
    internal Button? SmokeCloseRetryButton => _notice.ActionButton as Button;
    internal bool SmokeCloseInProgress => _closing;
    internal Task<bool> SmokeCloseCompletion => _closeCompleted.Task;
}
