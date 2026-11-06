using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace DingTalkInsights.Desktop;

public partial class App:Application
{
    private Window? window;
    public App(){InitializeComponent();UnhandledException+=(_,e)=>{MessageBoxW(IntPtr.Zero,"应用遇到异常，请重新打开。断点及费用预留已保存在本机；不会自动重发消息。","DingTalk Insights",0x10);};}
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try{window=new MainWindow();window.Activate();}
        catch(Exception){MessageBoxW(IntPtr.Zero,"应用无法初始化。请完整解压 ZIP，并确认本机数据未被另一个实例占用。原数据不会自动覆盖。","DingTalk Insights",0x10);Environment.Exit(1);}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBoxW(IntPtr h,string text,string caption,uint type);
}
