using System.Diagnostics;
using System.Runtime.Loader;
using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using DingTalkInsights.Workbench;
using Microsoft.UI.Xaml.Controls;
using Workbench.WinUI.Sdk;
using Workbench.Core;
using Workbench.Sdk;

namespace Workbench.App;

internal static partial class AcceptanceDriver
{
    // API1 has no injected-service activation factory. This is a separate explicitly labeled
    // source-linked production-page phase in the real presenter, not an installed-plugin test hook.
    static async Task VerifyPendingStopControls(MainWindow window,HarnessOptions options,List<object> observations,Action<string> record)
    {
        foreach(var readiness in new[]{true,false})
        {
            var mode=readiness?"initial-readiness":"one-shot-read";
            CurrentStep="source-linked-pending-stop-"+mode;
            var root=Path.Combine(options.Root,"pending-stop",mode);HarnessOptions.RejectLinks(root);
            var canonical=Path.Combine(root,"canonical");
            var fixtureUser=Path.Combine(root,"fixture-user");using var fixtureLease=HostRuntime.AcquireLease(fixtureUser);
            await using var fixtureManager=new PluginManager(new(){UserRoot=fixtureUser,PluginsRoot=Path.Combine(fixtureUser,"Plugins"),
                SharedAssemblies=[typeof(PluginSession).Assembly,typeof(InsightController).Assembly,typeof(MonitorSupervisor).Assembly]});
            var fixturePackage=Path.Combine(root,"control-package");Directory.CreateDirectory(fixturePackage);
            var fixtureAssembly=Path.Combine(AppContext.BaseDirectory,"control-fixture","DingTalkInsights.Workbench.RuntimeFixture.dll");
            File.Copy(fixtureAssembly,Path.Combine(fixturePackage,Path.GetFileName(fixtureAssembly)));
            const string fixtureId="com.projectworkbench.dti-control";
            File.WriteAllText(Path.Combine(fixturePackage,"plugin.json"),JsonSerializer.Serialize(new{id=fixtureId,name="Synthetic production bridge control",version="0.3.1",apiVersion=1,minimumHostVersion="0.2.0",
                entryAssembly=Path.GetFileName(fixtureAssembly),entryType="DingTalkInsights.Workbench.RuntimeFixture.ControlFixturePlugin",description="Test-only synthetic source and production bridge",capabilities=new[]{"local.synthetic"}}));
            Require((await fixtureManager.InstallAsync(fixturePackage)).Succeeded,"Registered native control fixture install failed.");
            await fixtureManager.DiscoverAsync();Require((await fixtureManager.EnableAsync(fixtureId,confirmTrust:true)).Succeeded,"Registered native control fixture enable failed.");
            var fixtureView=fixtureManager.Contributions.Views.Single().View;
            var session=(PluginSession)fixtureView.GetType().GetProperty("Session")!.GetValue(fixtureView)!;
            var controller=session.Controller;var gate=session.Actions;
            var source=new SourceControl(fixtureView.GetType().GetProperty("Source")!.GetValue(fixtureView)!);source.HoldReadiness=readiness;
            var cipher=(IDataCipher)fixtureView.GetType().GetProperty("Cipher")!.GetValue(fixtureView)!;
            var statuses=(Func<IReadOnlyList<MonitorStatus>>)(()=> (IReadOnlyList<MonitorStatus>)cipher.GetType().GetProperty("Statuses")!.GetValue(cipher)!);
            var registered=fixtureManager.GetBackgroundTaskSnapshots(fixtureId).Single();
            Require(registered.RequiresPluginStart&&!registered.ResumeOnStartup&&!registered.DesiredRunning
                &&!(await fixtureManager.StartBackgroundTaskAsync(fixtureId,"monitor")).Succeeded,"Control fixture host Start/resume metadata bypass.");
            using(var seed=new BusinessStore(canonical,cipher))
            {
                var state=seed.Load();
                if(!readiness)
                {
                    var now=DateTimeOffset.UtcNow;var oldScope=new QueryScope("fixture:local","fixture-old-room",now.AddDays(-10),now.AddDays(-10).AddHours(1));
                    var old=new SourceMessage("old-fixture",oldScope.ConversationId,"fixture-user","Synthetic sender","Synthetic old source",oldScope.Start.AddMinutes(1));
                    state.Ingest(new(oldScope,[old],true,[],new Dictionary<string,JsonElement>()));
                    state.Reports.Add(new(now,oldScope.Key,"Synthetic old task","Synthetic old report",false,old.CreatedAt));
                }
                seed.Save(state);
            }
            session.Preferences=new(){PollSeconds=60,RetentionDays=7};
            session.Draft=new(){Profile="fixture:local",ConversationId="fixture-stop-room",Start=DateTimeOffset.UtcNow.AddMinutes(-10),Executable=Path.Combine(root,"synthetic-dws.fixture")};
            try
            {
                Require(await controller.ConnectAsync(canonical,true),"Guarded pending-Stop fixture canonical connect failed.");
                var page=new InsightPage(session,false,gate);
                await window.SmokeAttachAsync(new WinUiView("pending-stop-fixture","Synthetic pending Stop",()=>page));
                Find<CheckBox>(page,"DTI 使用模拟来源").IsChecked=false;
                if(readiness)await ConfirmAction(window,page,"DTI 开始监控",true);
                else
                {
                    Invoke(Find<Button>(page,"DTI 读取一次"));await Wait(()=>OpenDialogs(window).Any(),"Native read retention dialog did not open.");
                    var approval=Find<TextBox>(OpenDialogs(window).Single(),"DTI 本次授权正文").Text;
                    Require(approval.Contains("保留 7 天")&&approval.Contains("清理 1 条旧消息 / 1 份旧结果"),"Native read dialog omitted selected retention/exact trim effects.");
                    await Dialog(window,true,"确认本次操作");
                }
                CurrentStep="source-linked-pending-stop-"+mode;
                await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(gate.IsBusy&&controller.Observation.PendingOperations==1,"Real page action was not pending in the production controller.");
                var pending=controller.Observation;var writeIndex=statuses().Count;var stopWatch=Stopwatch.StartNew();
                Invoke(Find<Button>(page,"DTI 停止监控"));
                await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var cancellationMilliseconds=stopWatch.ElapsedMilliseconds;
                Require(stopWatch.Elapsed<TimeSpan.FromSeconds(5)&&gate.IsBusy&&controller.Observation.PendingOperations==1,
                    "Same-page Stop did not cancel promptly or returned before ignored transport retired.");
                await Wait(()=>Find<TextBlock>(page,"DTI 监控与预算").Text.Contains("停止中")&&!Find<Button>(page,"DTI 开始监控").IsEnabled,"Native pending Stop did not render retained ownership/block new Start.");
                var stopping=controller.Observation;
                // The actual lower source still owns pending work. Stop must keep waiting, rather than pretend it retired.
                Require(!source.Release.Task.IsCompleted&&stopping.Generation>pending.Generation,"Native Stop did not invalidate pending authority before release.");
                source.Release.TrySetResult();
                await Wait(()=>!gate.IsBusy&&controller.Observation.PendingOperations==0,"Native Stop did not join pending controller/page work.");
                Require(!controller.Snapshot.IsMonitoring&&!controller.Observation.SupervisorPresent&&controller.Snapshot.Candidates.Count==0,
                    "Late pending source committed Running or content after native Stop.");
                var postStopRunningWrites=statuses().Skip(writeIndex).Count(status=>status==MonitorStatus.Running);
                Require(controller.Observation.Dispatches==pending.Dispatches&&postStopRunningWrites==0,
                    "Pending completion dispatched again or saved Running after Stop.");
                var retired=controller.Observation;var statePath=Path.Combine(canonical,"state.dpapi");var stoppedHash=Hash(statePath);
                await Task.Delay(100);
                Require(controller.Observation.Dispatches==retired.Dispatches&&controller.Observation.OwnedStoreWrites==retired.OwnedStoreWrites&&Hash(statePath)==stoppedHash,
                    "Source-linked native Stop had later dispatch/state writes after retirement.");
                // A distinct native confirmation is required; releasing the old source never starts anything.
                var beforeStart=source.Reads;
                await ConfirmAction(window,page,"DTI 开始监控",true);
                await Wait(()=>controller.Snapshot.IsMonitoring&&source.Reads>beforeStart&&controller.Snapshot.Health.NextReadAt is not null&&!gate.IsBusy,
                    "Independent new native Start did not create the real supervisor.");
                var actualLoop=controller.MonitoringCompletion;Require(!actualLoop.IsCompleted,"New supervisor Task was not alive.");
                Invoke(Find<Button>(page,"DTI 停止监控"));
                await Wait(()=>actualLoop.IsCompleted&&!gate.IsBusy&&!controller.Snapshot.IsMonitoring,"Independent new monitor did not retire through the real Stop control.");
                observations.Add(new
                {
                    stage="source-linked-production-control-pending-stop",mode,
                    evidenceKind="separate source-linked production page in genuine original presenter; not installed-plugin service injection",
                    simulatedBoundary="IRuntimeServices readiness/read only; production controller/store/Core/MonitorSupervisor and native controls",
                    linkedSources=new[]{"src/DingTalkInsights.Workbench/InsightPage.cs","src/DingTalkInsights.Workbench/PluginSession.cs","src/DingTalkInsights.Workbench/NativeActionGate.cs","src/DingTalkInsights.Workbench/WorkbenchMonitorTask.cs"},
                    registeredTask=registered,fixtureAssemblySha256=Hash(fixtureAssembly),pageAssembly=typeof(InsightPage).Assembly.FullName,runtimeAssembly=typeof(InsightController).Assembly.FullName,
                    coreAssembly=typeof(MonitorSupervisor).Assembly.FullName,runtimeModuleId=typeof(InsightController).Assembly.ManifestModule.ModuleVersionId,
                    runtimeSha256=Hash(typeof(InsightController).Assembly.Location),
                    pending,stopping,retired,stopCancellationMilliseconds=cancellationMilliseconds,
                    sourceInspections=source.Inspections,sourceReads=source.Reads,postStopRunningWrites,independentLoopId=actualLoop.Id,independentLoopTaskType=actualLoop.GetType().FullName,independentLoopCompleted=actualLoop.IsCompleted
                });
                record("Separate source-linked production control: real same-page Stop cancels pending "+mode+", awaits retirement with no late dispatch/Running/content; separate native confirmation starts a new real supervisor");
            }
            finally
            {
                source.Release.TrySetResult();window.SmokeDetach();await session.Monitoring!.DeactivateAsync();Require((await fixtureManager.DisableAsync(fixtureId)).Succeeded,"Registered control fixture disable failed.");
                Require(controller.Observation.Subscriptions==0&&!controller.Observation.StoreOwned&&controller.Observation.PendingOperations==0,
                    "Pending-Stop fixture cleanup retained real resources.");
                using var lease=new FileStream(Path.Combine(canonical,"state.dpapi.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            }
        }
        Require(AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="DingTalkInsights.Workbench")
            .All(a=>AssemblyLoadContext.GetLoadContext(a)!=AssemblyLoadContext.Default),"Business plugin DLL was preloaded into default host context.");
    }
    sealed class SourceControl(object target)
    {
        T Get<T>(string name)=>(T)target.GetType().GetProperty(name)!.GetValue(target)!;
        public bool HoldReadiness{set=>target.GetType().GetProperty("HoldReadiness")!.SetValue(target,value);}
        public TaskCompletionSource Entered=>Get<TaskCompletionSource>("Entered");
        public TaskCompletionSource Cancelled=>Get<TaskCompletionSource>("Cancelled");
        public TaskCompletionSource Release=>Get<TaskCompletionSource>("Release");
        public int Inspections=>Get<int>("Inspections");public int Reads=>Get<int>("Reads");
    }
}
