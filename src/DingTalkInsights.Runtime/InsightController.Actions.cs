using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed partial class InsightController
{
    // Memory-only compatibility checks must precede previews and any approved policy/content mutation.
    void ValidateSourceConnection(MonitoringRequest request)
    {
        request.Validate();
        if(request.Source=="demo")
        {
            if(connection!=RuntimeConnection.Demo||request.Profile!=demo.Scope.Profile||request.ConversationId!=demo.Scope.ConversationId)throw new RuntimeFault(RuntimeFailure.Connection);
        }
        else RequireCanonical();
    }
    async Task<MonitoringConfiguration> BuildConfiguration(MonitoringRequest request,long expected,CancellationToken cancellation)
    {
        lock(sync){EnsureCurrent(expected,cancellation);ValidateSourceConnection(request);}
        if(request.Source=="demo")
        {
            return new("demo",request.Profile,request.ConversationId,request.Start,"","",request.UploadToModel,request.Model,request.PollSeconds,request.RetentionDays,"");
        }
        RequireCanonical();
        var credential=request.UploadToModel?await CredentialRevisionAsync(CredentialUse.Model,expected,cancellation):Guard.Hash(ownerContext+"\n"+canonicalRoot+"\nread-only");
        var revision=await Dispatch(expected,cancellation,()=>services.InspectSourceAsync(request.Executable,request.Profile,cancellation));
        lock(sync)EnsureCurrent(expected,cancellation);
        if(request.UploadToModel)
        {
            if(await CredentialRevisionAsync(CredentialUse.Model,expected,cancellation)!=credential)throw new RuntimeFault(RuntimeFailure.Credentials);
            _ = await PrivateCredentialsAsync(CredentialUse.Model,expected,cancellation);
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
            Credentials? preparedModel=null;
            async Task Prepare(CancellationToken token)
            {
                var values=await PrivateCredentialsAsync(CredentialUse.Model,operation.Generation,token);
                if(await CredentialRevisionAsync(CredentialUse.Model,operation.Generation,token)!=config.CredentialRevision)throw new RuntimeFault(RuntimeFailure.Credentials);
                lock(sync){EnsureCurrent(operation.Generation,token);preparedModel=values;}
            }
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
                    config.UploadToModel?(batch,task,token)=>
                    {
                        var values=preparedModel;preparedModel=null;
                        return AnalyzePrepared(batch,task,config.Model,values,config.CredentialRevision,operation.Generation,token);
                    }:null,
                    async token=>await BuildConfiguration(request,operation.Generation,token),clock,operations,
                    prepareModel:config.Source=="dws"&&config.UploadToModel?Prepare:null);
                supervisor=created;created.Changed+=MonitorChanged;
            });
            var started=await created.StartAsync(config,confirmed:true,cancellation:operation.Token);
            Commit(operation,()=>{if(started){monitoringCompletion=created.Completion;configuration=config;notice=config.Source=="demo"?"模拟持续检查已开始，不连接任何服务。":"持续检查已按本次明确范围开始，不自动发送 Webhook。";}});
            return started;
        },RuntimeFailure.Configuration,cancellation);
    }
    Task<MessageBatch> Fetch(MonitoringConfiguration config,QueryScope scope,long expected,CancellationToken cancellation)=>
        Dispatch(expected,cancellation,()=>config.Source=="demo"?Task.FromResult(SliceDemo(scope)):services.ReadAsync(config.Executable,scope,cancellation));
    Task<ModelResult> AnalyzePrepared(MessageBatch batch,string task,ModelSettings model,Credentials? values,string? revision,long expected,CancellationToken cancellation,Action? authorizeDispatch=null)=>
        Dispatch(expected,cancellation,()=>
        {
            authorizeDispatch?.Invoke();
            // No async provider work may occur between the engine's durable UTC reservation and dispatch.
            EnforceRetention();state.EnsureRetained(batch);
            if(batch.IsDemo)return Task.FromResult(new ModelResult(Demo.Analyze(batch,task),0,0));
            if(values is null||BoundCredentialRevision(CredentialUse.Model)!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
            return services.AnalyzeAsync(values.OpenAiKey,batch,task,model,cancellation);
        });
    MessageBatch SliceDemo(QueryScope scope)=>demo with{Scope=scope,Messages=demo.Messages.Where(m=>m.CreatedAt>=scope.Start&&m.CreatedAt<scope.End).ToArray()};
    public Task<bool> ReadOnceAsync(MonitoringRequest request,bool confirmed,CancellationToken cancellation=default)=>ReadOnceCore(request,null,confirmed,cancellation);
    /// <summary>Native dialog path: consume the same immutable scope preview and keep its exact bounded end.</summary>
    public Task<bool> ReadOnceAsync(MonitoringPreview preview,bool confirmed,CancellationToken cancellation=default)=>ReadOnceCore(confirmed?preview.Request:null!,preview,confirmed,cancellation);
    Task<bool> ReadOnceCore(MonitoringRequest request,MonitoringPreview? preview,bool confirmed,CancellationToken cancellation)
    {
        if(!confirmed)return Task.FromResult(false);
        lock(sync){EnsureActive();RequireStore();ValidateSourceConnection(request);}
        return Run(async operation=>
        {
            RequireStore();Commit(operation,()=>ValidateSourceConnection(request));
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
    public Task<bool> AnalyzeCandidatesAsync(AnalysisPreview preview,bool confirmed,CancellationToken cancellation=default,bool reuseConfirmed=false)
    {
        if(!confirmed||preview.PreviouslyAttempted>0&&!reuseConfirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStore();
            Commit(operation,()=>
            {
                Consume(preview.Authority,operation);
                if(candidates?.Fingerprint!=preview.CandidateFingerprint||AnalysisTask!=preview.Task)throw new RuntimeFault(RuntimeFailure.Configuration);
                var selected=AnalysisAttemptPolicy.Select(state,candidates,preview.Task);
                if(selected.Batch.Fingerprint!=preview.Batch.Fingerprint||selected.SkippedSameTask!=preview.SkippedSameTask||selected.PreviouslyAttempted!=preview.PreviouslyAttempted)
                    throw new RuntimeFault(RuntimeFailure.Configuration);
                state.EnsureRetained(preview.Batch);
            });
            var batch=preview.Batch;var model=preview.Model;string? revision=null;Credentials? credential=null;
            if(!batch.IsDemo)
            {
                RequireCanonical();revision=await CredentialRevisionAsync(CredentialUse.Model,operation.Generation,operation.Token);
                credential=await PrivateCredentialsAsync(CredentialUse.Model,operation.Generation,operation.Token);
            }
            Commit(operation,()=>RequireCurrentAuthority(preview.Authority,operation));
            var result=await new MonitorEngine(state,OwnedStore(operation.Generation)).AnalyzeOnceAsync(batch,preview.Task,model,
                (selected,text,token)=>AnalyzePrepared(selected,text,model,credential,revision,operation.Generation,token,()=>
                {try{RequireCurrentAuthority(preview.Authority,operation);}catch(RuntimeFault){throw new MonitoringFault(MonitorFailure.Configuration);}}),clock.UtcNow,operation.Token,()=>clock.UtcNow,reuseConfirmed);
            Commit(operation,()=>{if(SetReport(result.Report))notice="已生成带来源的模型整理结果；人工核实可选。";});return true;
        },RuntimeFailure.Model,cancellation);
    }
    public Task<bool> SelectHistoryAsync(SavedReport selected,CancellationToken cancellation=default)=>Run(operation=>
    {
        RequireStore();
        Commit(operation,()=>
        {
            if(!state.Reports.Contains(selected))throw new RuntimeFault(RuntimeFailure.Retention);
            previews.Clear();review=null;report=null;markdown="";reportOldest=null;
            ApplyReview(store!.ReviewsAvailable?store.Reviews.OpenReadable(state,selected):ReportReviewView.Legacy(selected,unavailable:true));
            notice=review!.AmbiguousIdentity?"报告身份或旧版核实状态无法安全区分，已载入原始文本。":review.Unavailable?"核实文件暂不可用，已保留原文件并仅载入原始报告文本；可检查存储或明确清理索引。":review.Report is null?"旧历史仅保留原 Markdown，可查看和导出；没有可持久核实的证据快照。":review.CanReview?"已载入历史及本机人工核实记录。":"证据已修订或缺失，原人工核实记录已失效。";
        });return Task.FromResult(true);
    },RuntimeFailure.Storage,cancellation,readOnly:true);
    public async Task<bool> ConfirmResolvedAsync(int index,bool evidenceReviewed,CancellationToken cancellation=default)
    {
        if(!evidenceReviewed)return false;
        var preview=await PreviewResolutionAsync(index,true,cancellation);
        return preview is not null&&await ConfirmResolvedAsync(preview,true,cancellation);
    }
    public Task<bool> ConfirmResolvedAsync(ResolutionPreview preview,bool evidenceReviewed,CancellationToken cancellation=default)
    {
        if(!evidenceReviewed)return Task.FromResult(false);
        return Run(operation=>
        {
            Commit(operation,()=>
            {
                RequireStore();Consume(preview.Authority,operation);RefreshReview();
                if(review?.Fingerprint!=preview.View.Fingerprint)throw new RuntimeFault(RuntimeFailure.Configuration);
                var saved=store!.Reviews.Decide(state,preview.View,preview.Index,preview.Resolved,true);
                ApplyReview(saved);previews.Clear();notice=preview.Resolved?"人工核实已加密保存，重开此报告可恢复；仅适用于这些原文修订。":"人工确认已撤销并保存，恢复原报告的模型状态。";
            });return Task.FromResult(true);
        },RuntimeFailure.Storage,cancellation);
    }
    public Task<bool> SendReportAsync(SendPreview preview,bool confirmed,bool humanConfirmedRetry=false,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            Commit(operation,()=>
            {
                Consume(preview.Authority,operation);
                if(markdown!=preview.Markdown||!preview.Identity.SameAs(CurrentSendIdentity()))throw new RuntimeFault(RuntimeFailure.Configuration);
                RequireSendableReport();
            });
            var content=preview.Markdown;var credential=await PrivateCredentialsAsync(CredentialUse.Webhook,operation.Generation,operation.Token);
            var revision=await CredentialRevisionAsync(CredentialUse.Webhook,operation.Generation,operation.Token);
            Commit(operation,()=>{RequireCurrentAuthority(preview.Authority,operation);EnforceRetention();RequireSendableReport();if(markdown!=content||!preview.Identity.SameAs(CurrentSendIdentity()))throw new RuntimeFault(RuntimeFailure.Retention);});
            var target=new WebhookTarget(preview.DestinationName,credential.WebhookUrl,credential.WebhookSecret);
            if(WebhookClient.Fingerprint(target,content)!=preview.Fingerprint)throw new RuntimeFault(RuntimeFailure.Configuration);
            var sender=new DurableWebhookSender(store!.SendAttempts,(approved,text,token)=>Dispatch(operation.Generation,token,()=>
            {
                RequireCurrentAuthority(preview.Authority,operation);EnforceRetention();RequireSendableReport();
                if(markdown!=content||!preview.Identity.SameAs(CurrentSendIdentity())||BoundCredentialRevision(CredentialUse.Webhook)!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
                return services.SendAsync(approved,text,token);
            }),time);
            var result=await sender.SendAsync(target,content,true,humanConfirmedRetry,operation.Token,preview.Identity);
            Commit(operation,()=>notice=result.Suppressed
                ?(preview.LegacyFormatRisk&&preview.PreviousStatus is null?"旧格式或人工状态的发送记录无法完整核对，本次未发送；请先确认重复送达风险。":"同一目标与报告或正文已有发送尝试，未重发；请先核对接收群。")
                :result.Status switch
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
            RequireStore();RequireStopped();Commit(operation,()=>{ClearVisible();store!.ClearIndex(state);});return Task.FromResult(true);
        },RuntimeFailure.Storage,cancellation,readOnly:true);
    }
    static MessageBatch Limit(MessageBatch batch)=>batch.Messages.Count<=40?batch:batch with{Messages=batch.Messages.Take(40).ToArray(),Complete=false,
        Warnings=batch.Warnings.Concat(["当前可整理预览最多 40 条；未显示来源不会上传。"]).ToArray()};
}
