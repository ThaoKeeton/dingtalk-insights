namespace DingTalkInsights.Core;

public sealed record TickResult(int Changed,int Stored,bool Complete,AnalysisReport? Report,MonitorFailure ModelFailure=MonitorFailure.None,DateTimeOffset? ModelDeferredUntil=null);

public sealed class MonitorEngine(InsightState state,IStateStore store)
{
    readonly SemaphoreSlim semaphore=new(1,1);
    public InsightState State=>state;
    public static DateTimeOffset ResumeCursor(InsightState state,QueryScope initial,DateTimeOffset now)
    {
        initial.ValidateOrigin();var cursor=initial.Start;
        if(now<=initial.Start)throw new SafetyException("监控开始时间尚未到达。");
        foreach(var range in state.Coverage.GetValueOrDefault(initial.Key,[]).OrderBy(r=>r.Start))
        {if(range.Start>cursor)break;if(range.End>cursor)cursor=range.End;}
        return cursor>now?now:cursor;
    }
    public static QueryScope NextScope(InsightState state,QueryScope initial,DateTimeOffset now)
    {
        var cursor=ResumeCursor(state,initial,now);
        var start=cursor==initial.Start?cursor:cursor.AddMinutes(-2);
        if(start<initial.Start)start=initial.Start;
        var end=cursor.AddHours(1)<now?cursor.AddHours(1):now;
        return initial with{Start=start,End=end};
    }
    public async Task<TickResult> TickAsync(QueryScope initial,Func<QueryScope,CancellationToken,Task<MessageBatch>> fetch,
        Func<MessageBatch,string,CancellationToken,Task<ModelResult>>? analyze,ModelSettings model,int retentionDays,DateTimeOffset now,CancellationToken cancellation=default,bool tolerateModelFailure=false,Func<CancellationToken,Task>? beforeModel=null,Func<DateTimeOffset>? utcNow=null)
    {
        await semaphore.WaitAsync(cancellation);
        try
        {
            state.Trim(now,retentionDays);
            var scope=NextScope(state,initial,now);state.EnsureScopeRetained(scope);var batch=await fetch(scope,cancellation);
            cancellation.ThrowIfCancellationRequested();
            if(batch.Scope!=scope)throw new SafetyException("读取结果与授权范围不一致。");
            var changed=state.Ingest(batch);Save();
            AnalysisReport? report=null;
            var modelFailure=MonitorFailure.None;DateTimeOffset? deferred=null;
            if(analyze is not null&&batch.Complete)
            {
                var pending=state.Unanalyzed(initial with{End=now},40);
                if(pending.Count>0)
                {
                    var selected=batch with{Scope=initial with{End=now},Messages=pending,Complete=false,Warnings=batch.Warnings.Concat(["本次只整理尚未处理的增量候选，最多 40 条；不是全部历史结论。"]).ToArray()};
                    if(beforeModel is not null)await beforeModel(cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    try{report=(await AnalyzeLocked(selected,"发现可能的沟通难点，核对口径、验收和待确认事项，不进行人员评价。",model,analyze,now,cancellation,utcNow)).Report;}
                    catch(MonitoringFault e)when(tolerateModelFailure&&e.Category==MonitorFailure.Budget){modelFailure=e.Category;deferred=e.DeferredUntil;}
                    catch(ModelAttemptFailure e)when(tolerateModelFailure){modelFailure=e.Category;}
                }
            }
            return new TickResult(changed,state.Messages.Count,batch.Complete,report,modelFailure,deferred);
        }
        finally{semaphore.Release();}
    }
    public async Task<ModelResult> AnalyzeOnceAsync(MessageBatch batch,string task,ModelSettings model,
        Func<MessageBatch,string,CancellationToken,Task<ModelResult>> analyze,DateTimeOffset now,CancellationToken cancellation=default,Func<DateTimeOffset>? utcNow=null)
    {
        await semaphore.WaitAsync(cancellation);
        try{state.Trim(now,state.RetentionDays);state.EnsureRetained(batch);return await AnalyzeLocked(batch,task,model,analyze,now,cancellation,utcNow);}
        finally{semaphore.Release();}
    }
    private async Task<ModelResult> AnalyzeLocked(MessageBatch batch,string task,ModelSettings model,
        Func<MessageBatch,string,CancellationToken,Task<ModelResult>> analyze,DateTimeOffset now,CancellationToken cancellation,Func<DateTimeOffset>? utcNow)
    {
        cancellation.ThrowIfCancellationRequested();
        var request=OpenAiClient.BuildRequest(batch,task,model);
        // Fetch, readiness and semaphore waits may cross UTC midnight. Reserve on the dispatch boundary.
        var reservation=OpenAiClient.Reservation(request,model);
        if(!batch.IsDemo)state.Reserve(model,reservation,utcNow?.Invoke()??DateTimeOffset.UtcNow);
        state.MarkAttempted(batch.Scope,batch.Messages);
        // Commit the reservation and attempt BEFORE HTTP: a crash must not release them.
        Save();
        state.EnsureRetained(batch);
        cancellation.ThrowIfCancellationRequested();
        ModelResult result;
        try{result=await analyze(batch,task,cancellation);cancellation.ThrowIfCancellationRequested();
            if(result.Report.Batch.Fingerprint!=batch.Fingerprint)throw new ModelAttemptFailure(MonitorFailure.ModelUnknown);}
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
        catch(MonitoringFault e)when(e.Category is MonitorFailure.Configuration or MonitorFailure.Storage or MonitorFailure.Retention){throw;}
        catch(Exception e){throw new ModelAttemptFailure(e is MonitoringFault{Category:MonitorFailure.ModelAuthorization}?MonitorFailure.ModelAuthorization:MonitorFailure.ModelUnknown);}
        var previousReports=state.Reports;var previousInput=state.InputTokens;var previousOutput=state.OutputTokens;
        state.InputTokens+=result.InputTokens;state.OutputTokens+=result.OutputTokens;
        state.Reports=previousReports.Append(new SavedReport(now,batch.Scope.Key,task,ReportRenderer.Markdown(result.Report),batch.IsDemo,batch.Messages.Min(m=>m.CreatedAt))).TakeLast(100).ToList();
        try{Save();}
        catch
        {
            // A failed final commit must not surface an undurable history entry or resurrect it on Stop.
            // The pre-HTTP attempt and budget reservation deliberately remain committed.
            state.Reports=previousReports;state.InputTokens=previousInput;state.OutputTokens=previousOutput;throw;
        }
        return result;
    }
    void Save(){try{store.Save(state);}catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}}
    sealed class ModelAttemptFailure(MonitorFailure category):SafetyException(MonitorNotices.For(category)){public MonitorFailure Category{get;}=category;}

}

public static class LocalSearch
{
    public static MessageBatch Find(InsightState state,QueryScope scope,string keywords,DateTimeOffset? now=null)
    {
        state.Trim(now??DateTimeOffset.UtcNow,state.RetentionDays);
        scope.Validate();Guard.Identifier(keywords,"请输入搜索词。");
        var terms=keywords.Split([',','，',';','；',' ','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(terms.Length is <1 or >8)throw new SafetyException("每次可检索 1–8 个关键词。");
        var rows=state.Messages.Values.Where(v=>v.Profile==scope.Profile&&v.Message.ConversationId==scope.ConversationId&&v.Message.CreatedAt>=scope.Start&&v.Message.CreatedAt<scope.End)
            .Select(v=>v.Message).Where(m=>terms.Any(t=>m.Text.Contains(t,StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m=>m.CreatedAt).Take(40).ToArray();
        return new MessageBatch(scope,rows,false,["仅检索本机已采集且仍在保留期内的消息，按关键词任一匹配，最多 40 条；同义表述和未采集历史可能遗漏。"],new Dictionary<string,System.Text.Json.JsonElement>(),scope.Profile=="demo:local");
    }
}
