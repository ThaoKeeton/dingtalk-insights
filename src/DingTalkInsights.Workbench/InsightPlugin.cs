using System.Text.Json;
using DingTalkInsights.Runtime;
using Microsoft.UI.Xaml;
using Workbench.Sdk;
using Workbench.WinUI.Sdk;

namespace DingTalkInsights.Workbench;

public sealed class InsightPlugin:IWorkbenchPlugin
{
    PluginSession? session;
    WorkbenchMonitorTask? monitor;
    object? viewFailure;
    WeakReference<InsightPage>? currentPage;
    public InsightPlugin() { }
    public async Task InitializeAsync(IPluginContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(session is not null)throw new InvalidOperationException("Plugin instance already initialized.");
        if(context is not IPluginRuntimeContext runtime)throw new InvalidOperationException("Workbench host 0.2 runtime services are required.");
        var cipher=new DpapiCipher();
        var credentials=new WorkbenchCredentialProvider(runtime.Secrets);
        var controller=new InsightController(context.DataDirectory,context.PluginId,cipher,lifetime:context.Lifetime,hostCredentials:credentials);
        session=new(controller,context.DataDirectory,cipher,CanonicalStoreLocation.Production(),
            token=>context.Settings.GetAsync("preferences",new Preferences(),token),
            value=>context.Settings.SetAsync("preferences",value));
        try
        {
            monitor=new(session,runtime.BackgroundTasks,()=>runtime.StopReason,context.Lifetime);session.Monitoring=monitor;monitor.Register();
            credentials.Register();
            context.RegisterView(new WinUiView("workspace","钉钉沟通事项",CreateWorkspace));
            context.RegisterView(new WinUiView("settings","钉钉沟通事项",CreateSettings,WinUiViewKind.Settings));
            await controller.InitializeAsync();
        }
        catch{if(monitor is not null)await monitor.DeactivateAsync();else await session.DeactivateAsync();throw;}
    }
    FrameworkElement CreateWorkspace()=>CreatePage(false);
    FrameworkElement CreateSettings()=>CreatePage(true);
    FrameworkElement CreatePage(bool settings)
    {
        try{var page=new InsightPage(session??throw new InvalidOperationException("Inactive plugin."),settings);currentPage=new(page);return page;}
        catch(Exception error)
        {
            // The presenter catches factory exceptions. Keep only safe category/HRESULT diagnostics, never content or keys.
            try
            {
                viewFailure=new{View=settings?"settings":"workspace",ErrorType=error.GetType().FullName,HResult=$"0x{unchecked((uint)error.HResult):X8}",
                    Frames=new System.Diagnostics.StackTrace(error,true).GetFrames().Take(5).Select(frame=>new
                    {Type=frame.GetMethod()?.DeclaringType?.FullName,Method=frame.GetMethod()?.Name,File=Path.GetFileName(frame.GetFileName()),Line=frame.GetFileLineNumber()}).ToArray()};
            }
            catch(Exception){} // Diagnostic collection must never replace the original factory failure.
            throw;
        }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if(monitor is not null)await monitor.DeactivateAsync();else if(session is not null)await session.DeactivateAsync();
    }
    // Read-only product diagnostics. No content, credentials, provider bodies, filesystem or business work.
    public string ObservationJson=>JsonSerializer.Serialize(new
    {
        ViewFailure=viewFailure,
        RegisteredMonitor=monitor?.Observation,
        Page=currentPage is not null&&currentPage.TryGetTarget(out var page)?page.ActionObservation:null,
        Resources=session?.Controller.Observation,
        AdapterPendingPersistence=session?.PendingPersistence??0,
        Active=session?.Controller.Snapshot.Active??false,
        IsMonitoring=session?.Controller.Snapshot.IsMonitoring??false,
        Connection=session?.Controller.Snapshot.Connection.ToString(),
        Candidates=session?.Controller.Snapshot.Candidates.Count??0,
        ReportCharacters=session?.Controller.Snapshot.Markdown.Length??0,
        History=session?.Controller.Snapshot.History.Count??0,
        Health=session?.Controller.Snapshot.Health,
        BudgetDay=session?.Controller.Snapshot.BudgetDay,
        Requests=session?.Controller.Snapshot.Requests??0,
        ReservedUsd=session?.Controller.Snapshot.ReservedUsd??0
    });
    public Task MonitoringCompletion=>session?.Controller.MonitoringCompletion??Task.CompletedTask;
    public Task Retirement=>session?.Controller.Deactivation??Task.CompletedTask;
}
