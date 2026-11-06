using System.Text.Json;

namespace Workbench.App;

internal sealed record HarnessOptions(string Root,string RunId,string Phase,string Package,string Report)
{
    public string UserRoot=>Path.Combine(Root,"user");
    public static HarnessOptions Parse(string[] args)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        for(var i=0;i<args.Length;i+=2)
        {
            if(i+1>=args.Length||!new[]{"--run-root","--run-id","--phase","--package"}.Contains(args[i])||!values.TryAdd(args[i],args[i+1]))throw new ArgumentException("Invalid isolated acceptance arguments.");
        }
        if(values.Count!=4||!Guid.TryParseExact(values["--run-id"],"N",out var id))throw new ArgumentException("Run ID missing.");
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(values["--run-root"]));
        var expected=Path.Combine(Path.GetTempPath(),"DingTalkInsights-WorkbenchAcceptance",id.ToString("N"));
        if(!root.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),StringComparison.OrdinalIgnoreCase))throw new IOException("Only launcher-owned random temporary roots are allowed.");
        RejectLinks(root);
        using var marker=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,".dti-acceptance-owner.json")));
        if(marker.RootElement.GetProperty("runId").GetString()!=id.ToString("N"))throw new IOException("Owned root marker does not match.");
        var phase=values["--phase"];
        if(phase is not("initial" or "restart" or "changed" or "absent"))throw new ArgumentException("Invalid phase.");
        var package=Path.GetFullPath(values["--package"]);RejectLinks(package);
        Directory.CreateDirectory(Path.Combine(root,"evidence"));
        return new(root,id.ToString("N"),phase,package,Path.Combine(root,"evidence",phase+".json"));
    }
    public static void RejectLinks(string path)
    {
        for(var part=Path.GetFullPath(path);part is not null;part=Path.GetDirectoryName(part))
            if((Directory.Exists(part)||File.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Acceptance roots/packages cannot contain links or reparse points.");
    }
}
