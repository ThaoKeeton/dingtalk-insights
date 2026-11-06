using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Desktop;

internal static class HostStartupTests
{
    static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    public static IEnumerable<(string,Action)> All()
    {
        foreach(var recover in new[]{false,true})
        foreach(var ai in new[]{false,true})
        {
            var recovery=recover;var upload=ai;
            yield return ($"host Stop retires queued initial {(recovery?"recovery":"confirmed start")} configuration (AI {upload})",()=>
            {
                using var dws=new SyntheticDws(messages:true);
                var previous=SynchronizationContext.Current;var context=new HostQueuedContext();SynchronizationContext.SetSynchronizationContext(context);
                var host=new MainWindow(dws,upload);Task? starting=null;Task? stopping=null;
                try
                {
                    if(recovery)host.SeedRecovery();else host.BeginOperation();
                    starting=recovery?host.Recover():host.Start();
                    context.PumpUntil(()=>context.HasQueued||starting.IsCompleted,"initial hash completion queued",pump:false);
                    Assert(!starting.IsCompleted&&!host.HasSupervisor,"fixture did not stop at the initial native configuration await");
                    stopping=host.Stop();
                    Assert(!stopping.IsCompleted,"Stop returned before the queued host startup retired");
                    context.PumpUntil(()=>starting.IsCompleted&&stopping.IsCompleted,"host startup and Stop retirement");
                    starting.GetAwaiter().GetResult();stopping.GetAwaiter().GetResult();host.EndOperation();context.Drain();
                    var stopped=host.Store.Load();
                    Assert(stopped.UserStopped&&stopped.Recovery is null,"Stop did not durably revoke recovery");
                    Assert(!host.IsRunning&&!host.Store.SavedStatuses.Contains(MonitorStatus.Running),"old configuration continuation saved Running authority");
                    Assert(dws.Reads==0&&host.ModelCalls==0&&stopped.Requests==0&&stopped.Attempted.Count==0
                        &&stopped.Messages.Count==0&&stopped.Coverage.Count==0&&stopped.Receipts.Count==0,"stopped host dispatched or committed old work");
                    // A separately confirmed fresh Start remains usable after the cancelled continuation retires.
                    host.BeginOperation();var fresh=host.Start();context.PumpUntil(()=>fresh.IsCompleted,"fresh explicit Start");fresh.GetAwaiter().GetResult();host.EndOperation();
                    context.PumpUntil(()=>host.State.Receipts.Count>0,"fresh Start synthetic collection");
                    Assert(host.IsRunning&&dws.Reads==1&&host.Confirmations==(recovery?1:2),"fresh explicit Start did not collect under new confirmation");
                }
                finally
                {
                    try
                    {
                        context.PumpUntil(()=>starting is null||starting.IsCompleted,"old startup cleanup");
                        host.EndOperation();var cleanup=host.Stop();
                        context.PumpUntil(()=>cleanup.IsCompleted&&(stopping is null||stopping.IsCompleted),"host Stop cleanup");cleanup.GetAwaiter().GetResult();context.Drain();
                    }
                    finally{SynchronizationContext.SetSynchronizationContext(previous);}
                }
            });
        }
        yield return ("host Exit retires queued initial recovery and preserves separately approved policy",()=>
        {
            using var dws=new SyntheticDws(messages:true);
            var previous=SynchronizationContext.Current;var context=new HostQueuedContext();SynchronizationContext.SetSynchronizationContext(context);
            var host=new MainWindow(dws,true);Task? starting=null;
            try
            {
                host.SeedRecovery();starting=host.Recover();
                context.PumpUntil(()=>context.HasQueued||starting.IsCompleted,"recovery hash completion queued",pump:false);
                Assert(!starting.IsCompleted&&!host.HasSupervisor,"recovery did not reach the outer native await");
                var exiting=host.Exit();Assert(!exiting.IsCompleted&&!host.IsClosed,"Exit completed before the old startup retired");
                context.PumpUntil(()=>starting.IsCompleted&&exiting.IsCompleted,"Exit and recovery retirement");
                starting.GetAwaiter().GetResult();exiting.GetAwaiter().GetResult();context.Drain();
                var saved=host.Store.Load();
                Assert(host.IsClosed&&saved.Recovery is not null&&!saved.UserStopped,"Exit lost separately saved recovery authority");
                Assert(!host.IsRunning&&dws.Reads==0&&host.ModelCalls==0&&saved.Requests==0&&saved.Attempted.Count==0
                    &&!host.Store.SavedStatuses.Contains(MonitorStatus.Running),"recovery resumed work after Exit");
            }
            finally
            {
                try
                {
                    context.PumpUntil(()=>starting is null||starting.IsCompleted,"Exit startup cleanup");var cleanup=host.Stop();
                    context.PumpUntil(()=>cleanup.IsCompleted,"Exit monitor cleanup");cleanup.GetAwaiter().GetResult();context.Drain();
                }
                finally{SynchronizationContext.SetSynchronizationContext(previous);}
            }
        });
    }
}

internal sealed class HostQueuedContext:SynchronizationContext
{
    readonly ConcurrentQueue<(SendOrPostCallback Callback,object? State)> callbacks=new();
    public bool HasQueued=>!callbacks.IsEmpty;
    public override void Post(SendOrPostCallback callback,object? state)=>callbacks.Enqueue((callback,state));
    public void PumpUntil(Func<bool> completed,string step,bool pump=true)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        while(!completed()&&watch.Elapsed<TimeSpan.FromSeconds(10))
        {if(pump&&callbacks.TryDequeue(out var item))item.Callback(item.State);else Thread.Yield();}
        if(!completed())throw new Exception("Synthetic host gate timed out: "+step);
    }
    public void Drain(){while(callbacks.TryDequeue(out var item))item.Callback(item.State);}
}

// Only native controls, ordinary settings IO and the paid transport are replaced.
// The linked MainWindow.Monitoring.cs owns all production startup/configuration/Stop/Exit/restore bodies.
namespace DingTalkInsights.Desktop
{
    public sealed partial class MainWindow
    {
        public HostMemoryStore Store{get;}=new();
        public InsightState State=>state;
        public int ModelCalls{get;private set;}
        public int Confirmations{get;private set;}
        public bool HasSupervisor=>supervisor is not null;
        public bool IsRunning=>monitorCancellation is not null;
        public bool IsClosed{get;private set;}
        readonly InsightState state=new();readonly IStateStore store;readonly MonitorEngine engine;
        readonly ConsentGate gate=new();readonly MessageBatch demoBatch=Demo.CreateBatch();readonly TestVault vault=new();
        readonly bool smoke=false;readonly string dataRoot;readonly SemaphoreSlim operations=new(1,1);
        bool operationOwned,exiting,allowClose;CancellationTokenSource? active,monitorCancellation;Task? monitorTask;
        MonitorSupervisor? supervisor;TestDialog? activeDialog=null;IDisposable? tray=null;
        readonly List<TestControl> actions=[];
        readonly TestControl BusyBar=new(),StatusText=new(),source=new(),dwsPath=new();
        readonly TestControl profile=new(){Text="corp:user"},conversation=new(){Text="host-synthetic-chat"};
        readonly TestControl since=new(){Text=DateTimeOffset.UtcNow.AddHours(-2).ToString("O")};
        readonly TestControl interval=new(){Value=60},retention=new(){Value=30},enableAi=new(),closeToTray=new(),saveRecovery=new(){IsOn=true};
        TestSettings savedSettings=new(false,false);
        bool IsDemo=>source.SelectedIndex==0;
        CancellationToken ActiveToken=>active?.Token??CancellationToken.None;
        internal MainWindow(SyntheticDws fixture,bool upload)
        {
            store=Store;engine=new MonitorEngine(state,store);dataRoot=fixture.DirectoryPath;dwsPath.Text=fixture.Executable;
            source.SelectedIndex=1;enableAi.IsOn=upload;savedSettings=new(false,upload);File.WriteAllText(Path.Combine(dataRoot,"settings.json"),"{}");
        }
        public Task Start()=>StartMonitoringAsync();public Task Stop()=>CompleteHostStopAsync();public Task Exit()=>ExitApplicationAsync();public Task Recover()=>TryRecoverOnLaunchAsync();
        public void SeedRecovery()
        {
            var config=new MonitoringConfiguration("dws",profile.Text,conversation.Text,ParseTime(since.Text),dwsPath.Text,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dwsPath.Text))),enableAi.IsOn,Model(),60,30,Guard.Hash(Guard.Hash(profile.Text)+"\n"+vault.Revision));
            BackgroundPolicy.Approve(state,config,DateTimeOffset.UtcNow,true);store.Save(state);
        }
        public void BeginOperation(){operations.Wait();operationOwned=true;active=new CancellationTokenSource();}
        public void EndOperation(){if(!operationOwned)return;operationOwned=false;active?.Dispose();active=null;operations.Release();}
        QueryScope Scope(bool monitoring=false)=>new(profile.Text,conversation.Text,ParseTime(since.Text),DateTimeOffset.UtcNow);
        static DateTimeOffset ParseTime(string text)=>DateTimeOffset.Parse(text);ModelSettings Model()=>new();
        Task<bool> Confirm(string title,string text,string accept){Confirmations++;return Task.FromResult(true);}
        Task SaveSettingsAsync(){BackgroundPolicy.Revoke(state);store.Save(state);return Task.CompletedTask;}
        Func<MessageBatch,string,CancellationToken,Task<ModelResult>> Analyzer(bool demo,string key,ModelSettings model)=>
            (batch,task,_)=>{ModelCalls++;return Task.FromResult(new ModelResult(new AnalysisReport(batch,[],[],task),1,1));};
        void LoadConnectionFields(){}void ShowMainWindow(){}void RefreshStatus(){}void UpdateMonitorControls(){}void SetReport(AnalysisReport report){}
        void Close(){IsClosed=allowClose;}
    }
    public sealed class HostMemoryStore:IStateStore
    {
        string json="{}";public List<MonitorStatus> SavedStatuses=[];
        public InsightState Load()=>JsonSerializer.Deserialize<InsightState>(json)!;
        public void Save(InsightState state){json=JsonSerializer.Serialize(state);SavedStatuses.Add(state.Supervisor.Status);}
    }
    sealed class TestControl
    {
        public string Text{get;set;}="";public bool IsOn{get;set;}public double Value{get;set;}public bool IsEnabled{get;set;}
        public int SelectedIndex{get;set;}public Microsoft.UI.Xaml.Visibility Visibility{get;set;}
    }
    sealed class TestDialog{public void Hide(){}}
    sealed record TestSettings(bool CloseToTray,bool UploadToModel);
    sealed class TestVault{public string Revision=>Guard.Hash("synthetic-vault");public TestCredentials Read()=>new("SYNTHETIC_KEY");}
    sealed record TestCredentials(string OpenAiKey);
}
namespace Microsoft.UI.Xaml
{
    public enum Visibility{Visible,Collapsed}
    public sealed class Application{public static Application Current{get;}=new();public void Exit(){}}
}
