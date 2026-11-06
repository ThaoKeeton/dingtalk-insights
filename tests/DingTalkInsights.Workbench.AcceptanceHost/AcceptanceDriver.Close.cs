using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Workbench.App;

internal static partial class AcceptanceDriver
{
    [DllImport("user32.dll",SetLastError=true)]static extern bool PostMessage(nint hwnd,uint message,nuint wParam,nint lParam);
    sealed record PresenterCloseState(bool Closing,bool Stopped,bool AllowClose);
    sealed record CloseLease(string Name,bool Acquired,string? ErrorType=null,string? HResult=null);
    sealed record CloseEvidence(PresenterCloseState Presenter,Task? Controller,Task? Session,JsonElement? Plugin,CloseLease[] Leases);
    static void RequestClose(MainWindow window,RunOptions report,HarnessOptions options,object? plugin,List<string> checks,List<object> observations,string packageHash)
    {
        const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
        var data=Path.Combine(options.UserRoot,"data",Id);
        var appWindow=window.AppWindow; // Own the actual native observer through ProcessExit.
        var closingLoop=plugin is null?null:Loop(plugin);
        var closingSession=plugin?.GetType().GetField("session",fields)!.GetValue(plugin);
        var closeCompletion=((TaskCompletionSource)typeof(MainWindow).GetField("_closeCompleted",fields)!.GetValue(window)!).Task;
        PresenterCloseState Presenter()=>new((bool)typeof(MainWindow).GetField("_closing",fields)!.GetValue(window)!,
            (bool)typeof(MainWindow).GetField("_stopped",fields)!.GetValue(window)!,
            (bool)typeof(MainWindow).GetField("_allowClose",fields)!.GetValue(window)!);
        var before=Presenter();
        Require(!before.Closing&&!before.Stopped&&!before.AllowClose&&!closeCompletion.IsCompleted,"Presenter was already closing before the actual system request.");
        var closingObserved=false;var closedObserved=false;var destroyingObserved=false;var closeReported=false;var diagnosticFailed=false;var systemRequestAdmitted=false;
        var closeEvents=new List<string>();var sequence=0;var sync=new object();
        JsonElement? destroyedResources=null;string? destroyedDataHash=null;
        var tracePath=Path.Combine(options.Root,"evidence",options.Phase+".close.jsonl");HarnessOptions.RejectLinks(tracePath);
        var jsonOptions=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
        void Persist(object entry)
        {
            using var trace=new FileStream(tracePath,FileMode.Append,FileAccess.Write,FileShare.Read);
            trace.Write(JsonSerializer.SerializeToUtf8Bytes(entry,jsonOptions));trace.WriteByte((byte)'\n');trace.Flush(flushToDisk:true);
        }
        CloseEvidence Capture(PresenterCloseState presenter)=>new(presenter,plugin is null?null:(Task)plugin.GetType().GetProperty("Retirement")!.GetValue(plugin)!,
            closingSession?.GetType().GetField("retirement",fields)!.GetValue(closingSession) as Task,plugin is null?null:Observation(plugin),
            [ProbeCloseLease("host",Path.Combine(options.UserRoot,"host.lock")),ProbeCloseLease("demo",Path.Combine(data,"demo","state.dpapi.lock")),
             ProbeCloseLease("initial-readiness",Path.Combine(options.Root,"pending-stop","initial-readiness","canonical","state.dpapi.lock")),
             ProbeCloseLease("one-shot-read",Path.Combine(options.Root,"pending-stop","one-shot-read","canonical","state.dpapi.lock"))]);
        void Fail(Exception error)
        {diagnosticFailed=true;closeReported=true;Environment.ExitCode=1;WriteFailure(report,"actual-window-retired",error,window);}
        void RequireStable(CloseEvidence evidence)
        {
            if(destroyedResources is {} retired)
            {
                var current=evidence.Plugin!.Value.GetProperty("Resources");
                Require(current.GetProperty("Dispatches").GetInt64()==retired.GetProperty("Dispatches").GetInt64()
                    &&current.GetProperty("OwnedStoreWrites").GetInt64()==retired.GetProperty("OwnedStoreWrites").GetInt64(),"Plugin dispatched or wrote after actual window destruction.");
            }
            if(destroyedDataHash is not null)Require(Fingerprint(data)==destroyedDataHash,"Plugin data changed after actual window destruction.");
        }
        CloseEvidence? Observe(string boundary,bool? cancelled=null,bool? handled=null)
        {
            lock(sync)
            {
                PresenterCloseState? observedPresenter=null;
                try
                {
                    Require(++sequence<=16,"Close observation bound exceeded.");
                    observedPresenter=Presenter();var evidence=Capture(observedPresenter);
                    // Only bounded labels, task states, counts, hashes and existing-file lease outcomes.
                    var entry=new{sequence,boundary,events=closeEvents.ToArray(),systemRequestAdmitted,acceptanceFailed=AcceptanceFailed,exitCode=Environment.ExitCode,closingObserved,closedObserved,destroyingObserved,cancelled,handled,
                        originalPresenterBefore=before,originalPresenter=evidence.Presenter,originalCloseTask=CloseTaskState(closeCompletion),originalLoop=CloseTaskState(closingLoop),
                        controllerRetirement=CloseTaskState(evidence.Controller),sessionRetirement=CloseTaskState(evidence.Session),pluginPresent=plugin is not null,
                        active=evidence.Plugin?.GetProperty("Active").GetBoolean(),monitoring=evidence.Plugin?.GetProperty("IsMonitoring").GetBoolean(),
                        pendingPersistence=evidence.Plugin?.GetProperty("AdapterPendingPersistence").GetInt32(),resources=evidence.Plugin?.GetProperty("Resources"),
                        leases=evidence.Leases,destroyedDataSha256=destroyedDataHash};
                    Persist(entry);
                    RequireStable(evidence);
                    GC.KeepAlive(appWindow);
                    return evidence;
                }
                catch(Exception error)
                {
                    // Preserve the genuine event even if a teardown-time observation itself fails.
                    try{if(sequence<=16)Persist(new{sequence,boundary,events=closeEvents.Take(16).ToArray(),closingObserved,closedObserved,destroyingObserved,
                        originalPresenterBefore=before,originalPresenter=observedPresenter,originalCloseTask=CloseTaskState(closeCompletion),originalLoop=CloseTaskState(closingLoop),
                        diagnosticErrorType=error.GetType().Name,diagnosticHResult=$"0x{unchecked((uint)error.HResult):X8}"});}
                    catch(Exception){} // A failed durable write still fails acceptance below.
                    Fail(error);return null;
                }
            }
        }
        appWindow.Closing+=(_,args)=>{lock(sync){closingObserved=true;closeEvents.Add("AppWindow.Closing");Observe("AppWindow.Closing",cancelled:args.Cancel);}};
        window.Closed+=(_,args)=>{lock(sync){closedObserved=true;closeEvents.Add("Window.Closed");Observe("Window.Closed",handled:args.Handled);}};
        appWindow.Destroying+=(_,_)=>
        {
            lock(sync)
            {
                destroyingObserved=true;closeEvents.Add("AppWindow.Destroying");
                var evidence=Observe("AppWindow.Destroying");
                if(closeReported||AcceptanceFailed||Environment.ExitCode!=0)return;
                closeReported=true;
                try
                {
                    Require(systemRequestAdmitted&&evidence is not null&&!diagnosticFailed&&closedObserved,"Actual admitted system request/native close observations were incomplete.");
                    // Pinned MainWindow sets _stopped only AFTER Manager.ShutdownAsync and HostRuntime.DisposeAsync;
                    // _allowClose follows StopAsync and precedes Close(). No driver cleanup method is called.
                    // A later external Closing observer or the private post-Close TCS need not be delivered here.
                    Require(evidence!.Presenter is {Closing:true,Stopped:true,AllowClose:true},"Actual destruction preceded original presenter shutdown/disposal.");
                    Require(!closeCompletion.IsFaulted&&!closeCompletion.IsCanceled,"Original presenter close task failed.");
                    if(plugin is not null)
                    {
                        var actual=evidence.Plugin!.Value;var resources=actual.GetProperty("Resources");
                        Require(closingLoop is {IsCompletedSuccessfully:true}&&evidence.Controller is {IsCompletedSuccessfully:true}&&evidence.Session is {IsCompletedSuccessfully:true}
                            &&!actual.GetProperty("Active").GetBoolean()&&!actual.GetProperty("IsMonitoring").GetBoolean()
                            &&actual.GetProperty("RegisteredMonitor").GetProperty("Subscriptions").GetInt32()==0&&!actual.GetProperty("RegisteredMonitor").GetProperty("WorkerOwned").GetBoolean()
                            &&actual.GetProperty("AdapterPendingPersistence").GetInt32()==0&&resources.GetProperty("PendingOperations").GetInt32()==0
                            &&resources.GetProperty("Subscriptions").GetInt32()==0&&!resources.GetProperty("SupervisorPresent").GetBoolean()
                            &&resources.GetProperty("MonitoringCompleted").GetBoolean()&&!resources.GetProperty("StoreOwned").GetBoolean(),"Real close left plugin work/resources.");
                        destroyedResources=resources.Clone();
                    }
                    Require(evidence.Leases.All(lease=>lease.Acquired),"Original close did not release every existing host/demo/canonical lease before destruction.");
                    destroyedDataHash=Fingerprint(data);
                    Require(Observe("destruction-retirement-verified") is not null&&!diagnosticFailed,"Close retirement evidence could not be persisted.");
                    observations.Add(new{stage="actual-system-window-close",request="WM_SYSCOMMAND/SC_CLOSE",events=closeEvents.ToArray(),
                        originalPresenterBefore=before,originalPresenterAtDestruction=evidence.Presenter,externalClosingObserved=closingObserved,
                        originalCloseTask=CloseTaskState(closeCompletion),originalLoop=CloseTaskState(closingLoop),controllerRetirement=CloseTaskState(evidence.Controller),
                        sessionRetirement=CloseTaskState(evidence.Session),resources=destroyedResources,leases=evidence.Leases,destroyedDataSha256=destroyedDataHash});
                    checks.Add("Actual system Close, Window.Closed and AppWindow.Destroying; original presenter shutdown/disposal markers, real task/resource retirement and all four exclusive existing leases; launcher must verify exit0 and unchanged data");
                    Require(!AcceptanceFailed&&Environment.ExitCode==0,"An earlier acceptance failure prevents provisional close success.");
                    report.WriteReport(new{succeeded=true,stage="closed-awaiting-launcher-exit-validation",phase=options.Phase,evidenceKind="source-linked-unmodified-presenter-compatibility",businessBoundary="production-demo-Core-MonitorSupervisor; no real DWS/account/model/Webhook",options.RunId,packageSha256=packageHash,platformCommit="82bd958e220aaf94ccdbf1ea68b0ec53b770706a",productBuild=JsonDocument.Parse(File.ReadAllText(Path.Combine(options.Package,"build-info.json"))).RootElement.Clone(),os=RuntimeInformation.OSDescription,osBuild=Environment.OSVersion.Version.ToString(),sessionId=Process.GetCurrentProcess().SessionId,processId=Environment.ProcessId,uiThreadId=Environment.CurrentManagedThreadId,checks,observations,report.StartupStages,completedUtc=DateTimeOffset.UtcNow});
                }
                catch(Exception error){Fail(error);}
            }
        };
        // These observers only record truth and reject later work; they never initiate or repair cleanup.
        _=closeCompletion.ContinueWith(_=>Observe("original-close-task-completed"),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>Observe("ProcessExit");
        Require(Observe("before-system-close-request") is not null,"Initial close observations could not be persisted.");
        report.WriteReport(new{succeeded=false,stage="closing-"+options.Phase,checks,observations,report.StartupStages});
        if(!PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(window),0x0112,0xF060,0))
        {
            var error=new IOException("Could not post actual owned-window system Close command.");Fail(error);throw error;
        }
        systemRequestAdmitted=true;
    }
    static object? CloseTaskState(Task? task)=>task is null?null:new{id=task.Id,status=task.Status.ToString(),completedSuccessfully=task.IsCompletedSuccessfully};
    static CloseLease ProbeCloseLease(string name,string path)
    {
        try
        {
            HarnessOptions.RejectLinks(path);
            using var lease=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            return new(name,true);
        }
        catch(Exception error){return new(name,false,error.GetType().Name,$"0x{unchecked((uint)error.HResult):X8}");}
    }
}
