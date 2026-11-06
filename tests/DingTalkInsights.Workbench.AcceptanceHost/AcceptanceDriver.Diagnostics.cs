using System.Text.Json;

namespace Workbench.App;

internal static partial class AcceptanceDriver
{
    static string CurrentStep{get;set;}="bootstrap";
    static List<string> completedChecks=[];
    static int failureRecorded;
    static bool AcceptanceFailed=>Volatile.Read(ref failureRecorded)!=0;
    // Only driver-owned assertions may contribute text to the failure log; provider/native exception bodies never do.
    sealed class OwnedAssertionException(string safeAssertion):Exception(safeAssertion)
    {public string SafeAssertion{get;}=safeAssertion;}
    sealed record SavedFixture(bool DraftExists,long DraftBytes,bool PreferencesFileExists,bool PreferencesMatch,string? ErrorType);
    static SavedFixture ObserveSavedFixture(string userRoot)
    {
        var draft=Path.Combine(userRoot,"data",Id,"connection-draft.dpapi");
        var settings=Path.Combine(userRoot,"settings",Id,"settings.json");
        var exists=File.Exists(draft);var settingsExist=File.Exists(settings);var bytes=exists?new FileInfo(draft).Length:0;
        try
        {
            if(!settingsExist)return new(exists,bytes,false,false,null);
            if(new FileInfo(settings).Length>128*1024)return new(exists,bytes,true,false,"FixtureSettingsOversize");
            using var document=JsonDocument.Parse(File.ReadAllBytes(settings));
            var match=document.RootElement.TryGetProperty("preferences",out var prefs)&&prefs.TryGetProperty("pollSeconds",out var poll)&&poll.TryGetInt32(out var seconds)&&seconds==60
                &&prefs.TryGetProperty("retentionDays",out var retention)&&retention.TryGetInt32(out var days)&&days==7;
            return new(exists,bytes,true,match,null);
        }
        catch(Exception error){return new(exists,bytes,settingsExist,false,error.GetType().Name);}
    }
    internal static void WriteFailure(RunOptions options,string stage,Exception error,MainWindow? window)
    {
        Environment.ExitCode=1;
        // Original attribution survives later teardown callbacks and bootstrap fallback cleanup.
        if(Interlocked.CompareExchange(ref failureRecorded,1,0)!=0)return;
        JsonElement? diagnostics=null;
        try
        {
            if(window is not null)diagnostics=Observation(PluginTarget(window.SmokeManager));
        }
        catch(Exception){} // Startup may precede manager/contribution availability; preserve the original failure.
        var root=error.GetBaseException();
        options.WriteReport(new
        {
            succeeded=false,stage,errorType=root.GetType().FullName,errorHResult=$"0x{unchecked((uint)root.HResult):X8}",
            ownedAssertion=(root as OwnedAssertionException)?.SafeAssertion,
            frames=new System.Diagnostics.StackTrace(root,true).GetFrames().Take(5).Select(frame=>new
            {Type=frame.GetMethod()?.DeclaringType?.FullName,Method=frame.GetMethod()?.Name,File=Path.GetFileName(frame.GetFileName()),Line=frame.GetFileLineNumber()}).ToArray(),
            acceptanceStep=CurrentStep,checks=completedChecks.ToArray(),startupStages=options.StartupStages,
            pluginDiagnostics=diagnostics,settingsFixture=ObserveSavedFixture(options.UserRoot),completedUtc=DateTimeOffset.UtcNow
        });
    }
}
