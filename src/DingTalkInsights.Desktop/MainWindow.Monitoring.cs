using System.Security.Cryptography;
using DingTalkInsights.Core;
using Microsoft.UI.Xaml;

namespace DingTalkInsights.Desktop;

public sealed partial class MainWindow
{
    // All host continuations run on the UI context. Own startup before confirmation/hash awaits,
    // including launch recovery, and retire it before Stop/Exit can report completion.
    HostStartup? hostStartup;
    long hostStartupGeneration;
    Task hostRetirement=Task.CompletedTask;
    sealed class HostStartup(long generation,CancellationToken cancellation)
    {
        public long Generation{get;}=generation;
        public CancellationTokenSource Cancellation{get;}=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public TaskCompletionSource Retired{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    async Task OwnHostStartupAsync(Func<HostStartup,Task> start)
    {
        if(exiting)return;
        if(hostStartup is {Cancellation.IsCancellationRequested:false})throw new SafetyException("请先停止现有监控。");
        var pendingRetirement=hostRetirement;
        var startup=new HostStartup(hostStartupGeneration,ActiveToken);hostStartup=startup;UpdateMonitorControls();
        try
        {
            await pendingRetirement.WaitAsync(startup.Cancellation.Token);
            EnsureHostStartup(startup);await start(startup);
        }
        catch(OperationCanceledException)when(startup.Cancellation.IsCancellationRequested){}
        finally
        {
            if(ReferenceEquals(hostStartup,startup))hostStartup=null;
            startup.Cancellation.Dispose();startup.Retired.TrySetResult();UpdateMonitorControls();
        }
    }
    void EnsureHostStartup(HostStartup startup)
    {
        startup.Cancellation.Token.ThrowIfCancellationRequested();
        if(startup.Generation!=hostStartupGeneration||!ReferenceEquals(hostStartup,startup))throw new OperationCanceledException();
    }
    Task StartMonitoringAsync()=>OwnHostStartupAsync(StartMonitoringCoreAsync);
    async Task StartMonitoringCoreAsync(HostStartup startup)
    {
        if(monitorCancellation is not null)throw new SafetyException("请先停止现有监控。");
        var scope=Scope(monitoring:true);var model=Model();var isDemo=IsDemo;var useAi=enableAi.IsOn||isDemo;
        var poll=(int)interval.Value;var days=isDemo?state.RetentionDays:(int)retention.Value;
        if(poll is <60 or >3600||days is <1 or >365)throw new SafetyException("检查间隔或保留天数无效。");
        var now=DateTimeOffset.UtcNow;
        var next=MonitorEngine.NextScope(state,scope,now);
        if(next.Start<now.AddDays(-days))throw new SafetyException("下一采集窗口早于所选保留期；请明确增加保留天数，或改晚起点并在开始确认中核对放弃的缺口。");
        var resumeNote="";
        if(!isDemo&&state.Monitoring is {} previous&&previous.Scope(now).Key==scope.Key)
        {
            var priorCursor=MonitorEngine.ResumeCursor(state,previous.Scope(now),now);
            resumeNote=$"\n上次授权起点：{previous.Start:O}；从覆盖断点恢复的下一起点：{priorCursor:O}。";
            if(scope.Start>priorCursor)resumeNote+=$"\n你已改晚起点：确认开始将明确放弃此前尚未检查的缺口 [{priorCursor:O}, {scope.Start:O})。";
        }
        var backgroundNote=$"\n关闭窗口：{(closeToTray.IsOn?"通知区域图标可用时继续；图标失败保留窗口":"退出应用")}"+
            (saveRecovery.IsOn?"\n保存重启恢复权限：今后手动打开应用将恢复同一身份、会话、原始起点、来源与凭据版本、模型、预算、间隔和保留期。无需每日重复确认；配置变化必须重新授权。":"\n不保存恢复权限；退出后再次开始需要确认。");
        var uploadNote=useAi?$"允许今后新增/修订正文上传 OpenAI：{model.Model}，输入/输出单价 {model.InputUsdPerMillion}/{model.OutputUsdPerMillion} USD/百万 tokens，每次最多 {model.MaxOutputTokens} 输出 tokens；每日费用预留 USD {model.DailyBudgetUsd}，请求上限 {model.MaxRequestsPerDay}。":"不上传到模型，仅采集到本机。";
        if((!isDemo||closeToTray.IsOn)&&!await Confirm("确认持续监控与后台范围",scope.Display+resumeNote+$"\n来源：{(isDemo?"模拟数据":dwsPath.Text.Trim())}\n每 {poll} 秒检查，原文加密保留 {days} 天。\n"+uploadNote+backgroundNote+"\n停止会撤销恢复权限。退出保留单独批准的恢复策略。不会自动发送 Webhook，也不安装开机启动。","授权以上范围并开始"))return;
        EnsureHostStartup(startup);
        MonitoringConfiguration config;
        if(isDemo)config=new MonitoringConfiguration("demo",scope.Profile,scope.ConversationId,scope.Start,"","",true,model,poll,days,"");
        else config=await CurrentConfigurationAsync(startup.Cancellation.Token)??throw new MonitoringFault(MonitorFailure.Configuration);
        EnsureHostStartup(startup);
        // Ordinary settings are saved before recovery permission, so a partial settings write cannot broaden a policy.
        if(!isDemo){state.Monitoring=new MonitoringOrigin(scope.Profile,scope.ConversationId,scope.Start);savedSettings=savedSettings with{CloseToTray=closeToTray.IsOn};await SaveSettingsAsync();}
        else if(closeToTray.IsOn!=savedSettings.CloseToTray){savedSettings=savedSettings with{CloseToTray=closeToTray.IsOn};}
        EnsureHostStartup(startup);
        if(saveRecovery.IsOn&&!isDemo)BackgroundPolicy.Approve(state,config,now,confirmed:true);
        else BackgroundPolicy.Revoke(state);
        store.Save(state);
        await BeginSupervisorAsync(config,isDemo,recover:false,startup);
        EnsureHostStartup(startup);
        StatusText.Text=isDemo?"模拟持续检查已开始；不连接任何服务。":"持续检查已开始，仅限明确确认的后台范围与预算。";
    }
    async Task CompleteHostStopAsync()
    {
        try{await StopMonitoringAsync();StatusText.Text="监控已停止并撤销重启恢复；已有断点、尝试和费用预留保留。";}
        catch(Exception){ShowMainWindow();StatusText.Text=MonitorNotices.For(MonitorFailure.Storage)+" 停止许可未能持久保存，请重试；窗口保持可见。";}
    }
    async Task StopMonitoringAsync(bool revokeRecovery=true)
    {
        var retiring=hostStartup;var previousRetirement=hostRetirement;
        var barrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);hostRetirement=barrier.Task;
        hostStartupGeneration++;retiring?.Cancellation.Cancel();
        activeDialog?.Hide();monitorCancellation?.Cancel();active?.Cancel();
        try
        {
            await previousRetirement;
            var current=supervisor;
            try{if(current is not null)await current.StopAsync(revokeRecovery);}
            finally{if(retiring is not null)await retiring.Retired.Task;}
            if(current is null&&revokeRecovery){BackgroundPolicy.Revoke(state);store.Save(state);}
            if(monitorTask is not null)await monitorTask;
            if(revokeRecovery)saveRecovery.IsOn=false;
            UpdateMonitorControls();RefreshStatus();
        }
        finally
        {
            if(ReferenceEquals(hostRetirement,barrier.Task))hostRetirement=Task.CompletedTask;
            barrier.TrySetResult();
        }
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
    async Task<MonitoringConfiguration?> CurrentConfigurationAsync(CancellationToken cancellation)
    {
        // Command-line smoke arguments can never select stored real collectors or transports.
        if(smoke||IsDemo)return null;
        var path=Path.GetFullPath(dwsPath.Text.Trim());
        if(!File.Exists(path))return null;
        cancellation.ThrowIfCancellationRequested();
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var revision=Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellation));
        cancellation.ThrowIfCancellationRequested();
        return new MonitoringConfiguration("dws",profile.Text.Trim(),conversation.Text.Trim(),ParseTime(since.Text),path,revision,
            enableAi.IsOn,Model(),(int)interval.Value,(int)retention.Value,Guard.Hash(Guard.Hash(profile.Text.Trim())+"\n"+vault.Revision));
    }
    async Task BeginSupervisorAsync(MonitoringConfiguration config,bool demo,bool recover,HostStartup startup)
    {
        EnsureHostStartup(startup);
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
        EnsureHostStartup(startup);await AttachSupervisorAsync(current,config,recover,startup.Cancellation.Token);
    }
    async Task AttachSupervisorAsync(MonitorSupervisor current,MonitoringConfiguration config,bool recover,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        supervisor=current;
        current.Activity+=busy=>
        {
            BusyBar.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
            foreach(var button in actions)button.IsEnabled=!busy;UpdateMonitorControls();
        };
        current.Changed+=result=>
        {
            if(!ReferenceEquals(supervisor,current))return;
            var archived=result?.Report is not {} report||SetReport(report);
            RefreshStatus();StatusText.Text=archived?current.Notice:ReviewStorageNotice;UpdateMonitorControls();
            if(state.Supervisor.Status==MonitorStatus.NeedsAttention)ShowMainWindow();
        };
        var started=await current.StartAsync(config,confirmed:!recover,recover:recover,cancellation:cancellation);
        cancellation.ThrowIfCancellationRequested();
        if(!started)throw new MonitoringFault(state.Supervisor.Failure==MonitorFailure.Storage?MonitorFailure.Storage:MonitorFailure.Configuration);
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
    Task TryRecoverOnLaunchAsync()=>OwnHostStartupAsync(RecoverOnLaunchCoreAsync);
    async Task RecoverOnLaunchCoreAsync(HostStartup startup)
    {
        if(smoke||state.Recovery is null||state.UserStopped)return;
        if(ReviewRecoveryBlocked)
        {
            source.SelectedIndex=0;state.Supervisor=state.Supervisor with{Status=MonitorStatus.NeedsAttention,Failure=MonitorFailure.Storage};
            StatusText.Text="本机整理尝试或核实数据维护未完成，仅能查看保留期内原始历史；本次没有读取凭据或检查 DWS。请检查存储或明确清理索引。";
            ShowMainWindow();RefreshStatus();return;
        }
        try
        {
            if(!File.Exists(Path.Combine(dataRoot,"settings.json")))throw new MonitoringFault(MonitorFailure.Configuration);
            source.SelectedIndex=1;LoadConnectionFields();enableAi.IsOn=savedSettings.UploadToModel;
            var config=await CurrentConfigurationAsync(startup.Cancellation.Token);
            EnsureHostStartup(startup);
            if(config is null||!BackgroundPolicy.CanRecover(state,config,true))throw new MonitoringFault(MonitorFailure.Configuration);
            saveRecovery.IsOn=true;await BeginSupervisorAsync(config,demo:false,recover:true,startup);
            EnsureHostStartup(startup);
            StatusText.Text="已按明确保存的单会话权限恢复；窗口可见，后台范围与预算未扩大。";
        }
        catch(Exception)
        {
            EnsureHostStartup(startup);
            source.SelectedIndex=0;state.Supervisor=state.Supervisor with{Status=MonitorStatus.NeedsAttention,Failure=MonitorFailure.Configuration};
            try{store.Save(state);}catch(Exception){state.Supervisor=state.Supervisor with{Failure=MonitorFailure.Storage};}
            StatusText.Text=MonitorNotices.For(state.Supervisor.Failure)+" 当前保持模拟模式。";ShowMainWindow();RefreshStatus();
        }
    }
}
