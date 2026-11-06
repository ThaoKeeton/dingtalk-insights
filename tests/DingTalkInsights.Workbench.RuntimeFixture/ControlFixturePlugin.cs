using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using DingTalkInsights.Workbench;
using Workbench.Sdk;

namespace DingTalkInsights.Workbench.RuntimeFixture;

// Test-only activation fixture. Production bridge/controller/Core; only source IO is held or synthesized.
public sealed class ControlFixturePlugin:IWorkbenchPlugin
{
    ControlView? view;
    public Task InitializeAsync(IPluginContext context,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var runtime=(IPluginRuntimeContext)context;
        view=new(runtime);view.Bridge.Register();context.RegisterView(view);
        return view.Controller.InitializeAsync();
    }
    public Task StopAsync(CancellationToken cancellation)=>view?.Bridge.DeactivateAsync()??Task.CompletedTask;
}
public sealed class ControlView:IPluginView
{
    public string Id=>"control";public string Title=>"Synthetic production bridge control";
    public IPluginRuntimeContext Context{get;}
    public ControlledSource Source{get;}=new();public ControlCipher Cipher{get;}=new();
    public InsightController Controller{get;}
    public PluginSession Session{get;}
    public WorkbenchMonitorTask Bridge{get;}
    public ControlView(IPluginRuntimeContext context)
    {
        Context=context;var credentials=new WorkbenchCredentialProvider(context.Secrets);credentials.Register();
        Controller=new(context.DataDirectory,context.PluginId,Cipher,Source,lifetime:context.Lifetime,hostCredentials:credentials);
        Session=new(Controller,context.DataDirectory,Cipher,new(()=>throw new InvalidOperationException("Fixture path requires explicit test selection.")),
            token=>context.Settings.GetAsync("preferences",new Preferences(),token),value=>context.Settings.SetAsync("preferences",value));
        Bridge=new(Session,context.BackgroundTasks,()=>context.StopReason,context.Lifetime);Session.Monitoring=Bridge;
    }
}
public sealed class ControlCipher:IDataCipher
{
    readonly DpapiCipher native=new();readonly List<MonitorStatus> statuses=[];
    public string Kind=>OperatingSystem.IsWindows()?"actual Windows DPAPI fixture":"portable synthetic cipher fixture";
    public IReadOnlyList<MonitorStatus> Statuses{get{lock(statuses)return statuses.ToArray();}}
    public byte[] Protect(byte[] bytes)
    {
        using(var json=JsonDocument.Parse(bytes))if(json.RootElement.TryGetProperty("Supervisor",out var health))
            lock(statuses)statuses.Add((MonitorStatus)health.GetProperty("Status").GetInt32());
        return OperatingSystem.IsWindows()?native.Protect(bytes):[71,14,..bytes];
    }
    public byte[] Unprotect(byte[] bytes)=>OperatingSystem.IsWindows()?native.Unprotect(bytes):bytes[2..];
}
public sealed class ControlledSource:IRuntimeServices
{
    int held,inspections,reads;
    public bool? HoldReadiness{get;set;}
    public int Inspections=>Volatile.Read(ref inspections);public int Reads=>Volatile.Read(ref reads);
    public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task Hold(CancellationToken cancellation)
    {using var registration=cancellation.Register(()=>Cancelled.TrySetResult());Entered.TrySetResult();await Release.Task;}
    public async Task<string> InspectSourceAsync(string executable,string profile,CancellationToken cancellation)
    {
        Interlocked.Increment(ref inspections);if(HoldReadiness==true&&Interlocked.CompareExchange(ref held,1,0)==0)await Hold(cancellation);
        return Guard.Hash("synthetic-controlled-DWS");
    }
    public async Task<MessageBatch> ReadAsync(string executable,QueryScope scope,CancellationToken cancellation)
    {
        Interlocked.Increment(ref reads);if(HoldReadiness==false&&Interlocked.CompareExchange(ref held,1,0)==0)await Hold(cancellation);
        return new(scope,[new("fixture-1",scope.ConversationId,"fixture-user","Synthetic sender","Synthetic pending-stop evidence",scope.Start+(scope.End-scope.Start)/2)],true,[],new Dictionary<string,JsonElement>());
    }
    public Task<MessageBatch> SearchAsync(string executable,QueryScope scope,string keywords,CancellationToken cancellation)=>throw new InvalidOperationException("Unexpected fixture search.");
    public Task<IReadOnlyList<Conversation>> DiscoverAsync(string executable,string profile,DateTimeOffset start,DateTimeOffset end,CancellationToken cancellation)=>throw new InvalidOperationException("Unexpected fixture discovery.");
    public Task<ModelResult> AnalyzeAsync(string key,MessageBatch batch,string task,ModelSettings model,CancellationToken cancellation)=>throw new InvalidOperationException("Unexpected fixture model.");
    public Task<DeliveryStatus> SendAsync(WebhookTarget target,string text,CancellationToken cancellation)=>throw new InvalidOperationException("Unexpected fixture sender.");
}
