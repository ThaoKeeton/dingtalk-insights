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
public sealed record ReportSendIdentity(string ReportKey,IReadOnlyList<string> LegacyBodies,bool LegacyRisk)
{
    public void Validate()
    {
        if(!MonitoringConfiguration.IsDigest(ReportKey)||LegacyBodies is null||LegacyBodies.Count>2||
            LegacyBodies.Any(s=>s is null||Encoding.UTF8.GetByteCount(s)>1024*1024))throw new MonitoringFault(MonitorFailure.Storage);
    }
    public bool SameAs(ReportSendIdentity other)=>ReportKey==other.ReportKey&&LegacyRisk==other.LegacyRisk&&LegacyBodies.SequenceEqual(other.LegacyBodies);
}

/// <summary>Local replay prevention, not a server exactly-once guarantee. Interrupted Pending means Unknown.</summary>
public sealed class DurableWebhookSender(ISendAttemptStore store,Func<WebhookTarget,string,CancellationToken,Task<DeliveryStatus>> transport,TimeProvider? clock=null)
{
    readonly SemaphoreSlim operation=new(1,1);
    readonly TimeProvider clock=clock??TimeProvider.System;
    public static string AttemptKey(WebhookTarget target,string text)=>Guard.Hash(Guard.Hash(target.Url)+"\n"+Guard.Hash(text));
    public static string ReportAttemptKey(WebhookTarget target,string reportKey)=>Guard.Hash(Guard.Hash(target.Url)+"\nreport:"+reportKey);
    public static SendAttempt? FindPrevious(SendLedger ledger,WebhookTarget target,string text,ReportSendIdentity? report=null)
    {
        ledger.Validate();report?.Validate();var keys=new HashSet<string>{AttemptKey(target,text)};
        if(report is not null)
        {
            keys.Add(ReportAttemptKey(target,report.ReportKey));
            foreach(var body in report.LegacyBodies)keys.Add(AttemptKey(target,body));
        }
        return keys.Where(ledger.Attempts.ContainsKey).Select(key=>ledger.Attempts[key])
            .OrderBy(a=>a.Status is SendAttemptStatus.Pending or SendAttemptStatus.Unknown?0:1).ThenByDescending(a=>a.AttemptedAt).FirstOrDefault();
    }
    public async Task<SendResult> SendAsync(WebhookTarget target,string text,bool confirmed,bool humanConfirmedRetry=false,CancellationToken cancellation=default,ReportSendIdentity? report=null)
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
            var previous=FindPrevious(ledger,target,text,report);
            if((previous is not null||report?.LegacyRisk==true)&&!humanConfirmedRetry)return new(previous is null?DeliveryStatus.Unknown:Status(previous.Status),false,true,previous?.Attempts??0);
            var reportKey=report is null?null:ReportAttemptKey(target,report.ReportKey);
            var required=new[]{key,reportKey}.Where(k=>k is not null).Distinct().Count(k=>!ledger.Attempts.ContainsKey(k!));
            if(ledger.Attempts.Count+required>10000)throw new MonitoringFault(MonitorFailure.Storage);
            var attempt=new SendAttempt(destination,payload,clock.GetUtcNow(),SendAttemptStatus.Pending,checked((previous?.Attempts??0)+1));
            ledger.Attempts[key]=attempt;
            if(reportKey is not null)ledger.Attempts[reportKey]=attempt with{PayloadHash=Guard.Hash("report:"+report!.ReportKey)};
            Save(ledger); // Both the exact payload and report identity precede any dispatch.
            cancellation.ThrowIfCancellationRequested();
            DeliveryStatus result;
            try{result=await transport(target,text,cancellation);}catch(Exception){result=DeliveryStatus.Unknown;}
            // Stop may race an HTTP transport ignoring cancellation. The durable Pending remains uncertain.
            if(cancellation.IsCancellationRequested)return new(DeliveryStatus.Unknown,true,false,attempt.Attempts);
            if(!Enum.IsDefined(result))result=DeliveryStatus.Unknown;
            var status=result switch{DeliveryStatus.Sent=>SendAttemptStatus.Sent,DeliveryStatus.Failed=>SendAttemptStatus.Failed,_=>SendAttemptStatus.Unknown};
            ledger.Attempts[key]=attempt with{Status=status};
            if(reportKey is not null)ledger.Attempts[reportKey]=attempt with{PayloadHash=Guard.Hash("report:"+report!.ReportKey),Status=status};
            Save(ledger);return new(result,true,false,attempt.Attempts);
        }
        finally{operation.Release();}
    }
    static DeliveryStatus Status(SendAttemptStatus status)=>status switch{SendAttemptStatus.Sent=>DeliveryStatus.Sent,SendAttemptStatus.Failed=>DeliveryStatus.Failed,_=>DeliveryStatus.Unknown};
    void Save(SendLedger ledger){try{store.Save(ledger);}catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}}
}
