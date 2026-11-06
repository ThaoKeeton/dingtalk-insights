using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Core;

internal static class PackageMaintenanceTests
{
    static void Require(bool value,string assertion){if(!value)throw new IOException(assertion);}
    internal static async Task<int> Run(string package)
    {
        package=Path.GetFullPath(package);PackageMaintenance.RejectLinks(package);
        var checks=0;
        using(var f=new Root())
        {
            var zip=Path.Combine(f.Path,"actual-plugin.zip");ZipFile.CreateFromDirectory(package,zip);
            await f.Command(true,"install",zip);Require(Directory.Exists(f.Installed),"ZIP install absent");checks++;
            await f.Command(false,"install",package);checks++;
            var original=PackageMaintenance.Fingerprint(f.Installed);var future=f.Future(package);var preserved=PackageMaintenance.Preserved(f.Path);
            using(var lease=f.Lease())
            {
                await using var manager=new PluginManager(f.Options);await manager.DiscoverAsync();var installed=manager.Plugins.Single();
                Require(installed.State==PluginState.Disabled&&!installed.IsTrusted&&!installed.IsLoaded,"ZIP install executed/trusted plugin");
                var store=new PluginPackageStore(f.Options);Require((await store.StageUpdateAsync(PackageMaintenance.Id,future)).Succeeded,"real update staging failed");
                Require(PackageMaintenance.Fingerprint(f.Installed)==original&&store.PendingOperations.Single().Phase==PackageOperationPhase.Prepared,"staging changed active code");checks++;
                Require((await store.ApplyPendingAsync()).All(r=>!r.Succeeded)&&store.PendingOperations.Count==1,"discovered process applied maintenance");checks++;
                Require((await store.CancelPendingAsync(PackageMaintenance.Id)).Succeeded&&store.PendingOperations.Count==0,"real cancellation failed");
                Require((await store.StageUpdateAsync(PackageMaintenance.Id,future)).Succeeded,"restaging failed");checks++;
            }
            await f.Command(true,"apply");Require(f.Version()=="0.3.3","fresh helper did not update");await f.Command(true,"apply");checks++;
            File.AppendAllText(f.CurrentData,"current data after update, never restore old backup");var current=PackageMaintenance.Preserved(f.Path);var updated=PackageMaintenance.Fingerprint(f.Installed);
            await f.Command(true,"remove");Require(Directory.Exists(f.Installed),"staged remove changed active code");await f.Command(true,"apply");
            Require(!Directory.Exists(f.Installed),"SDK remove left current package");checks++;
            await f.Command(true,"rollback");await f.Command(true,"apply");
            Require(PackageMaintenance.Fingerprint(f.Installed)==updated&&current.SequenceEqual(PackageMaintenance.Preserved(f.Path)),"rollback restored historical data or wrong code");
            using(var lease=f.Lease())
            {
                await using var manager=new PluginManager(f.Options);await manager.DiscoverAsync();var installed=manager.Plugins.Single();
                Require(!installed.IsTrusted&&!installed.DesiredEnabled&&!installed.IsLoaded,"rollback inherited approval/authority");
                var store=new PluginPackageStore(f.Options);await store.RefreshAsync();Require(store.History.Count>=2&&store.History.All(h=>h.DataBackupPath is not null),"code/data history missing");
                Require(store.History.Any(h=>h.Kind==PackageOperationKind.Remove),"real removal history missing");
            }
            Require(preserved["externalCanonicalSentinel"]==PackageMaintenance.Preserved(f.Path)["externalCanonicalSentinel"],"external canonical bytes changed");checks++;
        }
        using(var f=new Root())
        {
            await f.Command(true,"install",package);var original=PackageMaintenance.Fingerprint(f.Installed);await f.Command(true,"update",f.Future(package));
            var state=System.IO.Path.Combine(f.Path,"user","plugin-state.json");var saved=System.IO.Path.Combine(f.Path,"owned-state-backup.json");File.Move(state,saved);Directory.CreateDirectory(state);
            await f.Command(false,"apply");
            var store=new PluginPackageStore(f.Options);await store.RefreshAsync();Require(store.PendingOperations.Single().Phase==PackageOperationPhase.NewMoved,"failed revocation lost pending ownership");checks++;
            File.AppendAllText(System.IO.Path.Combine(f.Installed,"build-info.json")," synthetic corruption after interrupted switch");
            File.AppendAllText(f.CurrentData,"current data during interrupted update");var current=PackageMaintenance.Preserved(f.Path);
            Directory.Delete(state);File.Move(saved,state);await f.Command(false,"apply");
            Require(PackageMaintenance.Fingerprint(f.Installed)==original&&current.SequenceEqual(PackageMaintenance.Preserved(f.Path)),"safe code recovery failed or restored old data");
            await store.RefreshAsync();Require(store.PendingOperations.Count==0&&Directory.EnumerateDirectories(System.IO.Path.Combine(f.Path,"user","Plugins",".workbench-packages","operations"),"quarantine",SearchOption.AllDirectories).Any(),"recovery lost quarantine/journal completion");
            await f.Command(true,"apply");checks++;
        }
        using(var f=new Root())
        {
            await f.Command(true,"install",package);var original=PackageMaintenance.Fingerprint(f.Installed);
            var pending=System.IO.Path.Combine(f.Path,"user","Plugins",".workbench-packages","pending",PackageMaintenance.Id+".json");Directory.CreateDirectory(pending);var unknown=System.IO.Path.Combine(pending,"unknown-owned-by-test.bin");File.WriteAllBytes(unknown,[11,9,7]);
            var state=File.ReadAllBytes(System.IO.Path.Combine(f.Path,"user","plugin-state.json"));await f.Command(false,"install",package);
            Require(Directory.Exists(pending)&&File.ReadAllBytes(unknown).SequenceEqual(new byte[]{11,9,7})&&PackageMaintenance.Fingerprint(f.Installed)==original
                &&File.ReadAllBytes(System.IO.Path.Combine(f.Path,"user","plugin-state.json")).SequenceEqual(state),"unknown pending ownership was guessed away");
            using var lease=f.Lease();await using var manager=new PluginManager(f.Options);await manager.DiscoverAsync();
            Require(!(await manager.EnableAsync(PackageMaintenance.Id,confirmTrust:true)).Succeeded&&!manager.Plugins.Single().IsLoaded,"unknown pending ownership allowed activation");checks++;
        }
        Console.WriteLine($"{checks}/{checks} actual DingTalk package maintenance checks passed: ZIP/folder, disabled trust, stage/cancel, discovery seal, fresh-process apply, keep-data remove/code rollback, interrupted revocation/recovery/quarantine, unknown ownership; no plugin execution.");return 0;
    }
    sealed class Root:IDisposable
    {
        readonly string runId=Guid.NewGuid().ToString("N");public string Path{get;}public string Installed=>System.IO.Path.Combine(Path,"user","Plugins",PackageMaintenance.Id);
        public string CurrentData=>System.IO.Path.Combine(Path,"user","data",PackageMaintenance.Id,"synthetic-config.dpapi");
        public PluginManagerOptions Options=>new(){UserRoot=System.IO.Path.Combine(Path,"user"),PluginsRoot=System.IO.Path.Combine(Path,"user","Plugins")};
        public Root()
        {
            Path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DingTalkInsights-WorkbenchAcceptance",runId);Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path,".dti-acceptance-owner.json"),JsonSerializer.Serialize(new{runId,purpose="synthetic SDK package integration"}));
            foreach(var relative in new[]{"user/data/"+PackageMaintenance.Id+"/synthetic-config.dpapi","user/settings/"+PackageMaintenance.Id+"/synthetic.json","user/secrets/"+PackageMaintenance.Id+"/ciphertext-sentinel.bin","external-canonical/state.dpapi","external-canonical/send-attempts.dpapi"})
            {var target=System.IO.Path.Combine(Path,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);File.WriteAllBytes(target,[71,14,12,9,3]);}
        }
        public FileStream Lease()=>new(System.IO.Path.Combine(Path,"user","host.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        public string Version()=>JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(Installed,"plugin.json")))!["version"]!.GetValue<string>();
        public string Future(string source)
        {
            var target=System.IO.Path.Combine(Path,"synthetic-0.3.3-code-update");Directory.CreateDirectory(target);
            foreach(var path in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){var destination=System.IO.Path.Combine(target,System.IO.Path.GetRelativePath(source,path));Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);File.Copy(path,destination);}
            var manifest=System.IO.Path.Combine(target,"plugin.json");var json=JsonNode.Parse(File.ReadAllText(manifest))!;json["version"]="0.3.3";File.WriteAllText(manifest,json.ToJsonString());
            var inventory=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).Where(p=>System.IO.Path.GetFileName(p)!="inventory.json").ToDictionary(p=>System.IO.Path.GetRelativePath(target,p).Replace('\\','/'),p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));File.WriteAllText(System.IO.Path.Combine(target,"inventory.json"),JsonSerializer.Serialize(inventory));
            return target;
        }
        public async Task Command(bool success,string action,string? extra=null)
        {
            var executable=Environment.ProcessPath??throw new IOException("Process executable absent.");var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            if(System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(PackageTool).Assembly.Location);
            foreach(var argument in new[]{"maintenance",action,Path,runId})start.ArgumentList.Add(argument);if(extra is not null)start.ArgumentList.Add(extra);
            using var child=Process.Start(start)??throw new IOException("Could not launch owned maintenance child.");var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
            catch(TimeoutException){child.Kill(entireProcessTree:true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));throw new IOException("Owned maintenance child timed out; actual exit awaited before cleanup.");}
            var output=await stdout;var errors=await stderr;
            Require((child.ExitCode==0)==success,"Unexpected owned maintenance child result: "+action+" exit="+child.ExitCode+" "+output+errors);
        }
        public void Dispose(){if(Directory.Exists(Path))Directory.Delete(Path,true);} // No plugin assembly was loaded; every child has exited.
    }
}
