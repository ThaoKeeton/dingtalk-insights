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
            if(stopButton is not null)Invoke(stopButton);
            if(monitorTask is not null)await monitorTask;
            Check(monitorCancellation is null&&state.Messages.Count==4,"native-monitor-stop-dedup");
            Navigate("settings");theme.SelectedIndex=1;Root.RequestedTheme=ElementTheme.Light;await Task.Delay(100);await CaptureAsync("settings-light.png");
            theme.SelectedIndex=2;Root.RequestedTheme=ElementTheme.Dark;await Task.Delay(100);await CaptureAsync("settings-dark.png");
            Check(Root.ActualTheme==ElementTheme.Dark,"native-theme-switch");
            Navigate("help");await Task.Delay(100);Check(PageHost.Content==pages["help"],"native-help-navigation");
            File.WriteAllText(Path.Combine(output,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,steps,source="synthetic-only",httpRequests=0},new JsonSerializerOptions{WriteIndented=true}));
            Environment.ExitCode=0;Application.Current.Exit();
        }
        catch(Exception e)
        {
            File.WriteAllText(Path.Combine(output,"smoke-result.json"),JsonSerializer.Serialize(new{success=false,steps,error=e.GetType().Name,message=e.Message}));
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
