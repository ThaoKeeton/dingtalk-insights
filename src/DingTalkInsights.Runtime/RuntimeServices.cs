using System.Security.Cryptography;
using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

/// <summary>No I/O in construction. Only explicit controller actions call this boundary.</summary>
public sealed class RuntimeServices(HttpClient? modelHttp=null,HttpClient? webhookHttp=null):IRuntimeServices
{
    readonly ConsentGate consent=new();
    public async Task<string> InspectSourceAsync(string executable,string profile,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!Path.IsPathFullyQualified(executable))throw new RuntimeFault(RuntimeFailure.Configuration);
        var digest=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable,cancellation)));
        cancellation.ThrowIfCancellationRequested();
        var readiness=await new DwsClient(executable,consent).ReadinessAsync(profile,cancellation);
        cancellation.ThrowIfCancellationRequested();
        if(!readiness.Ready)throw new RuntimeFault(RuntimeFailure.Configuration);
        return digest;
    }
    public Task<MessageBatch> ReadAsync(string executable,QueryScope scope,CancellationToken cancellation)=>
        new DwsClient(executable,consent).ReadAsync(scope,consent.Issue(ExternalAction.Read,scope.Fingerprint),cancellation);
    public Task<MessageBatch> SearchAsync(string executable,QueryScope scope,string keywords,CancellationToken cancellation)=>
        new DwsClient(executable,consent).SearchAsync(scope,keywords,consent.Issue(ExternalAction.Search,DwsClient.SearchFingerprint(scope,keywords)),cancellation);
    public Task<IReadOnlyList<Conversation>> DiscoverAsync(string executable,string profile,DateTimeOffset start,DateTimeOffset end,CancellationToken cancellation)=>
        new DwsClient(executable,consent).DiscoverAsync(profile,start,end,consent.Issue(ExternalAction.Discover,DwsClient.DiscoveryFingerprint(profile,start,end)),cancellation);
    public Task<ModelResult> AnalyzeAsync(string key,MessageBatch batch,string task,ModelSettings model,CancellationToken cancellation)=>
        new OpenAiClient(consent,modelHttp).AnalyzeAsync(key,batch,task,model,consent.Issue(ExternalAction.Analyze,OpenAiClient.Fingerprint(batch,task,model)),cancellation);
    public Task<DeliveryStatus> SendAsync(WebhookTarget target,string text,CancellationToken cancellation)=>
        new WebhookClient(consent,webhookHttp).SendAsync(target,text,consent.Issue(ExternalAction.Send,WebhookClient.Fingerprint(target,text)),cancellation);
}
