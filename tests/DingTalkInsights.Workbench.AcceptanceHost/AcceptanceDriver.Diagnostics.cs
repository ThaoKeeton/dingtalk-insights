using System.Text.Json;

namespace Workbench.App;

internal static partial class AcceptanceDriver
{
    static string CurrentStep{get;set;}="bootstrap";
    static List<string> completedChecks=[];
    // Only driver-owned assertions may contribute text to the failure log; provider/native exception bodies never do.
    sealed class OwnedAssertionException(string safeAssertion):Exception(safeAssertion)
    {public string SafeAssertion{get;}=safeAssertion;}
    internal static void WriteFailure(RunOptions options,string stage,Exception error,MainWindow? window)
    {
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
            pluginDiagnostics=diagnostics,completedUtc=DateTimeOffset.UtcNow
        });
    }
}
