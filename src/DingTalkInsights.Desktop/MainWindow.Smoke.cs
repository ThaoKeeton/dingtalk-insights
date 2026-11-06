using System.Text.Json;
using DingTalkInsights.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
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
                Check(history.Items.Count>0&&currentReport is null&&markdown.Contains("demo-2"),"restart-history-ready-without-model");
                Check(!state.Messages.Values.Any(m=>m.Message.MessageId=="expired-smoke-message")&&!state.Reports.Any(r=>r.Task=="EXPIRED_SMOKE_REPORT"),"restart-retention-before-use");
                Check(state.Receipts.Any(r=>!r.Complete&&r.Ledger.ContainsKey("syntheticLimit"))&&!monitorSummary.Text.Contains("SYNTHETIC_LIMIT"),"restart-collection-limitations-safe-summary");
                File.WriteAllText(Path.Combine(output,"restored-report.md"),markdown);
                Check(File.ReadAllText(Path.Combine(output,"restored-report.md")).Contains("demo-2"),"restart-saved-report-export");
                Navigate("settings");source.SelectedIndex=1;
                var origin=state.Monitoring??throw new SafetyException("Missing synthetic monitoring origin.");
                Check(profile.Text=="smoke:identity"&&conversation.Text=="smoke-conversation"&&ParseTime(since.Text)==origin.Start&&origin.Start<DateTimeOffset.UtcNow.AddDays(-59),"restart-authorized-identity-conversation-origin");
                var next=MonitorEngine.NextScope(state,Scope(monitoring:true),DateTimeOffset.UtcNow);
                Check(next.Start==origin.Start&&next.End-next.Start==TimeSpan.FromHours(1)&&monitorCancellation is null,"restart-bounded-gap-no-auto-monitor");
                source.SelectedIndex=0;
                File.WriteAllText(Path.Combine(output,"restart-smoke-result.json"),JsonSerializer.Serialize(new{success=true,steps,source="synthetic-only"},new JsonSerializerOptions{WriteIndented=true}));
                Application.Current.Exit();return;
            }
            Navigate("settings");retention.Value=1;await InvokeAsync("save-settings");
            Check(state.RetentionDays==1,"native-demo-one-day-retention");Navigate("monitor");
            await CaptureAsync("monitor.png");
            await InvokeAsync("read-once");
            Check(candidateBox.Text.Contains("demo-2")&&candidateBox.IsLoaded,"native-read-preview");
            await CaptureAsync("search.png");
            await InvokeAsync("analyze-candidates");
            Check(currentReport?.Issues.Count==2&&reportBox.Text.Contains("demo-2"),"native-analyze-evidence");
            Check(currentReport!.Issues.All(i=>i.Status==IssueStatus.PendingVerification),"no-automatic-resolution");
            Check(state.ReservedUsd==0&&state.Requests==0,"demo-no-api-spend");
            await CaptureAsync("results.png");
            File.WriteAllText(Path.Combine(output,"sample-report.md"),reportBox.Text);
            var restored=store.Load();Check(restored.Messages.Count==4&&restored.Attempted.Count==4,"encrypted-state-roundtrip");
            var synthetic="SYNTHETIC_SMOKE_CREDENTIAL_"+Guid.NewGuid();vault.Save(new Credentials(synthetic,"https://oapi.dingtalk.com/robot/send?access_token=synthetic",""));
            Check(vault.Read().OpenAiKey==synthetic&&!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dataRoot,"credentials.dpapi"))).Contains(synthetic),"windows-dpapi-credential-roundtrip");vault.Clear();
            Navigate("ask");await Task.Delay(100);await InvokeAsync("search-local");Check(candidates?.Messages.Count==2,"native-local-search");
            Navigate("monitor");await Task.Delay(100);await InvokeAsync("start-monitor");await Task.Delay(500);
            Check(monitorCancellation is not null,"native-monitor-start");
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
            if(monitorTask is not null)await monitorTask;
            Check(monitorCancellation is null&&state.Messages.Count==4,"native-monitor-stop-dedup");
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
            var seed=store.Load();var expired=demoBatch.Messages[0]with{MessageId="expired-smoke-message",CreatedAt=DateTimeOffset.UtcNow.AddDays(-400)};
            seed.Messages[InsightState.MessageKey(demoBatch.Scope,expired)]=new StoredMessage(demoBatch.Scope.Profile,expired);
            seed.Reports.Add(new SavedReport(DateTimeOffset.UtcNow,demoBatch.Scope.Key,"EXPIRED_SMOKE_REPORT","expired synthetic quote",true,expired.CreatedAt));
            var seedBytes=JsonSerializer.SerializeToUtf8Bytes(seed);
            try{File.WriteAllBytes(Path.Combine(dataRoot,"state.dpapi"),new DpapiCipher().Protect(seedBytes));}
            finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(seedBytes);}
            Check(IsDemo&&monitorCancellation is null,"restart-fixture-isolated-demo-default");
            File.WriteAllText(Path.Combine(output,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,steps,source="synthetic-only"},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=0;Application.Current.Exit();
        }
        catch(Exception e)
        {
            File.WriteAllText(Path.Combine(output,Environment.GetCommandLineArgs().Contains("--smoke-restore")?"restart-smoke-result.json":"smoke-result.json"),JsonSerializer.Serialize(new{success=false,steps,error=e.GetType().Name,message=e.Message}));
            Environment.Exit(1);
        }
        void Check(bool success,string step){if(!success)throw new SafetyException("Smoke check failed: "+step);steps.Add(step);}
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
