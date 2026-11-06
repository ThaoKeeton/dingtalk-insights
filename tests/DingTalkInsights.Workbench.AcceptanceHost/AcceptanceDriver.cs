using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Workbench.Core;
using Workbench.WinUI.Sdk;

namespace Workbench.App;

internal static partial class AcceptanceDriver
{
    const string Id="com.projectworkbench.dingtalk-insights";
    const string Draft="Synthetic native acceptance draft: verify evidence before conclusions.";
    public static async Task Run(MainWindow window,RunOptions report,HarnessOptions options)
    {
        var checks=new List<string>();var observations=new List<object>();completedChecks=checks;CurrentStep="native-application-contract";
        void Record(string check)
        {checks.Add(check);report.WriteReport(new{succeeded=false,stage="running-"+options.Phase,checks,observations,report.StartupStages});}
        window.AssertUiThread();
        Require(SynchronizationContext.Current is Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext,"Generated WinUI dispatcher context absent.");
        Require(Application.Current is IXamlMetadataProvider metadata&&metadata.GetXamlType(typeof(NavigationView)) is not null,"Original compiled XAML metadata absent.");
        var pri=Path.Combine(AppContext.BaseDirectory,"DingTalkInsights.Workbench.AcceptanceHost.pri");
        Require(File.Exists(pri)&&new FileInfo(pri).Length>0,"Complete native host/main PRI missing.");
        Record("SDK-generated WinUI initialization, original App.xaml resources/metadata and nonempty main PRI");
        var manager=window.SmokeManager;
        var data=Path.Combine(options.UserRoot,"data",Id);
        var packageHash=Fingerprint(options.Package);
        object? plugin=null;
        if(options.Phase=="initial")
        {
            Require(manager.Plugins.Count==0,"Fresh root discovered unexpected packages.");
            CurrentStep="initial-plugin-install";
            Require((await manager.InstallAsync(options.Package)).Succeeded,"Actual PluginManager install failed.");
            Require(!(await manager.InstallAsync(options.Package)).Succeeded,"Duplicate actual install accepted.");
            await manager.DiscoverAsync();
            var installed=manager.Plugins.Single();
            Require(installed.Manifest?.Id==Id&&installed.State==PluginState.Disabled&&!installed.IsTrusted&&!installed.IsLoaded&&!installed.DesiredEnabled,"Install ran or trusted plugin.");
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="DingTalkInsights.Workbench"),"Plugin assembly loaded before trust.");
            Require(Fingerprint(installed.PackagePath)==packageHash,"Installed package bytes differ from approved source.");
            Require(!(await manager.EnableAsync(Id)).Succeeded&&!manager.Plugins.Single().IsLoaded,"Trust denial loaded plugin.");
            Record("Actual install disabled/untrusted/unloaded, exact package fingerprint, duplicate rejection and trust denial");
            await TrustThroughPresenter(window,manager,false,observations);
            Require(!manager.Plugins.Single().IsTrusted&&!manager.Plugins.Single().IsLoaded,"Real trust cancel activated plugin.");
            await TrustThroughPresenter(window,manager,true,observations);
            Require(manager.Plugins.Single().State==PluginState.Enabled,"Real trust confirmation did not enable plugin.");
            Record("Ordinary presenter Enable button and actual native trust dialog cancel/confirm");
            plugin=PluginTarget(manager);
            var registration=manager.GetBackgroundTaskSnapshots(Id).Single();
            Require(registration.Id=="monitor"&&registration.RequiresPluginStart&&!registration.ResumeOnStartup&&!registration.DesiredRunning,"Installed task metadata or startup policy wrong.");
            Require(manager.GetSecretDefinitions(Id).Select(s=>s.Id).Order().SequenceEqual(new[]{"dingtalk.webhook-secret","dingtalk.webhook-url","openai.api-key"}),"Installed three secret definitions missing.");
            Require(!(await manager.StartBackgroundTaskAsync(Id,"monitor")).Succeeded&&!(await manager.SetBackgroundTaskResumeOnStartupAsync(Id,"monitor",true)).Succeeded,"Generic host Start/resume bypassed plugin confirmation.");
            observations.Add(new{stage="actual-installed-runtime-services",registration,secretDefinitions=manager.GetSecretDefinitions(Id).Select(s=>s.Id),hostStartAndResumeRejected=true,secretValues="not read or written; local DPAPI choices retained"});
            Require(Resources(plugin).GetProperty("Dispatches").GetInt64()==0&&!Resources(plugin).GetProperty("StoreOwned").GetBoolean(),"Initialize performed business work.");
            Require(!Directory.EnumerateFiles(data,"*.dpapi",SearchOption.AllDirectories).Any(),"Initialize/navigation created business/credential ciphertext.");
            var page=await Workspace(window);
            Require(Find<CheckBox>(page,"DTI 使用模拟来源").IsChecked==true,"Fresh source is not demo.");
            var settings=await Settings(window);
            foreach(var name in new[]{"DTI 新 OpenAI key","DTI 新 Webhook URL","DTI 新 Webhook 签名密钥"})Require(Find<PasswordBox>(settings,name).Password.Length==0,"Existing credential appeared in input.");
            Find<TextBox>(settings,"DTI 轮询秒数").Text="60";Find<TextBox>(settings,"DTI 保留天数").Text="7";
            Find<TextBox>(settings,"DTI 整理事项").Text=Draft;
            Invoke(Find<Button>(settings,"DTI 保存设置"));
            await Wait(()=>Find<TextBlock>(settings,"DTI 操作结果").Text.Contains("配置已保存")&&PageState(plugin).GetProperty("Outcome").GetString()=="completed"&&PageState(plugin).GetProperty("SaveStage").GetString()=="completed","Native save did not complete.");
            var saved=ObserveSavedFixture(options.UserRoot);
            Require(saved.DraftExists&&saved.DraftBytes>0&&saved.PreferencesMatch,"Native Save did not persist the captured fixture preferences/encrypted draft.");
            Require(PageState(plugin).GetProperty("ActionStartedOnUi").GetBoolean()&&PageState(plugin).GetProperty("SaveResumedOnUi").GetBoolean(),"Native Save capture/completion left the UI dispatcher.");
            observations.Add(new{stage="native-settings-save-completed",page=PageState(plugin),persistence=saved});
            Require(File.Exists(Path.Combine(data,"connection-draft.dpapi")),"Native configuration not encrypted.");
            var json=Directory.EnumerateFiles(Path.Combine(options.UserRoot,"settings"),"*.json",SearchOption.AllDirectories).Select(File.ReadAllText);
            Require(json.All(t=>!t.Contains(Draft)&&!t.Contains("OpenAiKey")&&!t.Contains("WebhookUrl")),"Sensitive data entered host settings JSON.");
            await window.SmokeNavigateAsync("home");page=await Workspace(window);
            Require(Find<TextBox>(page,"DTI 整理事项").Text==Draft,"Draft lost on recreated native page.");
            Require(Resources(plugin).GetProperty("Subscriptions").GetInt32()==1,"Page recreation duplicated subscriptions.");
            Record("Native page/settings controls, empty PasswordBoxes, native edit/save, encrypted draft and navigation retention");
            Invoke(Find<Button>(page,"DTI 打开模拟"));await Wait(()=>Resources(plugin).GetProperty("StoreOwned").GetBoolean(),"Demo store did not open.");
            await ConfirmAction(window,page,"DTI 读取一次",false);
            Require(Resources(plugin).GetProperty("Dispatches").GetInt64()==0,"Cancelled read dispatched.");
            await ConfirmAction(window,page,"DTI 读取一次",true);
            await WaitActionRendered(plugin,"DTI 读取一次",()=>Observation(plugin).GetProperty("Candidates").GetInt32()==4
                &&Find<TextBox>(page,"DTI 来源候选").Text.Contains("demo-2"),"Real demo Core read did not populate/render native candidates.");
            Require(Find<TextBox>(page,"DTI 来源候选").Text.Contains("demo-2"),"Native source evidence not rendered.");
            Find<TextBox>(page,"DTI 搜索词").Text="修复";await ConfirmAction(window,page,"DTI 搜索",true);
            await Wait(()=>Observation(plugin).GetProperty("Candidates").GetInt32()>0&&Resources(plugin).GetProperty("PendingOperations").GetInt32()==0,"Local native search did not finish.");
            await ConfirmAction(window,page,"DTI 整理候选",true);
            await WaitActionRendered(plugin,"DTI 整理候选",()=>Observation(plugin).GetProperty("ReportCharacters").GetInt32()>0
                &&Find<TextBox>(page,"DTI 待核实结果").Text.Contains("待核实")&&Find<ComboBox>(page,"DTI 历史结果").Items.Count>0,"Real demo model validation/rendering failed.");
            Require(Find<TextBox>(page,"DTI 待核实结果").Text.Contains("待核实"),"Tentative evidence result missing.");
            Require(Find<ComboBox>(page,"DTI 历史结果").Items.Count>0,"Real Core history not displayed.");
            Record("Real native read cancel/confirm, Core demo collection/local search/evidence-backed analysis/history; no network model/sender");
            await Start(window,page,plugin);
            var actualLoop=Loop(plugin);Require(!actualLoop.IsCompleted,"Actual MonitorSupervisor task not alive.");
            var reads=Resources(plugin).GetProperty("Dispatches").GetInt64();
            await window.SmokeNavigateAsync("home");page=await Workspace(window);
            Require(ReferenceEquals(actualLoop,Loop(plugin)),"Navigation replaced actual collector.");
            Require(!Find<Button>(page,"DTI 开始监控").IsEnabled&&Resources(plugin).GetProperty("Subscriptions").GetInt32()==1,"Recreated page permits duplicate collector/subscriptions.");
            Invoke(Find<Button>(page,"DTI 停止监控"));await Wait(()=>Loop(plugin).IsCompleted&&!Observation(plugin).GetProperty("IsMonitoring").GetBoolean()&&Resources(plugin).GetProperty("PendingOperations").GetInt32()==0,"Stop did not await actual monitor.");
            Require(actualLoop.IsCompleted,"Original supervisor task did not retire at Stop barrier.");
            var stopped=Resources(plugin).Clone();var stateFile=Path.Combine(data,"demo","state.dpapi");var stateHash=Hash(stateFile);
            await Task.Delay(300);
            Require(Resources(plugin).GetProperty("Dispatches").GetInt64()==stopped.GetProperty("Dispatches").GetInt64()&&Resources(plugin).GetProperty("OwnedStoreWrites").GetInt64()==stopped.GetProperty("OwnedStoreWrites").GetInt64()&&Hash(stateFile)==stateHash,"Work or state write after Stop barrier.");
            observations.Add(new{stage="monitor-stop",initialDispatches=reads,resources=stopped,stateHash});
            Record("Production controller/MonitorSupervisor demo loop survives page recreation singly; Stop joins real task with zero later dispatch or state writes");
            await Start(window,page,plugin);
            var hostStoppedLoop=Loop(plugin);Require((await manager.StopBackgroundTaskAsync(Id,"monitor")).Succeeded,"Actual generic host task Stop failed.");
            Require(hostStoppedLoop.IsCompleted&&Resources(plugin).GetProperty("StoreOwned").GetBoolean()&&Observation(plugin).GetProperty("Active").GetBoolean(),"Host task Stop did not join loop/retain explicit connection.");
            await Wait(()=>manager.GetBackgroundTaskSnapshots(Id).Single().State==Workbench.Sdk.PluginBackgroundTaskState.Stopped,"Registered task did not finish host Stop.");
            await Start(window,page,plugin);await VerifyActiveStaging(window,options,plugin,observations);
            Record("Actual registered task metadata, rejected generic Start/resume, host Stop joins real loop while retaining connection; active update staging/cancel/restage leaves code and authority unchanged");
            var old=plugin;var activeTask=Loop(old);await DisableThroughPresenter(window,manager,observations);
            Require(activeTask.IsCompleted&&!(Observation(old).GetProperty("Active").GetBoolean()),"Disable left live monitor/controller.");
            var retired=Resources(old);Require(retired.GetProperty("Subscriptions").GetInt32()==0&&retired.GetProperty("PendingOperations").GetInt32()==0&&!retired.GetProperty("StoreOwned").GetBoolean(),"Disable retained subscriptions/operations/lease.");
            Require(manager.Contributions.Views.All(v=>v.PluginId!=Id),"Disable retained native registrations.");
            using(var lease=new FileStream(Path.Combine(data,"demo","state.dpapi.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){}
            var afterDisable=Fingerprint(data);await Task.Delay(200);Require(Fingerprint(data)==afterDisable,"Disabled plugin wrote state later.");
            observations.Add(new{stage="disabled",resources=retired});
            Record("Actual presenter active disable removes contributions and fully retires tasks/subscriptions/leases; assemblies remain loaded");
            await TrustThroughPresenter(window,manager,true,observations);plugin=PluginTarget(manager);
            Require(!Observation(plugin).GetProperty("IsMonitoring").GetBoolean()&&Resources(plugin).GetProperty("Dispatches").GetInt64()==0,"Re-enable restored business authority.");
            settings=await Settings(window);Invoke(Find<Button>(settings,"DTI 载入设置"));await Wait(()=>Find<TextBlock>(settings,"DTI 操作结果").Text.Contains("已载入配置"),"Re-enable load failed.");
            Require(Find<TextBox>(settings,"DTI 轮询秒数").Text=="60"&&Find<TextBox>(settings,"DTI 整理事项").Text==Draft,"Saved setting/draft roundtrip failed.");
            page=await Workspace(window);Invoke(Find<Button>(page,"DTI 打开模拟"));await Wait(()=>Resources(plugin).GetProperty("StoreOwned").GetBoolean(),"Re-enabled demo open failed.");
            await Start(window,page,plugin);
            Record("Re-enable creates fresh controller/default demo; explicit saved-config load and separate new monitor confirmation work");
            await VerifyPendingStopControls(window,options,observations,Record);
            await Workspace(window);
        }
        else if(options.Phase=="restart")
        {
            var restored=manager.Plugins.Single();Require(restored.Manifest?.Id==Id&&restored.State==PluginState.Enabled&&restored.IsTrusted&&restored.DesiredEnabled,"Host did not restore byte-identical enabled package.");
            plugin=PluginTarget(manager);var resources=Resources(plugin);
            Require(resources.GetProperty("Dispatches").GetInt64()==0&&!resources.GetProperty("StoreOwned").GetBoolean()&&!Observation(plugin).GetProperty("IsMonitoring").GetBoolean(),"Restart restored business authority.");
            var page=await Workspace(window);Require(Find<CheckBox>(page,"DTI 使用模拟来源").IsChecked==true,"Restart source not default demo.");
            var settings=await Settings(window);Invoke(Find<Button>(settings,"DTI 载入设置"));await Wait(()=>Find<TextBlock>(settings,"DTI 操作结果").Text.Contains("已载入配置"),"Restart settings load failed.");
            Require(Find<TextBox>(settings,"DTI 轮询秒数").Text=="60"&&Find<TextBox>(settings,"DTI 整理事项").Text==Draft,"Cross-process native settings/draft lost.");
            Require(Resources(plugin).GetProperty("Dispatches").GetInt64()==0,"Loading restart config dispatched business work.");
            observations.Add(new{stage="safe-restart",resources=Resources(plugin)});
            Record("New actual process restores enabled/trusted plugin safely; default demo/no business authority; native saved preferences/draft roundtrip");
        }
        else if(options.Phase is "maintained" or "rolledback")
        {
            var fresh=manager.Plugins.Single();Require(fresh.Manifest?.Id==Id&&fresh.State==PluginState.Disabled&&!fresh.IsLoaded&&!fresh.IsTrusted&&!fresh.DesiredEnabled,"SDK install/rollback inherited trust or enabled authority.");
            Require(!(await manager.EnableAsync(Id)).Succeeded,"SDK install/rollback enabled without fresh trust.");
            Require(Fingerprint(fresh.PackagePath)==packageHash&&File.Exists(Path.Combine(data,"connection-draft.dpapi")),"SDK install/rollback lost code/current data.");
            Require(manager.GetBackgroundTaskSnapshots(Id).Count==0&&!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="DingTalkInsights.Workbench"),"SDK maintenance auto-loaded plugin/collector.");
            Record("Actual fresh-process SDK "+options.Phase+" code is disabled/untrusted/unloaded; existing encrypted data retained; fresh trust still required");
            if(options.Phase=="maintained")
            {
                var store=PackageStore(options);Require((await store.StageRemoveAsync(Id)).Succeeded&&Directory.Exists(fresh.PackagePath),"Real staged removal changed current code.");
                Require((await store.ApplyPendingAsync()).All(result=>!result.Succeeded)&&store.PendingOperations.Single().Kind==PackageOperationKind.Remove,"Discovered process applied staged removal.");
                Record("Actual SDK StageRemove retains code/current data until real exit; Apply refused after discovery; fresh guarded helper required");
            }
        }
        else if(options.Phase=="changed")
        {
            var changed=manager.Plugins.Single();Require(changed.Manifest?.Id==Id&&!changed.IsTrusted&&!changed.IsLoaded&&changed.State!=PluginState.Enabled,"Changed package inherited trust/load.");
            Require(!(await manager.EnableAsync(Id)).Succeeded,"Changed package enabled without fresh trust.");
            await TrustThroughPresenter(window,manager,false,observations);
            Require(!manager.Plugins.Single().IsLoaded,"Changed-package cancel loaded assembly.");
            Record("Actual discover/restore fingerprint change rejects old approval; enable denied and fresh real dialog cancelled");
        }
        else
        {
            Require(manager.Plugins.Count==0,"Offline removed package still discovered.");Require(Directory.Exists(data)&&File.Exists(Path.Combine(data,"connection-draft.dpapi")),"Offline package removal deleted user data.");
            var home=await window.SmokeNavigateAsync("plugins");Require(!Descendants<Button>(home).Any(b=>AutomationProperties.GetName(b)=="启用 钉钉沟通事项"),"Removed package has native action.");
            Record(options.Phase=="removed"?"Actual SDK staged removal applied by a fresh lease-owning helper before discovery; absent relaunch retains current encrypted data/settings/history; no purge":"After verified prior exit, driver-only offline package removal relaunches absent while encrypted data remain; separate legacy regression, not SDK uninstall");
        }
        Require(Fingerprint(options.Package)==packageHash,"Source package mutated during approval/load.");
        CurrentStep="actual-window-close";
        RequestClose(window,report,options,plugin,checks,observations,packageHash);
    }
    static object PluginTarget(PluginManager manager)=>((WinUiView)manager.Contributions.Views.Single(v=>v.PluginId==Id&&v.View.Id=="workspace").View).CreateView.Target??throw new OwnedAssertionException("Factory has no plugin instance target.");
    static JsonElement Observation(object plugin)=>JsonDocument.Parse((string)plugin.GetType().GetProperty("ObservationJson")!.GetValue(plugin)!).RootElement.Clone();
    static JsonElement Resources(object plugin)=>Observation(plugin).GetProperty("Resources");
    static Task Loop(object plugin)=>(Task)plugin.GetType().GetProperty("MonitoringCompletion")!.GetValue(plugin)!;
    static JsonElement PageState(object plugin)=>Observation(plugin).GetProperty("Page");
    static Task WaitActionRendered(object plugin,string action,Func<bool> rendered,string failure)=>Wait(()=>
    {
        var state=PageState(plugin);
        return state.GetProperty("Ready").GetBoolean()&&state.GetProperty("LastAction").GetString()==action
            &&state.GetProperty("Outcome").GetString()=="completed"&&!state.GetProperty("Busy").GetBoolean()&&rendered();
    },failure);
    static Task WaitPageReady(PluginManager manager)=>Wait(()=>PageState(PluginTarget(manager)).GetProperty("Ready").GetBoolean(),
        "Plugin page did not complete its own Loaded subscription and initial snapshot render.");
    static async Task<FrameworkElement> Workspace(MainWindow window)
    {
        CurrentStep="workspace-native-controls";
        var page=await window.SmokeNavigateAsync("view",Id,"workspace");Require(AutomationProperties.GetName(page)=="DTI 原生工作区","Native workspace factory returned error/unexpected page.");
        Find<TextBox>(page,"DTI 整理事项");Find<Button>(page,"DTI 开始监控");await WaitPageReady(window.SmokeManager);return page;
    }
    static async Task<FrameworkElement> Settings(MainWindow window)
    {
        CurrentStep="settings-native-controls";
        var page=await window.SmokeNavigateAsync("view",Id,"settings");Require(AutomationProperties.GetName(page)=="DTI 原生设置","Native settings factory returned error/unexpected page.");
        Find<TextBox>(page,"DTI 身份 corpId:userId");Find<Button>(page,"DTI 保存设置");await WaitPageReady(window.SmokeManager);return page;
    }
    static async Task Start(MainWindow window,FrameworkElement page,object plugin)
    {
        await ConfirmAction(window,page,"DTI 开始监控",true);
        await WaitActionRendered(plugin,"DTI 开始监控",()=>Observation(plugin).GetProperty("IsMonitoring").GetBoolean()&&!Loop(plugin).IsCompleted&&Observation(plugin).GetProperty("Health").GetProperty("NextReadAt").ValueKind==JsonValueKind.String,"Registered production demo supervisor did not acknowledge actual start/scheduled wait.");
    }
    static bool PresenterEnabled(MainWindow window)=>Find<NavigationView>((FrameworkElement)window.Content,"工作台导航").IsEnabled;
    static Task WaitPresenterReady(MainWindow window)=>Wait(()=>!OpenDialogs(window).Any()&&PresenterEnabled(window),
        "Presenter action did not finish restoring native navigation/content enablement.");
    static Task WaitAttachedEnabled(MainWindow window,Button target)=>Wait(()=>target.IsLoaded&&target.XamlRoot is not null&&target.IsEnabled&&PresenterEnabled(window)&&!OpenDialogs(window).Any(),
        "Presenter target did not become attached, loaded and effectively enabled.");
    static async Task TrustThroughPresenter(MainWindow window,PluginManager manager,bool approve,List<object> observations)
    {
        CurrentStep=approve?"presenter-enable-confirm":"presenter-enable-cancel";
        await WaitPresenterReady(window);
        var page=await window.SmokeNavigateAsync("plugins");var enable=Find<Button>(page,"启用 钉钉沟通事项");
        await WaitAttachedEnabled(window,enable);var wasTrusted=manager.Plugins.Single().IsTrusted;
        Require(approve||!wasTrusted,"Real cancel test requires an untrusted package.");Invoke(enable);
        if(!wasTrusted)await Dialog(window,approve,"信任并启用");
        var navigationEnabledBeforeCompletionWait=PresenterEnabled(window);
        // Popup dismissal can precede ShowAsync/RunAsync completion. Settings visibility is not an idle signal.
        CurrentStep=approve?"presenter-enable-completion":"presenter-cancel-completion";
        if(approve)await Wait(()=>manager.Plugins.Single().State==PluginState.Enabled,"Real presenter enable failed.");
        await WaitPresenterReady(window);
        if(!approve)
        {
            var cancelled=manager.Plugins.Single();
            Require(cancelled.State==PluginState.Disabled&&!cancelled.IsTrusted&&!cancelled.IsLoaded&&!cancelled.DesiredEnabled,
                "Completed real trust cancel changed package trust/load/enabled intent.");
        }
        var fresh=await window.SmokeNavigateAsync("plugins");
        var target=Find<Button>(fresh,approve?"停用 钉钉沟通事项":"启用 钉钉沟通事项");await WaitAttachedEnabled(window,target);
        var freshPage=!ReferenceEquals(page,fresh);var freshTarget=!ReferenceEquals(enable,target);
        Require(freshPage&&freshTarget,"Presenter completion reused the previous page/action control.");
        observations.Add(new{stage=approve?"presenter-enable-complete":"presenter-cancel-complete",trustDialogShown=!wasTrusted,navigationEnabledBeforeCompletionWait,
            navigationEnabledAfterCompletion=PresenterEnabled(window),popupClosed=!OpenDialogs(window).Any(),freshPage,freshTarget,
            targetLoaded=target.IsLoaded,targetAttached=target.XamlRoot is not null,targetEffectivelyEnabled=target.IsEnabled,
            state=manager.Plugins.Single().State.ToString(),trusted=manager.Plugins.Single().IsTrusted,loaded=manager.Plugins.Single().IsLoaded});
    }
    static async Task DisableThroughPresenter(MainWindow window,PluginManager manager,List<object> observations)
    {
        CurrentStep="presenter-disable";await WaitPresenterReady(window);
        var page=await window.SmokeNavigateAsync("plugins");var disable=Find<Button>(page,"停用 钉钉沟通事项");await WaitAttachedEnabled(window,disable);Invoke(disable);
        await Wait(()=>manager.Plugins.Single().State==PluginState.Disabled&&manager.Contributions.Views.All(v=>v.PluginId!=Id),"Real presenter disable failed.");
        await WaitPresenterReady(window);var fresh=await window.SmokeNavigateAsync("plugins");var enable=Find<Button>(fresh,"启用 钉钉沟通事项");await WaitAttachedEnabled(window,enable);
        var freshPage=!ReferenceEquals(page,fresh);var freshTarget=!ReferenceEquals(disable,enable);
        Require(freshPage&&freshTarget,"Disable completion reused the previous action control.");
        observations.Add(new{stage="presenter-disable-complete",navigationEnabled=PresenterEnabled(window),popupClosed=!OpenDialogs(window).Any(),
            freshPage,freshTarget,targetLoaded=enable.IsLoaded,targetAttached=enable.XamlRoot is not null,targetEffectivelyEnabled=enable.IsEnabled,
            state=manager.Plugins.Single().State.ToString(),remainingViews=manager.Contributions.Views.Count(v=>v.PluginId==Id)});
    }
    static async Task ConfirmAction(MainWindow window,FrameworkElement page,string name,bool approve)
    {
        CurrentStep=name switch{"DTI 开始监控"=>"native-monitor-start","DTI 读取一次"=>"native-read-once","DTI 搜索"=>"native-search","DTI 整理候选"=>"native-analysis",_=>"native-confirmed-action"};
        await Wait(()=>
        {
            var state=JsonSerializer.SerializeToElement(page.GetType().GetProperty("ActionObservation")!.GetValue(page));
            return page.IsLoaded&&page.XamlRoot is not null&&state.GetProperty("Ready").GetBoolean()&&!state.GetProperty("Busy").GetBoolean()&&Find<Button>(page,name).IsEnabled;
        },"Current native page/action did not become ready and idle.");
        Invoke(Find<Button>(page,name));await Dialog(window,approve,"确认本次操作");
    }
    static async Task Dialog(MainWindow window,bool approve,string primary)
    {
        await Wait(()=>OpenDialogs(window).Any(),"Actual ContentDialog did not open.");var dialog=OpenDialogs(window).Single();
        await Wait(()=>Descendants<Button>(dialog).Any(b=>Equals(b.Content,approve?primary:"取消")),"Dialog native button did not load.");
        Invoke(Descendants<Button>(dialog).Single(b=>Equals(b.Content,approve?primary:"取消")));
        await Wait(()=>!OpenDialogs(window).Any(),"Actual ContentDialog did not close.");
    }
    static IEnumerable<ContentDialog> OpenDialogs(MainWindow window)=>VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot)
        .SelectMany(p=>Descendants<ContentDialog>(p.Child));
    static T Find<T>(DependencyObject root,string name)where T:DependencyObject=>Descendants<T>(root).SingleOrDefault(v=>AutomationProperties.GetName(v)==name)??throw new OwnedAssertionException("Missing actual native control: "+name);
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject
    {
        if(root is T value)yield return value;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    static void Invoke(Button button)
    {
        Require(button.IsEnabled,"Native button disabled: "+AutomationProperties.GetName(button));
        var peer=new ButtonAutomationPeer(button);var invoke=peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider??throw new OwnedAssertionException("Native button has no Invoke provider.");invoke.Invoke();
    }
    static async Task Wait(Func<bool> predicate,string error)
    {var watch=Stopwatch.StartNew();while(!predicate()){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new OwnedAssertionException(error);await Task.Delay(25);}}
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static string Fingerprint(string root)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).OrderBy(p=>Path.GetRelativePath(root,p).Replace('\\','/'),StringComparer.Ordinal))
        {HarnessOptions.RejectLinks(path);hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root,path).Replace('\\','/')));hash.AppendData([0]);hash.AppendData(File.ReadAllBytes(path));hash.AppendData([0]);}
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    static void Require(bool value,string reason){if(!value)throw new OwnedAssertionException(reason);}
}
