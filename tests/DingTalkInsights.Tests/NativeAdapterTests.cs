using DingTalkInsights.Core;
using DingTalkInsights.Workbench;

internal static class NativeAdapterTests
{
    static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("native saved status survives delayed draft snapshots after actual encrypted persistence",()=>
            SavedNotice().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());
        foreach(var readiness in new[]{true,false})
            yield return ($"native action gate Stop cancels pending {(readiness?"initial readiness":"one-shot read")} while ordinary actions remain serialized",
                ()=>PendingStop(readiness).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());
    }
    static async Task SavedNotice()
    {
        using var f=new RuntimeTests.Fixture();var queued=new Queue<Action>();var status=new PageNotice();var visible="";
        using var binding=new SnapshotBinding(f.Controller,callback=>{queued.Enqueue(callback);return true;},snapshot=>visible=status.ApplyRuntime(snapshot.Notice));
        var persisted=new Preferences();
        var session=new PluginSession(f.Controller,f.PluginRoot,f.Cipher,new(()=>f.CanonicalRoot),_=>Task.FromResult(persisted),p=>{persisted=p;return Task.CompletedTask;})
        {Preferences=new(){PollSeconds=60,RetentionDays=7}};
        f.Controller.SetDraft("Synthetic saved status fixture");await session.SaveAsync();
        Assert(File.Exists(Path.Combine(f.PluginRoot,"connection-draft.dpapi"))&&persisted.PollSeconds==60&&persisted.RetentionDays==7,"real persistence did not finish");
        visible=status.ShowLocal("配置已保存；未授予业务或凭据权限。",f.Controller.Snapshot.Notice);
        while(queued.TryDequeue(out var callback))callback();
        Assert(visible.Contains("配置已保存"),"delayed runtime snapshot erased successful Save despite completed persistence");
        f.Controller.SetDraft("Another synthetic local edit");while(queued.TryDequeue(out var callback))callback();
        Assert(visible.Contains("配置已保存"),"same-notice draft update erased local action result");
        await f.Controller.StopMonitoringAsync();while(queued.TryDequeue(out var callback))callback();
        Assert(visible==f.Controller.Snapshot.Notice&&!visible.Contains("配置已保存"),"new real runtime Stop notice did not supersede saved status");
    }
    static async Task PendingStop(bool readiness)
    {
        using var f=new RuntimeTests.Fixture();var gate=new NativeActionGate();var entered=new TaskCompletionSource();
        var cancelled=new TaskCompletionSource();var release=new TaskCompletionSource();
        async Task Held(CancellationToken token)
        {
            using var registration=token.Register(()=>cancelled.TrySetResult());entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation to exercise the real retirement barrier.
        }
        if(readiness)f.Services.Inspect=async(path,identity,token)=>{await Held(token);return Guard.Hash("synthetic-executable");};
        else f.Services.Read=async(scope,token)=>{await Held(token);return RuntimeTests.FakeServices.Batch(scope);};
        Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true),"fixture canonical connect failed");
        var preview=f.Controller.PreviewMonitoring(f.Request);
        var original=gate.RunAsync(async()=>
        {
            if(readiness)await f.Controller.StartMonitoringAsync(preview,true);
            else await f.Controller.ReadOnceAsync(preview,true);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!await gate.RunAsync(()=>Task.FromException(new Exception("duplicate ordinary action entered"))),"ordinary action was not serialized");
            var writes=f.Cipher.Statuses.Count;var dispatched=f.Controller.Observation.Dispatches;
            var stopped=gate.RunAsync(f.Controller.StopMonitoringAsync,stop:true);
            Assert(!stopped.IsCompleted,"visible Stop was swallowed while its page was busy");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!stopped.IsCompleted&&f.Controller.Observation.PendingOperations==1,"Stop returned before ignored transport retired");
            Assert(!await gate.RunAsync(()=>Task.FromException(new Exception("new work during retirement"))),"retirement allowed business work");
            release.TrySetResult();await original;await stopped;
            Assert(f.Controller.Observation.Dispatches==dispatched&&!f.Controller.Snapshot.IsMonitoring&&!f.Controller.Observation.SupervisorPresent,"stale operation dispatched or committed Running after Stop");
            Assert(!f.Cipher.Statuses.Skip(writes).Contains(MonitorStatus.Running),"late Running state write after Stop");
            Assert(f.Controller.Snapshot.Candidates.Count==0&&f.Controller.Observation.PendingOperations==0&&!gate.IsBusy,"stopped action left state/work or page gate busy");
            f.Services.Inspect=null;f.Services.Read=null;
            var next=f.Controller.PreviewMonitoring(f.Request);bool started=false;
            Assert(await gate.RunAsync(async()=>started=await f.Controller.StartMonitoringAsync(next,true))&&started,"separate new confirmation did not start");
            await f.Clock.WaitForDelay();var loop=f.Controller.MonitoringCompletion;Assert(!loop.IsCompleted,"new actual supervisor did not run");
            await gate.RunAsync(f.Controller.StopMonitoringAsync,stop:true);Assert(loop.IsCompleted,"new loop did not retire");
        }
        finally{release.TrySetResult();await original;}
    }
}
