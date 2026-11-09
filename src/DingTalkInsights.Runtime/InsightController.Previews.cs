using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed partial class InsightController
{
    readonly Dictionary<Guid,PreviewAuthority> previews=[];
    string AnalysisTask=>string.IsNullOrWhiteSpace(draft)?"发现可能的沟通难点，核对口径、验收和待确认事项，不进行人员评价。":draft;
    PreviewAuthority NewAuthority(CredentialUse use)
    {
        foreach(var expired in previews.Where(p=>p.Value.ExpiresAt<clock.UtcNow).Select(p=>p.Key).ToArray())previews.Remove(expired);
        if(previews.Count>=128)throw new RuntimeFault(RuntimeFailure.Busy);
        var authority=new PreviewAuthority(Guid.NewGuid(),ownerContext,generation,draftRevision,state.RetentionDays,clock.UtcNow,clock.UtcNow.AddMinutes(5),use,use==CredentialUse.None?"":BoundCredentialRevision(use));
        previews.Add(authority.Id,authority);return authority;
    }
    void RequireCurrentAuthority(PreviewAuthority authority,Operation operation)
    {
        EnsureCurrent(operation.Generation,operation.Token);
        if(authority.Owner!=ownerContext||authority.Generation!=generation||authority.DraftRevision!=draftRevision||authority.RetentionDays!=state.RetentionDays||clock.UtcNow<authority.CreatedAt||clock.UtcNow>authority.ExpiresAt||
            authority.Use!=CredentialUse.None&&(selectedCredentials is null||(allowedCredentials&authority.Use)!=authority.Use||BoundCredentialRevision(authority.Use)!=authority.CredentialRevision))
            throw new RuntimeFault(RuntimeFailure.Configuration);
    }
    void Consume(PreviewAuthority authority,Operation operation)
    {
        RequireCurrentAuthority(authority,operation);
        if(!previews.Remove(authority.Id))throw new RuntimeFault(RuntimeFailure.Configuration);
    }
    /// <summary>Local, memory-only scope/retention effects. No source or credential-file I/O.</summary>
    public MonitoringPreview PreviewMonitoring(MonitoringRequest request)
    {
        lock(sync)
        {
            EnsureActive();RequireStore();RequireStopped();if(pending.Count>0||stopCount>0)throw new RuntimeFault(RuntimeFailure.Busy);
            try
            {
                ValidateSourceConnection(request);
                var now=RetentionNow();var initial=new QueryScope(request.Profile,request.ConversationId,request.Start,clock.UtcNow);
                var next=MonitorEngine.NextScope(state,initial,clock.UtcNow);DateTimeOffset? covered=null;CoveredRange? abandoned=null;
                if(state.Monitoring is {} prior&&prior.Scope(clock.UtcNow).Key==initial.Key)
                {
                    covered=MonitorEngine.ResumeCursor(state,prior.Scope(clock.UtcNow),clock.UtcNow);
                    if(request.Start>covered)abandoned=new(covered.Value,request.Start);
                }
                var cutoff=now.AddDays(-request.RetentionDays);
                return new(NewAuthority(request.Source=="dws"&&request.UploadToModel?CredentialUse.Model:CredentialUse.None),request,next,covered,abandoned,
                    state.Messages.Values.Count(m=>m.Message.CreatedAt<cutoff),state.Reports.Count(r=>r.CreatedAt<cutoff||r.OldestSourceAt is null||r.OldestSourceAt<cutoff),next.Start>=cutoff);
            }
            catch(RuntimeFault){throw;}
            catch(Exception){throw new RuntimeFault(RuntimeFailure.Configuration);}
        }
    }
    public async Task<AnalysisPreview?> PreviewAnalysisAsync(ModelSettings model,CancellationToken cancellation=default)
    {
        AnalysisPreview? result=null;
        await Run(async operation=>
        {
            RequireStore();model.Validate();var batch=candidates??throw new RuntimeFault(RuntimeFailure.Configuration);state.EnsureRetained(batch);
            var task=AnalysisTask;var selected=AnalysisAttemptPolicy.Select(state,batch,task);
            if(selected.Batch.Messages.Count==0){Commit(operation,()=>notice="这些来源在同一事项已有模型尝试；请核对历史，不自动重试。");return false;}
            var use=batch.IsDemo?CredentialUse.None:CredentialUse.Model;
            if(!batch.IsDemo){RequireCanonical();_ = await CredentialRevisionAsync(use,operation.Generation,operation.Token);}
            Commit(operation,()=>
            {
                if(candidates?.Fingerprint!=batch.Fingerprint||AnalysisTask!=task)throw new RuntimeFault(RuntimeFailure.Configuration);
                result=new(NewAuthority(use),selected,batch.Fingerprint,task,model);
            });return true;
        },RuntimeFailure.Configuration,cancellation);return result;
    }
    public async Task<SendPreview?> PreviewSendAsync(string destinationName,CancellationToken cancellation=default)
    {
        SendPreview? result=null;
        await Run(async operation=>
        {
            RequireSendableReport();var credential=await PrivateCredentialsAsync(CredentialUse.Webhook,operation.Generation,operation.Token);
            var target=new WebhookTarget(destinationName,credential.WebhookUrl,credential.WebhookSecret);target.Validate();
            var identity=CurrentSendIdentity();var content=markdown;var ledger=store!.SendAttempts.Load();
            var previous=DurableWebhookSender.FindPrevious(ledger,target,content,identity);
            Commit(operation,()=>
            {
                EnforceRetention();RequireSendableReport();if(markdown!=content||!identity.SameAs(CurrentSendIdentity()))throw new RuntimeFault(RuntimeFailure.Configuration);
                result=new(NewAuthority(CredentialUse.Webhook),target.Name,Guard.Hash(target.Url)[..12],content,WebhookClient.Fingerprint(target,content),previous,identity);
            });return true;
        },RuntimeFailure.Storage,cancellation);return result;
    }
    public async Task<ResolutionPreview?> PreviewResolutionAsync(int index,bool resolved,CancellationToken cancellation=default)
    {
        ResolutionPreview? result=null;
        await Run(operation=>
        {
            Commit(operation,()=>
            {
                RequireStore();RefreshReview();
                if(review is null&&report is not null)ApplyReview(store!.Reviews.Capture(state,report));
                var view=review??throw new RuntimeFault(RuntimeFailure.Configuration);
                if(view.Report is null||index<0||index>=view.Report.Issues.Count||resolved&&!view.CanReview)
                    throw new RuntimeFault(RuntimeFailure.Configuration);
                result=new(NewAuthority(CredentialUse.None),view,index,resolved);
            });return Task.FromResult(true);
        },RuntimeFailure.Storage,cancellation);return result;
    }
    void RequireSendableReport()
    {
        RequireCanonical();if(markdown.Length==0||reportOldest is null||reportOldest<state.RetentionCutoff)throw new RuntimeFault(RuntimeFailure.Retention);
        if(review?.Saved.IsDemo==true||report?.Batch.IsDemo==true||report is null&&state.Reports.Any(r=>r.IsDemo&&r.Markdown==markdown))throw new RuntimeFault(RuntimeFailure.Connection);
    }
}
