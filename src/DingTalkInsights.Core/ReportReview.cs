using System.Text.Json;

namespace DingTalkInsights.Core;

public sealed record ManualIssueReview(int Index,string IssueFingerprint,bool Resolved,DateTimeOffset RecordedAt);
public sealed class ReportReviewLedger
{
    public int Version{get;set;}=1;
    public Dictionary<string,ReportReviewSnapshot> Reports{get;set;}=[];
}

/// <summary>Validated original evidence, not an automatically merged or newly assessed issue.</summary>
public sealed record ReportReviewSnapshot(string ReportKey,QueryScope Scope,SourceMessage[] Sources,bool Complete,string[] Warnings,
    DateTimeOffset OldestSourceAt,string ModelJson,ManualIssueReview[] Decisions,bool Invalidated=false)
{
    public const int MaximumBytes=512*1024;
    public static string Key(SavedReport saved)=>Guard.Hash(JsonSerializer.Serialize(new
    {saved.CreatedAt,saved.ScopeKey,saved.Task,saved.Markdown,saved.IsDemo,saved.OldestSourceAt}));
    public static string IssueKey(Issue issue)=>Guard.Hash(JsonSerializer.Serialize(new
    {issue.Title,issue.Summary,issue.Communication,issue.NextStep,Evidence=issue.Evidence.Select(e=>new{Revision=e.Message.Revision,e.Quote})}));
    public static ReportReviewSnapshot Capture(SavedReport saved,AnalysisReport report)
    {
        if(report.Issues.Any(i=>i.HumanConfirmed||i.Status==IssueStatus.Resolved)||ReportRenderer.Markdown(report)!=saved.Markdown||
            saved.ScopeKey!=report.Batch.Scope.Key||saved.Task!=report.Task||saved.IsDemo!=report.Batch.IsDemo||saved.OldestSourceAt is not {} oldest)
            throw new MonitoringFault(MonitorFailure.Storage);
        var model=JsonSerializer.Serialize(new
        {
            issues=report.Issues.Select(i=>new{i.Title,i.Summary,status=i.Status switch{IssueStatus.Open=>"open",IssueStatus.InProgress=>"in_progress",_=>"pending_verification"},
                i.Communication,i.NextStep,evidence=i.Evidence.Select(e=>new{messageId=e.Message.MessageId,quote=e.Quote})}),
            pendingQuestions=report.PendingQuestions
        },new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        var snapshot=new ReportReviewSnapshot(Key(saved),report.Batch.Scope,
            report.Issues.SelectMany(i=>i.Evidence).Select(e=>e.Message).DistinctBy(m=>m.MessageId).ToArray(),report.Batch.Complete,report.Batch.Warnings.ToArray(),oldest,model,[]);
        _=snapshot.Original(saved);return snapshot;
    }
    public AnalysisReport Original(SavedReport saved)
    {
        if(ReportKey!=Key(saved)||Scope is null||Scope.Key!=saved.ScopeKey||saved.OldestSourceAt!=OldestSourceAt||Sources is null||Sources.Length>40||Warnings is null||Warnings.Length>100||
            Warnings.Any(w=>w is null||w.Length>4096)||ModelJson is null||ModelJson.Length>MaximumBytes||Decisions is null||Decisions.Length>30||
            Decisions.Any(d=>d is null)||JsonSerializer.SerializeToUtf8Bytes(this).Length>MaximumBytes)throw new MonitoringFault(MonitorFailure.Storage);
        Scope.ValidateOrigin();
        if(Sources.Any(m=>m is null||m.Text is null||m.MessageId is null||m.ConversationId!=Scope.ConversationId||m.CreatedAt<OldestSourceAt||m.CreatedAt<Scope.Start||m.CreatedAt>=Scope.End)||
            Sources.Select(m=>m.MessageId).Distinct(StringComparer.Ordinal).Count()!=Sources.Length)throw new MonitoringFault(MonitorFailure.Storage);
        var batch=new MessageBatch(Scope,Array.AsReadOnly(Sources.ToArray()),Complete,Array.AsReadOnly(Warnings.ToArray()),new Dictionary<string,JsonElement>(),saved.IsDemo);
        var report=AnalysisValidator.Validate(ModelJson,batch,saved.Task);
        if(ReportRenderer.Markdown(report)!=saved.Markdown||Decisions.Select(d=>d.Index).Distinct().Count()!=Decisions.Length||
            Decisions.Any(d=>d.Index<0||d.Index>=report.Issues.Count||d.IssueFingerprint!=IssueKey(report.Issues[d.Index])||d.RecordedAt<saved.CreatedAt))throw new MonitoringFault(MonitorFailure.Storage);
        return report;
    }
    public bool EvidenceCurrent(InsightState state)=>!Invalidated&&Sources.All(m=>state.Messages.TryGetValue(InsightState.MessageKey(Scope,m),out var current)&&current.Message.Revision==m.Revision);
    public ReportReviewView View(InsightState state,SavedReport saved)
    {
        var report=Original(saved);var current=EvidenceCurrent(state);
        foreach(var decision in Decisions.Where(d=>d.Resolved&&current))report.ConfirmResolved(decision.Index,true);
        var markdown=ReportRenderer.Markdown(report);
        if(!current)markdown+="\n## 核实记录已失效\n\n原文缺失或已有新修订；历史核实不作为当前确认，请重新采集并整理。\n";
        if(Decisions.Length>0)
        {
            markdown+="\n## 本机人工核实记录\n\n";
            foreach(var decision in Decisions.OrderBy(d=>d.Index))markdown+=$"- 第 {decision.Index+1} 项：{(!current?"记录失效":decision.Resolved?"已核对原文并确认解决":"人工确认已撤销，恢复原待核实状态")} · {decision.RecordedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC\n";
        }
        return new(saved,this,report,markdown,current,Array.AsReadOnly(Decisions.ToArray()));
    }
}

public sealed class ReportReviewView
{
    readonly ReportReviewSnapshot? snapshot;
    public SavedReport Saved{get;}
    public AnalysisReport? Report{get;}
    public string Markdown{get;}
    public bool CanReview{get;}
    public bool Unavailable{get;}
    public IReadOnlyList<ManualIssueReview> Decisions{get;}
    public string Key=>ReportReviewSnapshot.Key(Saved);
    public string Fingerprint{get;}
    public static ReportReviewView Legacy(SavedReport saved,bool unavailable=false)=>new(saved,null,null,
        saved.Markdown+(unavailable?"\n## 核实记录暂不可用\n\n本机核实文件未能读取或维护，未应用人工确认。此处仅显示原始保存文本；请检查存储或明确清理索引。原核实文件未自动覆盖。\n":""),false,[],unavailable);
    internal ReportReviewView(SavedReport saved,ReportReviewSnapshot? snapshot,AnalysisReport? report,string markdown,bool canReview,IReadOnlyList<ManualIssueReview> decisions,bool unavailable=false)
    {
        Saved=saved;this.snapshot=snapshot;Report=report;Markdown=markdown;CanReview=canReview;Decisions=decisions;Unavailable=unavailable;
        Fingerprint=Guard.Hash(Key+"\n"+markdown+"\n"+JsonSerializer.Serialize(new{canReview,unavailable,decisions}));
    }
    public ReportReviewView Refresh(InsightState state)=>snapshot is {} value
        ?(value.EvidenceCurrent(state)?value:value with{Invalidated=true}).View(state,Saved):this;
}
