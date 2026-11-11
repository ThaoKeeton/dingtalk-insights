using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Core;

internal static class PackageMaintenance
{
    internal const string Id="com.projectworkbench.dingtalk-insights";
    internal static string ValidateRoot(string root,string runId)
    {
        if(!Guid.TryParseExact(runId,"N",out _))throw new IOException("Invalid owned run ID.");
        var expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DingTalkInsights-WorkbenchAcceptance",runId));
        if(!Path.GetFullPath(root).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("Arbitrary maintenance root refused.");
        RejectLinks(root);using var marker=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,".dti-acceptance-owner.json")));
        if(marker.RootElement.GetProperty("runId").GetString()!=runId)throw new IOException("Maintenance owner marker mismatch.");return expected;
    }
    // Guarded install/staging helper; the original native HostRuntime owns normal startup replay.
    internal static async Task<int> Run(string[] args)
    {
        var mode=args[0];var root=ValidateRoot(args[1],args[2]);var user=Path.Combine(root,"user");RejectLinks(user);Directory.CreateDirectory(user);
        RejectLinks(Path.Combine(user,"host.lock"));using var lease=new FileStream(Path.Combine(user,"host.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var options=new PluginManagerOptions{UserRoot=user,PluginsRoot=Path.Combine(user,"Plugins")};var store=new PluginPackageStore(options);
        var before=Preserved(root);IReadOnlyList<PluginOperationResult> results;
        switch(mode)
        {
            case "install":results=[await store.InstallAsync(args[3])];break;
            case "update":results=[await store.StageUpdateAsync(Id,args[3])];break;
            case "remove":results=[await store.StageRemoveAsync(Id)];break;
            case "cancel":results=[await store.CancelPendingAsync(Id)];break;
            case "rollback":await store.RefreshAsync();results=[await store.StageRollbackAsync(Id,store.History.First(h=>h.PluginId==Id&&h.Kind==PackageOperationKind.Remove).HistoryId)];break;
            case "apply":results=await store.ApplyPendingAsync();break;
            case "inspect":results=[];break;
            default:throw new ArgumentException("Unsupported owned maintenance action.");
        }
        await store.RefreshAsync();var after=Preserved(root);
        if(!before.OrderBy(p=>p.Key).SequenceEqual(after.OrderBy(p=>p.Key)))throw new IOException("Maintenance changed current data/settings/secret ciphertext or external canonical state.");
        var summary=new{succeeded=results.All(r=>r.Succeeded),mode,scope="guarded SDK Core install/staging helper; fixed original native HostRuntime owns startup replay before any discovery",keepData="unconditional; no purge or historical-data restore",results,
            preserved=after,pending=store.PendingOperations.Select(p=>new{p.PluginId,p.Kind,p.Phase}),history=store.History.Select(h=>new{h.PluginId,h.Kind,h.Version,h.HistoryId,dataBackup=h.DataBackupPath is not null}),diagnostics=store.Diagnostics.Select(d=>d.Code.ToString())};
        Directory.CreateDirectory(Path.Combine(root,"evidence"));File.WriteAllText(Path.Combine(root,"evidence","maintenance-"+mode+".json"),JsonSerializer.Serialize(summary));
        Console.WriteLine(JsonSerializer.Serialize(new{summary.succeeded,mode,resultCount=results.Count,pending=store.PendingOperations.Count,history=store.History.Count,preserved=true}));
        return results.All(r=>r.Succeeded)?0:1;
    }
    internal static SortedDictionary<string,string> Preserved(string root)
    {
        var user=Path.Combine(root,"user");return new(new[]{("pluginData",Path.Combine(user,"data",Id)),("settings",Path.Combine(user,"settings",Id)),("secretCiphertext",Path.Combine(user,"secrets",Id)),
            ("externalInitialCanonical",Path.Combine(root,"pending-stop","initial-readiness","canonical")),("externalManualCanonical",Path.Combine(root,"pending-stop","one-shot-read","canonical")),("externalCanonicalSentinel",Path.Combine(root,"external-canonical"))}
            .ToDictionary(p=>p.Item1,p=>Fingerprint(p.Item2)),StringComparer.Ordinal);
    }
    internal static string Fingerprint(string root)
    {
        if(!Directory.Exists(root))return "absent";using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).OrderBy(p=>Path.GetRelativePath(root,p).Replace('\\','/'),StringComparer.Ordinal))
        {RejectLinks(file);hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root,file).Replace('\\','/')));hash.AppendData([0]);hash.AppendData(File.ReadAllBytes(file));hash.AppendData([0]);}
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static void RejectLinks(string path)
    {for(var part=Path.GetFullPath(path);part is not null;part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Maintenance link refused.");}
}
