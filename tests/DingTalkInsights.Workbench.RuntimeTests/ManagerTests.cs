using System.Reflection;
using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using DingTalkInsights.Workbench;
using Workbench.Core;
using Workbench.Sdk;

internal static class ManagerTests
{
    const string Id="com.projectworkbench.dti-control";
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static async Task Wait(Func<bool> condition){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(10,deadline.Token);}
    public static IEnumerable<(string,Func<Task>)> All(string fixture)
    {
        yield return ("actual Manager enforces plugin-only metadata, confirmed starts, host Stop and safe restore",async()=>
        {
            using var f=new ManagedRoot(fixture);await using(var manager=f.Manager())
            {
                Require((await manager.InstallAsync(f.Package)).Succeeded,"real install failed");await manager.DiscoverAsync();
                var installed=manager.Plugins.Single();Require(!installed.IsLoaded&&!installed.IsTrusted&&installed.State==PluginState.Disabled,"install granted authority");
                Require(!(await manager.EnableAsync(Id)).Succeeded,"untrusted enable accepted");
                Require((await manager.EnableAsync(Id,confirmTrust:true)).Succeeded,"explicit trust/enable failed");
                var view=manager.Contributions.Views.Single().View;var session=Get<PluginSession>(view,"Session");var bridge=Get<WorkbenchMonitorTask>(view,"Bridge");
                var context=Get<IPluginRuntimeContext>(view,"Context");var task=manager.GetBackgroundTaskSnapshots(Id).Single();
                Require(task.Id=="monitor"&&task.RequiresPluginStart&&!task.ResumeOnStartup&&!task.DesiredRunning,"task metadata/consent wrong");
                Require(manager.GetSecretDefinitions(Id).Select(d=>d.Id).Order().SequenceEqual(new[]{"dingtalk.webhook-secret","dingtalk.webhook-url","openai.api-key"}),"secret definitions wrong");
                Require(!(await manager.StartBackgroundTaskAsync(Id,"monitor")).Succeeded&&!(await manager.SetBackgroundTaskResumeOnStartupAsync(Id,"monitor",true)).Succeeded,"host bypassed preview");
                Require((await context.BackgroundTasks.StartAsync("monitor")).Succeeded,"context scheduling control failed");
                await Wait(()=>context.BackgroundTasks.GetSnapshots().Single().State==PluginBackgroundTaskState.Completed);
                Require(!session.Controller.Observation.StoreOwned&&session.Controller.Observation.Dispatches==0,"unconfirmed direct context start created collector");
                await session.Controller.OpenDemoAsync();var request=new MonitoringRequest("demo","demo:local","demo-project",DateTimeOffset.UtcNow.AddHours(-3),"",false,new(),60,30);
                Require(await bridge.StartAsync(session.Controller.PreviewMonitoring(request),true),"confirmed bridge start failed");
                var original=bridge.CapturedLoop;Require(!original.IsCompleted,"missing actual Core loop");
                Require((await manager.StopBackgroundTaskAsync(Id,"monitor")).Succeeded,"host task Stop failed");
                Require(original.IsCompleted&&session.Controller.Observation.StoreOwned&&session.Controller.Snapshot.Active,"task Stop lost connection or missed loop");
                var fresh=session.Controller.PreviewMonitoring(request);Require(await bridge.StartAsync(fresh,true),"fresh confirmed restart failed");
                Require((await manager.DisableAsync(Id)).Succeeded,"terminal disable failed");
                Require(!session.Controller.Observation.StoreOwned&&!session.Controller.Snapshot.Active&&bridge.CapturedLoop.IsCompleted,"disable left work/lease");
                Require((await manager.EnableAsync(Id)).Succeeded,"re-enable failed");
                var newSession=Get<PluginSession>(manager.Contributions.Views.Single().View,"Session");
                Require(!ReferenceEquals(session,newSession)&&!newSession.Controller.Observation.StoreOwned&&newSession.Controller.Observation.Dispatches==0,"re-enable inherited authority");
            }
            await using var restored=f.Manager();await restored.DiscoverAsync();await restored.RestoreEnabledAsync();
            var saved=Get<PluginSession>(restored.Contributions.Views.Single().View,"Session");
            Require(!saved.Controller.Observation.StoreOwned&&saved.Controller.Observation.Dispatches==0&&!restored.GetBackgroundTaskSnapshots(Id).Single().ResumeOnStartup,"restore started business work");
        });
        yield return ("actual Manager Stop timeout retains Stopping ownership and late terminal upgrade",async()=>
        {
            using var f=new ManagedRoot(fixture);await using var manager=f.Manager(TimeSpan.FromMilliseconds(80));
            Require((await manager.InstallAsync(f.Package)).Succeeded,"install failed");await manager.DiscoverAsync();await manager.EnableAsync(Id,confirmTrust:true);
            var view=manager.Contributions.Views.Single().View;var session=Get<PluginSession>(view,"Session");var bridge=Get<WorkbenchMonitorTask>(view,"Bridge");var source=Get<object>(view,"Source");
            Set(source,"HoldReadiness",false);var release=Get<TaskCompletionSource>(source,"Release");
            await session.Controller.OpenDemoAsync(); // connect a synthetic canonical store to exercise real held source IO
            await session.Controller.DisconnectAsync();var canonical=Path.Combine(f.Root,"canonical");var cipher=Get<IDataCipher>(view,"Cipher");using(var seed=new BusinessStore(canonical,cipher))seed.Save(seed.Load());
            await session.Controller.ConnectAsync(canonical,true);var request=new MonitoringRequest("dws","fixture:user","fixture-room",DateTimeOffset.UtcNow.AddMinutes(-10),Path.Combine(f.Root,"synthetic-dws"),false,new(),60,30);
            Require(await bridge.StartAsync(session.Controller.PreviewMonitoring(request),true),"start failed");await Get<TaskCompletionSource>(source,"Entered").Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Require(!(await manager.StopBackgroundTaskAsync(Id,"monitor")).Succeeded,"uncooperative worker reported stopped");
                Require(manager.GetBackgroundTaskSnapshots(Id).Single().State==PluginBackgroundTaskState.Stopping&&bridge.IsStopping,"timeout lost Stopping state");
                Require(!(await manager.StartBackgroundTaskAsync(Id,"monitor")).Succeeded,"Stopping accepted host Start");
                try{await manager.ShutdownAsync();throw new Exception("pending shutdown reported completion");}catch(PluginShutdownException){}
                Require(!(await manager.DisableAsync(Id)).Succeeded&&Get<IPluginRuntimeContext>(view,"Context").StopReason==PluginStopReason.PluginDisabled&&manager.Plugins.Single().HasPendingStop,"terminal timeout released ownership");
                try{using var lease=new FileStream(Path.Combine(canonical,"state.dpapi.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);throw new Exception("active lease released");}catch(IOException){}
                release.TrySetResult();await bridge.DeactivateAsync();await Wait(()=>!manager.HasPendingStops);
                Require(!session.Controller.Observation.StoreOwned&&!session.Controller.Snapshot.Active,"late terminal cleanup did not release");
            }
            finally{release.TrySetResult();await bridge.DeactivateAsync();}
        });
        yield return ("actual changed-package revocation rejects retained-context Start without business IO",async()=>
        {
            using var f=new ManagedRoot(fixture);await using var manager=f.Manager();await manager.InstallAsync(f.Package);await manager.DiscoverAsync();await manager.EnableAsync(Id,confirmTrust:true);
            var view=manager.Contributions.Views.Single().View;var session=Get<PluginSession>(view,"Session");var bridge=Get<WorkbenchMonitorTask>(view,"Bridge");
            await session.Controller.OpenDemoAsync();var preview=session.Controller.PreviewMonitoring(new("demo","demo:local","demo-project",DateTimeOffset.UtcNow.AddHours(-3),"",false,new(),60,30));
            // Isolated package marker only; no loaded DLL is replaced or deleted.
            File.WriteAllText(Path.Combine(manager.Plugins.Single().PackagePath,"changed-fixture-marker.txt"),"synthetic revocation trigger");await manager.DiscoverAsync();
            Require(!await bridge.StartAsync(preview,true)&&session.Controller.Observation.Dispatches==0,"revoked retained context granted start");
            Require(!manager.Plugins.Single().IsTrusted&&(await manager.DisableAsync(Id)).Succeeded,"revocation cleanup failed");
        });
    }
    static T Get<T>(object target,string name)=>(T)target.GetType().GetProperty(name)!.GetValue(target)!;
    static void Set(object target,string name,object value)=>target.GetType().GetProperty(name)!.SetValue(target,value);
    sealed class ManagedRoot:IDisposable
    {
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"DTI-real-manager-"+Guid.NewGuid().ToString("N"));public string Package=>Path.Combine(Root,"source");
        public ManagedRoot(string fixture)
        {
            Directory.CreateDirectory(Package);File.Copy(fixture,Path.Combine(Package,Path.GetFileName(fixture)));
            File.WriteAllText(Path.Combine(Package,"plugin.json"),JsonSerializer.Serialize(new{id=Id,name="Synthetic production bridge fixture",version="0.3.1",apiVersion=1,minimumHostVersion="0.2.0",entryAssembly=Path.GetFileName(fixture),entryType="DingTalkInsights.Workbench.RuntimeFixture.ControlFixturePlugin",description="Test-only synthetic source and production bridge",capabilities=new[]{"local.synthetic"}}));
        }
        public PluginManager Manager(TimeSpan? timeout=null)=>new(new(){UserRoot=Path.Combine(Root,"user"),PluginsRoot=Path.Combine(Root,"user","Plugins"),StopTimeout=timeout??TimeSpan.FromSeconds(5),SharedAssemblies=[typeof(PluginSession).Assembly,typeof(InsightController).Assembly,typeof(MonitorSupervisor).Assembly]});
        public void Dispose(){/* Loaded fixture code is retained until this test process exits. Never replace/delete loaded DLLs. */}
    }
}
