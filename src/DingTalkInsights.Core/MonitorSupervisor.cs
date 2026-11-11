namespace DingTalkInsights.Core;

public interface IMonitorClock
{
    DateTimeOffset UtcNow {get;}
    Task Delay(TimeSpan duration,CancellationToken cancellation);
}
public sealed class SystemMonitorClock:IMonitorClock
{
    public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan duration,CancellationToken cancellation)=>Task.Delay(duration,cancellation);
}

/// <summary>A single, cancellable session. The host must await StopAsync before disposing its store.</summary>
public sealed class MonitorSupervisor(MonitorEngine engine,IStateStore store,
    Func<QueryScope,CancellationToken,Task<MessageBatch>> fetch,
    Func<MessageBatch,string,CancellationToken,Task<ModelResult>>? analyze,
    Func<CancellationToken,Task<MonitoringConfiguration?>> currentConfiguration,IMonitorClock? clock=null,SemaphoreSlim? operationGate=null,
    Func<CancellationToken,Task>? prepareModel=null)
{
    readonly IMonitorClock clock=clock??new SystemMonitorClock();
    readonly SemaphoreSlim lifecycle=new(1,1);
    readonly object wakeSync=new();
    TaskCompletionSource wake=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object cancellationSync=new();
    readonly HashSet<CancellationTokenSource> pendingStarts=[];
    TaskCompletionSource? stopBarrier;
    long cancellationGeneration;
    CancellationTokenSource? session;
    public Task Completion {get;private set;}=Task.CompletedTask;
    public bool IsRunning=>session is {IsCancellationRequested:false}&&!Completion.IsCompleted;
    public string Notice=>MonitorNotices.For(engine.State.Supervisor.Failure);
    public event Action<TickResult?>? Changed;
    public event Action<bool>? Activity;

    public async Task<bool> StartAsync(MonitoringConfiguration config,bool confirmed=false,bool recover=false,CancellationToken cancellation=default)
    {
        // Register a cancellable pending start before any asynchronous wait, including another Stop.
        var startup=CancellationTokenSource.CreateLinkedTokenSource(cancellation);long generation;Task pendingStop;
        lock(cancellationSync)
        {
            generation=cancellationGeneration;pendingStarts.Add(startup);pendingStop=stopBarrier?.Task??Task.CompletedTask;
        }
        var acquired=false;var adopted=false;
        try
        {
            await pendingStop.WaitAsync(startup.Token);
            await lifecycle.WaitAsync(startup.Token);acquired=true;
            startup.Token.ThrowIfCancellationRequested();
            if(session is not null&&!Completion.IsCompleted)return false;
            if(!confirmed&&!recover)return false;
            config.Validate();
            if(recover&&!BackgroundPolicy.CanRecover(engine.State,config,true))return false;
            var actual=await currentConfiguration(startup.Token);
            startup.Token.ThrowIfCancellationRequested();
            if(actual is null||actual.Fingerprint!=config.Fingerprint)return false;
            var savedHealth=engine.State.Supervisor;
            if(recover&&(savedHealth.NextReadAt>clock.UtcNow.AddHours(1)||savedHealth.ModelDeferredUntil>clock.UtcNow.AddDays(1)))
                throw new MonitoringFault(MonitorFailure.Configuration);
            lock(cancellationSync)
            {
                // Stop and this final synchronous commit have one order: a cancelled generation cannot run.
                startup.Token.ThrowIfCancellationRequested();
                if(generation!=cancellationGeneration)return false;
                session?.Dispose();session=startup;adopted=true;
                var previous=engine.State.Supervisor;
                engine.State.UserStopped=false;
                engine.State.Supervisor=confirmed?new SupervisorHealth{Status=MonitorStatus.Running,ConfigurationFingerprint=config.Fingerprint,LastCompletedAt=previous.LastCompletedAt}:
                    previous with{Status=previous.Status==MonitorStatus.ReadRetry?MonitorStatus.ReadRetry:MonitorStatus.Running,ConfigurationFingerprint=config.Fingerprint};
                if(!Save()){session=null;adopted=false;return false;}
                startup.Token.ThrowIfCancellationRequested();
                if(generation!=cancellationGeneration)return false;
                Completion=LoopAsync(config,startup.Token);
            }
            return true;
        }
        catch(OperationCanceledException)when(startup.IsCancellationRequested){return false;}
        catch(Exception)
        {
            engine.State.Supervisor=engine.State.Supervisor with{Status=MonitorStatus.NeedsAttention,Failure=MonitorFailure.Configuration};return false;
        }
        finally
        {
            lock(cancellationSync){pendingStarts.Remove(startup);if(!adopted)startup.Dispose();}
            if(acquired)lifecycle.Release();
        }
    }
    public async Task StopAsync(bool revokeRecovery=true)
    {
        CancellationTokenSource[] cancel;var barrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(cancellationSync)
        {
            cancellationGeneration++;stopBarrier=barrier;
            cancel=pendingStarts.Concat(session is {} current?[current]:Array.Empty<CancellationTokenSource>()).Distinct().ToArray();
        }
        var acquired=false;
        try
        {
            // Signal before waiting for lifecycle: pending readiness and running work must see Stop immediately.
            foreach(var source in cancel)try{source.Cancel();}catch(ObjectDisposedException){}
            Wake();await lifecycle.WaitAsync();acquired=true;
            session?.Cancel();await Completion;
            lock(cancellationSync){session?.Dispose();session=null;}
            if(revokeRecovery)BackgroundPolicy.Revoke(engine.State);
            else if(engine.State.Supervisor.Status!=MonitorStatus.NeedsAttention)engine.State.Supervisor=engine.State.Supervisor with{Status=MonitorStatus.Stopped};
            if(!Save(revokeRecovery))throw new MonitoringFault(MonitorFailure.Storage);
            Changed?.Invoke(null);
        }
        finally
        {
            if(acquired)lifecycle.Release();
            lock(cancellationSync){if(ReferenceEquals(stopBarrier,barrier))stopBarrier=null;barrier.TrySetResult();}
        }
    }
    public void Wake(){lock(wakeSync){wake.TrySetResult();}}
    bool Save(bool revocation=false)
    {
        try{if(revocation&&store is IReportCommitStore atomic)atomic.SaveRevocation(engine.State);else store.Save(engine.State);return true;}
        catch(Exception){engine.State.Supervisor=engine.State.Supervisor with{Status=MonitorStatus.NeedsAttention,Failure=MonitorFailure.Storage};return false;}
    }
    async Task ValidateCurrent(MonitoringConfiguration config,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var current=await currentConfiguration(cancellation);cancellation.ThrowIfCancellationRequested();
        if(current is null||current.Fingerprint!=config.Fingerprint)throw new MonitoringFault(MonitorFailure.Configuration);
    }
    async Task PrepareModel(MonitoringConfiguration config,CancellationToken cancellation)
    {
        await ValidateCurrent(config,cancellation);
        if(prepareModel is not null)await prepareModel(cancellation);
        cancellation.ThrowIfCancellationRequested();
    }
    async Task LoopAsync(MonitoringConfiguration config,CancellationToken cancellation)
    {
        await Task.Yield();
        try
        {
            while(!cancellation.IsCancellationRequested)
            {
                var health=engine.State.Supervisor;
                if(health.NextReadAt is {} due&&due>clock.UtcNow){await WaitUntil(due,cancellation);continue;}
                if(operationGate is not null)await operationGate.WaitAsync(cancellation);
                try
                {
                Activity?.Invoke(true);
                TickResult? result=null;
                try
                {
                    await ValidateCurrent(config,cancellation);
                    var ai=config.UploadToModel&&!health.AiSuppressed&&(health.ModelDeferredUntil is null||clock.UtcNow>=health.ModelDeferredUntil);
                    result=await engine.TickAsync(config.Scope(clock.UtcNow),fetch,ai?analyze:null,
                        config.Model,config.RetentionDays,clock.UtcNow,cancellation,tolerateModelFailure:true,beforeModel:c=>PrepareModel(config,c),utcNow:()=>clock.UtcNow);
                    cancellation.ThrowIfCancellationRequested();
                    health=engine.State.Supervisor;
                    if(!result.Complete)throw new MonitoringFault(MonitorFailure.Incomplete);
                    engine.State.Supervisor=health with{Status=MonitorStatus.Running,Failure=result.ModelFailure!=MonitorFailure.None?result.ModelFailure:
                        health.AiSuppressed?health.Failure:!ai&&health.ModelDeferredUntil is not null?MonitorFailure.Budget:MonitorFailure.None,
                        ReadFailures=0,NextReadAt=clock.UtcNow.AddSeconds(config.PollSeconds),LastCompletedAt=clock.UtcNow,
                        AiSuppressed=health.AiSuppressed||result.ModelFailure is MonitorFailure.ModelUnknown or MonitorFailure.ModelAuthorization,
                        ModelDeferredUntil=result.ModelDeferredUntil??(ai?null:health.ModelDeferredUntil)};
                }
                catch(OperationCanceledException)when(cancellation.IsCancellationRequested){break;}
                catch(Exception error)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var fault=error as MonitoringFault;var category=fault?.Category??MonitorFailure.Unknown;health=engine.State.Supervisor;
                    var count=health.ReadFailures+1;
                    if(category==MonitorFailure.TransientRead&&count<=5&&(fault?.RetryAfter??TimeSpan.Zero)<=TimeSpan.FromSeconds(Math.Max(config.PollSeconds,900)))
                    {
                        var seconds=Math.Min(Math.Max(config.PollSeconds,900),config.PollSeconds*Math.Pow(2,count-1));
                        seconds=Math.Max(seconds,fault?.RetryAfter?.TotalSeconds??0);
                        engine.State.Supervisor=health with{Status=MonitorStatus.ReadRetry,Failure=category,ReadFailures=count,NextReadAt=clock.UtcNow.AddSeconds(seconds)};
                    }
                    else engine.State.Supervisor=health with{Status=MonitorStatus.NeedsAttention,Failure=category==MonitorFailure.TransientRead?MonitorFailure.RetryLimit:category,ReadFailures=Math.Min(count,6),NextReadAt=null};
                }
                if(!Save()){Changed?.Invoke(null);break;}
                Changed?.Invoke(result);
                if(engine.State.Supervisor.Status==MonitorStatus.NeedsAttention)break;
                }
                finally{Activity?.Invoke(false);operationGate?.Release();}
            }
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
    }
    async Task WaitUntil(DateTimeOffset due,CancellationToken cancellation)
    {
        using var delayCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task signal;lock(wakeSync){signal=wake.Task;}
        var delay=clock.Delay(due-clock.UtcNow,delayCancellation.Token);
        try{await await Task.WhenAny(delay,signal);cancellation.ThrowIfCancellationRequested();}
        finally{delayCancellation.Cancel();lock(wakeSync){if(wake.Task.IsCompleted)wake=new(TaskCreationOptions.RunContinuationsAsynchronously);}}
    }
}
