using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

/// <summary>Private credential I/O. Views receive neither this provider nor its returned values.</summary>
public interface ICredentialProvider
{
    string Id{get;}
    Task<string> GetRevisionAsync(CredentialUse use,CancellationToken cancellation=default);
    Task<Credentials> ReadAsync(CredentialUse use,string revision,CancellationToken cancellation=default);
    Task SaveAsync(Credentials values,CancellationToken cancellation=default);
    Task DeleteAsync(CancellationToken cancellation=default);
}

public static class CredentialValues
{
    public static void Validate(Credentials values)
    {
        if(values is null||values.OpenAiKey is null||values.WebhookUrl is null||values.WebhookSecret is null||
            values.OpenAiKey.Length>8192||values.WebhookUrl.Length>8192||values.WebhookSecret.Length>8192)throw new RuntimeFault(RuntimeFailure.Credentials);
    }
    public static void ValidateUse(CredentialUse use)
    {if(use==CredentialUse.None||(use&~(CredentialUse.Model|CredentialUse.Webhook))!=0)throw new RuntimeFault(RuntimeFailure.Credentials);}
    public static Credentials Select(Credentials values,CredentialUse use)=>new(
        (use&CredentialUse.Model)!=0?values.OpenAiKey:"",(use&CredentialUse.Webhook)!=0?values.WebhookUrl:"",(use&CredentialUse.Webhook)!=0?values.WebhookSecret:"");
}

internal sealed class FileCredentialProvider(string path,IDataCipher cipher):ICredentialProvider
{
    readonly CredentialVault vault=new(path,cipher);
    public string Id=>"dpapi:"+path;
    public Task<string> GetRevisionAsync(CredentialUse use,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();CredentialValues.ValidateUse(use);
        if(!vault.Exists)throw new RuntimeFault(RuntimeFailure.Credentials);
        return Task.FromResult(vault.Revision);
    }
    public async Task<Credentials> ReadAsync(CredentialUse use,string revision,CancellationToken cancellation=default)
    {
        if(await GetRevisionAsync(use,cancellation)!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
        var values=vault.Read();
        if(await GetRevisionAsync(use,cancellation)!=revision)throw new RuntimeFault(RuntimeFailure.Credentials);
        return CredentialValues.Select(values,use);
    }
    public Task SaveAsync(Credentials values,CancellationToken cancellation=default)
    {cancellation.ThrowIfCancellationRequested();CredentialValues.Validate(values);vault.Save(values);return Task.CompletedTask;}
    public Task DeleteAsync(CancellationToken cancellation=default)
    {cancellation.ThrowIfCancellationRequested();vault.Clear();return Task.CompletedTask;}
}
