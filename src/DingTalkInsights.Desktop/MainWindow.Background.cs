using DingTalkInsights.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DingTalkInsights.Desktop;

public sealed partial class MainWindow
{
    MonitorSupervisor? supervisor;
    ContentDialog? activeDialog;
    Task? hostStopTask;
    NativeTrayIcon? tray;
    bool startupAttempted,allowClose,exiting;
    int activationCount;
    readonly ToggleSwitch closeToTray=new(){Header="关闭窗口后留在通知区域",OffContent="关闭时退出应用",OnContent="图标可用时隐藏；托盘可打开、停止或退出"};
    readonly ToggleSwitch saveRecovery=new(){Header="下次手动打开应用时恢复此监控",OffContent="重启不读取真实消息",OnContent="仅恢复本次明确确认的配置；不会安装开机自启动"};
    readonly NumberBox outputCap=new(){Header="每次最多输出 tokens",Minimum=500,Maximum=8000,Value=2000};
    void InitializeBackgroundHost()
    {
        tray=NewTray();
        AppWindow.Closing+=async(_,args)=>
        {
            if(allowClose)return;args.Cancel=true;
            if(savedSettings.CloseToTray)
            {
                if(TryHideToTray())StatusText.Text="窗口已隐藏；可从通知区域打开、停止或退出。";
                return;
            }
            await ExitApplicationAsync();
        };
    }
    NativeTrayIcon NewTray(INotificationArea? area=null)=>new(WinRT.Interop.WindowNative.GetWindowHandle(this),ShowMainWindow,
        ()=>_=StopFromHostAsync(),()=>_=ExitApplicationAsync(),()=>supervisor?.Wake(),area);
    void ShowMainWindow(){AppWindow.Show();Activate();}
    internal void ActivateExisting(){activationCount++;ShowMainWindow();}
    bool TryHideToTray()
    {
        if(savedSettings.CloseToTray&&tray?.CanHide()==true){AppWindow.Hide();return true;}
        ShowMainWindow();StatusText.Text="通知区域不可用，窗口保持可见。请使用“停止”或“退出应用”；不会隐藏到无法恢复的位置。";return false;
    }
    Task StopFromHostAsync()=>hostStopTask=CompleteHostStopAsync();
    async Task RevokeRecoveryAsync()
    {
        BackgroundPolicy.Revoke(state);store.Save(state);saveRecovery.IsOn=false;RefreshStatus();
        StatusText.Text="已撤销重启恢复权限；下次启动保持模拟模式。";await Task.CompletedTask;
    }
}
