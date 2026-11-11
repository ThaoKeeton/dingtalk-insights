using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DingTalkInsights.Desktop;

public sealed partial class MainWindow
{
    async Task RunSmokeAsync()
    {
        var output=Path.Combine(AppContext.BaseDirectory,"smoke-output");Directory.CreateDirectory(output);var steps=new List<string>();
        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1180,780));
            await Task.Delay(500);Root.UpdateLayout();
            Check(Root.IsLoaded&&Root.ActualWidth>600,"native-window-loaded");
            Check(IsDemo&&monitorCancellation is null,"safe-default-no-monitor");
            if(Environment.GetCommandLineArgs().Contains("--smoke-restore"))
            {
                Check(history.Items.Count>0&&currentReport is not null&&review?.CanReview==true&&markdown.Contains("demo-2"),"restart-history-ready-without-model");
                Check(markdown.Contains("人工确认已解决")&&currentReport!.Issues[0].HumanConfirmed,"restart-durable-human-confirmation-no-new-model");
                Check(markdown.Contains("模型判断已解决")&&!currentReport!.Issues[1].HumanConfirmed,"restart-model-assessment-without-human-review");
                Check(!state.Messages.Values.Any(m=>m.Message.MessageId=="expired-smoke-message")&&!state.Reports.Any(r=>r.Task=="EXPIRED_SMOKE_REPORT"),"restart-retention-before-use");
                Check(state.Receipts.Any(r=>!r.Complete&&r.Ledger.ContainsKey("syntheticLimit"))&&!monitorSummary.Text.Contains("SYNTHETIC_LIMIT"),"restart-collection-limitations-safe-summary");
                File.WriteAllText(Path.Combine(output,"restored-report.md"),markdown);
                Check(File.ReadAllText(Path.Combine(output,"restored-report.md")).Contains("demo-2"),"restart-saved-report-export");
                source.SelectedIndex=1;profile.Text="demo:local";conversation.Text="demo-project";
                var indexed=state.Messages.Values.Select(v=>v.Message).ToArray();askStart.Text=indexed.Min(m=>m.CreatedAt).AddSeconds(-1).ToString("O");askEnd.Text=DateTimeOffset.UtcNow.ToString("O");
                taskText.Text="Synthetic native distinct topic B";Navigate("ask");await InvokeAsync("search-local");await InvokeAsync("analyze-candidates");
                Check(StatusText.Text.Contains("同一事项已有模型尝试")&&state.Requests==0,"restart-same-task-refuses-replay");source.SelectedIndex=0;
                Navigate("settings");source.SelectedIndex=1;
                var origin=state.Monitoring??throw new SafetyException("Missing synthetic monitoring origin.");
                Check(profile.Text=="smoke:identity"&&conversation.Text=="smoke-conversation"&&ParseTime(since.Text)==origin.Start&&origin.Start<DateTimeOffset.UtcNow.AddDays(-59),"restart-authorized-identity-conversation-origin");
                var next=MonitorEngine.NextScope(state,Scope(monitoring:true),DateTimeOffset.UtcNow);
                Check(next.Start==origin.Start&&next.End-next.Start==TimeSpan.FromHours(1)&&monitorCancellation is null,"restart-bounded-gap-no-auto-monitor");
                source.SelectedIndex=0;
                await CheckSyntheticRecoveryAsync(steps);
                File.WriteAllText(Path.Combine(output,"restart-smoke-result.json"),JsonSerializer.Serialize(new{success=true,steps,source="synthetic-only",notificationArea=tray?.IsHealthy==true?"registered-and-exercised":"unavailable-fallback-only"},new JsonSerializerOptions{WriteIndented=true}));
                await ExitApplicationAsync();return;
            }
            Navigate("settings");retention.Value=1;await InvokeAsync("save-settings");
            Check(state.RetentionDays==1,"native-demo-one-day-retention");Navigate("monitor");
            await CaptureAsync("monitor.png");
            await InvokeAsync("read-once");
            Check(candidateBox.Text.Contains("demo-2")&&candidateBox.IsLoaded,"native-read-preview");
            await CaptureAsync("search.png");
            await InvokeAsync("analyze-candidates");
            Check(currentReport?.Issues.Count==2&&reportBox.Text.Contains("demo-2"),"native-analyze-evidence");
            Check(reportBox.Text.Contains("模型判断已解决")&&!reportBox.Text.Contains("人工确认已解决")&&currentReport!.Issues.All(i=>!i.HumanConfirmed),"native-model-assessment-without-human-review");
            Check(currentReport!.Issues.All(i=>i.Status==IssueStatus.PendingVerification),"no-automatic-resolution");
            Check(state.ReservedUsd==0&&state.Requests==0,"demo-no-api-spend");
            var originalReports=state.Reports.Count;taskText.Text="Synthetic native distinct topic B";Navigate("ask");
            await InvokeNewTaskAsync(false,false);
            Check(state.Reports.Count==originalReports&&currentReport.Task!="Synthetic native distinct topic B","native-new-task-risk-cancel");
            await InvokeNewTaskAsync(true,false);
            Check(state.Reports.Count==originalReports&&currentReport.Task!="Synthetic native distinct topic B","native-new-task-request-cancel");
            await InvokeNewTaskAsync(true,true);
            Check(state.Reports.Count==originalReports+1&&currentReport?.Task=="Synthetic native distinct topic B"&&state.TaskAttempts.Values.All(v=>v.Tasks.Count==2),"native-confirmed-new-task-reuses-source");
            await InvokeReviewAsync("confirm-resolved",false);
            Check(!currentReport!.Issues[0].HumanConfirmed,"native-review-cancel-no-certainty");
            using(var blockedReview=new FileStream(Path.Combine(dataRoot,"report-reviews.dpapi"),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                await InvokeReviewAsync("confirm-resolved",true);
                Check(!currentReport.Issues[0].HumanConfirmed&&!markdown.Contains("人工确认已解决"),"native-review-save-failure-no-false-success");
            }
            await InvokeReviewAsync("confirm-resolved",true);
            Check(currentReport.Issues[0].HumanConfirmed&&markdown.Contains("人工确认已解决"),"native-review-confirm-saved");
            history.SelectedIndex=0;
            Check(currentReport.Issues[0].HumanConfirmed,"native-review-history-retains-confirmation");
            await InvokeReviewAsync("reopen-resolved",true);
            Check(!currentReport.Issues[0].HumanConfirmed&&markdown.Contains("已撤销"),"native-review-revoke-saved");
            await InvokeReviewAsync("confirm-resolved",true);
            Check(store.Reviews.Open(state,review!.Saved).Report!.Issues[0].HumanConfirmed,"native-review-dpapi-roundtrip");
            await CaptureAsync("results.png");
            File.WriteAllText(Path.Combine(output,"sample-report.md"),reportBox.Text);
            var restored=store.Load();Check(restored.Messages.Count==4&&restored.Attempted.Count==4,"encrypted-state-roundtrip");
            var synthetic="SYNTHETIC_SMOKE_CREDENTIAL_"+Guid.NewGuid();vault.Save(new Credentials(synthetic,"https://oapi.dingtalk.com/robot/send?access_token=synthetic",""));
            Check(vault.Read().OpenAiKey==synthetic&&!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dataRoot,"credentials.dpapi"))).Contains(synthetic),"windows-dpapi-credential-roundtrip");vault.Clear();
            Navigate("ask");await Task.Delay(100);await InvokeAsync("search-local");Check(candidates?.Messages.Count==2,"native-local-search");
            Navigate("monitor");await Task.Delay(100);await InvokeAsync("start-monitor");await Task.Delay(500);
            Check(monitorCancellation is not null,"native-monitor-start");
            await CheckNativeBackgroundHostAsync(steps);
            Navigate("settings");await Task.Delay(100);
            Check(settingsPage is {IsEnabled:false}&&!new TextBoxAutomationPeer(profile).IsEnabled(),"native-monitor-disables-settings-keyboard");
            Navigate("results");history.SelectedIndex=-1;
            var keptReports=state.Reports.Count;
            state.Reports.Add(new SavedReport(DateTimeOffset.UtcNow,demoBatch.Scope.Key,"EXPIRED_HISTORY_SELECTION","must not display expired history",true,DateTimeOffset.UtcNow.AddDays(-2)));
            RefreshHistory();
            // Windows denies atomic replacement while a destination handle omits FileShare.Delete.
            using(var blockedState=new FileStream(Path.Combine(dataRoot,"state.dpapi"),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                history.SelectedIndex=0;await Task.Delay(50);
                Check(monitorCancellation is {IsCancellationRequested:false}&&markdown.Length==0&&history.SelectedIndex==-1
                    &&history.Items.Count==keptReports&&!state.Reports.Any(r=>r.Task=="EXPIRED_HISTORY_SELECTION"),"native-history-save-failure-prunes-keeps-monitor");
                history.SelectedIndex=0;await Task.Delay(50);
                Check(monitorCancellation is {IsCancellationRequested:false}&&markdown.Length==0&&history.SelectedIndex==-1,"native-retained-history-save-failure-not-displayed");
            }
            history.SelectedIndex=0;
            Check(markdown.Contains("demo-2")&&monitorCancellation is {IsCancellationRequested:false},"native-history-recovers-after-save-failure");
            Navigate("ask");var oldKeywords=keywords.Text;keywords.Text="";
            await InvokeAsync("search-local");keywords.Text=oldKeywords;
            Check(monitorCancellation is {IsCancellationRequested:false},"native-manual-validation-keeps-monitor");
            await Run(()=>throw new InvalidOperationException("synthetic manual failure"));
            Check(monitorCancellation is {IsCancellationRequested:false},"native-manual-error-keeps-monitor");
            await Run(()=>throw new SafetyException("synthetic tick failure"),quiet:true,monitorTick:true);
            if(monitorTask is not null)await monitorTask;
            Check(monitorCancellation is null,"native-tick-error-pauses-monitor");
            Navigate("monitor");await InvokeAsync("start-monitor");
            if(stopButton is not null)Invoke(stopButton);
            if(hostStopTask is not null)await hostStopTask;
            var stoppedState=store.Load();
            Check(monitorCancellation is null&&state.Messages.Count==4&&state.Recovery is null&&state.UserStopped
                &&stoppedState.Messages.Count==4&&stoppedState.Recovery is null&&stoppedState.UserStopped,"native-monitor-stop-dedup");
            Navigate("monitor");await InvokeAsync("start-monitor");
            tray?.HandleCommand(2);if(hostStopTask is not null)await hostStopTask;
            stoppedState=store.Load();
            Check(monitorCancellation is null&&state.Recovery is null&&state.UserStopped
                &&stoppedState.Recovery is null&&stoppedState.UserStopped,"native-tray-stop-command-revokes-recovery");
            Navigate("settings");theme.SelectedIndex=1;Root.RequestedTheme=ElementTheme.Light;await Task.Delay(100);await CaptureAsync("settings-light.png");
            theme.SelectedIndex=2;Root.RequestedTheme=ElementTheme.Dark;await Task.Delay(100);await CaptureAsync("settings-dark.png");
            Check(Root.ActualTheme==ElementTheme.Dark,"native-theme-switch");
            Navigate("help");await Task.Delay(100);Check(ReferenceEquals(PageHost.Content,pages["help"]),"native-help-navigation");
            // Seed only this isolated synthetic directory for a separate process to verify startup restoration.
            Navigate("settings");retention.Value=90;source.SelectedIndex=1;
            profile.Text="smoke:identity";conversation.Text="smoke-conversation";since.Text=DateTimeOffset.UtcNow.AddDays(-60).ToString("O");
            state.Monitoring=new MonitoringOrigin(profile.Text,conversation.Text,ParseTime(since.Text));await SaveSettingsAsync();
            source.SelectedIndex=0;await SaveSettingsAsync();
            Check(savedSettings.Profile=="smoke:identity"&&savedSettings.ConversationId=="smoke-conversation","demo-save-preserves-real-connection");
            state.Ingest(demoBatch with{Complete=false,Messages=Array.Empty<SourceMessage>(),Ledger=new Dictionary<string,JsonElement>{{"syntheticLimit",JsonSerializer.SerializeToElement("SYNTHETIC_LIMIT")}}});store.Save(state);
            await SeedSyntheticRecoveryAsync(steps);
            var seed=store.Load();var expired=demoBatch.Messages[0]with{MessageId="expired-smoke-message",CreatedAt=DateTimeOffset.UtcNow.AddDays(-400)};
            seed.Messages[InsightState.MessageKey(demoBatch.Scope,expired)]=new StoredMessage(demoBatch.Scope.Profile,expired);
            seed.Reports.Add(new SavedReport(DateTimeOffset.UtcNow,demoBatch.Scope.Key,"EXPIRED_SMOKE_REPORT","expired synthetic quote",true,expired.CreatedAt));
            var seedBytes=JsonSerializer.SerializeToUtf8Bytes(seed);
            try{File.WriteAllBytes(Path.Combine(dataRoot,"state.dpapi"),new DpapiCipher().Protect(seedBytes));}
            finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(seedBytes);}
            Check(IsDemo&&monitorCancellation is null,"restart-fixture-isolated-demo-default");
            File.WriteAllText(Path.Combine(output,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,steps,source="synthetic-only",notificationArea=tray?.IsHealthy==true?"registered-and-exercised":"unavailable-fallback-only"},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=0;Application.Current.Exit();
        }
        catch(Exception e)
        {
            File.WriteAllText(Path.Combine(output,Environment.GetCommandLineArgs().Contains("--smoke-restore")?"restart-smoke-result.json":"smoke-result.json"),JsonSerializer.Serialize(new{success=false,steps,error=e.GetType().Name,message=e.Message}));
            Environment.Exit(1);
        }
        void Check(bool success,string step){if(!success)throw new SafetyException("Smoke check failed: "+step);steps.Add(step);}
    }

    async Task CheckNativeBackgroundHostAsync(List<string> steps)
    {
        var original=savedSettings;savedSettings=savedSettings with{CloseToTray=true};
        try
        {
            if(tray?.IsHealthy==true)
            {
                if(!TryHideToTray()||AppWindow.IsVisible)throw new SafetyException("Smoke tray hide failed.");
                tray.HandleCommand(1);if(!AppWindow.IsVisible)throw new SafetyException("Smoke tray show failed.");
                tray.ReRegister();if(!tray.IsHealthy&&!AppWindow.IsVisible)throw new SafetyException("Smoke taskbar recovery failed.");
                steps.Add("native-shell-tray-register-hide-open-reregister");
            }
            else steps.Add("native-shell-tray-unavailable-real-shell-path-not-tested");
            var activations=activationCount;var requests=state.Requests;
            using(var second=new System.Diagnostics.Process{StartInfo=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false}})
            {
                second.StartInfo.ArgumentList.Add("--smoke-test");second.StartInfo.ArgumentList.Add("--activate-existing");
                if(!second.Start())throw new SafetyException("Smoke second instance failed to start.");
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                if(second.ExitCode!=0||activationCount<=activations||!AppWindow.IsVisible||state.Requests!=requests)throw new SafetyException("Smoke activation did not redirect safely.");
            }
            steps.Add("native-second-launch-redirects-show-no-collector-or-spend");
            tray?.Dispose();tray=NewTray(new UnavailableNotificationArea());
            if(TryHideToTray()||!AppWindow.IsVisible)throw new SafetyException("Smoke unavailable tray hid the window.");
            tray.ReRegister();if(!AppWindow.IsVisible)throw new SafetyException("Smoke failed tray recreation lost window.");
            steps.Add("native-unavailable-tray-keeps-window-and-recreation-restores");
        }
        finally{savedSettings=original;tray?.Dispose();tray=NewTray();}
    }
    MonitoringConfiguration SyntheticRecoveryConfiguration()=>new("dws","smoke:recovery","smoke-recovery-conversation",DateTimeOffset.UtcNow.AddHours(-2),
        Path.Combine(dataRoot,"synthetic-never-executed-dws.exe"),Guard.Hash("synthetic-executable"),false,new ModelSettings(),60,90,Guard.Hash("synthetic-credentials"));
    MonitorSupervisor SyntheticSupervisor(MonitoringConfiguration config)=>new(engine,store,(scope,_)=>Task.FromResult(new MessageBatch(scope,
        Array.Empty<SourceMessage>(),true,Array.Empty<string>(),new Dictionary<string,JsonElement>())),
        (_,_,_)=>throw new SafetyException("Read-only synthetic recovery attempted model upload."),
        _=>Task.FromResult<MonitoringConfiguration?>(config),clock:new SmokeRecoveryClock(),operationGate:operations);
    async Task SeedSyntheticRecoveryAsync(List<string> steps)
    {
        var config=SyntheticRecoveryConfiguration();BackgroundPolicy.Approve(state,config,DateTimeOffset.UtcNow,confirmed:true);store.Save(state);
        await AttachSupervisorAsync(SyntheticSupervisor(config),config,recover:true);
        await AwaitSmokeCondition(()=>state.Coverage.ContainsKey(config.Scope(DateTimeOffset.UtcNow).Key));
        await StopMonitoringAsync(revokeRecovery:false);
        if(store.Load().Recovery?.Configuration.Fingerprint!=config.Fingerprint)throw new SafetyException("Synthetic exit lost approved recovery.");
        steps.Add("native-synthetic-exit-retains-separately-approved-readonly-policy");
    }
    async Task CheckSyntheticRecoveryAsync(List<string> steps)
    {
        var config=state.Recovery?.Configuration??throw new SafetyException("Synthetic recovery fixture missing.");
        if(config.Profile!="smoke:recovery"||config.UploadToModel)throw new SafetyException("Unsafe smoke recovery fixture.");
        var prior=MonitorEngine.ResumeCursor(state,config.Scope(DateTimeOffset.UtcNow),DateTimeOffset.UtcNow);
        var receipts=state.Receipts.Count;
        await AttachSupervisorAsync(SyntheticSupervisor(config),config,recover:true);
        supervisor?.Wake();await AwaitSmokeCondition(()=>state.Receipts.Count>receipts);
        if(state.Receipts[^1].Scope.Start!=prior.AddMinutes(-2)||state.Requests!=0)throw new SafetyException("Synthetic recovery changed scope or uploaded.");
        steps.Add("native-restart-synthetic-readonly-policy-resumes-same-bounded-scope");
        await StopMonitoringAsync();
        var stopped=store.Load();if(stopped.Recovery is not null||!stopped.UserStopped)throw new SafetyException("Native Stop did not persist revocation.");
        var denied=SyntheticSupervisor(config);if(await denied.StartAsync(config,recover:true))throw new SafetyException("Stopped synthetic policy resurrected.");
        steps.Add("native-restart-stopped-policy-zero-read-no-resurrection");
        steps.Add("native-exit-command-cancels-and-retires-before-process-exit");
    }
    static async Task AwaitSmokeCondition(Func<bool> predicate)
    {
        var timeout=DateTimeOffset.UtcNow.AddSeconds(70);
        while(!predicate()&&DateTimeOffset.UtcNow<timeout)await Task.Delay(20);
        if(!predicate())throw new SafetyException("Synthetic background condition timed out.");
    }
    sealed class SmokeRecoveryClock:IMonitorClock
    {
        public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow.AddMinutes(2);
        public Task Delay(TimeSpan duration,CancellationToken cancellation)=>Task.Delay(duration,cancellation);
    }
    sealed class UnavailableNotificationArea:INotificationArea
    {
        public bool Add(IntPtr window,uint callback)=>false;
        public bool Probe(IntPtr window)=>false;
        public void Delete(IntPtr window){}
    }

    static void Invoke(Microsoft.UI.Xaml.Controls.Button button)
    {
        var peer=FrameworkElementAutomationPeer.CreatePeerForElement(button)??new ButtonAutomationPeer(button);
        if(peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider provider)throw new SafetyException("Native button does not expose Invoke.");
        provider.Invoke();
    }
    async Task InvokeAsync(string id)
    {
        var button=actions.First(b=>AutomationProperties.GetAutomationId(b)==id);Invoke(button);
        await Task.Delay(100);
        var timeout=DateTimeOffset.UtcNow.AddSeconds(20);
        while(operations.CurrentCount==0&&DateTimeOffset.UtcNow<timeout)await Task.Delay(25);
        if(operations.CurrentCount==0)throw new SafetyException("Native operation timed out: "+id);
    }
    async Task InvokeReviewAsync(string id,bool approve)
    {
        Invoke(actions.Single(b=>AutomationProperties.GetAutomationId(b)==id));
        await AwaitSmokeCondition(()=>activeDialog is not null&&SmokeDescendants<Button>(activeDialog).Any(b=>Equals(b.Content,approve?activeDialog.PrimaryButtonText:activeDialog.CloseButtonText)));
        var dialog=activeDialog!;
        Invoke(SmokeDescendants<Button>(dialog).Single(b=>Equals(b.Content,approve?dialog.PrimaryButtonText:dialog.CloseButtonText)));
        await AwaitSmokeCondition(()=>activeDialog is null&&operations.CurrentCount==1);
    }
    async Task InvokeNewTaskAsync(bool approveRisk,bool approveRequest)
    {
        Invoke(actions.Single(b=>AutomationProperties.GetAutomationId(b)=="analyze-candidates"));
        await AwaitSmokeCondition(()=>activeDialog is not null&&SmokeDescendants<Button>(activeDialog).Any(b=>Equals(b.Content,approveRisk?activeDialog.PrimaryButtonText:activeDialog.CloseButtonText)));
        var risk=activeDialog!;Invoke(SmokeDescendants<Button>(risk).Single(b=>Equals(b.Content,approveRisk?risk.PrimaryButtonText:risk.CloseButtonText)));
        if(approveRisk)
        {
            await AwaitSmokeCondition(()=>activeDialog is {} next&&!ReferenceEquals(next,risk)&&SmokeDescendants<Button>(next).Any(b=>Equals(b.Content,approveRequest?next.PrimaryButtonText:next.CloseButtonText)));
            var request=activeDialog!;Invoke(SmokeDescendants<Button>(request).Single(b=>Equals(b.Content,approveRequest?request.PrimaryButtonText:request.CloseButtonText)));
        }
        await AwaitSmokeCondition(()=>activeDialog is null&&operations.CurrentCount==1);
    }
    static IEnumerable<T> SmokeDescendants<T>(DependencyObject root)where T:DependencyObject
    {
        if(root is T item)yield return item;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in SmokeDescendants<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    async Task CaptureAsync(string filename)
    {
        Root.UpdateLayout();await Task.Delay(100);
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Root);var pixels=await bitmap.GetPixelsAsync();
        using var reader=DataReader.FromBuffer(pixels);var bytes=new byte[pixels.Length];reader.ReadBytes(bytes);
        using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,bytes);await encoder.FlushAsync();
        stream.Seek(0);using var imageReader=new DataReader(stream);await imageReader.LoadAsync((uint)stream.Size);var png=new byte[stream.Size];imageReader.ReadBytes(png);
        await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"smoke-output",filename),png);
    }
}
