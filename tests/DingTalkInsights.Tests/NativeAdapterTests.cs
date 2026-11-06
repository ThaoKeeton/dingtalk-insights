using DingTalkInsights.Core;
using DingTalkInsights.Workbench;

internal static class NativeAdapterTests
{
    static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    public static IEnumerable<(string,Action)> All()
    {
        foreach(var readiness in new[]{true,false})
            yield return ($"native action gate Stop cancels pending {(readiness?"initial readiness":"one-shot read")} while ordinary actions remain serialized",
                ()=>PendingStop(readiness).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());
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
