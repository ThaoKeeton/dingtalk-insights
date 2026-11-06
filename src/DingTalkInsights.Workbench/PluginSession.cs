using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

namespace DingTalkInsights.Workbench;

// Only public rates/limits are stored in the host JSON settings.
public sealed record Preferences
{
    public int PollSeconds{get;init;}=300;
    public int RetentionDays{get;init;}=30;
    public ModelSettings Model{get;init;}=new();
    public void Validate()
    {Model.Validate();if(PollSeconds is <60 or >3600||RetentionDays is <1 or >365)throw new RuntimeFault(RuntimeFailure.Configuration);}
}
public sealed record ConnectionDraft
{
    public string Profile{get;init;}="";
    public string ConversationId{get;init;}="";
    public DateTimeOffset Start{get;init;}=DateTimeOffset.UtcNow.AddHours(-3);
    public DateTimeOffset End{get;init;}=DateTimeOffset.UtcNow;
    public string Executable{get;init;}="";
    public string DestinationName{get;init;}="";
    public string Task{get;init;}="";
}
public sealed class PluginSession(InsightController controller,string dataDirectory,IDataCipher cipher,CanonicalStoreLocation location,
    Func<CancellationToken,Task<Preferences>> load,Func<Preferences,Task> save)
{
    readonly object sync=new();
    readonly HashSet<Task> pending=[];
    bool inactive;
    Task? retirement;
    readonly DateTimeOffset demoStart=DateTimeOffset.UtcNow.AddHours(-3);
    public InsightController Controller{get;}=controller;
    public int PendingPersistence{get{lock(sync)return pending.Count;}}
    public CanonicalStoreLocation Location{get;}=location;
    public Preferences Preferences{get;set;}=new();
    public ConnectionDraft Draft{get;set;}=new();
    public MonitoringRequest CaptureRequest(bool demo,bool upload)
    {
        var p=Preferences;var d=Draft;p.Validate();
        return new(demo?"demo":"dws",demo?"demo:local":d.Profile,demo?"demo-project":d.ConversationId,demo?demoStart:d.Start,
            demo?"":d.Executable,upload,p.Model,p.PollSeconds,p.RetentionDays);
    }
    public async Task SaveAsync()
    {
        var preferences=Preferences;var draft=Draft with{Task=Controller.Snapshot.Draft};preferences.Validate();
        await Owned(async()=>
        {
            new EncryptedDocument<ConnectionDraft>(Path.Combine(dataDirectory,"connection-draft.dpapi"),cipher,128*1024).Save(draft);
            await save(preferences);
        });
    }
    public async Task LoadAsync()
    {
        await Owned(async()=>
        {
        var result=await Task.Run(async()=>
        {
            var preferences=await load(CancellationToken.None);preferences.Validate();
            var draft=new EncryptedDocument<ConnectionDraft>(Path.Combine(dataDirectory,"connection-draft.dpapi"),cipher,128*1024).Read()??new();
            return(preferences,draft);
        });
        lock(sync)
        {
            if(inactive)return;
            Preferences=result.preferences;Draft=result.draft;Controller.SetDraft(Draft.Task);
        }
        });
    }
    public static Task Worker(Func<Task> action)=>Task.Run(action);
    public static Task<T> Worker<T>(Func<Task<T>> action)=>Task.Run(action);
    Task Owned(Func<Task> action)
    {
        lock(sync)
        {
            if(inactive)throw new RuntimeFault(RuntimeFailure.Inactive);
            var task=Task.Run(action);pending.Add(task);
            _=task.ContinueWith(completed=>{lock(sync)pending.Remove(completed);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return task;
        }
    }
    public Task DeactivateAsync()
    {
        lock(sync)
        {
            if(retirement is not null)return retirement;
            inactive=true;var controllerStop=Controller.DeactivateAsync();
            return retirement=Retire(controllerStop,pending.ToArray());
        }
    }
    static async Task Retire(Task controllerStop,Task[] owned)
    {
        // Persistence faults are shown by the originating action; retirement still awaits every operation.
        try{await Task.WhenAll(owned);}catch(Exception){}
        await controllerStop;
    }
}
/// <summary>A recreated page owns one subscription. Superseded or detached dispatcher callbacks are ignored.</summary>
public sealed class SnapshotBinding:IDisposable
{
    readonly IDisposable subscription;
    long revision;
    int disposed;
    public SnapshotBinding(InsightController controller,Func<Action,bool> enqueue,Action<RuntimeSnapshot> render)
    {
        subscription=controller.Subscribe(snapshot=>
        {
            var captured=Interlocked.Increment(ref revision);
            enqueue(()=>{if(Volatile.Read(ref disposed)==0&&Volatile.Read(ref revision)==captured)render(snapshot);});
        });
    }
    public void Dispose(){Interlocked.Exchange(ref disposed,1);Interlocked.Increment(ref revision);subscription.Dispose();}
}

/// <summary>Arbitrates local action feedback and dispatcher-delivered runtime notices.</summary>
public sealed class PageNotice
{
    string baseline="";
    public string Text{get;private set;}="";
    public string Origin{get;private set;}="runtime";
    public string BeginAction(string runtimeNotice){baseline=runtimeNotice;Text=runtimeNotice;Origin="runtime";return Text;}
    public string ShowLocal(string text,string runtimeNotice){baseline=runtimeNotice;Text=text;Origin="local";return Text;}
    public string ApplyRuntime(string runtimeNotice)
    {
        if(Origin=="local"&&runtimeNotice==baseline)return Text;
        baseline=runtimeNotice;Text=runtimeNotice;Origin="runtime";return Text;
    }
}
