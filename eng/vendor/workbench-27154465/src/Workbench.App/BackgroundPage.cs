using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Workbench.Core;
using Workbench.Sdk;
using Workbench.WinUI.Sdk;

namespace Workbench.App;

internal sealed partial class MainWindow
{
    private StackPanel BackgroundPage()
    {
        var page = Ui.Page("后台任务", "已登记的任务独立于页面运行。自动恢复默认关闭；开启后仍需符合插件自己的业务授权策略。");
        if (_shutdownPending) page.Children.Add(Ui.Text("关闭仍在等待已知工作结束；保留本进程与用户目录租约。可查看状态并使用上方“重试关闭”。"));
        List<Action> refresh = [];
        var count = 0;
        foreach (var plugin in Manager.Plugins.Where(plugin => plugin.Manifest is not null))
        {
            var pluginId = plugin.Manifest!.Id;
            foreach (var task in Manager.GetBackgroundTaskSnapshots(pluginId))
            {
                count++;
                var body = new StackPanel { Spacing = 10 };
                body.Children.Add(Ui.Text(task.Title, 20));
                body.Children.Add(Ui.Text($"{pluginId} / {task.Id}"));
                var status = Ui.Named(Ui.Text(""), $"后台任务状态 {pluginId}/{task.Id}");
                body.Children.Add(status);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                var start = Ui.Named(new Button { Content = "启动" }, $"启动后台任务 {pluginId}/{task.Id}");
                var stop = Ui.Named(new Button { Content = "停止" }, $"停止后台任务 {pluginId}/{task.Id}");
                var resume = Ui.Named(new ToggleSwitch { Header = "下次启动恢复", IsOn = task.ResumeOnStartup }, $"恢复后台任务 {pluginId}/{task.Id}");
                start.Click += async (_, _) => await RunAsync(async () => TaskNotice(await Manager.StartBackgroundTaskAsync(pluginId, task.Id)));
                stop.Click += async (_, _) => await RunAsync(async () => TaskNotice(await Manager.StopBackgroundTaskAsync(pluginId, task.Id)));
                var updating = false;
                resume.Toggled += async (_, _) =>
                {
                    if (updating) return;
                    await RunAsync(async () => TaskNotice(await Manager.SetBackgroundTaskResumeOnStartupAsync(pluginId, task.Id, resume.IsOn)));
                    Refresh();
                };
                actions.Children.Add(start);
                actions.Children.Add(stop);
                body.Children.Add(actions);
                body.Children.Add(resume);
                if (task.RequiresPluginStart)
                {
                    body.Children.Add(Ui.Text("此任务需在插件页面完成本次范围预览与确认；通用启动和自动恢复不可用。信任插件不代表业务授权。"));
                    var view = Manager.Contributions.Views.FirstOrDefault(view => view.PluginId == pluginId && view.View is WinUiView { Kind: WinUiViewKind.Page });
                    var open = Ui.Named(new Button { Content = "打开插件页面", IsEnabled = view is not null && !_shutdownPending }, $"打开后台任务所属插件页面 {pluginId}/{task.Id}");
                    open.Click += (_, _) => { if (view is not null) Navigate(new("view", pluginId, view.View.Id)); };
                    body.Children.Add(open);
                }
                void Refresh()
                {
                    var current = Manager.GetBackgroundTaskSnapshots(pluginId).SingleOrDefault(snapshot => snapshot.Id == task.Id);
                    var active = Manager.Plugins.Any(snapshot => snapshot.Manifest?.Id == pluginId && snapshot.State == PluginState.Enabled);
                    status.Text = current is null ? "已停止，插件运行实例已释放。" :
                        $"状态：{current.State} · 恢复偏好：{current.ResumeOnStartup} · 运行意图：{current.DesiredRunning}\n开始：{current.StartedAt:u} · 结束：{current.FinishedAt:u}"
                        + (current.ErrorCode is null ? "" : $"\n错误代码：{current.ErrorCode}");
                    start.IsEnabled = !_shutdownPending && active && current is { RequiresPluginStart: false }
                        && current.State is not (PluginBackgroundTaskState.Running or PluginBackgroundTaskState.Stopping);
                    stop.IsEnabled = !_shutdownPending && active && current?.State is (PluginBackgroundTaskState.Running or PluginBackgroundTaskState.Stopping);
                    resume.IsEnabled = !_shutdownPending && active && current is { RequiresPluginStart: false };
                    updating = true;
                    resume.IsOn = current?.ResumeOnStartup == true;
                    updating = false;
                }
                Refresh();
                refresh.Add(Refresh);
                page.Children.Add(Ui.Card(body));
            }
        }
        if (count == 0) page.Children.Add(Ui.Text("暂无任务。只有成功初始化并提交的插件才会出现在这里。"));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { foreach (var update in refresh) update(); };
        page.Loaded += (_, _) => timer.Start();
        page.Unloaded += (_, _) => timer.Stop();
        AddDiagnostics(page);
        return page;
    }

    private void TaskNotice(PluginTaskOperationResult result) => Notice(result.Succeeded ? "后台任务已更新" : "后台任务操作失败", result.Message,
        result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
}
