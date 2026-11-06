using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed partial class InsightController
{
    async Task<MonitoringConfiguration> BuildConfiguration(MonitoringRequest request,long expected,CancellationToken cancellation)
    {
        request.Validate();lock(sync)EnsureCurrent(expected,cancellation);
        if(request.Source=="demo")
        {
            if(connection!=RuntimeConnection.Demo||request.Profile!=demo.Scope.Profile||request.ConversationId!=demo.Scope.ConversationId)throw new RuntimeFault(RuntimeFailure.Connection);
            return new("demo",request.Profile,request.ConversationId,request.Start,"","",request.UploadToModel,request.Model,request.PollSeconds,request.RetentionDays,"");
        }
        RequireCanonical();
        var credential=request.UploadToModel?CredentialRevision(CredentialUse.Model):Guard.Hash(ownerContext+"\n"+canonicalRoot+"\nread-only");
        var revision=await Dispatch(expected,cancellation,()=>services.InspectSourceAsync(request.Executable,request.Profile,cancellation));
        lock(sync)
        {
            EnsureCurrent(expected,cancellation);
            if(request.UploadToModel){if(CredentialRevision(CredentialUse.Model)!=credential)throw new RuntimeFault(RuntimeFailure.Credentials);_ = PrivateCredentials(CredentialUse.Model);}
        }
        var config=new MonitoringConfiguration("dws",request.Profile,request.ConversationId,request.Start,request.Executable,revision,request.UploadToModel,request.Model,request.PollSeconds,request.RetentionDays,credential);
        config.Validate();return config;
    }
    public Task<bool> StartMonitoringAsync(MonitoringPreview preview,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStore();RequireStopped();Commit(operation,()=>Consume(preview.Authority,operation));
            var request=preview.Request;var config=await BuildConfiguration(request,operation.Generation,operation.Token);
            MonitorSupervisor created=null!;
            Commit(operation,()=>
            {
                var next=MonitorEngine.NextScope(state,config.Scope(clock.UtcNow),clock.UtcNow);
                if(next.Start<clock.UtcNow.AddDays(-config.RetentionDays))throw new RuntimeFault(RuntimeFailure.Retention);
                state.Trim(clock.UtcNow,config.RetentionDays);state.EnsureScopeRetained(next);
                BackgroundPolicy.Revoke(state);state.Monitoring=new(config.Profile,config.ConversationId,config.Start);store!.Save(state);
                var owned=OwnedStore(operation.Generation);var engine=new MonitorEngine(state,owned);
                created=new(engine,owned,
                    (scope,token)=>Fetch(config,scope,operation.Generation,token),
                    config.UploadToModel?(batch,task,token)=>Analyze(config,batch,task,operation.Generation,token):null,
                    async token=>await BuildConfiguration(request,operation.Generation,token),clock,operations);
                supervisor=created;created.Changed+=MonitorChanged;
            });
            var started=await created.StartAsync(config,confirmed:true,cancellation:operation.Token);
            Commit(operation,()=>{if(started){configuration=config;notice=config.Source=="demo"?"模拟持续检查已开始，不连接任何服务。":"持续检查已按本次明确范围开始，不自动发送 Webhook。";}});
            return started;
        },RuntimeFailure.Configuration,cancellation);
    }
    Task<MessageBatch> Fetch(MonitoringConfiguration config,QueryScope scope,long expected,CancellationToken cancellation)=>
        Dispatch(expected,cancellation,()=>config.Source=="demo"?Task.FromResult(SliceDemo(scope)):services.ReadAsync(config.Executable,scope,cancellation));
    Task<ModelResult> Analyze(MonitoringConfiguration config,MessageBatch batch,string task,long expected,CancellationToken cancellation)=>
        Dispatch(expected,cancellation,()=>config.Source=="demo"?Task.FromResult(new ModelResult(Demo.Analyze(batch,task),0,0)):
            services.AnalyzeAsync(PrivateCredentials(CredentialUse.Model).OpenAiKey,batch,task,config.Model,cancellation));
    MessageBatch SliceDemo(QueryScope scope)=>demo with{Scope=scope,Messages=demo.Messages.Where(m=>m.CreatedAt>=scope.Start&&m.CreatedAt<scope.End).ToArray()};
    public Task<bool> ReadOnceAsync(MonitoringRequest request,bool confirmed,CancellationToken cancellation=default)=>ReadOnceCore(request,null,confirmed,cancellation);
    /// <summary>Native dialog path: consume the same immutable scope preview and keep its exact bounded end.</summary>
    public Task<bool> ReadOnceAsync(MonitoringPreview preview,bool confirmed,CancellationToken cancellation=default)=>ReadOnceCore(confirmed?preview.Request:null!,preview,confirmed,cancellation);
    Task<bool> ReadOnceCore(MonitoringRequest request,MonitoringPreview? preview,bool confirmed,CancellationToken cancellation)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStore();
            if(preview is not null)Commit(operation,()=>
            {
                RequireStopped();Consume(preview.Authority,operation);
                var now=RetentionNow();var cutoff=now.AddDays(-request.RetentionDays);
                if(!preview.WithinRetention||preview.NextRead.Start<cutoff)throw new RuntimeFault(RuntimeFailure.Retention);
                var expiringMessages=state.Messages.Values.Count(m=>m.Message.CreatedAt<cutoff);
                var expiringReports=state.Reports.Count(r=>r.CreatedAt<cutoff||r.OldestSourceAt is null||r.OldestSourceAt<cutoff);
                if(expiringMessages!=preview.ExpiringMessages||expiringReports!=preview.ExpiringReports)throw new RuntimeFault(RuntimeFailure.Configuration);
                // The confirmation covers these exact trim effects and policy. Commit before inspecting/reading a source.
                state.Trim(now,request.RetentionDays);EnforceRetention();store!.Save(state);
                var initial=new QueryScope(request.Profile,request.ConversationId,request.Start,preview.NextRead.End);
                if(MonitorEngine.NextScope(state,initial,preview.NextRead.End)!=preview.NextRead)throw new RuntimeFault(RuntimeFailure.Configuration);
            });
            var config=await BuildConfiguration(request with{UploadToModel=false},operation.Generation,operation.Token);
            if(preview is not null)Commit(operation,()=>
            {
                if(preview.NextRead.Start<RetentionNow().AddDays(-request.RetentionDays))throw new RuntimeFault(RuntimeFailure.Retention);
            });
            var through=preview?.NextRead.End??clock.UtcNow;
            MessageBatch? fetched=null;var engine=new MonitorEngine(state,OwnedStore(operation.Generation));
            var result=await engine.TickAsync(config.Scope(through),async(scope,token)=>fetched=await Fetch(config,scope,operation.Generation,token),null,config.Model,state.RetentionDays,through,operation.Token);
            Commit(operation,()=>{previews.Clear();candidates=Limit(fetched!);notice=result.Complete?"本次原文已采集到本机；完整性和来源仍需核对。":"本次采集不完整，断点未跨过未知范围。";});
            return true;
        },RuntimeFailure.Source,cancellation);
    }
    public Task<bool> SearchAsync(string executable,QueryScope scope,string keywords,bool useDws,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStore();scope.Validate();Guard.Identifier(keywords,"请输入搜索词。");MessageBatch batch;
            if(useDws)
            {
                RequireCanonical();state.EnsureScopeRetained(scope);
                await Dispatch(operation.Generation,operation.Token,()=>services.InspectSourceAsync(executable,scope.Profile,operation.Token));
                batch=await Dispatch(operation.Generation,operation.Token,()=>services.SearchAsync(executable,scope,keywords,operation.Token));
                Commit(operation,()=>
                {
                    if(batch.Scope!=scope||batch.IsDemo)throw new RuntimeFault(RuntimeFailure.Configuration);
                    state.Ingest(batch with{Complete=false});OwnedStore(operation.Generation).Save(state);
                });
            }
            else batch=LocalSearch.Find(state,scope,keywords,clock.UtcNow);
            Commit(operation,()=>{previews.Clear();candidates=Limit(batch);});return true;
        },RuntimeFailure.Source,cancellation);
    }
    public Task<bool> DiscoverAsync(string executable,string profile,DateTimeOffset start,DateTimeOffset end,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireCanonical();var scope=new QueryScope(profile,"discovery",start,end);scope.Validate();state.EnsureScopeRetained(scope);
            await Dispatch(operation.Generation,operation.Token,()=>services.InspectSourceAsync(executable,profile,operation.Token));
            var rows=await Dispatch(operation.Generation,operation.Token,()=>services.DiscoverAsync(executable,profile,start,end,operation.Token));
            Commit(operation,()=>conversations=rows.ToArray());return true;
        },RuntimeFailure.Source,cancellation);
    }
    public Task<bool> AnalyzeCandidatesAsync(AnalysisPreview preview,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStore();
            Commit(operation,()=>
            {
                Consume(preview.Authority,operation);
                if(candidates?.Fingerprint!=preview.CandidateFingerprint||AnalysisTask!=preview.Task||preview.Batch.Messages.Any(m=>state.Attempted.Contains(InsightState.AttemptKey(preview.Batch.Scope,m))))
                    throw new RuntimeFault(RuntimeFailure.Configuration);
                state.EnsureRetained(preview.Batch);
            });
            var batch=preview.Batch;var model=preview.Model;string? credential=null;string? revision=null;
            if(!batch.IsDemo){RequireCanonical();revision=CredentialRevision(CredentialUse.Model);credential=PrivateCredentials(CredentialUse.Model).OpenAiKey;}
            var result=await new MonitorEngine(state,OwnedStore(operation.Generation)).AnalyzeOnceAsync(batch,preview.Task,model,
                (selected,text,token)=>Dispatch(operation.Generation,token,()=>
                {
                    if(selected.IsDemo)return Task.FromResult(new ModelResult(Demo.Analyze(selected,text),0,0));
                    if(CredentialRevision(CredentialUse.Model)!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
                    return services.AnalyzeAsync(credential!,selected,text,model,token);
                }),clock.UtcNow,operation.Token,()=>clock.UtcNow);
            Commit(operation,()=>{SetReport(result.Report);notice="已生成带来源的待核实结果；请回原会话核对。";});return true;
        },RuntimeFailure.Model,cancellation);
    }
    public Task<bool> SelectHistoryAsync(SavedReport selected,CancellationToken cancellation=default)=>Run(operation=>
    {
        RequireStore();
        Commit(operation,()=>
        {
            if(!state.Reports.Contains(selected))throw new RuntimeFault(RuntimeFailure.Retention);
            previews.Clear();report=null;markdown=selected.Markdown;reportOldest=selected.OldestSourceAt;
        });return Task.FromResult(true);
    },RuntimeFailure.Storage,cancellation);
    public Task<bool> ConfirmResolvedAsync(int index,bool evidenceReviewed,CancellationToken cancellation=default)
    {
        if(!evidenceReviewed)return Task.FromResult(false);
        return Run(operation=>
        {
            Commit(operation,()=>{if(report is null)throw new RuntimeFault(RuntimeFailure.Configuration);report.ConfirmResolved(index,true);SetReport(report);});return Task.FromResult(true);
        },RuntimeFailure.Configuration,cancellation);
    }
    public Task<bool> SendReportAsync(SendPreview preview,bool confirmed,bool humanConfirmedRetry=false,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            Commit(operation,()=>
            {
                Consume(preview.Authority,operation);
                if(markdown!=preview.Markdown)throw new RuntimeFault(RuntimeFailure.Configuration);
                RequireSendableReport();
            });
            var content=preview.Markdown;var credential=PrivateCredentials(CredentialUse.Webhook);var revision=CredentialRevision(CredentialUse.Webhook);
            var target=new WebhookTarget(preview.DestinationName,credential.WebhookUrl,credential.WebhookSecret);
            if(WebhookClient.Fingerprint(target,content)!=preview.Fingerprint)throw new RuntimeFault(RuntimeFailure.Configuration);
            var sender=new DurableWebhookSender(store!.SendAttempts,(approved,text,token)=>Dispatch(operation.Generation,token,()=>
            {
                if(CredentialRevision(CredentialUse.Webhook)!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
                return services.SendAsync(approved,text,token);
            }),time);
            var result=await sender.SendAsync(target,content,true,humanConfirmedRetry,operation.Token);
            Commit(operation,()=>notice=result.Suppressed?"同一目标与正文已有发送尝试，未重发；请先核对接收群，人工重发可能重复送达。":result.Status switch
            {
                DeliveryStatus.Sent=>"钉钉已确认接收。再次人工重发可能重复送达。",
                DeliveryStatus.Failed=>"钉钉未确认接收；尝试已保存，不自动重试。",
                _=>"发送结果不确定，尝试已保存；请先到钉钉检查，不自动重试。"
            });return result.Dispatched;
        },RuntimeFailure.Source,cancellation);
    }
    public Task<bool> ClearIndexAsync(bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(operation=>
        {
            RequireStore();RequireStopped();Commit(operation,()=>{state.ClearIndex();store!.Save(state);ClearVisible();});return Task.FromResult(true);
        },RuntimeFailure.Storage,cancellation);
    }
    static MessageBatch Limit(MessageBatch batch)=>batch.Messages.Count<=40?batch:batch with{Messages=batch.Messages.Take(40).ToArray(),Complete=false,
        Warnings=batch.Warnings.Concat(["当前可整理预览最多 40 条；未显示来源不会上传。"]).ToArray()};
}
