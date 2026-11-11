using System.Text.Json;
using DingTalkInsights.Core;

internal static class DwsProcessTests
{
    static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    static string Legacy(string category="api",int code=1,string retry="true",string extra="")=>
        "{\"error\":{\"code\":"+code+",\"category\":\""+category+"\",\"message\":\"SYNTHETIC_PRIVATE_BODY https://synthetic.invalid/?secret=SYNTHETIC\",\"retryable\":"+retry+extra+"}}";
    static QueryScope Scope()=>new("corp:user","process-synthetic-chat",DateTimeOffset.UtcNow.AddHours(-2),DateTimeOffset.UtcNow.AddHours(-1));
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("DWS production client accepts pinned legacy API retry metadata from stderr",()=>
        {
            using var dws=new SyntheticDws(stderr:Legacy(extra:",\"retry_after_seconds\":120"),exitCode:1);
            var fault=ReadFailure(dws);
            Assert(fault.Category==MonitorFailure.TransientRead&&fault.RetryAfter==TimeSpan.FromSeconds(120),"official legacy stderr error did not reach retry classification");
            Assert(dws.Reads==1&&!fault.ToString().Contains("SYNTHETIC_PRIVATE_BODY"),"raw diagnostic leaked or the process was retried internally");
        });
        var cases=new (string Name,string Stdout,string Stderr,int Exit)[]{
            ("auth","",Legacy("auth",2),2),
            ("auth with contradictory exit","",Legacy("auth",1),1),
            ("permission","",Legacy("permission",4),4),
            ("validation","",Legacy("validation",3),3),
            ("partial exit","",Legacy(code:7),7),
            ("internal","",Legacy("internal",5),5),
            ("unknown category","",Legacy("future"),1),
            ("wrong wire code","",Legacy(code:2),1),
            ("missing wire code","","{\"error\":{\"category\":\"api\",\"retryable\":true}}",1),
            ("missing retry flag","","{\"error\":{\"category\":\"api\",\"code\":1}}",1),
            ("string retry flag","",Legacy(retry:"\"true\""),1),
            ("explicit no retry","",Legacy(retry:"false"),1),
            ("auth HTTP status","",Legacy(extra:",\"http_status\":401"),1),
            ("conflicting type","",Legacy(extra:",\"type\":\"auth\""),1),
            ("success outcome","","{\"outcome\":\"success\",\"error\":{\"category\":\"api\",\"code\":1,\"retryable\":true}}",1),
            ("partial outcome","","{\"outcome\":\"partial_failure\",\"error\":{\"category\":\"api\",\"code\":1,\"retryable\":true}}",1),
            ("partial stdout","{\"contractVersion\":\"im.message-list.v1\",\"messages\":[],\"partial\":true}",Legacy(),1),
            ("unknown stdout","SYNTHETIC_PRIVATE_BODY",Legacy(),1),
            ("unknown stderr","","temporarily unavailable SYNTHETIC_PRIVATE_BODY",1),
            ("malformed stderr","",Legacy()+"junk",1),
            ("oversized stderr","",Legacy()+new string(' ',70*1024),1),
            ("negative retry hint","",Legacy(extra:",\"retry_after_seconds\":-1"),1),
            ("unknown absolute retry hint","",Legacy(extra:",\"next_retry_at\":\"2034-03-03T00:00:00Z\""),1),
            ("contradictory wire exit","",Legacy(extra:",\"exit_code\":2"),1),
            ("duplicate category","",Legacy(extra:",\"category\":\"api\""),1)
        };
        foreach(var item in cases)
        {
            var input=item;
            yield return ($"DWS process {input.Name} remains nonretryable and private",()=>
            {
                using var dws=new SyntheticDws(input.Stdout,input.Stderr,input.Exit);
                var fault=ReadFailure(dws);
                Assert(fault.Category!=MonitorFailure.TransientRead,"uncertain process result became retryable: "+input.Name);
                Assert(!fault.ToString().Contains("SYNTHETIC_PRIVATE_BODY")&&dws.Reads==1,"diagnostic leaked or process repeated");
            });
        }
        yield return ("DWS legacy stderr reaches persisted bounded supervisor backoff without advancing coverage",()=>
        {
            using var dws=new SyntheticDws(stderr:Legacy(extra:",\"retry_after_seconds\":120"),exitCode:1);
            var now=DateTimeOffset.UtcNow;var state=new InsightState();var store=new ProcessMemoryStore();var clock=new ProcessClock(now);
            var scope=Scope();var config=new MonitoringConfiguration("dws",scope.Profile,scope.ConversationId,scope.Start,dws.Executable,
                Guard.Hash("synthetic executable"),true,new ModelSettings(),60,30,Guard.Hash("synthetic credentials"));
            var gate=new ConsentGate();var client=new DwsClient(dws.Executable,gate);var models=0;
            var supervisor=new MonitorSupervisor(new MonitorEngine(state,store),store,(s,c)=>client.ReadAsync(s,gate.Issue(ExternalAction.Read,s.Fingerprint),c),
                (_,_,_)=>{models++;throw new Exception("failed read must never reach model");},_=>Task.FromResult<MonitoringConfiguration?>(config),clock);
            try
            {
                Assert(supervisor.StartAsync(config,confirmed:true).GetAwaiter().GetResult(),"supervisor did not start");
                clock.WaitForDelay().GetAwaiter().GetResult();var saved=store.Load();
                Assert(saved.Supervisor.Status==MonitorStatus.ReadRetry&&saved.Supervisor.ReadFailures==1
                    &&saved.Supervisor.NextReadAt==now.AddSeconds(120),"legacy process error did not persist the supported retry hint");
                for(var attempt=2;attempt<=6;attempt++)
                {
                    clock.Advance(state.Supervisor.NextReadAt!.Value-clock.UtcNow);
                    if(attempt<6)clock.WaitForDelay().GetAwaiter().GetResult();else supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                saved=store.Load();
                Assert(dws.Reads==6&&saved.Supervisor.Status==MonitorStatus.NeedsAttention&&saved.Supervisor.Failure==MonitorFailure.RetryLimit,
                    "structured read retries were unbounded or ended in the wrong state");
                Assert(models==0&&saved.Attempted.Count==0&&saved.Requests==0&&saved.Coverage.Count==0&&saved.Receipts.Count==0
                    &&!JsonSerializer.Serialize(saved).Contains("SYNTHETIC_PRIVATE_BODY"),"failed read advanced data/model authority or persisted the diagnostic");
            }
            finally{supervisor.StopAsync().GetAwaiter().GetResult();}
        });
        yield return ("DWS successful stdout preserves incomplete-data receipt and coverage guard",()=>
        {
            // A successful command may still truthfully report incomplete data; stderr notices don't rewrite its ledger.
            var scope=Scope();var payload=JsonSerializer.Serialize(new{contractVersion="im.message-list.v1",messages=Array.Empty<object>(),count=0,
                complete=false,paginationKnown=true,hasMore=false,pagesFetched=1,failedCount=1,failures=new[]{"synthetic failure"},partial=true,truncated=false,
                stopReason="page_limit",queryRange=new{startTime=scope.Start,endTime=scope.End,order="asc",semantics="[start,end)"}});
            using var dws=new SyntheticDws(payload,"SYNTHETIC_PRIVATE_BODY notice",0);var gate=new ConsentGate();
            var batch=new DwsClient(dws.Executable,gate).ReadAsync(scope,gate.Issue(ExternalAction.Read,scope.Fingerprint)).GetAwaiter().GetResult();
            var state=new InsightState();state.Ingest(batch);
            Assert(!batch.Complete&&state.Coverage.Count==0&&state.Receipts is [{Complete:false}],"partial success lost its receipt or advanced complete coverage");
        });
    }
    static MonitoringFault ReadFailure(SyntheticDws fixture)
    {
        var scope=Scope();var gate=new ConsentGate();
        try{new DwsClient(fixture.Executable,gate).ReadAsync(scope,gate.Issue(ExternalAction.Read,scope.Fingerprint)).GetAwaiter().GetResult();}
        catch(MonitoringFault fault){return fault;}
        throw new Exception("expected a safe typed process failure");
    }
    sealed class ProcessMemoryStore:IStateStore
    {
        string json="{}";public InsightState Load()=>JsonSerializer.Deserialize<InsightState>(json)!;
        public void Save(InsightState state)=>json=JsonSerializer.Serialize(state);
    }
    sealed class ProcessClock(DateTimeOffset now):IMonitorClock
    {
        public DateTimeOffset UtcNow{get;private set;}=now;
        readonly object sync=new();TaskCompletionSource signal=new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? pending;
        public Task Delay(TimeSpan duration,CancellationToken cancellation)
        {
            lock(sync){pending=new(TaskCreationOptions.RunContinuationsAsynchronously);cancellation.Register(()=>pending.TrySetCanceled(cancellation));signal.TrySetResult();return pending.Task;}
        }
        public async Task WaitForDelay()
        {
            Task entered;lock(sync){entered=signal.Task;}await entered.WaitAsync(TimeSpan.FromSeconds(10));
            lock(sync){signal=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        }
        public void Advance(TimeSpan duration){lock(sync){UtcNow+=duration;pending?.TrySetResult();}}
    }
}
