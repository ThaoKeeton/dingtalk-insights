using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

/// <summary>Instance-owned work and view state. Creating/recreating a view never grants authority.</summary>
public sealed partial class InsightController:IAsyncDisposable
{
    readonly string dataDirectory,ownerContext;
    readonly IDataCipher cipher;
    readonly IRuntimeServices services;
    readonly IMonitorClock clock;
    readonly TimeProvider time;
    readonly CancellationToken lifetime;
    readonly CancellationTokenRegistration lifetimeRegistration;
    readonly SemaphoreSlim operations=new(1,1),retirement=new(1,1);
    readonly object sync=new();
    readonly HashSet<Operation> pending=[];
    readonly HashSet<Subscription> subscriptions=[];
    readonly AsyncLocal<bool> retirementWrite=new();
    readonly MessageBatch demo;
    long generation,dispatches,ownedStoreWrites;
    int stopCount;
    bool deactivated;
    Task deactivation=Task.CompletedTask;
    BusinessStore? store;
    InsightState state=new();
    MonitorSupervisor? supervisor;
    Task monitoringCompletion=Task.CompletedTask;
    string? canonicalRoot;
    CredentialVault? selectedVault;
    string selectedRevision="";
    CredentialUse allowedCredentials;
    RuntimeConnection connection=RuntimeConnection.Demo;
    string draft="",markdown="",notice="默认模拟模式；启用与导航不会读取聊天、上传或发送。";
    MessageBatch? candidates;
    AnalysisReport? report;
    DateTimeOffset? reportOldest;
    IReadOnlyList<Conversation> conversations=[];
    MonitoringConfiguration? configuration;
    RuntimeSnapshot snapshot=new();
    DateTimeOffset visibleRetentionReference;

    public InsightController(string dataDirectory,string ownerContext,IDataCipher cipher,IRuntimeServices? services=null,IMonitorClock? clock=null,CancellationToken lifetime=default)
    {
        if(!Path.IsPathFullyQualified(dataDirectory))throw new RuntimeFault(RuntimeFailure.Connection);
        Guard.Identifier(ownerContext,"插件所有者上下文无效。");
        this.dataDirectory=Path.GetFullPath(dataDirectory);this.ownerContext=Guard.Hash(ownerContext+"\n"+this.dataDirectory+"\n"+Guid.NewGuid());
        this.cipher=cipher;this.services=services??new RuntimeServices();this.clock=clock??new SystemMonitorClock();time=new ClockTime(this.clock);this.lifetime=lifetime;
        demo=Demo.CreateBatch(this.clock.UtcNow);
        lifetimeRegistration=lifetime.Register(static value=>((InsightController)value!).OnLifetimeCancelled(),this);
    }
    public RuntimeObservation Observation
    {
        get{lock(sync)return new(generation,pending.Count,subscriptions.Count,store is not null,supervisor is not null,
            supervisor?.Completion.IsCompleted??true,dispatches,ownedStoreWrites);}
    }
    public Task InitializeAsync(){EnsureActive();return Task.CompletedTask;}
    public Task Deactivation{get{lock(sync)return deactivation;}}
    /// <summary>The actual supervisor loop. Capture after a successful Start; Stop/Deactivate await its retirement.</summary>
    public Task MonitoringCompletion{get{lock(sync)return monitoringCompletion;}}
    public RuntimeSnapshot Snapshot
    {
        get
        {
            RuntimeSnapshot value;DateTimeOffset cutoff;
            lock(sync)
            {
                value=snapshot;
                if(clock.UtcNow>visibleRetentionReference)visibleRetentionReference=clock.UtcNow;
                if(value.RetentionReference>visibleRetentionReference)visibleRetentionReference=value.RetentionReference;
                cutoff=visibleRetentionReference.AddDays(-value.RetentionDays);
            }
            return value with{Candidates=Array.AsReadOnly(value.Candidates.Where(m=>m.CreatedAt>=cutoff).ToArray()),
                History=Array.AsReadOnly(value.History.Where(r=>r.CreatedAt>=cutoff&&r.OldestSourceAt is {} oldest&&oldest>=cutoff).ToArray()),
                Markdown=value.ReportOldestSourceAt is {} at&&at>=cutoff?value.Markdown:""};
        }
    }
    public void SetDraft(string text)
    {
        lock(sync){EnsureActive();if(text.Length>2000)throw new RuntimeFault(RuntimeFailure.Configuration);draft=text;previews.Clear();snapshot=snapshot with{Draft=draft};}
        Notify();
    }
    public IDisposable Subscribe(Action<RuntimeSnapshot> changed)
    {
        Subscription subscription;
        lock(sync){EnsureActive();subscription=new(this,changed);subscriptions.Add(subscription);}
        subscription.Invoke(Snapshot);return subscription;
    }
    public Task<bool> OpenDemoAsync(CancellationToken cancellation=default)=>Run(async operation=>
    {
        RequireStopped();if(connection==RuntimeConnection.Canonical)throw new RuntimeFault(RuntimeFailure.Connection);
        if(store is null)
        {
            var opened=new BusinessStore(Path.Combine(dataDirectory,"demo"),cipher,time);
            try{var loaded=opened.Load();Commit(operation,()=>{store=opened;state=loaded;connection=RuntimeConnection.Demo;});}
            catch{opened.Dispose();throw;}
        }
        await Task.CompletedTask;return true;
    },RuntimeFailure.Storage,cancellation);
    public Task<bool> ConnectAsync(string root,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(operation=>
        {
            RequireStopped();if(store is not null)throw new RuntimeFault(RuntimeFailure.Connection);
            root=CanonicalRoot(root);BusinessStore opened;
            try{opened=new(root,cipher,time);}
            catch(SafetyException){throw new RuntimeFault(RuntimeFailure.Ownership);}
            try
            {
                var loaded=opened.Load();BackgroundPolicy.Revoke(loaded);opened.Save(loaded);
                Commit(operation,()=>{store=opened;state=loaded;canonicalRoot=root;connection=RuntimeConnection.Canonical;ClearVisible();ClearCredentialUse();notice="已连接同一个规范账本；恢复权限已撤销，尚未读取来源或使用凭据。";});
                return Task.FromResult(true);
            }
            catch{opened.Dispose();throw;}
        },RuntimeFailure.Storage,cancellation);
    }
    public Task<bool> UseCredentialsAsync(CredentialLocation location,CredentialUse use,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(operation=>
        {
            RequireStopped();if(!Enum.IsDefined(location)||use==CredentialUse.None||(use&~(CredentialUse.Model|CredentialUse.Webhook))!=0)throw new RuntimeFault(RuntimeFailure.Credentials);
            if(location==CredentialLocation.ConnectedStore)RequireCanonical();
            var vault=new CredentialVault(Path.Combine(location==CredentialLocation.Plugin?dataDirectory:canonicalRoot!,"credentials.dpapi"),cipher);
            if(!vault.Exists)throw new RuntimeFault(RuntimeFailure.Credentials);var revision=vault.Revision;
            Commit(operation,()=>{previews.Clear();selectedVault=vault;selectedRevision=revision;allowedCredentials=use;});return Task.FromResult(true);
        },RuntimeFailure.Credentials,cancellation);
    }
    /// <summary>Replaces plugin-local values only. Does not grant use or modify the connected standalone vault.</summary>
    public Task<bool> SaveCredentialsAsync(Credentials values,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        return Run(operation=>
        {
            RequireStopped();if(values.OpenAiKey.Length>8192||values.WebhookUrl.Length>8192||values.WebhookSecret.Length>8192)throw new RuntimeFault(RuntimeFailure.Credentials);
            Commit(operation,()=>{new CredentialVault(Path.Combine(dataDirectory,"credentials.dpapi"),cipher).Save(values);ClearCredentialUse();});return Task.FromResult(true);
        },RuntimeFailure.Storage,cancellation);
    }
    public Task<bool> LoadWorkspaceAsync(CancellationToken cancellation=default)=>Run(operation=>
    {
        var document=Workspace().Read();if(document is not null)
        {
            if(document.Version!=1||document.Draft is null||document.Draft.Length>2000)throw new RuntimeFault(RuntimeFailure.Storage);
            Commit(operation,()=>{draft=document.Draft;previews.Clear();});
        }
        return Task.FromResult(true);
    },RuntimeFailure.Storage,cancellation);
    public Task<bool> SaveWorkspaceAsync(CancellationToken cancellation=default)
    {
        string captured;lock(sync)captured=draft;
        return Run(operation=>{Commit(operation,()=>Workspace().Save(new(1,captured)));return Task.FromResult(true);},RuntimeFailure.Storage,cancellation);
    }
    EncryptedDocument<WorkspaceDocument> Workspace()=>new(Path.Combine(dataDirectory,"workspace.dpapi"),cipher,64*1024);
    sealed record WorkspaceDocument(int Version,string Draft);
    string CredentialRevision(CredentialUse use)
    {
        var revision=BoundCredentialRevision(use);
        if(!selectedVault!.Exists||selectedVault.Revision!=selectedRevision)throw new RuntimeFault(RuntimeFailure.Credentials);
        return revision;
    }
    string BoundCredentialRevision(CredentialUse use)
    {
        if(selectedVault is null||(allowedCredentials&use)!=use)throw new RuntimeFault(RuntimeFailure.Credentials);
        return Guard.Hash(ownerContext+"\n"+canonicalRoot+"\n"+selectedRevision+"\n"+use);
    }
    Credentials PrivateCredentials(CredentialUse use)
    {
        var revision=CredentialRevision(use);var values=selectedVault!.Read();
        if(CredentialRevision(use)!=revision||use==CredentialUse.Model&&string.IsNullOrWhiteSpace(values.OpenAiKey)||use==CredentialUse.Webhook&&string.IsNullOrWhiteSpace(values.WebhookUrl))throw new RuntimeFault(RuntimeFailure.Credentials);
        return values;
    }
    void ClearCredentialUse(){previews.Clear();selectedVault=null;selectedRevision="";allowedCredentials=CredentialUse.None;}
    void RequireCanonical(){if(connection!=RuntimeConnection.Canonical||store is null||canonicalRoot is null)throw new RuntimeFault(RuntimeFailure.Connection);}
    void RequireStopped(){if(supervisor?.IsRunning==true)throw new RuntimeFault(RuntimeFailure.Busy);}
    void RequireStore(){if(store is null)throw new RuntimeFault(RuntimeFailure.Connection);}
    void ClearVisible(){previews.Clear();candidates=null;report=null;markdown="";reportOldest=null;conversations=[];draft="";configuration=null;}
    DateTimeOffset RetentionNow()
    {
        if(clock.UtcNow>visibleRetentionReference)visibleRetentionReference=clock.UtcNow;
        if(state.RetentionReference>visibleRetentionReference)visibleRetentionReference=state.RetentionReference;
        return visibleRetentionReference;
    }
    void EnforceRetention()
    {
        state.Trim(RetentionNow(),state.RetentionDays);
        if(candidates?.Messages.Any(m=>m.CreatedAt<state.RetentionCutoff)==true)candidates=null;
        if(reportOldest is {} oldest&&oldest<state.RetentionCutoff){report=null;markdown="";reportOldest=null;}
    }
    void SetReport(AnalysisReport value){previews.Clear();report=value;markdown=ReportRenderer.Markdown(value);reportOldest=value.Batch.Messages.Min(m=>m.CreatedAt);}
    void Publish()
    {
        lock(sync)
        {
            snapshot=new RuntimeSnapshot{Active=!deactivated,IsBusy=pending.Count>0,IsMonitoring=supervisor?.IsRunning==true,Connection=connection,
                Draft=draft,Candidates=Array.AsReadOnly(candidates?.Messages.ToArray()??[]),Markdown=markdown,ReportOldestSourceAt=reportOldest,
                History=Array.AsReadOnly(state.Reports.ToArray()),Conversations=Array.AsReadOnly(conversations.ToArray()),Health=state.Supervisor,
                Origin=state.Monitoring,Configuration=configuration,BudgetDay=state.BudgetDay,ReservedUsd=state.ReservedUsd,Requests=state.Requests,Notice=notice,RetentionDays=state.RetentionDays,RetentionReference=state.RetentionReference};
        }
        Notify();
    }
    void Notify()
    {
        Subscription[] observers;
        lock(sync){snapshot=snapshot with{Active=!deactivated,IsBusy=pending.Count>0};observers=subscriptions.ToArray();}
        var value=Snapshot;foreach(var observer in observers)observer.Invoke(value);
    }
    static string CanonicalRoot(string root)
    {
        if(!Path.IsPathFullyQualified(root))throw new RuntimeFault(RuntimeFailure.Connection);
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for(var directory=new DirectoryInfo(root);directory is not null;directory=directory.Parent)
            if(!directory.Exists||(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new RuntimeFault(RuntimeFailure.Connection);
        foreach(var name in new[]{"state.dpapi","state.dpapi.lock","credentials.dpapi","send-attempts.dpapi"})
        {
            var path=Path.Combine(root,name);
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new RuntimeFault(RuntimeFailure.Connection);
        }
        if(!File.Exists(Path.Combine(root,"state.dpapi")))throw new RuntimeFault(RuntimeFailure.Connection);
        return root;
    }
    sealed class ClockTime(IMonitorClock clock):TimeProvider{public override DateTimeOffset GetUtcNow()=>clock.UtcNow;}
    sealed class Subscription(InsightController owner,Action<RuntimeSnapshot> changed):IDisposable
    {
        int disposed;
        public void Invoke(RuntimeSnapshot value){if(Volatile.Read(ref disposed)==0)try{changed(value);}catch(Exception){}}
        public void Dispose(){Interlocked.Exchange(ref disposed,1);lock(owner.sync)owner.subscriptions.Remove(this);}
    }
}
