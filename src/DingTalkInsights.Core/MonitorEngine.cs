namespace DingTalkInsights.Core;

public sealed record TickResult(int Changed,int Stored,bool Complete,AnalysisReport? Report);

public sealed class MonitorEngine(InsightState state,IStateStore store)
{
    readonly SemaphoreSlim semaphore=new(1,1);
    public InsightState State=>state;
    public static QueryScope NextScope(InsightState state,QueryScope initial,DateTimeOffset now)
    {
        initial.Validate();var cursor=initial.Start;
        if(now<=initial.Start)throw new SafetyException("监控开始时间尚未到达。");
        foreach(var range in state.Coverage.GetValueOrDefault(initial.Key,[]).OrderBy(r=>r.Start))
        {if(range.Start>cursor)break;if(range.End>cursor)cursor=range.End;}
        if(cursor>now)cursor=now;
        var start=cursor==initial.Start?cursor:cursor.AddMinutes(-2);
        if(start<initial.Start)start=initial.Start;
        var end=cursor.AddHours(1)<now?cursor.AddHours(1):now;
        return initial with{Start=start,End=end};
    }
    public async Task<TickResult> TickAsync(QueryScope initial,Func<QueryScope,CancellationToken,Task<MessageBatch>> fetch,
        Func<MessageBatch,string,CancellationToken,Task<ModelResult>>? analyze,ModelSettings model,int retentionDays,DateTimeOffset now,CancellationToken cancellation=default)
    {
        await semaphore.WaitAsync(cancellation);
        try
        {
            state.Trim(now,retentionDays);
            var scope=NextScope(state,initial,now);var batch=await fetch(scope,cancellation);
            if(batch.Scope!=scope)throw new SafetyException("读取结果与授权范围不一致。");
            var changed=state.Ingest(batch);store.Save(state);
            AnalysisReport? report=null;
            if(analyze is not null)
            {
                var pending=state.Unanalyzed(initial with{End=now},40);
                if(pending.Count>0)
                {
                    var selected=batch with{Scope=initial with{End=now},Messages=pending,Complete=false,Warnings=batch.Warnings.Concat(["本次只整理尚未处理的增量候选，最多 40 条；不是全部历史结论。"]).ToArray()};
                    report=(await AnalyzeLocked(selected,"发现可能的沟通难点，核对口径、验收和待确认事项，不进行人员评价。",model,analyze,now,cancellation)).Report;
                }
            }
            return new TickResult(changed,state.Messages.Count,batch.Complete,report);
        }
        finally{semaphore.Release();}
    }
    public async Task<ModelResult> AnalyzeOnceAsync(MessageBatch batch,string task,ModelSettings model,
        Func<MessageBatch,string,CancellationToken,Task<ModelResult>> analyze,DateTimeOffset now,CancellationToken cancellation=default)
    {
        await semaphore.WaitAsync(cancellation);
        try{return await AnalyzeLocked(batch,task,model,analyze,now,cancellation);}
        finally{semaphore.Release();}
    }
    private async Task<ModelResult> AnalyzeLocked(MessageBatch batch,string task,ModelSettings model,
        Func<MessageBatch,string,CancellationToken,Task<ModelResult>> analyze,DateTimeOffset now,CancellationToken cancellation)
    {
        var request=OpenAiClient.BuildRequest(batch,task,model);
        if(!batch.IsDemo)state.Reserve(model,OpenAiClient.Reservation(request,model),now);
        state.MarkAttempted(batch.Scope,batch.Messages);
        // Commit the reservation and attempt BEFORE HTTP: a crash must not release them.
        store.Save(state);
        var result=await analyze(batch,task,cancellation);
        if(result.Report.Batch.Fingerprint!=batch.Fingerprint)throw new SafetyException("整理结果不属于本批消息。");
        state.InputTokens+=result.InputTokens;state.OutputTokens+=result.OutputTokens;
        state.Reports.Add(new SavedReport(now,batch.Scope.Key,task,ReportRenderer.Markdown(result.Report),batch.IsDemo));
        state.Reports=state.Reports.TakeLast(100).ToList();store.Save(state);return result;
    }
}

public static class LocalSearch
{
    public static MessageBatch Find(InsightState state,QueryScope scope,string keywords)
    {
        scope.Validate();Guard.Identifier(keywords,"请输入搜索词。");
        var terms=keywords.Split([',','，',';','；',' ','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(terms.Length is <1 or >8)throw new SafetyException("每次可检索 1–8 个关键词。");
        var rows=state.Messages.Values.Where(v=>v.Profile==scope.Profile&&v.Message.ConversationId==scope.ConversationId&&v.Message.CreatedAt>=scope.Start&&v.Message.CreatedAt<scope.End)
            .Select(v=>v.Message).Where(m=>terms.Any(t=>m.Text.Contains(t,StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m=>m.CreatedAt).Take(40).ToArray();
        return new MessageBatch(scope,rows,false,["仅检索本机已采集且仍在保留期内的消息，按关键词任一匹配，最多 40 条；同义表述和未采集历史可能遗漏。"],new Dictionary<string,System.Text.Json.JsonElement>(),scope.Profile=="demo:local");
    }
}
