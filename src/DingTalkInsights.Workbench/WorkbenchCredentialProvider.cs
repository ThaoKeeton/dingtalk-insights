using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using Workbench.Sdk;

namespace DingTalkInsights.Workbench;

/// <summary>Public SDK only. The last-written manifest commits a batch; partial batches fail closed.</summary>
public sealed class WorkbenchCredentialProvider(IPluginSecretStore store):ICredentialProvider
{
    public const string CommitId="credentials.commit";
    const string KeyId="openai.api-key",UrlId="dingtalk.webhook-url",SecretId="dingtalk.webhook-secret";
    readonly SemaphoreSlim gate=new(1,1);
    public string Id=>"workbench-secrets-v1";
    sealed record Manifest(int Version,string Revision,string ModelHash,string UrlHash,string SecretHash);
    public void Register()
    {
        store.Register(new(KeyId,"OpenAI API key","仅经插件单独批准模型用途后读取。"));
        store.Register(new(UrlId,"钉钉 Webhook URL","仅经插件单独批准人工发送用途后读取。"));
        store.Register(new(SecretId,"钉钉 Webhook 签名密钥","与已批准的人工发送目标一起使用。"));
        store.Register(new(CommitId,"钉钉凭据批次版本","由插件完整保存后提交；单独编辑字段会使该批次不可用。"));
    }
    public Task<string> GetRevisionAsync(CredentialUse use,CancellationToken cancellation=default)=>Owned(async()=>
    {
        CredentialValues.ValidateUse(use);var (text,manifest)=await ReadManifest(cancellation);
        if((use&CredentialUse.Model)!=0&&manifest.ModelHash==Guard.Hash("")||(use&CredentialUse.Webhook)!=0&&manifest.UrlHash==Guard.Hash(""))throw new RuntimeFault(RuntimeFailure.Credentials);
        return Guard.Hash(text);
    },cancellation);
    public Task<Credentials> ReadAsync(CredentialUse use,string revision,CancellationToken cancellation=default)=>Owned(async()=>
    {
        CredentialValues.ValidateUse(use);var (text,manifest)=await ReadManifest(cancellation);
        if(Guard.Hash(text)!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
        var key="";var url="";var secret="";
        if((use&CredentialUse.Model)!=0)key=await ReadValue(KeyId,manifest.ModelHash,cancellation);
        if((use&CredentialUse.Webhook)!=0){url=await ReadValue(UrlId,manifest.UrlHash,cancellation);secret=await ReadValue(SecretId,manifest.SecretHash,cancellation);}
        var (after,_)=await ReadManifest(cancellation);
        if(after!=text)throw new RuntimeFault(RuntimeFailure.Credentials);
        return new Credentials(key,url,secret);
    },cancellation);
    public Task SaveAsync(Credentials values,CancellationToken cancellation=default)=>Owned(async()=>
    {
        CredentialValues.Validate(values);
        // Never read/copy previous secrets or try to roll back an uncertain write.
        await store.DeleteAsync(CommitId,cancellation);cancellation.ThrowIfCancellationRequested();
        foreach(var (id,value) in new[]{(KeyId,values.OpenAiKey),(UrlId,values.WebhookUrl),(SecretId,values.WebhookSecret)})
        {
            cancellation.ThrowIfCancellationRequested();
            if(value.Length==0)await store.DeleteAsync(id,cancellation);else await store.WriteAsync(id,value,cancellation);
            cancellation.ThrowIfCancellationRequested();
        }
        var manifest=new Manifest(1,Guid.NewGuid().ToString("N"),Guard.Hash(values.OpenAiKey),Guard.Hash(values.WebhookUrl),Guard.Hash(values.WebhookSecret));
        await store.WriteAsync(CommitId,JsonSerializer.Serialize(manifest),cancellation);cancellation.ThrowIfCancellationRequested();return true;
    },cancellation);
    public Task DeleteAsync(CancellationToken cancellation=default)=>Owned(async()=>
    {
        // Revoke the entire batch before removing any fields. A partial delete cannot be reused.
        foreach(var id in new[]{CommitId,KeyId,UrlId,SecretId}){cancellation.ThrowIfCancellationRequested();await store.DeleteAsync(id,cancellation);cancellation.ThrowIfCancellationRequested();}
        return true;
    },cancellation);
    async Task<(string,Manifest)> ReadManifest(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var text=await store.ReadAsync(CommitId,cancellation);cancellation.ThrowIfCancellationRequested();
        if(text is null||text.Length>2048)throw new RuntimeFault(RuntimeFailure.Credentials);
        var value=JsonSerializer.Deserialize<Manifest>(text);
        if(value is null||value.Version!=1||!Guid.TryParseExact(value.Revision,"N",out _)||!Hash(value.ModelHash)||!Hash(value.UrlHash)||!Hash(value.SecretHash))throw new RuntimeFault(RuntimeFailure.Credentials);
        return(text,value);
    }
    static bool Hash(string? text)=>text is {Length:64}&&text.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F');
    async Task<string> ReadValue(string id,string hash,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var value=await store.ReadAsync(id,cancellation)??"";cancellation.ThrowIfCancellationRequested();
        if(value.Length>8192||Guard.Hash(value)!=hash)throw new RuntimeFault(RuntimeFailure.Credentials);
        return value;
    }
    async Task<T> Owned<T>(Func<Task<T>> action,CancellationToken cancellation)
    {
        if(!store.IsSupported)throw new RuntimeFault(RuntimeFailure.Credentials);
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try{return await action().ConfigureAwait(false);}
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
        catch(Exception){throw new RuntimeFault(RuntimeFailure.Credentials);}
        finally{gate.Release();}
    }
}
