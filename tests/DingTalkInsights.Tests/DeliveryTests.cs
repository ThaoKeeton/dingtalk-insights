using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

internal static class DeliveryTests
{
    static readonly DateTimeOffset Now=DateTimeOffset.Parse("2034-03-02T10:00:00Z");
    static readonly WebhookTarget Target=new("Synthetic room","https://oapi.dingtalk.com/robot/send?access_token=synthetic-only","synthetic-signing-secret");
    static void Assert(bool condition,string message="assertion failed"){if(!condition)throw new Exception(message);}
    static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    public static IEnumerable<(string,Action)> All()
    {
        foreach(var outcome in new[]{DeliveryStatus.Sent,DeliveryStatus.Unknown,DeliveryStatus.Failed})
        {
            var status=outcome;
            yield return ($"delivery {status} survives restart clear and old-v0.2 state rewrite",()=>Run(async()=>
            {
                using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var calls=0;
                using(var business=new BusinessStore(temp.Path,cipher,new FixedTime(Now)))
                {
                    var state=business.Load();state.Reserve(new ModelSettings(),0.01m,Now);state.MarkAttempted(new("corp:user","chat",Now.AddHours(-1),Now),[new("m","chat","u","Synthetic","text",Now.AddMinutes(-10))]);business.Save(state);
                    var sender=new DurableWebhookSender(business.SendAttempts,async(_,_,_)=>{calls++;await Task.Yield();return status;});
                    var result=await sender.SendAsync(Target,"Synthetic approved payload",confirmed:true);
                    Assert(result.Dispatched&&result.Status==status&&calls==1);
                    state.ClearIndex();business.Save(state);
                }
                // Exact old binary's serializer behavior: unknown top-level fields are discarded on load/save.
                // StateStore.cs is unchanged from the pinned v0.2 source, so this runs its actual old production serializer.
                RewriteThroughV02(temp.Path,cipher);
                using(var business=new BusinessStore(temp.Path,cipher,new FixedTime(Now)))
                {
                    var sender=new DurableWebhookSender(business.SendAttempts,(_,_,_)=>{calls++;return Task.FromResult(DeliveryStatus.Sent);});
                    var result=await sender.SendAsync(Target with{Name="Renamed room",SigningSecret="changed secret"},"Synthetic approved payload",confirmed:true);
                    Assert(result.Suppressed&&!result.Dispatched&&result.Status==status&&calls==1,"persisted send was replayed");
                    Assert(business.Load().Requests==1&&business.Load().ReservedUsd==0.01m,"old rewrite reset the canonical budget");
                    var bytes=File.ReadAllBytes(System.IO.Path.Combine(temp.Path,"send-attempts.dpapi"));
                    Assert(!Encoding.UTF8.GetString(bytes).Contains("Synthetic approved payload")&&!Encoding.UTF8.GetString(bytes).Contains("synthetic-only"),"sidecar leaked content");
                    var ledger=JsonSerializer.Serialize(business.SendAttempts.Load());
                    Assert(!ledger.Contains("payload")&&!ledger.Contains("synthetic-only")&&!ledger.Contains("secret"),"ledger retained raw destination/content");
                }
            }));
        }
        yield return ("delivery interrupted pending attempt remains unknown and requires a separate confirmed retry",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var calls=0;
            using(var business=new BusinessStore(temp.Path,cipher,new FixedTime(Now)))
            {
                using var cancellation=new CancellationTokenSource();
                var sender=new DurableWebhookSender(business.SendAttempts,(_,_,_)=>{calls++;cancellation.Cancel();return Task.FromResult(DeliveryStatus.Sent);});
                var result=await sender.SendAsync(Target,"pending payload",confirmed:true,cancellation:cancellation.Token);
                Assert(result.Status==DeliveryStatus.Unknown&&calls==1);
            }
            using(var business=new BusinessStore(temp.Path,cipher,new FixedTime(Now)))
            {
                var sender=new DurableWebhookSender(business.SendAttempts,(_,_,_)=>{calls++;return Task.FromResult(DeliveryStatus.Sent);});
                Assert((await sender.SendAsync(Target,"pending payload",confirmed:true)).Suppressed&&calls==1);
                Assert(!(await sender.SendAsync(Target,"pending payload",confirmed:false,humanConfirmedRetry:true)).Dispatched&&calls==1);
                var result=await sender.SendAsync(Target,"pending payload",confirmed:true,humanConfirmedRetry:true);
                Assert(result.Dispatched&&result.Status==DeliveryStatus.Sent&&result.Attempts==2&&calls==2);
            }
        }));
        yield return ("delivery saves Pending before HTTP and suppresses concurrent page events",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();using var business=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(Now));
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
            var sender=new DurableWebhookSender(business.SendAttempts,async(_,_,_)=>
            {
                calls++;Assert(business.SendAttempts.Load().Attempts.Values.Single().Status==SendAttemptStatus.Pending,"HTTP preceded durable attempt");entered.SetResult();await release.Task;return DeliveryStatus.Sent;
            });
            var first=sender.SendAsync(Target,"concurrent payload",confirmed:true);
            try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var duplicate=await sender.SendAsync(Target,"concurrent payload",confirmed:true);Assert(!duplicate.Dispatched&&calls==1);}
            finally{release.TrySetResult();}
            Assert((await first).Status==DeliveryStatus.Sent&&business.SendAttempts.Load().Attempts.Values.Single().Status==SendAttemptStatus.Sent);
        }));
        yield return ("delivery corrupt sidecar and failed durable save cause zero HTTP",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var calls=0;
            using var business=new BusinessStore(temp.Path,cipher,new FixedTime(Now));
            var sender=new DurableWebhookSender(business.SendAttempts,(_,_,_)=>{calls++;return Task.FromResult(DeliveryStatus.Sent);});
            File.WriteAllText(System.IO.Path.Combine(temp.Path,"send-attempts.dpapi"),"corrupt synthetic data");
            try{await sender.SendAsync(Target,"corrupt payload",confirmed:true);throw new Exception("accepted corrupt ledger");}catch(MonitoringFault e){Assert(e.Category==MonitorFailure.Storage);}
            File.Delete(System.IO.Path.Combine(temp.Path,"send-attempts.dpapi"));cipher.FailProtect=true;
            try{await sender.SendAsync(Target,"failed save",confirmed:true);throw new Exception("sent before failed save");}catch(MonitoringFault e){Assert(e.Category==MonitorFailure.Storage);}
            Assert(calls==0,"storage failure reached HTTP");
        }));
        yield return ("delivery refuses stale access after the owning business lease is released",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var calls=0;var business=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(Now));
            var sender=new DurableWebhookSender(business.SendAttempts,(_,_,_)=>{calls++;return Task.FromResult(DeliveryStatus.Sent);});business.Dispose();
            try{await sender.SendAsync(Target,"stale payload",confirmed:true);throw new Exception("stale store was usable");}catch(MonitoringFault e){Assert(e.Category==MonitorFailure.Storage);}
            Assert(calls==0);
        }));
    }
    static void RewriteThroughV02(string root,IDataCipher cipher)
    {
        var path=System.IO.Path.Combine(root,"state.dpapi");var plain=cipher.Unprotect(File.ReadAllBytes(path));
        try
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(plain)!;node["FuturePluginField"]="synthetic-unknown-field";
            File.WriteAllBytes(path,cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(node)));
        }
        finally{CryptographicOperations.ZeroMemory(plain);}
        // The production EncryptedStateStore + InsightState source is byte-for-byte unchanged from
        // e85dbd5 (old v0.2). Exercise its actual Load/Save, including unknown-field loss.
        using(var legacy=new EncryptedStateStore(path,cipher,new FixedTime(Now)))legacy.Save(legacy.Load());
        plain=cipher.Unprotect(File.ReadAllBytes(path));
        try{Assert(!Encoding.UTF8.GetString(plain).Contains("FuturePluginField"),"fixture did not exercise old-format field loss");}
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
}

internal sealed class RuntimeTemp:IDisposable
{
    public string Path{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"dingtalk-runtime-synthetic-"+Guid.NewGuid());
    public RuntimeTemp()=>Directory.CreateDirectory(Path);
    public string Child(string name)=>System.IO.Path.Combine(Path,name);
    public void Dispose()=>Directory.Delete(Path,true);
}
internal sealed class SyntheticCipher:IDataCipher
{
    readonly byte[] key=SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-test-key-not-a-credential"));
    public bool FailProtect;public int Reads, Writes;public List<MonitorStatus> Statuses{get;}=[];
    public byte[] Protect(byte[] data)
    {
        Writes++;using(var doc=JsonDocument.Parse(data)){if(doc.RootElement.TryGetProperty("Supervisor",out var health))Statuses.Add((MonitorStatus)health.GetProperty("Status").GetInt32());}
        if(FailProtect)throw new IOException("SYNTHETIC_SECRET_STORAGE");
        var nonce=RandomNumberGenerator.GetBytes(12);var cipher=new byte[data.Length];var tag=new byte[16];using var aes=new AesGcm(key,16);aes.Encrypt(nonce,data,cipher,tag);return nonce.Concat(tag).Concat(cipher).ToArray();
    }
    public byte[] Unprotect(byte[] data)
    {
        Reads++;var plain=new byte[data.Length-28];using var aes=new AesGcm(key,16);aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),plain);return plain;
    }
}
internal sealed class FixedTime(DateTimeOffset now):TimeProvider
{
    public override DateTimeOffset GetUtcNow()=>now;
}
