using System.Security.Cryptography;
using DingTalkInsights.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DingTalkInsights.Desktop;

public sealed partial class MainWindow
{
    MonitorSupervisor? supervisor;
    ContentDialog? activeDialog;
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
    async Task StopFromHostAsync()
    {
        try{await StopMonitoringAsync();StatusText.Text="监控已停止并撤销重启恢复；已有断点、尝试和费用预留保留。";}
        catch(Exception){ShowMainWindow();StatusText.Text=MonitorNotices.For(MonitorFailure.Storage)+" 停止许可未能持久保存，请重试；窗口保持可见。";}
    }
    async Task StopMonitoringAsync(bool revokeRecovery=true)
    {
        activeDialog?.Hide();monitorCancellation?.Cancel();active?.Cancel();
        if(supervisor is {} current)await current.StopAsync(revokeRecovery);
        else if(revokeRecovery){BackgroundPolicy.Revoke(state);store.Save(state);}
        if(monitorTask is not null)await monitorTask;
        if(revokeRecovery)saveRecovery.IsOn=false;
        UpdateMonitorControls();RefreshStatus();
    }
    async Task ExitApplicationAsync()
    {
        if(exiting)return;exiting=true;
        try
        {
            active?.Cancel();await StopMonitoringAsync(revokeRecovery:false);
            await operations.WaitAsync();operations.Release();
            allowClose=true;tray?.Dispose();Close();Application.Current.Exit();
        }
        catch(Exception){exiting=false;ShowMainWindow();StatusText.Text=MonitorNotices.For(MonitorFailure.Storage)+" 退出前请重试。";}
    }
    async Task RevokeRecoveryAsync()
    {
        BackgroundPolicy.Revoke(state);store.Save(state);saveRecovery.IsOn=false;RefreshStatus();
        StatusText.Text="已撤销重启恢复权限；下次启动保持模拟模式。";await Task.CompletedTask;
    }
    async Task<MonitoringConfiguration?> CurrentConfigurationAsync(CancellationToken cancellation)
    {
        // Command-line smoke arguments can never select stored real collectors or transports.
        if(smoke||IsDemo)return null;
        var path=Path.GetFullPath(dwsPath.Text.Trim());
        if(!File.Exists(path))return null;
        cancellation.ThrowIfCancellationRequested();
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var revision=Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellation));
        return new MonitoringConfiguration("dws",profile.Text.Trim(),conversation.Text.Trim(),ParseTime(since.Text),path,revision,
            enableAi.IsOn,Model(),(int)interval.Value,(int)retention.Value,Guard.Hash(Guard.Hash(profile.Text.Trim())+"\n"+vault.Revision));
    }
    async Task BeginSupervisorAsync(MonitoringConfiguration config,bool demo,bool recover)
    {
        if(smoke&&!demo)throw new MonitoringFault(MonitorFailure.Configuration);
        var key=config.UploadToModel&&!demo?vault.Read().OpenAiKey:"";
        if(config.UploadToModel&&!demo&&string.IsNullOrWhiteSpace(key))throw new MonitoringFault(MonitorFailure.Configuration);
        var client=new DwsClient(config.Executable,gate);
        Func<QueryScope,CancellationToken,Task<MessageBatch>> fetch=demo?(s,_)=>Task.FromResult(demoBatch with{Scope=s,Messages=demoBatch.Messages.Where(m=>m.CreatedAt>=s.Start&&m.CreatedAt<s.End).ToArray()}):
            (s,c)=>client.ReadAsync(s,gate.Issue(ExternalAction.Read,s.Fingerprint),c);
        var current=new MonitorSupervisor(engine,store,fetch,Analyzer(demo,key,config.Model),demo?_=>Task.FromResult<MonitoringConfiguration?>(config):async cancellation=>
            {
                var actual=await CurrentConfigurationAsync(cancellation);
                if(actual is null||actual.Fingerprint!=config.Fingerprint)return null;
                // Match the local executable digest and exact policy before executing even readonly CLI status.
                var ready=await client.ReadinessAsync(config.Profile,cancellation);
                return ready.Ready?actual:null;
            },operationGate:operations);
        await AttachSupervisorAsync(current,config,recover);
    }
    async Task AttachSupervisorAsync(MonitorSupervisor current,MonitoringConfiguration config,bool recover)
    {
        supervisor=current;
        current.Activity+=busy=>
        {
            BusyBar.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
            foreach(var button in actions)button.IsEnabled=!busy;UpdateMonitorControls();
        };
        current.Changed+=result=>
        {
            if(!ReferenceEquals(supervisor,current))return;
            if(result?.Report is {} report)SetReport(report);
            RefreshStatus();StatusText.Text=current.Notice;UpdateMonitorControls();
            if(state.Supervisor.Status==MonitorStatus.NeedsAttention)ShowMainWindow();
        };
        if(!await current.StartAsync(config,confirmed:!recover,recover:recover))throw new MonitoringFault(state.Supervisor.Failure==MonitorFailure.Storage?MonitorFailure.Storage:MonitorFailure.Configuration);
        monitorCancellation=new CancellationTokenSource();UpdateMonitorControls();monitorTask=ObserveSupervisorAsync(current,monitorCancellation);
    }
    async Task ObserveSupervisorAsync(MonitorSupervisor current,CancellationTokenSource marker)
    {
        try{await current.Completion;}
        finally
        {
            if(ReferenceEquals(monitorCancellation,marker)){monitorCancellation=null;UpdateMonitorControls();RefreshStatus();}
            marker.Dispose();
        }
    }
    async Task TryRecoverOnLaunchAsync()
    {
        if(smoke||state.Recovery is null||state.UserStopped)return;
        try
        {
            if(!File.Exists(Path.Combine(dataRoot,"settings.json")))throw new MonitoringFault(MonitorFailure.Configuration);
            source.SelectedIndex=1;LoadConnectionFields();enableAi.IsOn=savedSettings.UploadToModel;
            var config=await CurrentConfigurationAsync(CancellationToken.None);
            if(config is null||!BackgroundPolicy.CanRecover(state,config,true))throw new MonitoringFault(MonitorFailure.Configuration);
            saveRecovery.IsOn=true;await BeginSupervisorAsync(config,demo:false,recover:true);
            StatusText.Text="已按明确保存的单会话权限恢复；窗口可见，后台范围与预算未扩大。";
        }
        catch(Exception)
        {
            source.SelectedIndex=0;state.Supervisor=state.Supervisor with{Status=MonitorStatus.NeedsAttention,Failure=MonitorFailure.Configuration};
            try{store.Save(state);}catch(Exception){state.Supervisor=state.Supervisor with{Failure=MonitorFailure.Storage};}
            StatusText.Text=MonitorNotices.For(state.Supervisor.Failure)+" 当前保持模拟模式。";ShowMainWindow();RefreshStatus();
        }
    }
}
