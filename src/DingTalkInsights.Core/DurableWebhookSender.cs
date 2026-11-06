using System.Text;

namespace DingTalkInsights.Core;

public enum SendAttemptStatus { Pending,Sent,Failed,Unknown }
public sealed record SendAttempt(string DestinationHash,string PayloadHash,DateTimeOffset AttemptedAt,SendAttemptStatus Status,int Attempts);
public sealed class SendLedger
{
    public int Version {get;set;}=1;
    public Dictionary<string,SendAttempt> Attempts {get;set;}=[];
    public void Validate()
    {
        if(Version!=1||Attempts is null||Attempts.Count>10000||Attempts.Any(p=>!MonitoringConfiguration.IsDigest(p.Key)||p.Value is null||
            !MonitoringConfiguration.IsDigest(p.Value.DestinationHash)||!MonitoringConfiguration.IsDigest(p.Value.PayloadHash)||
            !Enum.IsDefined(p.Value.Status)||p.Value.Attempts<1))throw new MonitoringFault(MonitorFailure.Storage);
    }
}
public interface ISendAttemptStore {SendLedger Load();void Save(SendLedger ledger);}
public sealed record SendResult(DeliveryStatus Status,bool Dispatched,bool Suppressed,int Attempts);

/// <summary>Local replay prevention, not a server exactly-once guarantee. Interrupted Pending means Unknown.</summary>
public sealed class DurableWebhookSender(ISendAttemptStore store,Func<WebhookTarget,string,CancellationToken,Task<DeliveryStatus>> transport,TimeProvider? clock=null)
{
    readonly SemaphoreSlim operation=new(1,1);
    readonly TimeProvider clock=clock??TimeProvider.System;
    public static string AttemptKey(WebhookTarget target,string text)=>Guard.Hash(Guard.Hash(target.Url)+"\n"+Guard.Hash(text));
    public async Task<SendResult> SendAsync(WebhookTarget target,string text,bool confirmed,bool humanConfirmedRetry=false,CancellationToken cancellation=default)
    {
        if(!confirmed)return new(DeliveryStatus.Unknown,false,false,0);
        target.Validate();
        if(string.IsNullOrWhiteSpace(text)||Encoding.UTF8.GetByteCount(text)>18000)throw new SafetyException("Webhook 内容为空或超过 18 KiB，请缩小整理范围。");
        if(!await operation.WaitAsync(0,cancellation))return new(DeliveryStatus.Unknown,false,true,0);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var destination=Guard.Hash(target.Url);var payload=Guard.Hash(text);var key=AttemptKey(target,text);
            SendLedger ledger;
            try{ledger=store.Load();ledger.Validate();}catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
            ledger.Attempts.TryGetValue(key,out var previous);
            if(previous is not null&&!humanConfirmedRetry)return new(Status(previous.Status),false,true,previous.Attempts);
            if(previous is null&&ledger.Attempts.Count>=10000)throw new MonitoringFault(MonitorFailure.Storage);
            var attempt=new SendAttempt(destination,payload,clock.GetUtcNow(),SendAttemptStatus.Pending,checked((previous?.Attempts??0)+1));
            ledger.Attempts[key]=attempt;Save(ledger);
            cancellation.ThrowIfCancellationRequested();
            DeliveryStatus result;
            try{result=await transport(target,text,cancellation);}catch(Exception){result=DeliveryStatus.Unknown;}
            // Stop may race an HTTP transport ignoring cancellation. The durable Pending remains uncertain.
            if(cancellation.IsCancellationRequested)return new(DeliveryStatus.Unknown,true,false,attempt.Attempts);
            if(!Enum.IsDefined(result))result=DeliveryStatus.Unknown;
            ledger.Attempts[key]=attempt with{Status=result switch{DeliveryStatus.Sent=>SendAttemptStatus.Sent,DeliveryStatus.Failed=>SendAttemptStatus.Failed,_=>SendAttemptStatus.Unknown}};
            Save(ledger);return new(result,true,false,attempt.Attempts);
        }
        finally{operation.Release();}
    }
    static DeliveryStatus Status(SendAttemptStatus status)=>status switch{SendAttemptStatus.Sent=>DeliveryStatus.Sent,SendAttemptStatus.Failed=>DeliveryStatus.Failed,_=>DeliveryStatus.Unknown};
    void Save(SendLedger ledger){try{store.Save(ledger);}catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}}
}
