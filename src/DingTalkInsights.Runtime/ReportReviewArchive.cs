using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

/// <summary>All operations share BusinessStore's canonical lease. No original report, budget or attempt is rewritten.</summary>
public sealed class ReportReviewArchive(string path,IDataCipher cipher,Action ensureOpen,Action<string>? persistUnavailableMarker=null)
{
    readonly EncryptedDocument<ReportReviewLedger> document=new(path,cipher,16*1024*1024);
    readonly string blockedPath=path+".blocked";
    bool Blocked=>File.Exists(blockedPath)||Directory.Exists(blockedPath);
    static void RequireSaved(InsightState state,SavedReport saved)
    {
        if(!state.Reports.Contains(saved)||saved.OldestSourceAt is not {} oldest||oldest<state.RetentionCutoff||saved.CreatedAt<state.RetentionCutoff)
            throw new MonitoringFault(MonitorFailure.Retention);
    }
    T Safe<T>(Func<T> action)
    {
        ensureOpen();
        try{return action();}
        catch(MonitoringFault){throw;}
        catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
    }
    ReportReviewLedger Load(InsightState state,bool maintenance=false,Action? removingEvidence=null)
    {
        if(!maintenance&&Blocked)throw new MonitoringFault(MonitorFailure.Storage);
        var ledger=document.Read()??new();
        if(ledger.Version!=1||ledger.Reports is null||ledger.Reports.Count>100||ledger.Reports.Any(p=>p.Value is null||p.Key.Length!=64||p.Key!=p.Value.ReportKey))
            throw new MonitoringFault(MonitorFailure.Storage);
        var retained=state.Reports.Where(r=>r.OldestSourceAt>=state.RetentionCutoff&&r.CreatedAt>=state.RetentionCutoff)
            .GroupBy(ReportReviewSnapshot.Key).ToDictionary(g=>g.Key,g=>g.First());
        var changed=false;
        foreach(var (key,entry) in ledger.Reports.ToArray())
        {
            if(!retained.TryGetValue(key,out var saved)){removingEvidence?.Invoke();ledger.Reports.Remove(key);changed=true;continue;}
            _=entry.Original(saved);
            if(!entry.Invalidated&&!entry.EvidenceCurrent(state)){ledger.Reports[key]=entry with{Invalidated=true};changed=true;}
        }
        if(changed)document.Save(ledger);
        return ledger;
    }
    public void Prune(InsightState state)=>Safe(()=>{_ = Load(state);return true;});
    // A presence-only, non-sensitive durable barrier prevents stale certainty after a failed
    // invalidation, including a later old-binary A→B→A rewrite. Never automatically unblock.
    public bool Maintain(InsightState state,bool requiredPrune=false)=>Safe(()=>
    {
        try{_ = Load(state,maintenance:true,removingEvidence:()=>requiredPrune=true);return !Blocked;}
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage)
        {
            if(!Blocked)
            {
                if(persistUnavailableMarker is {} writer)
                {
                    writer(blockedPath);
                    if(!Blocked)throw new IOException();
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(blockedPath))!);
                    using var marker=new FileStream(blockedPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    marker.Write("review-archive-unavailable-v1"u8);marker.Flush(true);
                }
            }
            if(requiredPrune)throw;
            return false;
        }
    });
    public void Clear()=>Safe(()=>{document.Save(new());File.Delete(blockedPath);return true;});
    public ReportReviewView Capture(InsightState state,AnalysisReport report)=>Safe(()=>
    {
        var original=ReportRenderer.Markdown(report);
        var saved=state.Reports.LastOrDefault(r=>r.Markdown==original&&r.Task==report.Task&&r.ScopeKey==report.Batch.Scope.Key&&r.IsDemo==report.Batch.IsDemo)
            ??throw new MonitoringFault(MonitorFailure.Storage);
        RequireSaved(state,saved);var ledger=Load(state);var key=ReportReviewSnapshot.Key(saved);
        if(!ledger.Reports.TryGetValue(key,out var snapshot))
        {
            snapshot=ReportReviewSnapshot.Capture(saved,report);ledger.Reports.Add(key,snapshot);document.Save(ledger);
        }
        return snapshot.View(state,saved);
    });
    public ReportReviewView Open(InsightState state,SavedReport saved)=>Safe(()=>
    {
        RequireSaved(state,saved);var ledger=Load(state);
        return ledger.Reports.TryGetValue(ReportReviewSnapshot.Key(saved),out var snapshot)?snapshot.View(state,saved):ReportReviewView.Legacy(saved);
    });
    public ReportReviewView OpenReadable(InsightState state,SavedReport saved)=>Safe(()=>
    {
        RequireSaved(state,saved);
        try{return Open(state,saved);}
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage){return ReportReviewView.Legacy(saved,unavailable:true);}
    });
    public ReportReviewView Decide(InsightState state,ReportReviewView preview,int index,bool resolved,bool evidenceReviewed)
    {
        if(!evidenceReviewed)throw new SafetyException("请先核对原会话证据后明确确认本次核实。");
        return Safe(()=>
        {
            RequireSaved(state,preview.Saved);var ledger=Load(state);
            if(!ledger.Reports.TryGetValue(preview.Key,out var snapshot))throw new MonitoringFault(MonitorFailure.Configuration);
            var current=snapshot.View(state,preview.Saved);
            if(current.Fingerprint!=preview.Fingerprint||current.Report is null||index<0||index>=current.Report.Issues.Count||resolved&&!current.CanReview)
                throw new MonitoringFault(MonitorFailure.Configuration);
            var before=snapshot.Decisions.SingleOrDefault(d=>d.Index==index);
            if(before is not null&&before.Resolved==resolved)return current;
            var decision=new ManualIssueReview(index,ReportReviewSnapshot.IssueKey(current.Report.Issues[index]),resolved,state.RetentionReference);
            var next=snapshot with{Decisions=snapshot.Decisions.Where(d=>d.Index!=index).Append(decision).OrderBy(d=>d.Index).ToArray()};
            var view=next.View(state,preview.Saved);ledger.Reports[preview.Key]=next;
            document.Save(ledger);return view; // Publish the new certainty only after durable encrypted replacement.
        });
    }
}
