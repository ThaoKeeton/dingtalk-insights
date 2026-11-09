using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DingTalkInsights.Core;
using Workbench.Core;
using Workbench.Sdk;

return await PackageTool.Run(args);

internal static class PackageTool
{
    const string Entry="DingTalkInsights.Workbench";
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static async Task<int> Run(string[] args)
    {
        try
        {
            switch(args[0])
            {
                case "test-maintenance":return await PackageMaintenanceTests.Run(args[1]);
                case "maintenance":return await PackageMaintenance.Run(args[1..]);
                case "verify-source":VerifySource(args[1]);break;
                case "self-test":SelfTest();break;
                case "stage":await Stage(args[1],args[2],args[3],args[4],args[5]);break;
                case "verify-package":VerifyPackage(args[1],Shared(args[2]));break;
                default:throw new ArgumentException("Unknown package-tool command.");
            }
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
    public static void VerifySource(string root)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"provenance.json")));
        if(doc.RootElement.GetProperty("commit").GetString()!="27154465c08d132798c8ea7b637f137f7340a90b")throw new IOException("Unexpected platform pin.");
        var files=doc.RootElement.GetProperty("files");
        foreach(var item in files.EnumerateObject())
        {
            var path=Path.Combine(root,item.Name);RejectLinks(path);var bytes=File.ReadAllBytes(path);
            var blob=Encoding.UTF8.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray();
            if(!Convert.ToHexString(SHA1.HashData(blob)).Equals(item.Value.GetProperty("gitBlob").GetString(),StringComparison.OrdinalIgnoreCase)||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(item.Value.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new IOException("Immutable source changed: "+item.Name);
        }
        foreach(var path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(root,path).Replace('\\','/');
            if(relative is not("provenance.json" or "ATTRIBUTION.md")&&!files.TryGetProperty(relative,out _))throw new IOException("Unlisted/generated vendor input: "+relative);
        }
        Console.WriteLine($"Verified {files.EnumerateObject().Count()} exact platform input blobs.");
    }
    static HashSet<string> Framework()
    {
        using var source=typeof(PluginManager).Assembly.GetManifestResourceStream("Workbench.Core.FrameworkAssemblies")??throw new IOException("Framework identity catalog absent.");
        using var reader=new StreamReader(source);
        return reader.ReadToEnd().Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Append(typeof(object).Assembly.GetName().Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    static HashSet<string> Shared(string host)
    {
        var names=Framework();
        names.UnionWith(["Workbench.Sdk","Workbench.Core","Workbench.WinUI.Sdk","Workbench.App","Microsoft.WinUI","WinRT.Runtime","Microsoft.Windows.SDK.NET","Microsoft.InteractiveExperiences.Projection"]);
        foreach(var path in Directory.EnumerateFiles(host,"*.dll"))
        {
            var identity=AssemblyIdentity(path);var name=identity??Path.GetFileNameWithoutExtension(path);
            if(name.StartsWith("Microsoft.Windows.",StringComparison.Ordinal)&&name.EndsWith(".Projection",StringComparison.Ordinal)||name.StartsWith("Microsoft.WindowsAppRuntime.",StringComparison.Ordinal)||IsHostWinRtProjection(path,name))names.Add(name);
        }
        // Shared WinUI/WinRT assembly references resolve from the host default context too.
        // Walk that actual metadata graph, including WebView2 projections supplied by WinUI.
        var available=Directory.EnumerateFiles(host,"*.dll").Select(path=>(path,identity:AssemblyIdentity(path)))
            .Where(item=>item.identity is not null).ToDictionary(item=>item.identity!,item=>item.path,StringComparer.OrdinalIgnoreCase);
        var queue=new Queue<string>(names);var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(queue.TryDequeue(out var current))
        {
            if(!visited.Add(current)||!available.TryGetValue(current,out var assembly))continue;
            using var stream=File.OpenRead(assembly);using var pe=new PEReader(stream);var metadata=pe.GetMetadataReader();
            for(var row=1;row<=System.Reflection.Metadata.Ecma335.MetadataReaderExtensions.GetTableRowCount(metadata,System.Reflection.Metadata.Ecma335.TableIndex.ModuleRef);row++)
                names.Add(Path.GetFileNameWithoutExtension(metadata.GetString(metadata.GetModuleReference(System.Reflection.Metadata.Ecma335.MetadataTokens.ModuleReferenceHandle(row)).Name)));
            foreach(var reference in metadata.AssemblyReferences)
            {var dependency=metadata.GetString(metadata.GetAssemblyReference(reference).Name);if(names.Add(dependency))queue.Enqueue(dependency);}
        }
        // Native framework/AppSDK payload identities come from actual host deps, not name-prefix deletion.
        foreach(var path in Directory.EnumerateFiles(host,"*.deps.json"))
        {
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            foreach(var target in document.RootElement.GetProperty("targets").EnumerateObject())
            {
                var libraries=target.Value.EnumerateObject().ToDictionary(l=>l.Name[..l.Name.LastIndexOf('/')],l=>l.Value,StringComparer.OrdinalIgnoreCase);
                var platformLibraries=new Queue<string>(libraries.Keys.Where(id=>id=="Microsoft.WindowsAppSDK"||id.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.",StringComparison.Ordinal)||id=="runtimepack.Microsoft.Windows.SDK.NET.Ref"));
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while(platformLibraries.TryDequeue(out var id))
                {
                    if(!seen.Add(id)||!libraries.TryGetValue(id,out var library))continue;
                    if(library.TryGetProperty("dependencies",out var dependencies))foreach(var dependency in dependencies.EnumerateObject())platformLibraries.Enqueue(dependency.Name);
                    foreach(var kind in new[]{"runtime","native","runtimeTargets"})
                        if(library.TryGetProperty(kind,out var assets))foreach(var asset in assets.EnumerateObject())names.Add(Path.GetFileNameWithoutExtension(asset.Name));
                }
            }
        }
        return names;
    }
    static async Task Stage(string build,string host,string output,string provenance,string productCommit)
    {
        if(Directory.Exists(output))throw new IOException("Stage target must not exist.");
        var shared=Shared(host);var dependencies=Path.Combine(build,Entry+".deps.json");
        using var deps=JsonDocument.Parse(File.ReadAllText(dependencies));
        var privateNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Entry};
        foreach(var target in deps.RootElement.GetProperty("targets").EnumerateObject())
        foreach(var library in target.Value.EnumerateObject())
        foreach(var kind in new[]{"runtime","native","runtimeTargets"})
            if(library.Value.TryGetProperty(kind,out var assets))foreach(var asset in assets.EnumerateObject())
                privateNames.Add(Path.GetFileNameWithoutExtension(asset.Name));
        Directory.CreateDirectory(output);
        try
        {
            foreach(var path in Directory.EnumerateFiles(build,"*",SearchOption.AllDirectories))
            {
                RejectLinks(path);var relative=Path.GetRelativePath(build,path);var extension=Path.GetExtension(path);
                if(extension.Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new IOException("Executable in class-library build: "+relative);
                if(extension.Equals(".dll",StringComparison.OrdinalIgnoreCase))
                {
                    var identity=AssemblyIdentity(path)??Path.GetFileNameWithoutExtension(path);
                    if(shared.Contains(identity)||!privateNames.Contains(identity))continue;
                    Copy(path,Path.Combine(output,relative));
                }
            }
            foreach(var name in new[]{Entry+".deps.json","plugin.json","THIRD_PARTY_NOTICES.md"})Copy(Path.Combine(build,name),Path.Combine(output,name));
            foreach(var name in new[]{Entry,"DingTalkInsights.Core","DingTalkInsights.Runtime"})
                if(!File.Exists(Path.Combine(output,name+".dll")))throw new IOException("Missing required private assembly: "+name);
            File.WriteAllText(Path.Combine(output,"build-info.json"),JsonSerializer.Serialize(new
            {
                productVersion="0.3.4",productCommit,platformCommit="27154465c08d132798c8ea7b637f137f7340a90b",apiVersion=1,sdk="10.0.403",appSdk="1.8.260921001",
                provenanceSha256=Hash(provenance),sharedIdentities=shared.Order(StringComparer.Ordinal).ToArray(),assemblies=Directory.EnumerateFiles(output,"*.dll",SearchOption.AllDirectories).Select(p=>new{path=Path.GetRelativePath(output,p),identity=AssemblyIdentity(p)??"native:"+Path.GetFileName(p)})
            },Json));
            File.WriteAllText(Path.Combine(output,"inventory.json"),JsonSerializer.Serialize(Inventory(output),Json));
            VerifyPackage(output,shared);
            // Verify using the actual platform loader validator; this never loads plugin code.
            var root=Path.Combine(Path.GetTempPath(),"DTI-package-validation-"+Guid.NewGuid().ToString("N"));
            try
            {
                await using var manager=new PluginManager(new(){UserRoot=root,PluginsRoot=Path.Combine(root,"Plugins")});
                if(!(await manager.InstallAsync(output)).Succeeded)throw new IOException("Actual platform package validation/install failed.");
                await manager.DiscoverAsync();
                var package=manager.Plugins.Single();
                if(package.State!=PluginState.Disabled||package.IsLoaded||package.IsTrusted||package.Manifest?.Id!="com.projectworkbench.dingtalk-insights")throw new IOException("Real platform rejected staged package: "+string.Join(";",package.Diagnostics.Select(d=>d.Message)));
            }
            finally{if(Directory.Exists(root))Directory.Delete(root,true);}
            Console.WriteLine("Staged native DLL package; actual platform discovery is disabled/untrusted/unloaded.");
        }
        catch{Directory.Delete(output,true);throw;}
    }
    static void Copy(string from,string to){Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(from,to);}
    static SortedDictionary<string,string> Inventory(string root)=>new(Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)
        .Where(p=>Path.GetFullPath(p)!=Path.GetFullPath(Path.Combine(root,"inventory.json"))).ToDictionary(p=>Path.GetRelativePath(root,p).Replace('\\','/'),Hash),StringComparer.Ordinal);
    public static void VerifyPackage(string root,HashSet<string> shared)
    {
        foreach(var path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
        {
            RejectLinks(path);var relative=Path.GetRelativePath(root,path);var extension=Path.GetExtension(path);
            if(extension.Equals(".exe",StringComparison.OrdinalIgnoreCase)||extension.Equals(".pri",StringComparison.OrdinalIgnoreCase)||extension.Equals(".xbf",StringComparison.OrdinalIgnoreCase)||extension.Equals(".winmd",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".runtimeconfig.json",StringComparison.OrdinalIgnoreCase))throw new IOException("Forbidden executable/XAML/framework payload: "+relative);
            if(extension.Equals(".dll",StringComparison.OrdinalIgnoreCase))
            {
                var identity=AssemblyIdentity(path)??Path.GetFileNameWithoutExtension(path);
                if(shared.Contains(identity)||identity.Contains("Desktop",StringComparison.OrdinalIgnoreCase)||identity.Contains("AcceptanceHost",StringComparison.OrdinalIgnoreCase))throw new IOException("Forbidden shared/product identity: "+identity);
            }
        }
        var notice=Path.Combine(root,"THIRD_PARTY_NOTICES.md");
        if(!File.Exists(notice)||!File.ReadAllText(notice).Contains("CaseFolding-15.1.0",StringComparison.Ordinal))throw new IOException("Unicode third-party notice missing.");
        var expected=JsonSerializer.Deserialize<SortedDictionary<string,string>>(File.ReadAllText(Path.Combine(root,"inventory.json")))!;
        var actual=Inventory(root);
        if(expected.Count!=actual.Count||expected.Any(item=>!actual.TryGetValue(item.Key,out var hash)||hash!=item.Value))throw new IOException("Package bytes/inventory changed.");
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"plugin.json")));
        if(manifest.RootElement.GetProperty("entryAssembly").GetString()!=Entry+".dll")throw new IOException("Unexpected entry assembly.");
    }
    static bool IsHostWinRtProjection(string path,string identity)
    {
        if(!identity.EndsWith(".Projection",StringComparison.Ordinal))return false;
        using var stream=File.OpenRead(path);using var pe=new PEReader(stream);
        if(!pe.HasMetadata)return false;var metadata=pe.GetMetadataReader();
        return metadata.TypeReferences.Any(handle=>
        {var type=metadata.GetTypeReference(handle);return metadata.GetString(type.Namespace)=="WinRT"&&metadata.GetString(type.Name)=="WindowsRuntimeTypeAttribute";});
    }
    static string? AssemblyIdentity(string path)
    {
        using var stream=File.OpenRead(path);using var reader=new PEReader(stream);
        if(!reader.HasMetadata)return null;
        var metadata=reader.GetMetadataReader();return metadata.IsAssembly?metadata.GetString(metadata.GetAssemblyDefinition().Name):null;
    }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void RejectLinks(string path)
    {for(var part=Path.GetFullPath(path);part is not null;part=Path.GetDirectoryName(part))if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Links/reparse points forbidden.");}
    static void SelfTest()
    {
        var root=Path.Combine(Path.GetTempPath(),"DTI-package-policy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var shared=Framework();shared.Add("Workbench.Sdk");
            if(shared.Contains(Assembly.GetExecutingAssembly().GetName().Name!))throw new Exception("Private Microsoft-prefixed identity misclassified as framework.");
            // A shared SDK assembly renamed on disk still has its forbidden real identity.
            File.Copy(typeof(IWorkbenchPlugin).Assembly.Location,Path.Combine(root,"innocent.dll"));
            ExpectFailure(()=>VerifyPackage(root,shared),"shared identity");File.Delete(Path.Combine(root,"innocent.dll"));
            File.WriteAllText(Path.Combine(root,"nested.EXE"),"owned synthetic contamination");ExpectFailure(()=>VerifyPackage(root,shared),"EXE");File.Delete(Path.Combine(root,"nested.EXE"));
            File.WriteAllText(Path.Combine(root,"plugin.json"),"{\"entryAssembly\":\"DingTalkInsights.Workbench.dll\"}");
            var notice=Path.Combine(root,"THIRD_PARTY_NOTICES.md");File.WriteAllText(notice,"Synthetic CaseFolding-15.1.0 notice");
            File.Copy(typeof(Demo).Assembly.Location,Path.Combine(root,"DingTalkInsights.Core.dll"));
            File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(root,"Microsoft.DingTalkInsights.PackageTool.dll"));
            File.WriteAllText(Path.Combine(root,"inventory.json"),JsonSerializer.Serialize(Inventory(root),Json));VerifyPackage(root,shared);
            Directory.CreateDirectory(Path.Combine(root,"nested"));File.WriteAllText(Path.Combine(root,"nested","inventory.json"),"unlisted nested payload");
            ExpectFailure(()=>VerifyPackage(root,shared),"unlisted nested inventory payload");Directory.Delete(Path.Combine(root,"nested"),true);
            File.Delete(notice);File.WriteAllText(Path.Combine(root,"inventory.json"),JsonSerializer.Serialize(Inventory(root),Json));
            ExpectFailure(()=>VerifyPackage(root,shared),"missing third-party notice");File.WriteAllText(notice,"Synthetic CaseFolding-15.1.0 notice");File.WriteAllText(Path.Combine(root,"inventory.json"),JsonSerializer.Serialize(Inventory(root),Json));
            File.AppendAllText(Path.Combine(root,"plugin.json")," ");ExpectFailure(()=>VerifyPackage(root,shared),"changed bytes");
            Console.WriteLine("6/6 package policy checks passed (renamed shared identity, EXE, private Microsoft identity, required notice, mutation).");
        }
        finally{Directory.Delete(root,true);}
    }
    static void ExpectFailure(Action action,string name)
    {try{action();}catch(IOException){return;}throw new Exception("Guard accepted "+name);}
}
