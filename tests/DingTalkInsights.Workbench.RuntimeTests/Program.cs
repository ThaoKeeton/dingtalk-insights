using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using DingTalkInsights.Workbench;
using Workbench.Sdk;

if(args.Length is <1 or >2)throw new ArgumentException("Supply the separately built public-SDK-only control fixture assembly and optional test-name filter.");
var tests=BridgeTests.All().Concat(CredentialTests.All()).Concat(ManagerTests.All(args[0])).Where(test=>args.Length==1||test.Item1.Contains(args[1],StringComparison.Ordinal)).ToList();var failed=0;
foreach(var (name,test) in tests)
{
    try{await test().WaitAsync(TimeSpan.FromSeconds(15));Console.WriteLine("PASS "+name);}
    catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.GetType().Name+" "+error.Message);}
}
Console.WriteLine($"{tests.Count-failed}/{tests.Count} bridge checks passed");
return failed==0?0:1;

internal static class BridgeTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static Task Gate(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(5));
    public static IEnumerable<(string,Func<Task>)> All()
    {
        yield return ("registered metadata and initialization grant no business or secret authority",async()=>
        {
            await using var f=new BridgeFixture();
            Require(f.Tasks.Definition is {Id:"monitor",RequiresPluginStart:true},"missing registered plugin-only monitor");
            Require(f.Source.Calls==0&&!f.Controller.Observation.StoreOwned&&!Directory.Exists(f.Data),"initialization acquired business resources");
            Require(!await f.Bridge.StartAsync(null!,false),"unconfirmed start accepted");
        });
        yield return ("queued platform Running is not the actual start acknowledgement",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();f.Tasks.QueueOnly=true;
            var preview=f.Preview();var start=f.Bridge.StartAsync(preview,true);await Gate(f.Tasks.Scheduled.Task);
            Require(!start.IsCompleted&&f.Source.Calls==0&&!f.Controller.Snapshot.IsMonitoring,"queueing was reported as real start");
            f.Tasks.RunQueued();Require(await start&&f.Controller.Snapshot.IsMonitoring,"scheduling admission was acknowledged without the actual running controller");await f.Clock.Wait();
            var original=f.Controller.MonitoringCompletion;
            Require(!original.IsCompleted&&ReferenceEquals(original,f.Bridge.CapturedLoop),"bridge did not capture original supervisor loop");
            await f.Bridge.StopAsync();Require(original.IsCompleted&&f.Tasks.Work.IsCompleted&&ReferenceEquals(original,f.Controller.MonitoringCompletion),"stop missed/replaced the original real loop/delegate observation");
        });
        yield return ("double Start and reused immutable preview cannot create another collector",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();var preview=f.Preview();
            Require(await f.Bridge.StartAsync(preview,true),"first start failed");await f.Clock.Wait();var count=f.Source.Reads;
            Require(!await f.Bridge.StartAsync(preview,true)&&f.Tasks.Starts==1&&f.Source.Reads==count,"duplicate start refreshed authority");
            await f.Bridge.StopAsync();Require(!await f.Bridge.StartAsync(preview,true),"used/stale preview accepted");
            Require(await f.Bridge.StartAsync(f.Preview(),true),"fresh separately reviewed preview refused");await f.Bridge.StopAsync();
        });
        yield return ("expired and generation-stale previews do not schedule or inspect a source",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();var preview=f.Preview();f.Clock.Now+=TimeSpan.FromMinutes(6);
            Require(!await f.Bridge.StartAsync(preview,true)&&f.Tasks.Starts==0&&f.Source.Calls==0,"expired intent scheduled");
            var stale=f.Preview();await f.Controller.StopMonitoringAsync();
            Require(!await f.Bridge.StartAsync(stale,true)&&f.Tasks.Starts==0,"generation-stale intent scheduled");
        });
        yield return ("queued preview expiry settles failure before controller IO",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();f.Tasks.QueueOnly=true;
            var start=f.Bridge.StartAsync(f.Preview(),true);await Gate(f.Tasks.Scheduled.Task);f.Clock.Now+=TimeSpan.FromMinutes(6);f.Tasks.RunQueued();
            Require(!await start&&f.Tasks.Work.IsCompleted&&!f.Tasks.Desired&&f.Source.Calls==0,"queued expired authority reached controller/source");
        });
        yield return ("Stop before delegate settles acknowledgement and prevents later IO",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();f.Tasks.QueueOnly=true;
            var start=f.Bridge.StartAsync(f.Preview(),true);await Gate(f.Tasks.Scheduled.Task);
            var stop=f.Bridge.StopAsync();Require(!await start,"invalidated queued start accepted");
            f.Tasks.RunQueued();await Gate(stop);
            Require(f.Source.Calls==0&&!f.Controller.Snapshot.IsMonitoring&&!f.Tasks.Desired,"queued start survived stop");
        });
        yield return ("Stop while readiness ignores cancellation owns the pending operation",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();var release=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Source.Inspect=async token=>{using var registration=token.Register(()=>f.Source.Cancelled.TrySetResult());f.Source.Entered.TrySetResult();return await release.Task;};
            var start=f.Bridge.StartAsync(f.Preview(),true);await Gate(f.Source.Entered.Task);
            Task? stop=null;
            try
            {
                stop=f.Bridge.StopAsync();await Gate(f.Source.Cancelled.Task);
                Require(!stop.IsCompleted,"stop did not retain pending readiness");
                Require(f.Controller.Observation.PendingOperations==1&&f.Bridge.IsStopping,"pending operation ownership disappeared");
                release.TrySetResult(Guard.Hash("fixture"));Require(!await start,"cancelled readiness acknowledged");await Gate(stop);
                Require(f.Source.Reads==0&&!f.Controller.Snapshot.IsMonitoring,"late readiness started collection");
            }
            finally{release.TrySetResult(Guard.Hash("fixture"));await start;if(stop is not null)await stop;}
        });
        yield return ("execution cancellation after start CTS disposal retires captured real loop",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();Require(await f.Bridge.StartAsync(f.Preview(),true),"start failed");await f.Clock.Wait();
            var original=f.Bridge.CapturedLoop;var count=f.Source.Calls;
            await f.Tasks.CancelExecution(PluginStopReason.UserRequest);
            Require(original.IsCompleted&&f.Tasks.Work.IsCompleted&&!f.Controller.Snapshot.IsMonitoring,"execution token failed to stop actual monitor");
            Require(f.Source.Calls==count&&f.Controller.Observation.StoreOwned,"task stop lost connection or dispatched later");
        });
        yield return ("explicit plugin Stop keeps the delegate owned until host cancellation is admitted",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();Require(await f.Bridge.StartAsync(f.Preview(),true),"start failed");await f.Clock.Wait();
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Tasks.BeforeStop=async()=>{entered.TrySetResult();await release.Task;};
            var stop=f.Bridge.StopAsync();await Gate(entered.Task);await Gate(f.Controller.MonitoringCompletion);
            try{Require(await Task.WhenAny(f.Tasks.Work,Task.Delay(250))!=f.Tasks.Work,"delegate completed before delayed host Stop could admit cancellation; registry would stay Completed");}
            finally{release.TrySetResult();await stop;}
            Require(f.Tasks.State==PluginBackgroundTaskState.Stopped&&!f.Tasks.Desired,"explicit Stop did not settle registered state");
        });
        yield return ("manual read Stop with no registered job invalidates immediately and joins IO",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Source.Read=async(scope,token)=>{using var registration=token.Register(()=>f.Source.Cancelled.TrySetResult());f.Source.Entered.TrySetResult();await release.Task;return LowerSource.Batch(scope);};
            var read=f.Controller.ReadOnceAsync(f.Preview(),true);await Gate(f.Source.Entered.Task);Task? stop=null;
            try
            {
                stop=f.Bridge.StopAsync();await Gate(f.Source.Cancelled.Task);
                Require(f.Tasks.Starts==0&&!stop.IsCompleted&&f.Controller.Observation.PendingOperations==1,"idle registry bypassed manual stop");
                release.TrySetResult();Require(!await read,"late manual result committed");await Gate(stop);
                Require(f.Controller.Snapshot.Candidates.Count==0&&f.Controller.Observation.StoreOwned,"manual stop released connection or published content");
            }
            finally{release.TrySetResult();await read;if(stop is not null)await stop;}
        });
        yield return ("terminal upgrade joins shared cleanup and holds lease through ignored read",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Source.Read=async(scope,token)=>{f.Source.Entered.TrySetResult();await release.Task;return LowerSource.Batch(scope);};
            Require(await f.Bridge.StartAsync(f.Preview(),true),"start failed");await Gate(f.Source.Entered.Task);
            var original=f.Bridge.CapturedLoop;Task? stop=null,terminal=null;
            try
            {
                stop=f.Bridge.StopAsync();f.Reason=PluginStopReason.PluginDisabled;terminal=f.Bridge.DeactivateAsync();
                Require(!terminal.IsCompleted&&!original.IsCompleted&&f.Bridge.IsStopping,"terminal cleanup lost work");
                try{using var conflicting=new EncryptedStateStore(Path.Combine(f.Canonical,"state.dpapi"),f.Cipher);throw new Exception("lease released before read retired");}catch(SafetyException){}
                release.TrySetResult();await Gate(Task.WhenAll(stop,terminal));
                Require(original.IsCompleted&&!f.Controller.Observation.StoreOwned&&!f.Controller.Snapshot.Active,"terminal upgrade failed");
                using var lease=new FileStream(Path.Combine(f.Canonical,"state.dpapi.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            }
            finally{release.TrySetResult();if(stop is not null)await stop;if(terminal is not null)await terminal;}
        });
        yield return ("unsuccessful context Start after queue never grants authority or orphan intent",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();f.Tasks.QueueOnly=true;f.Tasks.RejectScheduled=true;
            var start=f.Bridge.StartAsync(f.Preview(),true);await Gate(f.Tasks.Scheduled.Task);f.Tasks.RunQueued();
            Require(!await start,"unsuccessful context operation treated as success");
            Require(f.Tasks.Work.IsCompleted&&!f.Tasks.Desired&&!f.Controller.Snapshot.IsMonitoring,"failed scheduling left work/intent");
        });
        yield return ("real actual start failure clears intent without worker awaiting its own Stop",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();f.Source.Inspect=_=>throw new IOException("SYNTHETIC_PRIVATE_PROVIDER_BODY");
            Require(!await f.Bridge.StartAsync(f.Preview(),true),"failed controller start accepted");
            Require(f.Tasks.Work.IsCompleted&&!f.Tasks.Desired&&!f.Bridge.IsStopping,"failed start did not settle/drain");
        });
        yield return ("natural loop exit retires registration without automatic restart",async()=>
        {
            await using var f=new BridgeFixture();await f.Connect();f.Source.Read=(_,_)=>throw new MonitoringFault(MonitorFailure.Configuration);
            Require(await f.Bridge.StartAsync(f.Preview(),true),"start failed");
            try{await Gate(f.Tasks.Work);throw new Exception("business failure did not reach host delegate");}catch(RuntimeFault){}
            Require(f.Bridge.CapturedLoop.IsCompleted&&f.Tasks.Starts==1&&f.Source.Reads==1,"natural exit restarted or left delegate alive");
        });
    }
}

internal sealed class BridgeFixture:IAsyncDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"DTI-bridge-"+Guid.NewGuid().ToString("N"));
    public string Canonical=>Path.Combine(root,"canonical");public string Data=>Path.Combine(root,"plugin");
    public SyntheticCipher Cipher{get;}=new();public LowerSource Source{get;}=new();public BridgeClock Clock{get;}=new();public ControlledTasks Tasks{get;}=new();
    public InsightController Controller{get;}public PluginSession Session{get;}public WorkbenchMonitorTask Bridge{get;}public PluginStopReason Reason;
    public BridgeFixture()
    {
        Controller=new(Data,"synthetic-registered-owner",Cipher,Source,Clock);
        Session=new(Controller,Data,Cipher,new(()=>Canonical),_=>Task.FromResult(new Preferences()),_=>Task.CompletedTask);
        Bridge=new(Session,Tasks,()=>Reason,utcNow:()=>Clock.UtcNow);Session.Monitoring=Bridge;Bridge.Register();
    }
    public async Task Connect(){using(var seed=new BusinessStore(Canonical,Cipher))seed.Save(seed.Load());if(!await Controller.ConnectAsync(Canonical,true))throw new Exception("connect failed");}
    public MonitoringPreview Preview()=>Controller.PreviewMonitoring(new("dws","fixture:user","fixture-room",Clock.UtcNow.AddHours(-1),Path.Combine(root,"synthetic-dws"),false,new(),60,30));
    public async ValueTask DisposeAsync(){await Bridge.DeactivateAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
}
internal sealed class SyntheticCipher:IDataCipher
{
    public byte[] Protect(byte[] value)=>[71,14,..value];public byte[] Unprotect(byte[] value)=>value[2..];
}
internal sealed class BridgeClock:IMonitorClock
{
    readonly System.Threading.Channels.Channel<bool> waits=System.Threading.Channels.Channel.CreateUnbounded<bool>();
    public DateTimeOffset Now=DateTimeOffset.Parse("2034-03-02T10:00:00Z");public DateTimeOffset UtcNow=>Now;
    public Task Delay(TimeSpan duration,CancellationToken cancellation){waits.Writer.TryWrite(true);return Task.Delay(Timeout.InfiniteTimeSpan,cancellation);}
    public Task Wait()=>waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}
internal sealed class LowerSource:IRuntimeServices
{
    public int Inspections,Reads;public int Calls=>Inspections+Reads;public Func<CancellationToken,Task<string>>? Inspect;public Func<QueryScope,CancellationToken,Task<MessageBatch>>? Read;
    public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Cancelled{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> InspectSourceAsync(string executable,string profile,CancellationToken token){Interlocked.Increment(ref Inspections);return Inspect?.Invoke(token)??Task.FromResult(Guard.Hash("fixture"));}
    public Task<MessageBatch> ReadAsync(string executable,QueryScope scope,CancellationToken token){Interlocked.Increment(ref Reads);return Read?.Invoke(scope,token)??Task.FromResult(Batch(scope));}
    public static MessageBatch Batch(QueryScope scope)=>new(scope,[new("fixture-1",scope.ConversationId,"fixture-user","Synthetic sender","Synthetic source",scope.Start+(scope.End-scope.Start)/2)],true,[],new Dictionary<string,System.Text.Json.JsonElement>());
    public Task<MessageBatch> SearchAsync(string executable,QueryScope scope,string keywords,CancellationToken token)=>throw new Exception("unexpected search");
    public Task<IReadOnlyList<Conversation>> DiscoverAsync(string executable,string profile,DateTimeOffset start,DateTimeOffset end,CancellationToken token)=>throw new Exception("unexpected discovery");
    public Task<ModelResult> AnalyzeAsync(string key,MessageBatch batch,string task,ModelSettings model,CancellationToken token)=>throw new Exception("unexpected model");
    public Task<DeliveryStatus> SendAsync(WebhookTarget target,string text,CancellationToken token)=>throw new Exception("unexpected send");
}
internal sealed class ControlledTasks:IPluginBackgroundTasks
{
    public PluginBackgroundTask? Definition;public bool QueueOnly,RejectScheduled,Desired;public int Starts;public Task Work=Task.CompletedTask;
    readonly TaskCompletionSource scheduled=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Scheduled=>scheduled;
    TaskCompletionSource? queued;Execution? execution;public PluginBackgroundTaskState State=PluginBackgroundTaskState.Stopped;
    public Func<Task>? BeforeStop;
    public void Register(PluginBackgroundTask task){if(Definition is not null)throw new Exception("duplicate definition");Definition=task;}
    public IReadOnlyList<PluginBackgroundTaskSnapshot> GetSnapshots()=>Definition is null?[]:[new(Definition.Id,Definition.Title,State,false,Desired,null,null,null){RequiresPluginStart=Definition.RequiresPluginStart}];
    public Task<PluginTaskOperationResult> StartAsync(string id,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();Starts++;Desired=true;State=PluginBackgroundTaskState.Running;execution=new();queued=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Work=Run();scheduled.TrySetResult();if(!QueueOnly)RunQueued();return Task.FromResult(new PluginTaskOperationResult(!RejectScheduled,"controlled thread-pool handoff"));
        async Task Run(){await queued.Task;try{await Definition!.RunAsync(execution);}finally{State=PluginBackgroundTaskState.Completed;}}
    }
    public void RunQueued()=>queued?.TrySetResult();
    public async Task<PluginTaskOperationResult> StopAsync(string id,CancellationToken token=default)
    {if(BeforeStop is not null)await BeforeStop();Desired=false;if(execution is not null)await CancelExecution(PluginStopReason.UserRequest);return new(true,"stopped");}
    public async Task CancelExecution(PluginStopReason reason)
    {
        if(execution is null)return;State=PluginBackgroundTaskState.Stopping;execution.Reason=reason;execution.Cancellation.Cancel();RunQueued();await Work;State=PluginBackgroundTaskState.Stopped;
    }
    public Task<PluginTaskOperationResult> SetResumeOnStartupAsync(string id,bool enabled,CancellationToken token=default)=>Task.FromResult(new PluginTaskOperationResult(!enabled,"plugin start required"));
    sealed class Execution:IPluginBackgroundExecution
    {
        public CancellationTokenSource Cancellation{get;}=new();public CancellationToken CancellationToken=>Cancellation.Token;public PluginTaskStartReason StartReason=>PluginTaskStartReason.UserRequest;
        public PluginStopReason Reason;public PluginStopReason StopReason=>Reason;
    }
}
