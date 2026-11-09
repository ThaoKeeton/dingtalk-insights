using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Core;

namespace Workbench.App;

internal static partial class AcceptanceDriver
{
    static PluginPackageStore PackageStore(HarnessOptions options)=>new(new(){UserRoot=options.UserRoot,PluginsRoot=Path.Combine(options.UserRoot,"Plugins"),AdditionalDiscoveryRoots=[Path.Combine(AppContext.BaseDirectory,"Plugins")]});
    static async Task VerifyActiveStaging(MainWindow window,HarnessOptions options,object plugin,List<object> observations)
    {
        CurrentStep="actual-package-active-staging";
        var installed=window.SmokeManager.Plugins.Single();var current=Fingerprint(installed.PackagePath);var original=Loop(plugin);
        var future=Path.Combine(options.Root,"synthetic-0.3.4-update");Directory.CreateDirectory(future);
        foreach(var path in Directory.EnumerateFiles(options.Package,"*",SearchOption.AllDirectories))
        {HarnessOptions.RejectLinks(path);var target=Path.Combine(future,Path.GetRelativePath(options.Package,path));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(path,target);}
        var manifest=Path.Combine(future,"plugin.json");var json=JsonNode.Parse(File.ReadAllText(manifest))!;json["version"]="0.3.4";File.WriteAllText(manifest,json.ToJsonString());
        var inventory=Directory.EnumerateFiles(future,"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p)!="inventory.json")
            .ToDictionary(p=>Path.GetRelativePath(future,p).Replace('\\','/'),Hash);File.WriteAllText(Path.Combine(future,"inventory.json"),JsonSerializer.Serialize(inventory));
        var store=PackageStore(options);Require((await store.StageUpdateAsync(Id,future)).Succeeded,"Actual active update staging failed.");
        var staged=store.PendingOperations.Single();Require(staged.Phase==PackageOperationPhase.Prepared&&staged.Kind==PackageOperationKind.Update
            &&Fingerprint(installed.PackagePath)==current&&ReferenceEquals(original,Loop(plugin))&&!original.IsCompleted&&installed.IsTrusted,"Staging changed active code/authority/loop.");
        Require((await store.ApplyPendingAsync()).All(r=>!r.Succeeded)&&store.PendingOperations.Count==1,"Discovered/loaded process applied pending code.");
        Require((await store.CancelPendingAsync(Id)).Succeeded&&store.PendingOperations.Count==0,"Actual untouched Prepared update cancellation failed.");
        Require((await store.StageUpdateAsync(Id,future)).Succeeded&&Fingerprint(installed.PackagePath)==current,"Actual restaging changed code.");
        Require((await store.CancelPendingAsync(Id)).Succeeded,"Final owned update fixture cancellation failed.");
        observations.Add(new{stage="actual-sdk-active-update-staging",syntheticBoundary="same actual DingTalk DLL bytes, version-only 0.3.4 package fixture; not a shipping update",staged.Kind,staged.Phase,
            currentCodeUnchanged=true,actualOriginalLoopAlive=!original.IsCompleted,applyRefusedUntilFreshProcess=true,cancelledAndRestaged=true});
    }
}
