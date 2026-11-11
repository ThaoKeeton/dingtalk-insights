using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;
using DingTalkInsights.Core;

namespace DingTalkInsights.Desktop;

public partial class App:Application
{
    private MainWindow? window;
    private AppInstance? instance;
    private bool pendingActivation;
    public App(){InitializeComponent();UnhandledException+=(_,_)=>{MessageBoxW(IntPtr.Zero,"应用遇到异常，请重新打开。断点及费用预留已保存在本机；不会自动重发消息。","DingTalk Insights",0x10);};}
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var smoke=Environment.GetCommandLineArgs().Contains("--smoke-test");
            var key=smoke?"DingTalkInsights.Smoke."+Guard.Hash(Path.GetFullPath(AppContext.BaseDirectory)):"DingTalkInsights.UserSession";
            instance=AppInstance.FindOrRegisterForKey(key);
            if(!instance.IsCurrent)
            {
                await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Exit();return;
            }
            instance.Activated+=(_,_)=>
            {
                // Arguments are deliberately ignored: activation can only show the existing window.
                if(window is {} existing)existing.DispatcherQueue.TryEnqueue(existing.ActivateExisting);
                else pendingActivation=true;
            };
            window=new MainWindow();window.Activate();if(pendingActivation)window.ActivateExisting();
        }
        catch(Exception){MessageBoxW(IntPtr.Zero,"应用无法初始化或激活已有实例。请完整解压 ZIP，并核对已有窗口与本机数据锁。原数据不会自动覆盖。","DingTalk Insights",0x10);Environment.Exit(1);}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBoxW(IntPtr h,string text,string caption,uint type);
}
