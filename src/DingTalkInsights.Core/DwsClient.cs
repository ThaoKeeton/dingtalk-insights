using System.Diagnostics;
using System.Text;

namespace DingTalkInsights.Core;

public sealed class DwsClient(string executable,ConsentGate consent)
{
    public static string[] MessageArguments(QueryScope scope)
    {
        scope.Validate();
        // Exact stable conversation ID avoids ambiguous natural-name resolution.
        return ["--profile="+scope.Profile,"chat","+chat-messages","--conversation-id="+scope.ConversationId,
            "--start="+scope.Start.ToString("O"),"--end="+scope.End.ToString("O"),"--order=asc","--page-all","--page-limit=10","--max-items=1000","--format=json"];
    }
    public async Task<MessageBatch> ReadAsync(QueryScope scope,ConsentToken? token,CancellationToken cancellation=default)
    {
        consent.Consume(token,ExternalAction.Read,scope.Fingerprint);
        var (output,diagnostic,code)=await Run(MessageArguments(scope),cancellation);
        if(code!=0)throw DwsFailures.ClassifyProcessFailure(output,diagnostic,code);
        try{return DwsParser.ParseMessages(output,scope,code);}
        catch(SafetyException){throw new MonitoringFault(MonitorFailure.Schema);}
    }
    public async Task<DwsReadiness> ReadinessAsync(string profile,CancellationToken cancellation=default)
    {
        Guard.Identifier(profile,"请选择明确的 DWS 身份。");
        var (output,_,code)=await Run(["auth","status","--readonly","--profile="+profile,"--format=json"],cancellation);
        return DwsReadiness.Parse(output,code,profile);
    }
    public static string DiscoveryFingerprint(string profile,DateTimeOffset start,DateTimeOffset end)=>Guard.Hash(profile+start.ToString("O")+end.ToString("O"));
    public async Task<IReadOnlyList<Conversation>> DiscoverAsync(string profile,DateTimeOffset start,DateTimeOffset end,ConsentToken? token,CancellationToken cancellation=default)
    {
        new QueryScope(profile,"discovery",start,end).Validate();
        consent.Consume(token,ExternalAction.Discover,DiscoveryFingerprint(profile,start,end));
        var (output,_,code)=await Run(["--profile="+profile,"chat","+recent-conversations","--start="+start.ToString("yyyy-MM-ddTHH:mm:sszzz"),"--end="+end.ToString("yyyy-MM-ddTHH:mm:sszzz"),"--page-limit=10","--format=json"],cancellation);
        return DwsParser.ParseConversations(output,code);
    }
    public static string SearchFingerprint(QueryScope scope,string query)=>Guard.Hash(scope.Fingerprint+"\n"+query);
    public async Task<MessageBatch> SearchAsync(QueryScope scope,string query,ConsentToken? token,CancellationToken cancellation=default)
    {
        scope.Validate();Guard.Identifier(query,"请输入搜索关键词（最多 512 字）。");
        consent.Consume(token,ExternalAction.Search,SearchFingerprint(scope,query));
        var (output,_,code)=await Run(["--profile="+scope.Profile,"chat","+search-msg","--group="+scope.ConversationId,"--query="+query,
            "--start="+scope.Start.ToString("O"),"--end="+scope.End.ToString("O"),"--order=asc","--page-all","--page-limit=10","--limit=100","--format=json"],cancellation);
        var batch=DwsParser.ParseMessages(output,scope,code);
        return batch with{Complete=false,Warnings=batch.Warnings.Concat(["本次为关键词检索，可能遗漏其他表述；不是会话全量。 "]).ToArray()};
    }
    private async Task<(string Output,string? Diagnostic,int Exit)> Run(string[] arguments,CancellationToken cancellation)
    {
        if(!Path.IsPathFullyQualified(executable)||!File.Exists(executable))throw new SafetyException("请在连接设置中指定已安装的官方 DWS 可执行文件绝对路径。");
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var a in arguments)info.ArgumentList.Add(a);
        info.Environment["DWS_AGENT_PRODUCT"]="DingTalkInsights";info.Environment["DWS_AGENT_HOST"]="desktop";info.Environment["DWS_AGENT_VER"]="0.2.0";
        using var process=new Process{StartInfo=info};using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            if(!process.Start())throw new SafetyException("DWS 进程无法启动。");
            var stdout=ReadLimited(process.StandardOutput,4*1024*1024,timeout.Token);
            var stderr=ReadDiagnostic(process.StandardError,timeout.Token);
            await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(timeout.Token));
            return (await stdout,await stderr,process.ExitCode);
        }
        catch(OperationCanceledException)
        {
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            if(cancellation.IsCancellationRequested)throw;
            throw new MonitoringFault(MonitorFailure.TransientRead);
        }
        catch(SafetyException){try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}throw;}
        catch(Exception)
        {
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            throw new SafetyException("DWS 无法启动、超时或已取消；请在本机终端核对登录、企业授权和版本。未展示命令响应或凭据。");
        }
    }
    private static async Task<string> ReadLimited(StreamReader reader,int max,CancellationToken cancellation)
    {
        var buffer=new char[4096];var text=new StringBuilder();int n;
        while((n=await reader.ReadAsync(buffer,cancellation))>0)
        {if(text.Length+n>max)throw new SafetyException("DWS 输出过大，请缩小时间范围。");text.Append(buffer,0,n);}
        return text.ToString();
    }
    private static async Task<string?> ReadDiagnostic(StreamReader reader,CancellationToken cancellation)
    {
        var buffer=new char[4096];var text=new StringBuilder();var oversized=false;int n;
        while((n=await reader.ReadAsync(buffer,cancellation))>0)
        {
            if(oversized)continue;
            if(text.Length+n>DwsFailures.MaxDiagnosticChars){text.Clear();oversized=true;}
            else text.Append(buffer,0,n);
        }
        // Drain an oversized stream to avoid pipe deadlock, but never classify a retained prefix.
        return oversized?null:text.ToString();
    }
}
