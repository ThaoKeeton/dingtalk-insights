using System.Text.Json;

// The real DwsClient launches this test apphost with its unchanged production arguments.
// Only a test-owned environment selector enables the child fixture; no installed DWS is used.
internal sealed class SyntheticDws:IDisposable
{
    const string Selector="DINGTALK_INSIGHTS_TEST_DWS_FIXTURE";
    readonly string? previous=Environment.GetEnvironmentVariable(Selector);
    public string DirectoryPath{get;}=Path.Combine(Path.GetTempPath(),"dingtalk-process-fixture-"+Guid.NewGuid().ToString("N"));
    public string Executable{get;}=Path.Combine(AppContext.BaseDirectory,"DingTalkInsights.Tests"+(OperatingSystem.IsWindows()?".exe":""));
    public int Reads=>File.Exists(Path.Combine(DirectoryPath,"reads"))?File.ReadAllLines(Path.Combine(DirectoryPath,"reads")).Length:0;
    public int ReadinessChecks=>File.Exists(Path.Combine(DirectoryPath,"readiness"))?File.ReadAllLines(Path.Combine(DirectoryPath,"readiness")).Length:0;
    public SyntheticDws(string stdout="",string stderr="",int exitCode=0,bool messages=false)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath,"fixture.json"),JsonSerializer.Serialize(new Fixture(stdout,stderr,exitCode,messages)));
        Environment.SetEnvironmentVariable(Selector,DirectoryPath);
        if(!File.Exists(Executable))throw new Exception("Synthetic apphost missing.");
    }
    public static int? TryRun(string[] arguments)
    {
        var directory=Environment.GetEnvironmentVariable(Selector);
        if(directory is null||arguments.Length==0)return null;
        if(arguments.Contains("auth")&&arguments.Contains("status")&&arguments.Contains("--readonly"))
        {
            File.AppendAllText(Path.Combine(directory,"readiness"),"synthetic-readiness\n");
            Console.Write("{\"success\":true,\"authenticated\":true,\"token_valid\":true,\"corp_id\":\"corp\",\"user_id\":\"user\"}");
            return 0;
        }
        if(!arguments.Contains("chat")||!arguments.Contains("+chat-messages")||!arguments.Contains("--format=json")
            ||!arguments.Contains("--order=asc")||!arguments.Contains("--page-all"))return 5;
        var fixture=JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(directory,"fixture.json")))!;
        File.AppendAllText(Path.Combine(directory,"reads"),"synthetic-read\n");
        Console.Out.Write(fixture.Messages?MessagePayload(arguments):fixture.Stdout);
        Console.Error.Write(fixture.Stderr);
        return fixture.ExitCode;
    }
    static string MessagePayload(string[] arguments)
    {
        string Value(string name)=>arguments.Single(a=>a.StartsWith(name+"=",StringComparison.Ordinal))[(name.Length+1)..];
        var start=DateTimeOffset.Parse(Value("--start"));var end=DateTimeOffset.Parse(Value("--end"));
        return JsonSerializer.Serialize(new{contractVersion="im.message-list.v1",messages=new[]{new{
            messageId="synthetic-message",conversationId=Value("--conversation-id"),senderId="synthetic",sender="Synthetic",
            text="Synthetic body",createTime=start+(end-start)/2,messageType="text"}},count=1,complete=true,paginationKnown=true,
            hasMore=false,pagesFetched=1,failedCount=0,failures=Array.Empty<object>(),partial=false,truncated=false,stopReason="source_complete",
            queryRange=new{startTime=start,endTime=end,order="asc",semantics="[start,end)"}});
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Selector,previous);
        Directory.Delete(DirectoryPath,true);
    }
    sealed record Fixture(string Stdout,string Stderr,int ExitCode,bool Messages);
}
