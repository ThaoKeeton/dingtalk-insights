using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Workbench.Core;

namespace Workbench.App;

internal sealed partial class MainWindow
{
    private StackPanel CredentialPage()
    {
        var page = Ui.Page("凭据", "Windows 当前用户 DPAPI 保存密文。页面只读取声明与密文文件状态，不读取、显示或预填已保存的值。");
        page.Children.Add(Ui.Text("“已保存”仅表示存在普通密文文件，不表示凭据已验证或可以解密。JSON 设置不应保存凭据。"));
        var count = 0;
        foreach (var plugin in Manager.Plugins.Where(plugin => plugin.Manifest is not null))
        {
            var pluginId = plugin.Manifest!.Id;
            foreach (var secret in Manager.GetSecretSnapshots(pluginId))
            {
                count++;
                var supported = !_shutdownPending && plugin.State == PluginState.Enabled && Manager.IsSecretStorageSupported(pluginId);
                var body = new StackPanel { Spacing = 10 };
                body.Children.Add(Ui.Text(secret.Title, 20));
                body.Children.Add(Ui.Text($"{pluginId} / {secret.Id}\n{secret.Description}"));
                var status = Ui.Named(Ui.Text(""), $"凭据状态 {pluginId}/{secret.Id}");
                void RefreshStatus()
                {
                    var current = Manager.GetSecretSnapshots(pluginId).SingleOrDefault(snapshot => snapshot.Id == secret.Id);
                    status.Text = current?.IsConfigured switch
                    {
                        true => "已保存（未验证值）",
                        false => "未保存",
                        _ => "状态未知：SecretMetadataUnavailable"
                    };
                }
                RefreshStatus();
                body.Children.Add(status);
                var editor = Ui.Named(new PasswordBox { Header = "输入新值以替换", Password = string.Empty, IsEnabled = supported,
                    PasswordRevealMode = PasswordRevealMode.Hidden }, $"新凭据值 {pluginId}/{secret.Id}");
                body.Children.Add(editor);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                var save = Ui.Named(new Button { Content = "保存新值", IsEnabled = supported }, $"保存凭据 {pluginId}/{secret.Id}");
                var delete = Ui.Named(new Button { Content = "删除已保存值", IsEnabled = supported }, $"删除凭据 {pluginId}/{secret.Id}");
                save.Click += async (_, _) => await RunAsync(async () =>
                {
                    var value = editor.Password;
                    editor.Password = string.Empty;
                    try
                    {
                        if (value.Length == 0) { Notice("未保存", "请输入新值；若需移除，请使用删除。", InfoBarSeverity.Warning); return; }
                        await Manager.WriteSecretAsync(pluginId, secret.Id, value);
                        RefreshStatus();
                        Notice("凭据已保存", "仅保存当前 Windows 用户的密文；输入框已清空。", InfoBarSeverity.Success);
                    }
                    finally { value = string.Empty; editor.Password = string.Empty; }
                });
                delete.Click += async (_, _) => await RunAsync(async () =>
                {
                    editor.Password = string.Empty;
                    await Manager.DeleteSecretAsync(pluginId, secret.Id);
                    RefreshStatus();
                    Notice("凭据已删除", "已移除此插件声明项的密文。", InfoBarSeverity.Success);
                });
                actions.Children.Add(save);
                actions.Children.Add(delete);
                body.Children.Add(actions);
                if (!supported) body.Children.Add(Ui.Text(_shutdownPending ? "关闭待完成，当前只可查看状态。" : "需要已启用插件与 Windows DPAPI 支持才能编辑。"));
                page.Children.Add(Ui.Card(body));
            }
        }
        if (count == 0) page.Children.Add(Ui.Text("暂无声明的凭据。只有成功初始化的插件才会显示声明。"));
        AddDiagnostics(page);
        return page;
    }
}
