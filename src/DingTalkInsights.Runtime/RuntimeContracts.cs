using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public enum RuntimeConnection { Demo,Canonical,Disconnected }
public enum CredentialLocation { Plugin,ConnectedStore,Host }
[Flags] public enum CredentialUse { None=0,Model=1,Webhook=2 }
public enum RuntimeFailure { Inactive,Busy,Connection,Ownership,Credentials,Configuration,Retention,Storage,Source,Model }
/// <summary>Safe display text only; never wraps lower transport/provider exception text.</summary>
public sealed class RuntimeFault(RuntimeFailure category):SafetyException(category switch
{
    RuntimeFailure.Inactive=>"插件已停用；请重新启用后明确连接并开始。",
    RuntimeFailure.Busy=>"已有操作或监控正在执行，请停止或等待后再试。",
    RuntimeFailure.Connection=>"请明确选择并确认已有的规范数据目录；模拟数据不会连接真实服务。",
    RuntimeFailure.Ownership=>"规范数据正在被其他应用或插件实例使用；请先正常停止并退出该实例。",
    RuntimeFailure.Credentials=>"本次用途尚未明确批准凭据，或凭据版本已改变；请在本机重新确认，已保存值不会回显。",
    RuntimeFailure.Retention=>MonitorNotices.For(MonitorFailure.Retention),
    RuntimeFailure.Storage=>MonitorNotices.For(MonitorFailure.Storage),
    RuntimeFailure.Configuration=>MonitorNotices.For(MonitorFailure.Configuration),
    RuntimeFailure.Model=>MonitorNotices.For(MonitorFailure.ModelUnknown),
    _=>"来源操作未完成；未展示响应正文或凭据，请核对本机配置。"
})
{public RuntimeFailure Category{get;}=category;}

public sealed record MonitoringRequest(string Source,string Profile,string ConversationId,DateTimeOffset Start,string Executable,
    bool UploadToModel,ModelSettings Model,int PollSeconds,int RetentionDays)
{
    public void Validate()
    {
        if(Source is not("demo" or "dws")||Model is null||PollSeconds is <60 or >3600||RetentionDays is <1 or >365||
            Source=="dws"&&!Path.IsPathFullyQualified(Executable))throw new RuntimeFault(RuntimeFailure.Configuration);
        Model.Validate();new QueryScope(Profile,ConversationId,Start,Start.AddTicks(1)).ValidateOrigin();
    }
}

public sealed record RuntimeSnapshot
{
    public bool Active{get;init;}=true;
    public bool IsBusy{get;init;}
    public bool IsMonitoring{get;init;}
    public RuntimeConnection Connection{get;init;}=RuntimeConnection.Demo;
    public string Draft{get;init;}="";
    public IReadOnlyList<SourceMessage> Candidates{get;init;}=[];
    public string Markdown{get;init;}="";
    public DateTimeOffset? ReportOldestSourceAt{get;init;}
    public IReadOnlyList<SavedReport> History{get;init;}=[];
    public IReadOnlyList<Conversation> Conversations{get;init;}=[];
    public SupervisorHealth Health{get;init;}=new();
    public MonitoringOrigin? Origin{get;init;}
    public MonitoringConfiguration? Configuration{get;init;}
    public string BudgetDay{get;init;}="";
    public decimal ReservedUsd{get;init;}
    public int Requests{get;init;}
    public string Notice{get;init;}="默认模拟模式；启用与导航不会读取聊天、上传或发送。";
    public int RetentionDays{get;init;}=30;
    public DateTimeOffset RetentionReference{get;init;}
}

/// <summary>Only lower I/O is replaceable. Controller/engine/supervisor/storage remain production code in tests.</summary>
public interface IRuntimeServices
{
    Task<string> InspectSourceAsync(string executable,string profile,CancellationToken cancellation);
    Task<MessageBatch> ReadAsync(string executable,QueryScope scope,CancellationToken cancellation);
    Task<MessageBatch> SearchAsync(string executable,QueryScope scope,string keywords,CancellationToken cancellation);
    Task<IReadOnlyList<Conversation>> DiscoverAsync(string executable,string profile,DateTimeOffset start,DateTimeOffset end,CancellationToken cancellation);
    Task<ModelResult> AnalyzeAsync(string key,MessageBatch batch,string task,ModelSettings model,CancellationToken cancellation);
    Task<DeliveryStatus> SendAsync(WebhookTarget target,string text,CancellationToken cancellation);
}

/// <summary>Non-sensitive observations of actual owned resources and operations; reading never performs IO.</summary>
public sealed record RuntimeObservation(long Generation,int PendingOperations,int Subscriptions,bool StoreOwned,bool SupervisorPresent,bool MonitoringCompleted,long Dispatches,long OwnedStoreWrites);
