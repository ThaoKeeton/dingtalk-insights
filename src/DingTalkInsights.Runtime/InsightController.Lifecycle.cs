using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed partial class InsightController
{
    sealed class Operation(long generation,CancellationToken lifetime,CancellationToken cancellation):IDisposable
    {
        public long Generation{get;}=generation;
        public CancellationTokenSource Source{get;}=CancellationTokenSource.CreateLinkedTokenSource(lifetime,cancellation);
        public CancellationToken Token=>Source.Token;
        public TaskCompletionSource Retired{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()=>Source.Dispose();
    }
    void EnsureActive(){if(deactivated||lifetime.IsCancellationRequested)throw new RuntimeFault(RuntimeFailure.Inactive);}
    void EnsureCurrent(long expected,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(deactivated||stopCount>0||generation!=expected||lifetime.IsCancellationRequested)throw new OperationCanceledException();
    }
    void Commit(Operation operation,Action action)
    {lock(sync){EnsureCurrent(operation.Generation,operation.Token);action();}}
    Task<T> Dispatch<T>(long expected,CancellationToken cancellation,Func<Task<T>> action)
    {lock(sync){EnsureCurrent(expected,cancellation);return action();}}
    async Task<bool> Run(Func<Operation,Task<bool>> action,RuntimeFailure fallback,CancellationToken cancellation)
    {
        Operation operation;
        // Own pending work before the first semaphore/hash/configuration await.
        lock(sync)
        {
            EnsureActive();if(stopCount>0){notice=new RuntimeFault(RuntimeFailure.Busy).Message;return false;}
            operation=new(generation,lifetime,cancellation);pending.Add(operation);
        }
        var acquired=false;
        try
        {
            acquired=await operations.WaitAsync(0,operation.Token);
            if(!acquired){lock(sync)notice=new RuntimeFault(RuntimeFailure.Busy).Message;return false;}
            Commit(operation,()=>
            {
                EnforceRetention();if(store is not null)store.Save(state);
            });
            var result=await action(operation);Commit(operation,()=>EnforceRetention());return result;
        }
        catch(OperationCanceledException)when(operation.Source.IsCancellationRequested||operation.Generation!=generation||stopCount>0||deactivated){return false;}
        catch(Exception error)
        {
            var category=error switch
            {
                RuntimeFault fault=>fault.Category,
                MonitoringFault{Category:MonitorFailure.Storage}=>RuntimeFailure.Storage,
                MonitoringFault{Category:MonitorFailure.Retention}=>RuntimeFailure.Retention,
                MonitoringFault{Category:MonitorFailure.Configuration or MonitorFailure.Validation}=>RuntimeFailure.Configuration,
                _=>fallback
            };
            var safe=new RuntimeFault(category);lock(sync){if(operation.Generation==generation&&!deactivated)notice=safe.Message;}
            throw safe;
        }
        finally
        {
            lock(sync)pending.Remove(operation);
            // Publish only while holding the business gate; draft-only notifications read the cached snapshot.
            // Signal retirement last, after all controller/store use has finished.
            if(acquired){Publish();operations.Release();}else Notify();
            lock(sync){operation.Dispose();operation.Retired.TrySetResult();}
        }
    }
    public Task StopMonitoringAsync()=>BeginRetirement(disconnect:false,terminal:false);
    public Task DisconnectAsync()=>BeginRetirement(disconnect:true,terminal:false);
    public Task DeactivateAsync()
    {
        lock(sync)
        {
            if(deactivated)return deactivation;
            // BeginRetirement invalidates synchronously. Deactivation task is observable for Lifetime cleanup.
            deactivation=BeginRetirement(disconnect:true,terminal:true);return deactivation;
        }
    }
    void OnLifetimeCancelled()=>_=DeactivateAsync();
    Task BeginRetirement(bool disconnect,bool terminal)
    {
        Operation[] retiring;MonitorSupervisor? monitor;
        lock(sync)
        {
            if(deactivated&&!terminal)return deactivation;
            if(terminal)deactivated=true;
            generation++;stopCount++;previews.Clear();retiring=pending.ToArray();monitor=supervisor;
        }
        foreach(var operation in retiring)try{operation.Source.Cancel();}catch(ObjectDisposedException){}catch(AggregateException){}
        // Signal the supervisor before waiting behind another retirement or ignored lower transport.
        var stop=StopSupervisor(monitor);
        return RetireAsync(retiring,stop,disconnect,terminal);
    }
    async Task StopSupervisor(MonitorSupervisor? monitor)
    {
        if(monitor is null)return;
        retirementWrite.Value=true;
        try{await monitor.StopAsync(revokeRecovery:true);}
        finally{retirementWrite.Value=false;}
    }
    async Task RetireAsync(Operation[] retiring,Task stop,bool disconnect,bool terminal)
    {
        await retirement.WaitAsync();Exception? failure=null;
        try
        {
            try{await stop;}catch(Exception){failure=new RuntimeFault(RuntimeFailure.Storage);}
            await Task.WhenAll(retiring.Select(o=>o.Retired.Task));
            lock(sync)
            {
                if(supervisor is not null)supervisor.Changed-=MonitorChanged;
                supervisor=null;configuration=null;
                if(store is not null)
                {
                    BackgroundPolicy.Revoke(state);
                    try{store.Save(state);}catch(Exception){failure=new RuntimeFault(RuntimeFailure.Storage);}
                }
                if(disconnect)
                {
                    store?.Dispose();store=null;canonicalRoot=null;state=new();connection=RuntimeConnection.Disconnected;
                    ClearVisible();ClearCredentialUse();
                }
                notice=failure?.Message??(terminal?"插件已停用；工作已退役，数据与发送尝试保留。":"已停止并撤销恢复；已有断点、尝试和费用预留保留。");
            }
        }
        finally
        {
            lock(sync)stopCount--;
            retirement.Release();Publish();
            if(terminal)
            {
                lock(sync){foreach(var observer in subscriptions.ToArray())observer.Dispose();subscriptions.Clear();}
                lifetimeRegistration.Dispose();
            }
        }
        // Keep Lifetime cancellation cleanup observed and safe; the status carries persistence failure.
        // No future plugin initialization/start ever restores live authority, even if the final save fails.
    }
    void MonitorChanged(TickResult? result)
    {
        lock(sync)
        {
            if(deactivated||stopCount>0)return;
            if(result?.Report is {} value)SetReport(value);
            notice=MonitorNotices.For(state.Supervisor.Failure);
        }
        Publish();
    }
    IStateStore OwnedStore(long expected)=>new OperationStore(this,store??throw new RuntimeFault(RuntimeFailure.Connection),expected);
    sealed class OperationStore(InsightController owner,BusinessStore store,long expected):IStateStore
    {
        public InsightState Load(){lock(owner.sync){Check();return store.Load();}}
        public void Save(InsightState value){lock(owner.sync){Check();store.Save(value);}}
        void Check()
        {
            if(!ReferenceEquals(owner.store,store)||(!owner.retirementWrite.Value&&(owner.generation!=expected||owner.stopCount>0||owner.deactivated||owner.lifetime.IsCancellationRequested)))
                throw new MonitoringFault(MonitorFailure.Storage);
        }
    }
    public async ValueTask DisposeAsync()=>await DeactivateAsync();
}
