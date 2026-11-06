using System.Text.Json;
using DingTalkInsights.Runtime;
using Microsoft.UI.Xaml;
using Workbench.Sdk;
using Workbench.WinUI.Sdk;

namespace DingTalkInsights.Workbench;

public sealed class InsightPlugin:IWorkbenchPlugin
{
    PluginSession? session;
    public InsightPlugin() { }
    public Task InitializeAsync(IPluginContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(session is not null)throw new InvalidOperationException("Plugin instance already initialized.");
        var cipher=new DpapiCipher();
        var controller=new InsightController(context.DataDirectory,context.PluginId,cipher,lifetime:context.Lifetime);
        session=new(controller,context.DataDirectory,cipher,CanonicalStoreLocation.Production(),
            token=>context.Settings.GetAsync("preferences",new Preferences(),token),
            value=>context.Settings.SetAsync("preferences",value));
        context.RegisterView(new WinUiView("workspace","钉钉沟通事项",CreateWorkspace));
        context.RegisterView(new WinUiView("settings","钉钉沟通事项",CreateSettings,WinUiViewKind.Settings));
        return controller.InitializeAsync();
    }
    FrameworkElement CreateWorkspace()=>new InsightPage(session??throw new InvalidOperationException("Inactive plugin."),false);
    FrameworkElement CreateSettings()=>new InsightPage(session??throw new InvalidOperationException("Inactive plugin."),true);
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // API1 has no StopReason. Never infer business recovery authority from host shutdown/disable.
        if(session is not null)await session.DeactivateAsync();
    }
    // Read-only product diagnostics. No content, credentials, provider bodies, filesystem or business work.
    public string ObservationJson=>JsonSerializer.Serialize(new
    {
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
