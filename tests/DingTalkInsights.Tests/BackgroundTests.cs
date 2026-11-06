using System.Text.Json;
using System.Collections.Concurrent;
using DingTalkInsights.Core;

internal static class BackgroundTests
{
    static void Assert(bool b,string message="assertion failed"){if(!b)throw new Exception(message);}
    static void Run(Func<Task> f)=>f().GetAwaiter().GetResult();
    static readonly DateTimeOffset Now=DateTimeOffset.Parse("2034-03-02T10:00:00Z");
    static MonitoringConfiguration Config(bool ai=false)=>new("dws","corp:user","background-chat",Now.AddHours(-4),Path.Combine(Path.GetTempPath(),"dingtalk-insights-background-fixture","dws.exe"),Guard.Hash("binary"),ai,new ModelSettings(),60,30,Guard.Hash("credentials"));
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("background synthetic fixture uses a fully qualified path under platform temp",()=>
        {
            var config=Config();
            Assert(Path.IsPathFullyQualified(config.Executable),"synthetic executable must be fully qualified on this platform");
            var relative=Path.GetRelativePath(Path.GetTempPath(),config.Executable);
            Assert(!Path.IsPathRooted(relative)&&relative!=".."&&!relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal),"synthetic executable must stay under the platform temp directory");
            config.Validate();
        });
        yield return ("background old v1 state defaults to no recovery authority",()=>{var state=JsonSerializer.Deserialize<InsightState>("{\"Version\":1}")!;Assert(state.Recovery is null&&!state.UserStopped&&state.Supervisor.Status==MonitorStatus.Stopped);});
        yield return ("background exact policy matches and binds every field",()=>{var s=new InsightState();var c=Config(true);BackgroundPolicy.Approve(s,c,Now,true);Assert(BackgroundPolicy.CanRecover(s,c,true));foreach(var changed in new[]{c with{Profile="corp:other"},c with{ConversationId="other"},c with{Start=c.Start.AddMinutes(1)},c with{Executable=Path.Combine(Path.GetTempPath(),"dingtalk-insights-other-fixture","dws.exe")},c with{Source="demo"},c with{ExecutableRevision=Guard.Hash("new")},c with{UploadToModel=false},c with{Model=c.Model with{Model="other"}},c with{Model=c.Model with{InputUsdPerMillion=1}},c with{Model=c.Model with{OutputUsdPerMillion=2}},c with{Model=c.Model with{MaxOutputTokens=4000}},c with{Model=c.Model with{DailyBudgetUsd=2}},c with{Model=c.Model with{MaxRequestsPerDay=1}},c with{PollSeconds=120},c with{RetentionDays=60},c with{CredentialRevision=Guard.Hash("new")}})Assert(!BackgroundPolicy.CanRecover(s,changed,true));});
        yield return ("background approval requires confirmation and excludes demo",()=>{var s=new InsightState();Assert(!BackgroundPolicy.Approve(s,Config(),Now,false)&&s.Recovery is null);Assert(!BackgroundPolicy.Approve(s,Config()with{Source="demo"},Now,true));});
        yield return ("background incompatible policy unreadiness attention and stop block recovery",()=>{var s=new InsightState();var c=Config();BackgroundPolicy.Approve(s,c,Now,true);Assert(!BackgroundPolicy.CanRecover(s,c,false));s.Recovery=s.Recovery!with{Version=99};Assert(!BackgroundPolicy.CanRecover(s,c,true));BackgroundPolicy.Approve(s,c,Now,true);s.Supervisor=s.Supervisor with{Status=MonitorStatus.NeedsAttention};Assert(!BackgroundPolicy.CanRecover(s,c,true));BackgroundPolicy.Revoke(s);Assert(s.Recovery is null&&s.UserStopped&&!BackgroundPolicy.CanRecover(s,c,true));});
        yield return ("background default startup makes zero fetch and model calls",()=>Run(async()=>{var f=new Fixture(true);Assert(!await f.Supervisor.StartAsync(f.Config,recover:true));Assert(f.Fetches==0&&f.Models==0);Assert(!await f.Supervisor.StartAsync(f.Config));Assert(f.Fetches==0);}));
        yield return ("background read-only crash recovery cannot upload",()=>Run(async()=>{var f=new Fixture(false);BackgroundPolicy.Approve(f.State,f.Config,Now,true);Assert(await f.Supervisor.StartAsync(f.Config,recover:true));await f.Clock.WaitForDelay();Assert(f.Fetches==1&&f.Models==0&&f.State.Messages.Count==1);await f.Supervisor.StopAsync(false);}));
        yield return ("background restart and sleep preserve original bounded scope",()=>Run(async()=>{var f=new Fixture(false);BackgroundPolicy.Approve(f.State,f.Config,Now,true);Assert(await f.Supervisor.StartAsync(f.Config,recover:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();Assert(f.Scopes[0].Start==f.Config.Start&&f.Scopes[0].End==f.Config.Start.AddHours(1));await f.Supervisor.StopAsync(false);var restored=f.Store.Load();var r=new Fixture(false,restored);r.Clock.Advance(TimeSpan.FromHours(8));Assert(await r.Supervisor.StartAsync(r.Config,recover:true),"synthetic supervisor did not start");await r.Clock.WaitForDelay();Assert(r.Scopes[0].Start==f.Config.Start.AddHours(1).AddMinutes(-2)&&r.Scopes[0].End==f.Config.Start.AddHours(2));await r.Supervisor.StopAsync();Assert(r.Store.Load().Recovery is null&&r.Store.Load().UserStopped);}));
        yield return ("background transient retry persists bounded backoff without coverage",()=>Run(async()=>{var f=new Fixture(false);f.Fetch=(_,_)=>throw new MonitoringFault(MonitorFailure.TransientRead);Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();Assert(f.State.Coverage.Count==0&&f.State.Supervisor.Status==MonitorStatus.ReadRetry&&f.State.Supervisor.NextReadAt==Now.AddSeconds(60));for(var i=1;i<=4;i++){f.Clock.Advance(TimeSpan.FromSeconds(i==1?60:60*Math.Pow(2,i-1)));await f.Clock.WaitForDelay();Assert(f.State.Coverage.Count==0&&f.State.Supervisor.ReadFailures==i+1);}Assert(f.State.Supervisor.NextReadAt<=f.Clock.UtcNow.AddMinutes(15));f.Clock.Advance(TimeSpan.FromMinutes(15));await f.Supervisor.Completion;Assert(f.State.Supervisor.Status==MonitorStatus.NeedsAttention&&f.Fetches==6);await f.Supervisor.StopAsync();}));
        yield return ("background retry honors configured interval even above fifteen minutes",()=>Run(async()=>{var f=new Fixture(false);f.Config=f.Config with{PollSeconds=3600};f.Fetch=(_,_)=>throw new MonitoringFault(MonitorFailure.TransientRead);Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();Assert(f.State.Supervisor.NextReadAt==Now.AddHours(1));await f.Supervisor.StopAsync();}));
        foreach(var fault in new[]{MonitorFailure.Authorization,MonitorFailure.Schema,MonitorFailure.Validation,MonitorFailure.Storage,MonitorFailure.Unknown,MonitorFailure.Retention})
        {
            var category=fault;
            yield return ($"background {category} read failure pauses with safe notice",()=>Run(async()=>{var f=new Fixture(false);f.Fetch=(_,_)=>throw new MonitoringFault(category);Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Supervisor.Completion;Assert(f.Fetches==1&&f.State.Supervisor.Status==MonitorStatus.NeedsAttention&&f.State.Coverage.Count==0&&!f.Supervisor.Notice.Contains("SYNTHETIC_SECRET"));await f.Supervisor.StopAsync();}));
        }
        yield return ("background unknown exception text is never persisted",()=>Run(async()=>{var f=new Fixture(false);f.Fetch=(_,_)=>throw new InvalidOperationException("SYNTHETIC_SECRET_BODY");Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Supervisor.Completion;Assert(!JsonSerializer.Serialize(f.State.Supervisor).Contains("SYNTHETIC_SECRET")&&f.State.Supervisor.Failure==MonitorFailure.Unknown);await f.Supervisor.StopAsync();}));
        yield return ("background incomplete data pauses without model or coverage",()=>Run(async()=>{var f=new Fixture(true);f.Incomplete=true;Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Supervisor.Completion;Assert(f.Models==0&&f.State.Coverage.Count==0&&f.State.Supervisor.Failure==MonitorFailure.Incomplete);await f.Supervisor.StopAsync();}));
        yield return ("background storage failure before dispatch makes zero external calls",()=>Run(async()=>{var f=new Fixture(true);f.Store.Fail=true;Assert(!await f.Supervisor.StartAsync(f.Config,confirmed:true));Assert(f.Fetches==0&&f.Models==0&&f.State.Supervisor.Failure==MonitorFailure.Storage);}));
        yield return ("background config and credential change blocks next unattended read",()=>Run(async()=>{var f=new Fixture(true);BackgroundPolicy.Approve(f.State,f.Config,Now,true);Assert(await f.Supervisor.StartAsync(f.Config,recover:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();f.Current=f.Config with{CredentialRevision=Guard.Hash("new")};f.Clock.Advance(TimeSpan.FromSeconds(60));await f.Supervisor.Completion;Assert(f.Fetches==1&&f.State.Supervisor.Failure==MonitorFailure.Configuration);await f.Supervisor.StopAsync();}));
        yield return ("background cancellation retires ignored fetch before restart and commit",()=>Run(async()=>
        {
            var f=new Fixture(true);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetches=0;Task? stopping=null;Task<bool>? starting=null;
            f.Fetch=async(scope,_)=>
            {
                if(Interlocked.Increment(ref fetches)==1){entered.TrySetResult();await AwaitGate(release.Task,"original fetch release");}
                else{replacementEntered.TrySetResult();await AwaitGate(replacementRelease.Task,"replacement fetch release");}
                return f.Batch(scope);
            };
            try
            {
                Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");
                await AwaitGate(entered.Task,"original fetch entry");
                stopping=f.Supervisor.StopAsync();Assert(!stopping.IsCompleted,"Stop must await the ignored-cancellation fetch");
                starting=f.Supervisor.StartAsync(f.Config,confirmed:true);Assert(!starting.IsCompleted,"replacement must await retired session");
                release.TrySetResult();await stopping;Assert(await starting,"replacement supervisor did not start");
                await AwaitGate(replacementEntered.Task,"replacement fetch entry");
                Assert(f.State.Coverage.Count==0&&f.Models==0&&f.State.Attempted.Count==0,"cancelled original fetch committed stale state");
                replacementRelease.TrySetResult();await f.Clock.WaitForDelay();
            }
            finally
            {
                // Always release owned fake transports and retire both lifecycle operations before another test runs.
                release.TrySetResult();replacementRelease.TrySetResult();
                if(stopping is not null)await stopping;
                if(starting is not null)await starting;
                await f.Supervisor.StopAsync();
            }
        }));
        yield return ("background concurrent starts and wakes coalesce one poll",()=>Run(async()=>{var f=new Fixture(false);var starts=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>f.Supervisor.StartAsync(f.Config,confirmed:true)));await f.Clock.WaitForDelay();for(var i=0;i<20;i++)f.Supervisor.Wake();await f.Clock.WaitForDelay();Assert(starts.Count(b=>b)==1&&f.Fetches==1);await f.Supervisor.StopAsync();}));
        yield return ("background daily request cap defers model and keeps pending collection",()=>Run(async()=>{var f=new Fixture(true);f.Config=f.Config with{Model=new ModelSettings{MaxRequestsPerDay=1}};f.State.Reserve(f.Config.Model,0.001m,Now);BackgroundPolicy.Approve(f.State,f.Config,Now,true);Assert(await f.Supervisor.StartAsync(f.Config,recover:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();Assert(f.Models==0&&f.State.Attempted.Count==0&&f.State.Messages.Count==1&&f.State.Requests==1&&f.State.Supervisor.ModelDeferredUntil==DateTimeOffset.Parse("2034-03-03T00:00:00Z"));await f.Supervisor.StopAsync(false);var restored=f.Store.Load();var r=new Fixture(true,restored);r.Config=f.Config;r.Current=r.Config;Assert(await r.Supervisor.StartAsync(r.Config,recover:true),"synthetic supervisor did not start");await r.Clock.WaitForDelay();Assert(r.Models==0&&r.State.Requests==1);r.Clock.Advance(TimeSpan.FromDays(1));await r.Clock.WaitForDelay();Assert(r.Models==1&&r.State.Requests==1&&r.State.Attempted.Count>0);await r.Supervisor.StopAsync();}));
        yield return ("background unknown model suppresses AI persists attempts and local collection",()=>Run(async()=>{var f=new Fixture(true);f.Analyze=(_,_,_)=>throw new InvalidOperationException("SYNTHETIC_PROVIDER_KEY");BackgroundPolicy.Approve(f.State,f.Config,Now,true);Assert(await f.Supervisor.StartAsync(f.Config,recover:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();Assert(f.Models==1&&f.State.Requests==1&&f.State.Supervisor.AiSuppressed&&f.State.Supervisor.Failure==MonitorFailure.ModelUnknown);f.Clock.Advance(TimeSpan.FromSeconds(60));await f.Clock.WaitForDelay();Assert(f.Fetches==2&&f.Models==1&&f.State.Requests==1);await f.Supervisor.StopAsync(false);var restored=f.Store.Load();var r=new Fixture(true,restored);Assert(await r.Supervisor.StartAsync(r.Config,recover:true),"synthetic supervisor did not start");await r.Clock.WaitForDelay();Assert(r.Models==0&&r.Fetches==0&&r.State.Supervisor.AiSuppressed);r.Clock.Advance(TimeSpan.FromMinutes(2));await r.Clock.WaitForDelay();Assert(r.Models==0&&r.Fetches==1&&r.State.Requests==1&&r.State.Supervisor.AiSuppressed);await r.Supervisor.StopAsync();}));
        yield return ("background model auth failure cannot form unattended paid loop",()=>Run(async()=>{var f=new Fixture(true);f.Analyze=(_,_,_)=>throw new MonitoringFault(MonitorFailure.ModelAuthorization);Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();f.Clock.Advance(TimeSpan.FromDays(1));await f.Clock.WaitForDelay();Assert(f.Models==1&&f.State.Requests==1&&f.State.Supervisor.AiSuppressed);await f.Supervisor.StopAsync();}));
        yield return ("background changed readiness before model preserves unattempted sources",()=>Run(async()=>{var f=new Fixture(true);f.Fetch=(scope,_)=>{f.Current=f.Config with{CredentialRevision=Guard.Hash("changed-before-model")};return Task.FromResult(f.Batch(scope));};Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Supervisor.Completion;Assert(f.Models==0&&f.State.Attempted.Count==0&&f.State.Requests==0&&f.State.Messages.Count==1&&f.State.Supervisor.Failure==MonitorFailure.Configuration);await f.Supervisor.StopAsync();}));
        yield return ("background stop start and changed limits never reset saved usage",()=>Run(async()=>{var f=new Fixture(true);f.Config=f.Config with{Model=new ModelSettings{MaxRequestsPerDay=1}};f.State.Reserve(f.Config.Model,0.001m,Now);Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();await f.Supervisor.StopAsync();f.Config=f.Config with{Model=f.Config.Model with{DailyBudgetUsd=1}};f.Current=f.Config;Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Clock.WaitForDelay();Assert(f.Models==0&&f.State.Requests==1&&f.State.ReservedUsd==0.001m&&f.State.Supervisor.ModelDeferredUntil is not null);await f.Supervisor.StopAsync();}));
        yield return ("background cancelled model completion cannot append report or revive session",()=>Run(async()=>
        {
            var f=new Fixture(true);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Analyze=async(batch,task,_)=>
            {
                entered.TrySetResult();await AwaitGate(release.Task,"model completion release");
                return new ModelResult(new AnalysisReport(batch,[],[],task),5,5);
            };
            try
            {
                Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");
                await AwaitGate(entered.Task,"model entry");
                var stopping=f.Supervisor.StopAsync();release.TrySetResult();await stopping;
                Assert(f.State.Reports.Count==0&&f.State.Attempted.Count==1&&f.State.Requests==1&&f.State.InputTokens==0&&!f.Supervisor.IsRunning&&f.State.UserStopped);
            }
            finally{release.TrySetResult();await f.Supervisor.StopAsync();}
        }));
        yield return ("background source save failure blocks model and pauses",()=>Run(async()=>{var f=new Fixture(true);f.Fetch=(scope,_)=>{f.Store.Fail=true;return Task.FromResult(f.Batch(scope));};Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Supervisor.Completion;Assert(f.Models==0&&f.State.Attempted.Count==0&&f.State.Supervisor.Failure==MonitorFailure.Storage);f.Store.Fail=false;await f.Supervisor.StopAsync();}));
        yield return ("background retained gap is never skipped on recovery",()=>Run(async()=>{var f=new Fixture(false);f.Config=f.Config with{Start=Now.AddDays(-40)};f.Current=f.Config;BackgroundPolicy.Approve(f.State,f.Config,Now,true);Assert(await f.Supervisor.StartAsync(f.Config,recover:true),"synthetic supervisor did not start");await f.Supervisor.Completion;Assert(f.Fetches==0&&f.State.Coverage.Count==0&&f.State.Supervisor.Failure==MonitorFailure.Retention);await f.Supervisor.StopAsync();}));
        yield return ("background clock rollback cannot reset budget",()=>Run(async()=>{var f=new Fixture(true);f.State.Reserve(f.Config.Model,0.001m,Now.AddDays(1));Assert(await f.Supervisor.StartAsync(f.Config,confirmed:true),"synthetic supervisor did not start");await f.Supervisor.Completion;Assert(f.Models==0&&f.State.Requests==1&&f.State.BudgetDay=="2034-03-03"&&f.State.Supervisor.Status==MonitorStatus.NeedsAttention);await f.Supervisor.StopAsync();}));
        yield return ("background impossible saved scheduling blocks recovery",()=>Run(async()=>{var f=new Fixture(false);BackgroundPolicy.Approve(f.State,f.Config,Now,true);f.State.Supervisor=f.State.Supervisor with{NextReadAt=Now.AddYears(1)};Assert(!await f.Supervisor.StartAsync(f.Config,recover:true)&&f.Fetches==0&&f.State.Supervisor.Status==MonitorStatus.NeedsAttention);}));
        foreach(var recover in new[]{false,true})
        foreach(var honorsCancellation in new[]{false,true})
        {
            var recovery=recover;var honors=honorsCancellation;
            yield return ($"background Stop cancels pending {(recovery?"recovery":"confirmed start")} readiness on queued UI context ({(honors?"honors":"ignores")} cancellation)",()=>
            {
                var previous=SynchronizationContext.Current;var context=new QueuedContext();SynchronizationContext.SetSynchronizationContext(context);
                var f=new Fixture(true);var release=new TaskCompletionSource<MonitoringConfiguration?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var validations=0;CancellationToken startupToken=default;Task<bool>? starting=null;Task? stopping=null;
                f.Readiness=cancellation=>
                {
                    if(++validations!=1)return Task.FromResult<MonitoringConfiguration?>(f.Config);
                    startupToken=cancellation;return honors?release.Task.WaitAsync(cancellation):release.Task;
                };
                try
                {
                    if(recovery)BackgroundPolicy.Approve(f.State,f.Config,Now,true);
                    starting=f.Supervisor.StartAsync(f.Config,confirmed:!recovery,recover:recovery);
                    context.PumpUntil(()=>validations==1,"startup readiness entry");
                    stopping=f.Supervisor.StopAsync();
                    if(!honors)release.TrySetResult(f.Config);
                    context.PumpUntil(()=>starting.IsCompleted&&stopping.IsCompleted,"pending startup Stop retirement");
                    Assert(!starting.GetAwaiter().GetResult(),"cancelled startup reported success");stopping.GetAwaiter().GetResult();
                    Assert(startupToken.CanBeCanceled&&startupToken.IsCancellationRequested,"Stop did not cancel pending readiness");
                    Assert(f.Fetches==0&&f.Models==0&&f.State.Requests==0&&f.State.Attempted.Count==0,"work dispatched after pending-start Stop request");
                    Assert(f.State.UserStopped&&f.Store.Load().Recovery is null&&!f.Store.SavedStatuses.Contains(MonitorStatus.Running),"cancelled startup saved running authority");
                }
                finally
                {
                    try
                    {
                        release.TrySetResult(f.Config);var cleanup=f.Supervisor.StopAsync();
                        context.PumpUntil(()=>cleanup.IsCompleted&&(starting is null||starting.IsCompleted)&&(stopping is null||stopping.IsCompleted),"startup test cleanup");
                        cleanup.GetAwaiter().GetResult();
                    }
                    finally{SynchronizationContext.SetSynchronizationContext(previous);}
                }
            });
        }
        foreach(var crossing in new[]{"fetch","pre-model readiness"})
        {
            var boundary=crossing;
            yield return ($"background actual UTC day cap survives midnight during {boundary} and restart",()=>Run(async()=>
            {
                var f=new Fixture(true);f.Config=f.Config with{Model=f.Config.Model with{MaxRequestsPerDay=1}};
                f.Clock.Advance(DateTimeOffset.Parse("2034-03-02T23:59:50Z")-f.Clock.UtcNow);
                var validations=0;var dispatches=new List<(string Actual,string Saved,int Requests)>();
                f.Readiness=_=>
                {
                    if(++validations==3&&boundary=="pre-model readiness")f.Clock.Advance(TimeSpan.FromSeconds(20));
                    return Task.FromResult<MonitoringConfiguration?>(f.Config);
                };
                f.Fetch=(scope,_)=>{if(f.Fetches==1&&boundary=="fetch")f.Clock.Advance(TimeSpan.FromSeconds(20));return Task.FromResult(f.Batch(scope));};
                f.Analyze=(batch,task,_)=>{dispatches.Add((f.Clock.UtcNow.ToString("yyyy-MM-dd"),f.State.BudgetDay,f.State.Requests));return Task.FromResult(new ModelResult(new AnalysisReport(batch,[],[],task),1,1));};
                BackgroundPolicy.Approve(f.State,f.Config,f.Clock.UtcNow,true);
                try
                {
                    Assert(await f.Supervisor.StartAsync(f.Config,recover:true),"midnight supervisor did not start");await f.Clock.WaitForDelay();
                    Assert(dispatches.Count==1&&dispatches[0]==("2034-03-03","2034-03-03",1),"first new-day dispatch reserved against stale day");
                    var attempted=new HashSet<string>(f.State.Attempted);var reserved=f.State.ReservedUsd;
                    f.Clock.Advance(TimeSpan.FromMinutes(1));await f.Clock.WaitForDelay();
                    Assert(dispatches.Count==1&&f.State.Requests==1&&f.State.ReservedUsd==reserved&&f.State.Attempted.SetEquals(attempted),"next tick bypassed actual-day cap or marked deferred sources");
                    Assert(f.State.Supervisor.ModelDeferredUntil==DateTimeOffset.Parse("2034-03-04T00:00:00Z"),"budget deferral used stale UTC day");
                    await f.Supervisor.StopAsync(false);
                    var r=new Fixture(true,f.Store.Load());r.Config=f.Config;r.Current=r.Config;
                    r.Clock.Advance(f.Clock.UtcNow-r.Clock.UtcNow+TimeSpan.FromMinutes(1));
                    var replayed=false;
                    r.Analyze=(batch,task,_)=>
                    {
                        replayed|=batch.Messages.Any(message=>attempted.Contains(InsightState.AttemptKey(batch.Scope,message)));
                        dispatches.Add((r.Clock.UtcNow.ToString("yyyy-MM-dd"),r.State.BudgetDay,r.State.Requests));
                        return Task.FromResult(new ModelResult(new AnalysisReport(batch,[],[],task),1,1));
                    };
                    try
                    {
                        Assert(await r.Supervisor.StartAsync(r.Config,recover:true),"midnight restart did not start");await r.Clock.WaitForDelay();
                        Assert(r.Models==0&&r.State.Requests==1&&r.State.ReservedUsd==reserved&&r.State.BudgetDay=="2034-03-03","restart reset actual-day usage");
                        r.Clock.Advance(TimeSpan.FromDays(1));await r.Clock.WaitForDelay();
                        Assert(r.Models==1&&!replayed&&dispatches[^1]==("2034-03-04","2034-03-04",1),"next UTC day replayed attempts or bypassed saved limits");
                    }
                    finally{await r.Supervisor.StopAsync();}
                }
                finally{await f.Supervisor.StopAsync(false);}
            }));
        }
        yield return ("background invalid persisted health fails safe",()=>{var s=new InsightState{Supervisor=new SupervisorHealth{ReadFailures=-1}};try{s.Validate();}catch(SafetyException){return;}throw new Exception("invalid health accepted");});
        yield return ("background clear index revokes recovery but retains budget",()=>{var s=new InsightState();BackgroundPolicy.Approve(s,Config(),Now,true);s.Reserve(new ModelSettings(),0.001m,Now);s.ClearIndex();Assert(s.Recovery is null&&s.UserStopped&&s.Requests==1);});
        yield return ("DWS transient classification requires positive wire evidence",()=>{Assert(DwsFailures.Classify("{\"outcome\":\"failure\",\"error\":{\"type\":\"api\",\"exit_code\":1,\"retryable\":true,\"http_status\":503}}",1).Category==MonitorFailure.TransientRead);foreach(var j in new[]{"{\"error\":{\"type\":\"api\"}}","{\"error\":{\"type\":\"auth\",\"retryable\":true}}","{\"error\":{\"type\":\"permission\",\"retryable\":true}}","{\"error\":{\"type\":\"api\",\"retryable\":true,\"http_status\":401}}","{\"outcome\":\"partial_failure\",\"error\":{\"type\":\"api\",\"retryable\":true}}","{\"error\":{\"type\":\"api\",\"retryable\":\"true\"}}","not-json"})Assert(DwsFailures.Classify(j,1).Category!=MonitorFailure.TransientRead);});
        yield return ("DWS readonly readiness matches stable identity and metadata",()=>{var j="{\"success\":true,\"authenticated\":true,\"token_valid\":true,\"corp_id\":\"corp\",\"user_id\":\"user\",\"expires_at\":\"2034-03-03T00:00:00Z\",\"refresh_expires_at\":\"2034-03-10T00:00:00Z\"}";var r=DwsReadiness.Parse(j,0,"corp:user");Assert(r.Ready&&r.CredentialRevision.Length==64);Assert(DwsReadiness.Parse(j.Replace("\"token_valid\":true","\"refresh_token_valid\":true"),0,"corp:user").Ready);Assert(!DwsReadiness.Parse(j.Replace("\"success\":true","\"reason\":{},\"success\":true"),0,"corp:user").Ready);Assert(!DwsReadiness.Parse(j,0,"corp:other").Ready&&!DwsReadiness.Parse(j.Replace("\"token_valid\":true","\"token_valid\":false"),0,"corp:user").Ready&&!DwsReadiness.Parse(j,2,"corp:user").Ready);});
    }
    static async Task AwaitGate(Task task,string gate)
    {
        try{await task.WaitAsync(TimeSpan.FromSeconds(10));}
        catch(TimeoutException){throw new Exception("Synthetic background gate timed out: "+gate);}
    }
    sealed class Fixture
    {
        public InsightState State;public MemoryStore Store=new();public ManualClock Clock=new(Now);public MonitorSupervisor Supervisor;public MonitoringConfiguration Config,Current;public int Fetches,Models;public bool Incomplete;public List<QueryScope> Scopes=[];
        public Func<CancellationToken,Task<MonitoringConfiguration?>>? Readiness;
        public Func<QueryScope,CancellationToken,Task<MessageBatch>>? Fetch;
        public Func<MessageBatch,string,CancellationToken,Task<ModelResult>>? Analyze;
        public Fixture(bool ai,InsightState? state=null)
        {
            State=state??new InsightState();Config=BackgroundTests.Config(ai);Current=Config;
            Supervisor=new MonitorSupervisor(new MonitorEngine(State,Store),Store,async(s,c)=>{Fetches++;Scopes.Add(s);return Fetch is null?Batch(s):await Fetch(s,c);},async(b,t,c)=>{Models++;return Analyze is null?new ModelResult(new AnalysisReport(b,[],["Synthetic pending"],t),1,1):await Analyze(b,t,c);},c=>Readiness is not null?Readiness(c):Task.FromResult<MonitoringConfiguration?>(Current==BackgroundTests.Config(ai)?Config:Current),Clock);
        }
        public MessageBatch Batch(QueryScope s)=>new(s,[new SourceMessage("message-"+s.End.ToUnixTimeSeconds(),s.ConversationId,"sender","Synthetic","Synthetic body",s.End.AddMinutes(-1))],!Incomplete,[],new Dictionary<string,JsonElement>());
    }
    sealed class MemoryStore:IStateStore
    {
        string json="{}";public bool Fail;public List<MonitorStatus> SavedStatuses=[];
        public InsightState Load()=>JsonSerializer.Deserialize<InsightState>(json)!;
        public void Save(InsightState state){if(Fail)throw new IOException("SYNTHETIC_STORAGE_SECRET");json=JsonSerializer.Serialize(state);SavedStatuses.Add(state.Supervisor.Status);}
    }
    sealed class QueuedContext:SynchronizationContext
    {
        readonly ConcurrentQueue<(SendOrPostCallback Callback,object? State)> callbacks=new();
        public override void Post(SendOrPostCallback callback,object? state)=>callbacks.Enqueue((callback,state));
        public void PumpUntil(Func<bool> completed,string step)
        {
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            while(!completed()&&deadline.Elapsed<TimeSpan.FromSeconds(10))
            {
                if(callbacks.TryDequeue(out var item))item.Callback(item.State);else Thread.Yield();
            }
            if(!completed())throw new Exception("Queued UI context timed out: "+step);
        }
    }
    sealed class ManualClock(DateTimeOffset now):IMonitorClock
    {
        public DateTimeOffset UtcNow{get;private set;}=now;
        readonly object sync=new();TaskCompletionSource signal=new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<(DateTimeOffset Due,TaskCompletionSource Done)> waits=[];
        public Task Delay(TimeSpan duration,CancellationToken cancellation)
        {
            lock(sync){var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);waits.Add((UtcNow+duration,done));cancellation.Register(()=>done.TrySetCanceled(cancellation));signal.TrySetResult();return done.Task;}
        }
        public async Task WaitForDelay(){Task task;lock(sync){task=signal.Task;}await task.WaitAsync(TimeSpan.FromSeconds(10));lock(sync){signal=new(TaskCreationOptions.RunContinuationsAsynchronously);}}
        public void Advance(TimeSpan delta){lock(sync){UtcNow+=delta;foreach(var w in waits.Where(x=>x.Due<=UtcNow).ToArray()){waits.Remove(w);w.Done.TrySetResult();}}}
    }
}
