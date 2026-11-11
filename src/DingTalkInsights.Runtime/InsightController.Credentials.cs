using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed partial class InsightController
{
    ICredentialProvider Provider(CredentialLocation location,bool writing=false)
    {
        if(!Enum.IsDefined(location)||writing&&location==CredentialLocation.ConnectedStore)throw new RuntimeFault(RuntimeFailure.Credentials);
        if(location==CredentialLocation.Host)return hostCredentials??throw new RuntimeFault(RuntimeFailure.Credentials);
        if(location==CredentialLocation.ConnectedStore)RequireCanonical();
        return new FileCredentialProvider(Path.Combine(location==CredentialLocation.Plugin?dataDirectory:canonicalRoot!,"credentials.dpapi"),cipher);
    }
    public Task<bool> UseCredentialsAsync(CredentialLocation location,CredentialUse use,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStopped();CredentialValues.ValidateUse(use);var provider=Provider(location);
            // A failed/revoked new selection cannot leave the previous provider implicitly authorized.
            Commit(operation,ClearCredentialUse);
            var revision=await provider.GetRevisionAsync(use,operation.Token);
            Commit(operation,()=>{selectedCredentials=provider;selectedRevision=revision;allowedCredentials=use;notice="已批准所选来源的本次用途；现有值不回显，开始和发送仍须单独确认。";});return true;
        },RuntimeFailure.Credentials,cancellation);
    }
    /// <summary>Compatibility: replace plugin-local values only; never write the connected standalone vault.</summary>
    public Task<bool> SaveCredentialsAsync(Credentials values,bool confirmed,CancellationToken cancellation=default)=>SaveCredentialsAsync(CredentialLocation.Plugin,values,confirmed,cancellation);
    public Task<bool> SaveCredentialsAsync(CredentialLocation location,Credentials values,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStopped();CredentialValues.Validate(values);var provider=Provider(location,writing:true);
            Commit(operation,ClearCredentialUse);await provider.SaveAsync(values,operation.Token);
            Commit(operation,()=>notice="凭据已加密保存；旧用途批准已撤销，需单独批准后才能使用。");return true;
        },RuntimeFailure.Credentials,cancellation);
    }
    public Task<bool> DeleteCredentialsAsync(CredentialLocation location,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(async operation=>
        {
            RequireStopped();var provider=Provider(location,writing:true);Commit(operation,ClearCredentialUse);
            await provider.DeleteAsync(operation.Token);Commit(operation,()=>notice="所选来源的凭据已删除，用途批准已撤销；业务数据与发送尝试保留。");return true;
        },RuntimeFailure.Credentials,cancellation);
    }
    string BoundCredentialRevision(CredentialUse use)
    {
        if(selectedCredentials is null||(allowedCredentials&use)!=use)throw new RuntimeFault(RuntimeFailure.Credentials);
        return Guard.Hash(ownerContext+"\n"+canonicalRoot+"\n"+selectedCredentials.Id+"\n"+selectedRevision+"\n"+use);
    }
    async Task<string> CredentialRevisionAsync(CredentialUse use,long expected,CancellationToken cancellation)
    {
        ICredentialProvider provider;string revision;
        lock(sync){EnsureCurrent(expected,cancellation);_ = BoundCredentialRevision(use);provider=selectedCredentials!;revision=selectedRevision;}
        var actual=await provider.GetRevisionAsync(use,cancellation);
        lock(sync)
        {
            EnsureCurrent(expected,cancellation);
            if(!ReferenceEquals(selectedCredentials,provider)||selectedRevision!=revision||actual!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
            return BoundCredentialRevision(use);
        }
    }
    async Task<Credentials> PrivateCredentialsAsync(CredentialUse use,long expected,CancellationToken cancellation)
    {
        var bound=await CredentialRevisionAsync(use,expected,cancellation);ICredentialProvider provider;string revision;
        lock(sync){EnsureCurrent(expected,cancellation);provider=selectedCredentials!;revision=selectedRevision;}
        var values=await provider.ReadAsync(use,revision,cancellation);CredentialValues.Validate(values);
        if(await CredentialRevisionAsync(use,expected,cancellation)!=bound||(use&CredentialUse.Model)!=0&&string.IsNullOrWhiteSpace(values.OpenAiKey)||
            (use&CredentialUse.Webhook)!=0&&string.IsNullOrWhiteSpace(values.WebhookUrl))throw new RuntimeFault(RuntimeFailure.Credentials);
        return values;
    }
    void ClearCredentialUse(){previews.Clear();selectedCredentials=null;selectedRevision="";allowedCredentials=CredentialUse.None;}
}
