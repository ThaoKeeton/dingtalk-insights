using DingTalkInsights.Runtime;
using Workbench.Sdk;

namespace DingTalkInsights.Workbench;

/// <summary>Public SDK scheduling owns the real controller loop; queue admission is never readiness.</summary>
public sealed class WorkbenchMonitorTask:IMonitoringCoordinator
{
    public const string TaskId="monitor";
    readonly object sync=new();
    readonly PluginSession session;
    readonly InsightController controller;
    readonly IPluginBackgroundTasks tasks;
    readonly Func<PluginStopReason> stopReason;
    readonly Func<DateTimeOffset> utcNow;
    readonly CancellationTokenRegistration lifetimeRegistration;
    StartEnvelope? pending,active;
    long epoch;
    bool terminal,stopping,registered;
    Task cleanup=Task.CompletedTask,stopJoin=Task.CompletedTask;
    Task? deactivation;
    Task capturedLoop=Task.CompletedTask;
    readonly HashSet<StateSubscription> subscriptions=[];

    sealed class StartEnvelope(MonitoringPreview preview,long epoch)
    {
        public MonitoringPreview Preview{get;}=preview;
        public long Epoch{get;}=epoch;
        public bool Claimed,Invalidated;
        public TaskCompletionSource<bool> Admission{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Retired{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public WorkbenchMonitorTask(PluginSession session,IPluginBackgroundTasks tasks,Func<PluginStopReason> stopReason,
        CancellationToken lifetime=default,Func<DateTimeOffset>? utcNow=null)
    {
        this.session=session;controller=session.Controller;this.tasks=tasks;this.stopReason=stopReason;this.utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
        lifetimeRegistration=lifetime.Register(()=>{BeginCleanup(terminalStop:true);});
    }
    public bool IsStopping{get{lock(sync)return stopping;}}
    public Task CapturedLoop{get{lock(sync)return capturedLoop;}}
    public Task Completion{get{lock(sync)return Task.WhenAll(cleanup,stopJoin,active?.Retired.Task??pending?.Retired.Task??Task.CompletedTask);}}
    public object Observation{get{lock(sync)return new{Epoch=epoch,PendingStart=pending is not null,WorkerOwned=active is not null,Stopping=stopping,Terminal=terminal,Subscriptions=subscriptions.Count,ActualLoopCompleted=capturedLoop.IsCompleted};}}
    public IDisposable Subscribe(Action changed)
    {
        lock(sync){if(terminal)return new StateSubscription(this,changed);var subscription=new StateSubscription(this,changed);subscriptions.Add(subscription);return subscription;}
    }
    void NotifyState()
    {
        StateSubscription[] current;lock(sync)current=subscriptions.ToArray();foreach(var subscription in current)subscription.Invoke();
    }
    sealed class StateSubscription(WorkbenchMonitorTask owner,Action changed):IDisposable
    {
        int disposed;
        public void Invoke(){if(Volatile.Read(ref disposed)==0)try{changed();}catch(Exception){}}
        public void Dispose(){Interlocked.Exchange(ref disposed,1);lock(owner.sync)owner.subscriptions.Remove(this);}
    }
    public void Register()
    {
        lock(sync)
        {
            if(registered||terminal)throw new InvalidOperationException("Monitor registration is unavailable.");
            tasks.Register(new(TaskId,"钉钉沟通事项监控",RunAsync){RequiresPluginStart=true});registered=true;
        }
        // RequiresPluginStart clears saved resume/running intent during the actual host commit.
        // No task/secret/store operation is performed from registration.
    }
    bool Valid(StartEnvelope envelope)=>!terminal&&!stopping&&!envelope.Invalidated&&envelope.Epoch==epoch
        &&envelope.Preview.Generation==controller.Observation.Generation&&utcNow()>=envelope.Preview.CreatedAt&&utcNow()<=envelope.Preview.ExpiresAt;
    public async Task<bool> StartAsync(MonitoringPreview preview,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return false;
        StartEnvelope envelope;
        lock(sync)
        {
            if(!registered||terminal||stopping||pending is not null||active is not null||!stopJoin.IsCompleted
                ||tasks.GetSnapshots().Any(task=>task.Id==TaskId&&task.State is PluginBackgroundTaskState.Running or PluginBackgroundTaskState.Stopping))return false;
            envelope=new(preview,epoch);
            if(!Valid(envelope)||cancellation.IsCancellationRequested)return false;
            pending=envelope;
        }
        using var cancelled=cancellation.Register(()=>BeginCleanup(terminalStop:IsTerminal(stopReason())));
        var admitted=false;
        try
        {
            var scheduled=await tasks.StartAsync(TaskId,cancellation).ConfigureAwait(false);
            lock(sync)
            {
                admitted=scheduled.Succeeded&&Valid(envelope)&&!cancellation.IsCancellationRequested;
                envelope.Admission.TrySetResult(admitted);
            }
            if(!admitted)return false;
            return await envelope.Started.Task.ConfigureAwait(false);
        }
        catch(Exception){return false;} // UI receives a safe failed-start result, never a provider body.
        finally
        {
            if(!admitted||!envelope.Started.Task.IsCompletedSuccessfully||!envelope.Started.Task.Result)
            {
                var retiring=BeginCleanup(terminalStop:IsTerminal(stopReason()));
                // Only the caller/coordinator clears failed intent. The registered worker never awaits its own Stop.
                try{await tasks.StopAsync(TaskId).ConfigureAwait(false);}catch(Exception){}
                await Join(retiring,envelope.Retired.Task).ConfigureAwait(false);
            }
        }
    }
    async Task RunAsync(IPluginBackgroundExecution execution)
    {
        StartEnvelope? envelope;
        lock(sync)
        {
            envelope=pending;
            if(envelope is null||envelope.Claimed)return;
            envelope.Claimed=true;pending=null;active=envelope;
        }
        Task originalLoop=Task.CompletedTask;
        using var cancelled=execution.CancellationToken.Register(()=>BeginCleanup(terminalStop:IsTerminal(execution.StopReason)||IsTerminal(stopReason())));
        try
        {
            if(!await envelope.Admission.Task.ConfigureAwait(false))return;
            Task<bool> actualStart;
            lock(sync)
            {
                if(!Valid(envelope)||execution.CancellationToken.IsCancellationRequested||execution.StartReason!=PluginTaskStartReason.UserRequest)return;
                // Atomic with invalidation: Stop either cancels this real operation, or prevents its admission.
                actualStart=controller.StartMonitoringAsync(envelope.Preview,true,execution.CancellationToken);
            }
            var started=await actualStart.ConfigureAwait(false);
            if(started)
            {
                // Controller retains the original completed loop as well, even if Stop wins this continuation race.
                originalLoop=controller.MonitoringCompletion;
                lock(sync)capturedLoop=originalLoop;
            }
            lock(sync)envelope.Started.TrySetResult(started&&Valid(envelope)&&!execution.CancellationToken.IsCancellationRequested);
            if(started)await originalLoop.ConfigureAwait(false);
        }
        finally
        {
            envelope.Started.TrySetResult(false);envelope.Admission.TrySetResult(false);
            // Execution cancellation must explicitly stop real work: Start's linked CTS is disposed after its result.
            // This cleanup owns only controller/persistence work, never this registered delegate or registry Stop.
            var retiring=BeginCleanup(terminalStop:IsTerminal(execution.StopReason)||IsTerminal(stopReason()));
            try{await Task.WhenAll(retiring,originalLoop).ConfigureAwait(false);}
            finally
            {
                lock(sync)
                {
                    if(ReferenceEquals(active,envelope))active=null;
                    envelope.Retired.TrySetResult();
                    if(!terminal&&cleanup.IsCompletedSuccessfully)stopping=false;
                }
                NotifyState();
            }
        }
    }
    Task BeginCleanup(bool terminalStop)
    {
        Task result;
        lock(sync)
        {
            terminalStop|=IsTerminal(stopReason());
            if(terminal)return cleanup;
            if(stopping&&!terminalStop)return cleanup;
            stopping=true;epoch++;
            foreach(var envelope in new[]{pending,active}.OfType<StartEnvelope>())
            {
                envelope.Invalidated=true;envelope.Admission.TrySetResult(false);envelope.Started.TrySetResult(false);
                if(!envelope.Claimed)envelope.Retired.TrySetResult();
            }
            pending=null;
            // Synchronous invalidation happens before any SDK persistence/Stop wait, even with no registered job.
            var previous=cleanup;
            if(terminalStop){terminal=true;cleanup=Task.WhenAll(previous,session.DeactivateAsync());}
            else cleanup=controller.StopMonitoringAsync();
            result=cleanup;
        }
        NotifyState();return result;
    }
    public async Task StopAsync()
    {
        Task worker;lock(sync)worker=active?.Retired.Task??pending?.Retired.Task??Task.CompletedTask;
        var retiring=BeginCleanup(terminalStop:IsTerminal(stopReason()));
        Task joined;lock(sync)joined=stopJoin=Join(retiring,worker);
        PluginTaskOperationResult result;
        try{result=await tasks.StopAsync(TaskId).ConfigureAwait(false);}
        catch(Exception){result=new(false,"Background stop could not complete.");}
        if(!result.Succeeded&&!terminal)throw new RuntimeFault(RuntimeFailure.Busy); // Stopping still owns the real cleanup/lease.
        await joined.ConfigureAwait(false);
    }
    async Task Join(Task retiring,Task worker)
    {
        await Task.WhenAll(retiring,worker).ConfigureAwait(false);
        lock(sync){if(!terminal&&cleanup.IsCompletedSuccessfully&&active is null&&pending is null)stopping=false;}
        NotifyState();
    }
    public Task DeactivateAsync()
    {
        lock(sync)
        {
            if(deactivation is not null)return deactivation;
            var worker=active?.Retired.Task??pending?.Retired.Task??Task.CompletedTask;
            return deactivation=Finish(BeginCleanup(terminalStop:true),worker);
        }
        async Task Finish(Task retiring,Task worker)
        {
            await Task.WhenAll(retiring,worker,stopJoin).ConfigureAwait(false);lifetimeRegistration.Unregister(); // Host owns any in-flight Lifetime callback; never wait for it while holding the coordinator lock.
            lock(sync){foreach(var subscription in subscriptions.ToArray())subscription.Dispose();subscriptions.Clear();}
        }
    }
    static bool IsTerminal(PluginStopReason reason)=>reason is PluginStopReason.PluginDisabled or PluginStopReason.HostShutdown or PluginStopReason.InitializationFailed;
}
