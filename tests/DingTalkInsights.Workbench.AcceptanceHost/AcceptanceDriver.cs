using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    [DllImport("user32.dll",SetLastError=true)]static extern bool PostMessage(nint hwnd,uint message,nuint wParam,nint lParam);
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
            await TrustThroughPresenter(window,manager,false);
            Require(!manager.Plugins.Single().IsTrusted&&!manager.Plugins.Single().IsLoaded,"Real trust cancel activated plugin.");
            await TrustThroughPresenter(window,manager,true);
            Require(manager.Plugins.Single().State==PluginState.Enabled,"Real trust confirmation did not enable plugin.");
            Record("Ordinary presenter Enable button and actual native trust dialog cancel/confirm");
            plugin=PluginTarget(manager);
            Require(Resources(plugin).GetProperty("Dispatches").GetInt64()==0&&!Resources(plugin).GetProperty("StoreOwned").GetBoolean(),"Initialize performed business work.");
            Require(!Directory.EnumerateFiles(data,"*.dpapi",SearchOption.AllDirectories).Any(),"Initialize/navigation created business/credential ciphertext.");
            var page=await Workspace(window);
            Require(Find<CheckBox>(page,"DTI 使用模拟来源").IsChecked==true,"Fresh source is not demo.");
            var settings=await Settings(window);
            foreach(var name in new[]{"DTI 新 OpenAI key","DTI 新 Webhook URL","DTI 新 Webhook 签名密钥"})Require(Find<PasswordBox>(settings,name).Password.Length==0,"Existing credential appeared in input.");
            Find<TextBox>(settings,"DTI 轮询秒数").Text="60";Find<TextBox>(settings,"DTI 保留天数").Text="7";
            Find<TextBox>(settings,"DTI 整理事项").Text=Draft;
            Invoke(Find<Button>(settings,"DTI 保存设置"));
            await Wait(()=>Find<TextBlock>(settings,"DTI 操作结果").Text.Contains("配置已保存"),"Native save did not complete.");
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
            await Wait(()=>Observation(plugin).GetProperty("Candidates").GetInt32()==4,"Real demo Core read did not populate native candidates.");
            Require(Find<TextBox>(page,"DTI 来源候选").Text.Contains("demo-2"),"Native source evidence not rendered.");
            Find<TextBox>(page,"DTI 搜索词").Text="修复";await ConfirmAction(window,page,"DTI 搜索",true);
            await Wait(()=>Observation(plugin).GetProperty("Candidates").GetInt32()>0&&Resources(plugin).GetProperty("PendingOperations").GetInt32()==0,"Local native search did not finish.");
            await ConfirmAction(window,page,"DTI 整理候选",true);
            await Wait(()=>Observation(plugin).GetProperty("ReportCharacters").GetInt32()>0,"Real demo model validation/rendering failed.");
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
            var old=plugin;var activeTask=Loop(old);await DisableThroughPresenter(window,manager);
            Require(activeTask.IsCompleted&&!(Observation(old).GetProperty("Active").GetBoolean()),"Disable left live monitor/controller.");
            var retired=Resources(old);Require(retired.GetProperty("Subscriptions").GetInt32()==0&&retired.GetProperty("PendingOperations").GetInt32()==0&&!retired.GetProperty("StoreOwned").GetBoolean(),"Disable retained subscriptions/operations/lease.");
            Require(manager.Contributions.Views.All(v=>v.PluginId!=Id),"Disable retained native registrations.");
            using(var lease=new FileStream(Path.Combine(data,"demo","state.dpapi.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){}
            var afterDisable=Fingerprint(data);await Task.Delay(200);Require(Fingerprint(data)==afterDisable,"Disabled plugin wrote state later.");
            observations.Add(new{stage="disabled",resources=retired});
            Record("Actual presenter active disable removes contributions and fully retires tasks/subscriptions/leases; assemblies remain loaded");
            await TrustThroughPresenter(window,manager,true);plugin=PluginTarget(manager);
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
        else if(options.Phase=="changed")
        {
            var changed=manager.Plugins.Single();Require(changed.Manifest?.Id==Id&&!changed.IsTrusted&&!changed.IsLoaded&&changed.State!=PluginState.Enabled,"Changed package inherited trust/load.");
            Require(!(await manager.EnableAsync(Id)).Succeeded,"Changed package enabled without fresh trust.");
            await TrustThroughPresenter(window,manager,false);
            Require(!manager.Plugins.Single().IsLoaded,"Changed-package cancel loaded assembly.");
            Record("Actual discover/restore fingerprint change rejects old approval; enable denied and fresh real dialog cancelled");
        }
        else
        {
            Require(manager.Plugins.Count==0,"Offline removed package still discovered.");Require(Directory.Exists(data)&&File.Exists(Path.Combine(data,"connection-draft.dpapi")),"Offline package removal deleted user data.");
            var home=await window.SmokeNavigateAsync("plugins");Require(!Descendants<Button>(home).Any(b=>AutomationProperties.GetName(b)=="启用 钉钉沟通事项"),"Removed package has native action.");
            Record("After verified prior exit, driver-only offline package removal relaunches absent while encrypted data remain; SDK has no uninstall API");
        }
        Require(Fingerprint(options.Package)==packageHash,"Source package mutated during approval/load.");
        CurrentStep="actual-window-close";
        var closingLoop=plugin is null?Task.CompletedTask:Loop(plugin);
        var closingObserved=false;
        window.AppWindow.Closing+=(_,_)=>closingObserved=true;
        window.Closed+=(_,_)=>
        {
            try
            {
                Require(closingObserved,"Actual AppWindow close event not observed.");
                if(plugin is not null)
                {
                    var final=Resources(plugin);Require(closingLoop.IsCompleted&&Observation(plugin).GetProperty("AdapterPendingPersistence").GetInt32()==0&&final.GetProperty("PendingOperations").GetInt32()==0&&final.GetProperty("Subscriptions").GetInt32()==0&&!final.GetProperty("StoreOwned").GetBoolean(),"Real close left plugin work/resources.");
                    observations.Add(new{stage="actual-window-closed",resources=final});
                }
                checks.Add("Actual WM_CLOSE/AppWindow.Closing and Window.Closed after full plugin retirement; launcher must still verify process exit0");
                report.WriteReport(new{succeeded=true,stage="closed-awaiting-launcher-exit-validation",phase=options.Phase,evidenceKind="source-linked-unmodified-presenter-compatibility",businessBoundary="production-demo-Core-MonitorSupervisor; no real DWS/account/model/Webhook",options.RunId,packageSha256=packageHash,platformCommit="6565180b17130f6d494978291df4138de3fdb0b4",productBuild=JsonDocument.Parse(File.ReadAllText(Path.Combine(options.Package,"build-info.json"))).RootElement.Clone(),os=RuntimeInformation.OSDescription,osBuild=Environment.OSVersion.Version.ToString(),sessionId=Process.GetCurrentProcess().SessionId,processId=Environment.ProcessId,uiThreadId=Environment.CurrentManagedThreadId,checks,observations,report.StartupStages,completedUtc=DateTimeOffset.UtcNow});
            }
            catch(Exception error){Environment.ExitCode=1;WriteFailure(report,"actual-window-closed",error,window);}
        };
        if(!PostMessage(WinRT.Interop.WindowNative.GetWindowHandle(window),0x0010,0,0))throw new IOException("Could not post actual owned-window WM_CLOSE.");
    }
    static object PluginTarget(PluginManager manager)=>((WinUiView)manager.Contributions.Views.Single(v=>v.PluginId==Id&&v.View.Id=="workspace").View).CreateView.Target??throw new OwnedAssertionException("Factory has no plugin instance target.");
    static JsonElement Observation(object plugin)=>JsonDocument.Parse((string)plugin.GetType().GetProperty("ObservationJson")!.GetValue(plugin)!).RootElement.Clone();
    static JsonElement Resources(object plugin)=>Observation(plugin).GetProperty("Resources");
    static Task Loop(object plugin)=>(Task)plugin.GetType().GetProperty("MonitoringCompletion")!.GetValue(plugin)!;
    static async Task<FrameworkElement> Workspace(MainWindow window)
    {
        CurrentStep="workspace-native-controls";
        var page=await window.SmokeNavigateAsync("view",Id,"workspace");Require(AutomationProperties.GetName(page)=="DTI 原生工作区","Native workspace factory returned error/unexpected page.");
        Find<TextBox>(page,"DTI 整理事项");Find<Button>(page,"DTI 开始监控");return page;
    }
    static async Task<FrameworkElement> Settings(MainWindow window)
    {
        CurrentStep="settings-native-controls";
        var page=await window.SmokeNavigateAsync("view",Id,"settings");Require(AutomationProperties.GetName(page)=="DTI 原生设置","Native settings factory returned error/unexpected page.");
        Find<TextBox>(page,"DTI 身份 corpId:userId");Find<Button>(page,"DTI 保存设置");return page;
    }
    static async Task Start(MainWindow window,FrameworkElement page,object plugin)
    {
        await ConfirmAction(window,page,"DTI 开始监控",true);
        await Wait(()=>Observation(plugin).GetProperty("IsMonitoring").GetBoolean()&&!Loop(plugin).IsCompleted&&Observation(plugin).GetProperty("Health").GetProperty("NextReadAt").ValueKind==JsonValueKind.String,"Production demo supervisor did not reach actual scheduled wait.");
    }
    static async Task TrustThroughPresenter(MainWindow window,PluginManager manager,bool approve)
    {
        CurrentStep=approve?"presenter-enable-confirm":"presenter-enable-cancel";
        var page=await window.SmokeNavigateAsync("plugins");Invoke(Find<Button>(page,"启用 钉钉沟通事项"));
        if(!manager.Plugins.Single().IsTrusted)await Dialog(window,approve,"信任并启用");
        await Wait(()=>!OpenDialogs(window).Any()&&window.SmokeNavigationAvailable,"Trust presenter did not settle.");
        if(approve)await Wait(()=>manager.Plugins.Single().State==PluginState.Enabled,"Real presenter enable failed.");
    }
    static async Task DisableThroughPresenter(MainWindow window,PluginManager manager)
    {
        var page=await window.SmokeNavigateAsync("plugins");Invoke(Find<Button>(page,"停用 钉钉沟通事项"));
        await Wait(()=>manager.Plugins.Single().State==PluginState.Disabled&&manager.Contributions.Views.All(v=>v.PluginId!=Id),"Real presenter disable failed.");
    }
    static async Task ConfirmAction(MainWindow window,FrameworkElement page,string name,bool approve)
    {CurrentStep=name switch{"DTI 开始监控"=>"native-monitor-start","DTI 读取一次"=>"native-read-once","DTI 搜索"=>"native-search","DTI 整理候选"=>"native-analysis",_=>"native-confirmed-action"};Invoke(Find<Button>(page,name));await Dialog(window,approve,"确认本次操作");await Task.Delay(40);}
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
