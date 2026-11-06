using System.Text.Json;

namespace DingTalkInsights.Core;

public enum MonitorFailure { None, TransientRead, Authorization, Validation, Schema, Incomplete, Retention, Storage, Budget, ModelUnknown, ModelAuthorization, Unknown, Configuration, RetryLimit }
public enum MonitorStatus { Stopped, Running, ReadRetry, NeedsAttention }

/// <summary>Only safe categories cross into health/UI; never carries transport text or an inner exception.</summary>
public sealed class MonitoringFault(MonitorFailure category,TimeSpan? retryAfter=null):SafetyException(MonitorNotices.For(category))
{
    public MonitorFailure Category {get;}=category;
    public TimeSpan? RetryAfter {get;}=retryAfter;
}
public static class MonitorNotices
{
    public static string For(MonitorFailure category)=>category switch
    {
        MonitorFailure.None=>"等待下一次有界检查；休眠、退出或注销期间不提供实时采集。",
        MonitorFailure.TransientRead=>"DWS 明确报告可重试的暂时故障；断点未推进，正在有界退避等待。",
        MonitorFailure.Authorization=>"DWS 登录或权限需要核对；监控暂停，不自动登录或切换身份。",
        MonitorFailure.Validation=>"监控配置需要核对；监控已暂停。",
        MonitorFailure.Schema=>"DWS 数据契约无法验证；监控暂停，断点未跨过未知范围。",
        MonitorFailure.Incomplete=>"采集不完整；监控暂停，断点未跨过缺页或未知范围。",
        MonitorFailure.Retention=>"下一窗口早于保留期；监控暂停，请明确调整保留天数或确认放弃缺口。",
        MonitorFailure.Storage=>"本机状态无法安全保存；后续外部操作已停止，请核对存储后重试。",
        MonitorFailure.Budget=>"模型达到本地每日预算或请求上限；未尝试来源保留，采集继续，模型等下个 UTC 日复核限额。",
        MonitorFailure.ModelUnknown=>"模型结果不确定或无效；已保留尝试与费用预留，不自动重试。AI 已暂停，本机采集继续；未整理不表示没有问题。",
        MonitorFailure.ModelAuthorization=>"OpenAI 返回 HTTP 401/403 认证失败；AI 已暂停且不自动付费重试，本机采集继续。请核对凭据后明确重新授权。",
        MonitorFailure.Configuration=>"恢复权限、身份、配置或凭据已改变，或本机就绪状态无法确认；请核对并明确重新授权。",
        MonitorFailure.RetryLimit=>"DWS 暂时故障已达重试上限或等待要求超出支持范围；监控暂停，请人工核对。",
        _=>"监控遇到无法确定的故障，已暂停；未展示响应正文或凭据，请人工核对。"
    };
}

public sealed record MonitoringConfiguration(string Source,string Profile,string ConversationId,DateTimeOffset Start,
    string Executable,string ExecutableRevision,bool UploadToModel,ModelSettings Model,int PollSeconds,int RetentionDays,string CredentialRevision)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Fingerprint=>Guard.Hash(JsonSerializer.Serialize(this));
    public QueryScope Scope(DateTimeOffset now)=>new(Profile,ConversationId,Start,now);
    public void Validate()
    {
        if(Source is not ("dws" or "demo")||PollSeconds is <60 or >3600||RetentionDays is <1 or >365||Model is null)
            throw new MonitoringFault(MonitorFailure.Validation);
        Model.Validate();Scope(Start.AddTicks(1)).ValidateOrigin();
        if(Source=="dws"&&(!Path.IsPathFullyQualified(Executable)||!IsDigest(ExecutableRevision)||!IsDigest(CredentialRevision)))throw new MonitoringFault(MonitorFailure.Configuration);
    }
    internal static bool IsDigest(string? text)=>text is {Length:64}&&text.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
public sealed record RecoveryPolicy(int Version,MonitoringConfiguration Configuration,DateTimeOffset ApprovedAt);
public sealed record SupervisorHealth
{
    public MonitorStatus Status {get;init;}=MonitorStatus.Stopped;
    public MonitorFailure Failure {get;init;}=MonitorFailure.None;
    public string ConfigurationFingerprint {get;init;}="";
    public int ReadFailures {get;init;}
    public DateTimeOffset? NextReadAt {get;init;}
    public DateTimeOffset? ModelDeferredUntil {get;init;}
    public bool AiSuppressed {get;init;}
    public DateTimeOffset? LastCompletedAt {get;init;}
    public void Validate()
    {
        if(!Enum.IsDefined(Status)||!Enum.IsDefined(Failure)||ReadFailures is <0 or >6||ConfigurationFingerprint is null||ConfigurationFingerprint.Length is not (0 or 64))
            throw new MonitoringFault(MonitorFailure.Configuration);
    }
}
public static class BackgroundPolicy
{
    public static bool Approve(InsightState state,MonitoringConfiguration config,DateTimeOffset now,bool confirmed)
    {
        if(!confirmed||config.Source!="dws")return false;
        config.Validate();state.Recovery=new RecoveryPolicy(1,config,now);state.UserStopped=false;
        state.Supervisor=new SupervisorHealth{ConfigurationFingerprint=config.Fingerprint};return true;
    }
    public static bool CanRecover(InsightState state,MonitoringConfiguration config,bool ready)
    {
        try
        {
            config.Validate();state.Supervisor.Validate();
            return ready&&!state.UserStopped&&state.Recovery is {Version:1,Configuration:{} approved}
                &&approved.Fingerprint==config.Fingerprint&&state.Supervisor.Status!=MonitorStatus.NeedsAttention
                &&(state.Supervisor.ConfigurationFingerprint.Length==0||state.Supervisor.ConfigurationFingerprint==config.Fingerprint);
        }
        catch(Exception){return false;}
    }
    public static void Revoke(InsightState state)
    {
        state.Recovery=null;state.UserStopped=true;
        state.Supervisor=state.Supervisor with{Status=MonitorStatus.Stopped,NextReadAt=null};
    }
}
