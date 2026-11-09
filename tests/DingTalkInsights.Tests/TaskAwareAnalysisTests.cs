using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

internal static class TaskAwareAnalysisTests
{
    static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("task A does not hide the same source from a different task B preview",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;
            await c.OpenDemoAsync();await c.ReadOnceAsync(c.PreviewMonitoring(f.DemoRequest),true);
            c.SetDraft("Synthetic task A");var first=await c.PreviewAnalysisAsync(new());Check(first is not null,"task A unavailable");
            await c.AnalyzeCandidatesAsync(first!,true);c.SetDraft("Synthetic task B");
            var second=await c.PreviewAnalysisAsync(new());Check(second is not null&&second.Batch.Messages.Count>0,"task B incorrectly lost previously analyzed sources");
            var dispatches=c.Observation.Dispatches;Check(second!.PreviouslyAttempted>0,"prior source reuse was not disclosed");
            Check(!await c.AnalyzeCandidatesAsync(second,true)&&c.Observation.Dispatches==dispatches,"reuse without separate approval dispatched");
            Check(await c.AnalyzeCandidatesAsync(second,true,reuseConfirmed:true)&&c.Observation.Dispatches==dispatches+1,"explicit new task failed");
            Check(await c.PreviewAnalysisAsync(new()) is null,"same task and source were previewed twice");
        }));
        yield return ("unknown same-task result remains blocked while a separately approved new task reserves again",()=>Run(async()=>
        {
            var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};var state=new InsightState();var store=new MemoryStore();
            var engine=new MonitorEngine(state,store);var calls=0;
            Task<ModelResult> Unknown(MessageBatch _,string __,CancellationToken ___){calls++;throw new IOException("SYNTHETIC_UNKNOWN");}
            try{await engine.AnalyzeOnceAsync(batch,"Synthetic   Task A",new(),Unknown,now,utcNow:()=>now);throw new Exception("unknown result accepted");}catch(SafetyException){}
            var after=store.Load();engine=new MonitorEngine(after,store);var reserved=after.ReservedUsd;
            try{await engine.AnalyzeOnceAsync(batch," synthetic task a ",new(),Unknown,now,utcNow:()=>now);throw new Exception("unknown result replayed");}catch(SafetyException){}
            try{await engine.AnalyzeOnceAsync(batch,"Synthetic task B",new(),Unknown,now,utcNow:()=>now);throw new Exception("unapproved new task dispatched");}catch(SafetyException){}
            Check(calls==1&&after.Requests==1&&after.ReservedUsd==reserved,"refusal changed calls or budget");
            Task<ModelResult> Success(MessageBatch b,string task,CancellationToken _){calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,task),1,1));}
            await engine.AnalyzeOnceAsync(batch,"Synthetic task B",new(),Success,now,utcNow:()=>now,reuseConfirmed:true);
            Check(calls==2&&after.Requests==2&&after.ReservedUsd>reserved,"approved distinct task did not reserve exactly once");
            Check(AnalysisAttemptPolicy.Select(after,batch,"Synthetic Task A").Batch.Messages.Count==0,"new task erased unknown old attempt");
        }));
        yield return ("monitor task reuse requires an explicit new matter and never alters monitor deduplication",()=>Run(async()=>
        {
            var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now);var state=new InsightState();var store=new MemoryStore();var engine=new MonitorEngine(state,store);var calls=0;
            Task<MessageBatch> Fetch(QueryScope scope,CancellationToken _)=>Task.FromResult(batch with{Scope=scope,Messages=batch.Messages.Where(m=>m.CreatedAt>=scope.Start&&m.CreatedAt<scope.End).ToArray()});
            Task<ModelResult> Analyze(MessageBatch b,string task,CancellationToken _){calls++;return Fake(b,task,_);}
            await engine.TickAsync(batch.Scope,Fetch,Analyze,new(),30,now);
            var reuse=AnalysisAttemptPolicy.Select(state,batch,"Synthetic user question B");
            Check(calls==1&&reuse.PreviouslyAttempted==batch.Messages.Count,"monitor attempt was not classified as reuse");
            try{await engine.AnalyzeOnceAsync(reuse.Batch,"Synthetic user question B",new(),Analyze,now);throw new Exception("monitor reuse dispatched without approval");}catch(SafetyException){}
            await engine.AnalyzeOnceAsync(reuse.Batch,"Synthetic user question B",new(),Analyze,now,reuseConfirmed:true);
            await engine.TickAsync(batch.Scope,Fetch,Analyze,new(),30,now.AddMinutes(1));
            Check(calls==2&&state.Attempted.Count==batch.Messages.Count,"monitor repeated the sources after a different task");
        }));
        yield return ("legacy unknown attempts require a separate explicit new-matter approval",()=>
        {
            var batch=Demo.CreateBatch(RuntimeTests.Now);var state=new InsightState();state.MarkAttempted(batch.Scope,batch.Messages);
            var preview=AnalysisAttemptPolicy.Select(state,batch,"Synthetic new task");
            Check(preview.PreviouslyAttempted==batch.Messages.Count&&preview.Batch.Messages.Count==batch.Messages.Count,"legacy sources were silently trusted or hidden");
            try{AnalysisAttemptPolicy.RequireExact(state,preview.Batch,"Synthetic new task",false);throw new Exception("legacy ambiguity bypassed acknowledgement");}catch(SafetyException){}
            AnalysisAttemptPolicy.RequireExact(state,preview.Batch,"Synthetic new task",true);
        });
        yield return ("task identity stays isolated by profile and conversation",()=>
        {
            var batch=Demo.CreateBatch(RuntimeTests.Now);var state=new InsightState();AnalysisAttemptPolicy.Record(state,batch,"Synthetic task A");
            var otherProfile=batch with{Scope=batch.Scope with{Profile="other:user"}};
            var otherChat=batch with{Scope=batch.Scope with{ConversationId="other-chat"},Messages=batch.Messages.Select(m=>m with{ConversationId="other-chat"}).ToArray()};
            foreach(var other in new[]{otherProfile,otherChat})
            {var preview=AnalysisAttemptPolicy.Select(state,other,"Synthetic task A");Check(preview.Batch.Messages.Count==batch.Messages.Count&&preview.PreviouslyAttempted==0,"task attempts crossed an identity or conversation");}
        });
        yield return ("task identity preserves common Unicode case and canonical equivalence",()=>
        {
            foreach(var (a,b) in new[]{("Straße","STRAẞE"),("Maße","MASSE"),("ﬁ","FI"),("ΐ","Ϊ\u0301"),("İ","i\u0307"),("ΟΣ","οσ"),("  Work\nItem ","work item")})
                Check(AnalysisAttemptPolicy.TaskKey(a)==AnalysisAttemptPolicy.TaskKey(b),"equivalent synthetic task spellings evaded the prior attempt guard");
        });
        yield return ("legacy state plus a lost sidecar stays subject to explicit new-matter approval",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SelectiveCipher();var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};
            using(var first=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {var state=first.Load();state.Ingest(batch);first.Save(state);await new MonitorEngine(state,first).AnalyzeOnceAsync(batch,"Synthetic original task",new(),Fake,now,utcNow:()=>now);}
            var path=Path.Combine(temp.Path,"state.dpapi");var node=System.Text.Json.Nodes.JsonNode.Parse(cipher.Unprotect(File.ReadAllBytes(path)))!;
            node.AsObject().Remove("TaskAttempts");node.AsObject().Remove("TaskAttemptSidecarRequired");File.WriteAllBytes(path,cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(node)));
            File.Delete(Path.Combine(temp.Path,"analysis-attempts.dpapi"));
            using var next=new BusinessStore(temp.Path,cipher,new FixedTime(now));var stateAfter=next.Load();
            var preview=AnalysisAttemptPolicy.Select(stateAfter,batch,"Synthetic original task");var calls=0;var previous=stateAfter.ReservedUsd;
            Check(preview.PreviouslyAttempted==batch.Messages.Count,"lost legacy identity was falsely treated as new sources");
            try{await new MonitorEngine(stateAfter,next).AnalyzeOnceAsync(batch,"Synthetic original task",new(),(b,t,c)=>{calls++;return Fake(b,t,c);},now,utcNow:()=>now);throw new Exception("legacy ambiguity dispatched without extra approval");}catch(SafetyException){}
            Check(calls==0&&stateAfter.Requests==1&&stateAfter.ReservedUsd==previous,"legacy ambiguity bypassed the budget/confirmation boundary");
        }));
        yield return ("task ledger capacity refusal occurs before the budget reservation",()=>Run(async()=>
        {
            var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};batch=batch with{Messages=[batch.Messages[0]]};var state=new InsightState();
            var source=InsightState.AttemptKey(batch.Scope,batch.Messages[0]);state.Attempted.Add(source);
            state.TaskAttempts[source]=new(){CreatedAt=batch.Messages[0].CreatedAt,Tasks=Enumerable.Range(0,AnalysisAttemptPolicy.MaximumTasksPerSource).Select(i=>Guard.Hash("synthetic-"+i)).ToHashSet()};var calls=0;
            try{await new MonitorEngine(state,new MemoryStore()).AnalyzeOnceAsync(batch,"One more synthetic task",new(),(b,t,c)=>{calls++;return Fake(b,t,c);},now,utcNow:()=>now,reuseConfirmed:true);throw new Exception("capacity limit bypassed");}
            catch(MonitoringFault fault){Check(fault.Category==MonitorFailure.Storage,"wrong capacity failure");}
            Check(calls==0&&state.Requests==0&&state.ReservedUsd==0,"capacity refusal consumed a budget or dispatched");
        }));
        yield return ("partial same-task sources are excluded across changed time windows and retain source revisions",()=>
        {
            var batch=Demo.CreateBatch(RuntimeTests.Now);var state=new InsightState();var first=batch with{Messages=[batch.Messages[0]]};
            AnalysisAttemptPolicy.Record(state,first,"Synthetic topic A");
            var wider=batch with{Scope=batch.Scope with{Start=batch.Scope.Start.AddHours(-1)}};
            var selected=AnalysisAttemptPolicy.Select(state,wider," synthetic   TOPIC a ");
            Check(selected.SkippedSameTask==1&&selected.Batch.Messages.Count==batch.Messages.Count-1&&!selected.Batch.Complete,"same task was not filtered from a wider range");
            var b=batch.Messages[0] with{Text=batch.Messages[0].Text+" revised"};AnalysisAttemptPolicy.Record(state,batch with{Messages=[b]},"Synthetic topic A");
            Check(AnalysisAttemptPolicy.Select(state,first,"Synthetic topic A").Batch.Messages.Count==0,"A-B-A revision replayed");
            var text=JsonSerializer.Serialize(state.TaskAttempts);Check(!text.Contains("Synthetic topic")&&!text.Contains(batch.Messages[0].Text),"attempt ledger stored plaintext content");
        });
        yield return ("same task and source revision cannot be sent twice from the standalone core entry",()=>Run(async()=>
        {
            var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};var state=new InsightState();var store=new MemoryStore();
            var engine=new MonitorEngine(state,store);var calls=0;
            Task<ModelResult> Analyze(MessageBatch b,string task,CancellationToken _){calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,task),1,1));}
            await engine.AnalyzeOnceAsync(batch,"Synthetic task A",new(),Analyze,now,utcNow:()=>now);
            try{await engine.AnalyzeOnceAsync(batch,"Synthetic task A",new(),Analyze,now,utcNow:()=>now);throw new Exception("same task dispatched twice");}
            catch(SafetyException){}
            Check(calls==1&&state.Requests==1,"duplicate attempt changed calls or budget");
        }));
        yield return ("task attempt sidecar survives a legacy state rewrite and blocks the old task",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SelectiveCipher();var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var state=store.Load();state.Ingest(batch);store.Save(state);
                await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,"Synthetic topic A",new(),Fake,now,utcNow:()=>now);
                Check(File.Exists(Path.Combine(temp.Path,"analysis-attempts.dpapi")),"attempt sidecar missing");
            }
            var path=Path.Combine(temp.Path,"state.dpapi");var node=System.Text.Json.Nodes.JsonNode.Parse(cipher.Unprotect(File.ReadAllBytes(path)))!;
            node.AsObject().Remove("TaskAttempts");node.AsObject().Remove("TaskAttemptSidecarRequired");File.WriteAllBytes(path,cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(node)));
            using var next=new BusinessStore(temp.Path,cipher,new FixedTime(now));var restored=next.Load();
            Check(AnalysisAttemptPolicy.Select(restored,batch,"  SYNTHETIC   topic a ").Batch.Messages.Count==0,"legacy rewrite lost task protection");
            var other=AnalysisAttemptPolicy.Select(restored,batch,"Synthetic topic B");Check(other.Batch.Messages.Count==batch.Messages.Count&&other.PreviouslyAttempted==batch.Messages.Count,"legacy rewrite lost separate confirmation");
            Check(restored.Requests==1&&restored.ReservedUsd>0,"legacy rewrite changed the reservation");
        }));
        yield return ("sidecar saved before a failed state write conservatively blocks replay without dispatch",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SelectiveCipher();var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};var calls=0;
            var store=new BusinessStore(temp.Path,cipher,new FixedTime(now));var state=store.Load();state.Ingest(batch);store.Save(state);cipher.FailState=true;
            try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,"Synthetic interrupted task",new(),(b,t,c)=>{calls++;return Fake(b,t,c);},now,utcNow:()=>now);throw new Exception("failed state save dispatched");}
            catch(MonitoringFault fault){Check(fault.Category==MonitorFailure.Storage,"wrong storage failure");}
            Check(calls==0&&File.Exists(Path.Combine(temp.Path,"analysis-attempts.dpapi")),"pending attempt was not saved before canonical state");
            store.Dispose();cipher.FailState=false;using var restoredStore=new BusinessStore(temp.Path,cipher,new FixedTime(now));var restored=restoredStore.Load();
            Check(AnalysisAttemptPolicy.Select(restored,batch,"Synthetic interrupted task").Batch.Messages.Count==0&&restored.Attempted.Count==batch.Messages.Count,"pending sidecar did not block a future duplicate");
            Check(restored.Requests==0,"undispatched operation changed durable budget");
        }));
        yield return ("attempt sidecar save failure makes no model dispatch or durable budget reservation",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SelectiveCipher();var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};var calls=0;
            var store=new BusinessStore(temp.Path,cipher,new FixedTime(now));var state=store.Load();state.Ingest(batch);store.Save(state);
            var path=Path.Combine(temp.Path,"state.dpapi");var before=File.ReadAllBytes(path);cipher.FailAttempts=true;
            try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,"Synthetic task",new(),(b,t,c)=>{calls++;return Fake(b,t,c);},now,utcNow:()=>now);throw new Exception("sidecar failure dispatched");}
            catch(MonitoringFault fault){Check(fault.Category==MonitorFailure.Storage,"wrong sidecar failure");}
            Check(calls==0&&before.SequenceEqual(File.ReadAllBytes(path)),"sidecar failure altered canonical state or dispatched");
            store.Dispose();cipher.FailAttempts=false;using var next=new BusinessStore(temp.Path,cipher,new FixedTime(now));Check(next.Load().Requests==0,"undispatched attempt reserved a durable budget");
        }));
        yield return ("corrupt task sidecar fails closed for analysis while retained reports remain readable",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SelectiveCipher();var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var state=store.Load();state.Ingest(batch);store.Save(state);
                await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,"Synthetic prior task",new(),Fake,now,utcNow:()=>now);
            }
            var path=Path.Combine(temp.Path,"analysis-attempts.dpapi");File.WriteAllText(path,"corrupt synthetic ledger");
            using var next=new BusinessStore(temp.Path,cipher,new FixedTime(now));var retained=next.Load();
            Check(next.AnalysisMaintenanceBlocked&&!retained.AnalysisAttemptsAvailable&&retained.Reports.Count==1,"corrupt sidecar did not expose safe read-only history");
            try{await new MonitorEngine(retained,next).AnalyzeOnceAsync(batch,"Synthetic different task",new(),Fake,now,utcNow:()=>now,reuseConfirmed:true);throw new Exception("corrupt ledger allowed dispatch");}catch(MonitoringFault fault){Check(fault.Category==MonitorFailure.Storage,"wrong failure category");}
            Check(File.ReadAllText(path)=="corrupt synthetic ledger","corrupt ledger silently overwritten");
            next.ClearIndex(retained);Check(!next.AnalysisMaintenanceBlocked&&retained.Reports.Count==0&&retained.Requests==1,"explicit recovery lost budget or left the guard blocked");
        }));
        yield return ("missing required task sidecar blocks analysis rather than silently recreating it",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SelectiveCipher();var now=RuntimeTests.Now;var batch=Demo.CreateBatch(now) with{IsDemo=false};
            using(var first=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {var state=first.Load();state.Ingest(batch);first.Save(state);await new MonitorEngine(state,first).AnalyzeOnceAsync(batch,"Synthetic task A",new(),Fake,now,utcNow:()=>now);}
            var path=Path.Combine(temp.Path,"analysis-attempts.dpapi");File.Delete(path);
            using var next=new BusinessStore(temp.Path,cipher,new FixedTime(now));var value=next.Load();
            Check(next.AnalysisMaintenanceBlocked&&!value.AnalysisAttemptsAvailable&&value.Reports.Count==1&&!File.Exists(path),"missing task ledger was silently reconstructed");
        }));
        yield return ("expired source removes task hashes without resetting budget",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SelectiveCipher();var clock=new MutableClock();var batch=Demo.CreateBatch(clock.Now) with{IsDemo=false};
            using var store=new BusinessStore(temp.Path,cipher,clock);var state=store.Load();state.Ingest(batch);store.Save(state);
            await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,"Synthetic task A",new(),Fake,clock.Now,utcNow:()=>clock.Now);
            var before=state.ReservedUsd;clock.Now=clock.Now.AddDays(31);store.Save(state);
            var doc=JsonDocument.Parse(cipher.Unprotect(File.ReadAllBytes(Path.Combine(temp.Path,"analysis-attempts.dpapi"))));
            Check(state.TaskAttempts.Count==0&&state.Attempted.Count==0&&!doc.RootElement.GetProperty("Sources").EnumerateObject().Any()&&state.Requests==1&&state.ReservedUsd==before,"retention did not prune task evidence or kept cost incorrectly");
        }));
        yield return ("controller detects a corrupt task sidecar before dispatch and retains read-only history",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.OpenDemoAsync();await c.ReadOnceAsync(c.PreviewMonitoring(f.DemoRequest),true);
            c.SetDraft("Synthetic task A");await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);c.SetDraft("Synthetic task B");
            var preview=(await c.PreviewAnalysisAsync(new()))!;var before=c.Observation.Dispatches;
            var path=Path.Combine(f.PluginRoot,"demo","analysis-attempts.dpapi");File.WriteAllText(path,"corrupt synthetic ledger");
            try{await c.AnalyzeCandidatesAsync(preview,true,reuseConfirmed:true);throw new Exception("corrupt sidecar dispatched");}
            catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Storage,"wrong controller sidecar error");}
            await c.SelectHistoryAsync(c.Snapshot.History.Single());
            Check(c.Observation.Dispatches==before&&c.Snapshot.Markdown.Length>0&&File.ReadAllText(path)=="corrupt synthetic ledger","corrupt ledger lost history or dispatched");
            await c.ClearIndexAsync(true);Check(c.Snapshot.History.Count==0&&f.Services.Calls==0,"explicit clearing did not recover safely");
        }));
        yield return ("canonical plugin permits read-only history and explicit recovery when no recovery policy exists",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var now=RuntimeTests.Now;var scope=new QueryScope("corp:user","synthetic-chat",now.AddHours(-2),now);
            var batch=RuntimeTests.FakeServices.Batch(scope);
            using(var store=new BusinessStore(f.CanonicalRoot,f.Cipher,new FixedTime(now)))
            {var state=store.Load();state.Ingest(batch);store.Save(state);await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,"Synthetic prior task",new(),Fake,now,utcNow:()=>now);}
            var path=Path.Combine(f.CanonicalRoot,"analysis-attempts.dpapi");File.WriteAllText(path,"corrupt synthetic ledger");
            var c=f.Controller;Check(await c.ConnectAsync(f.CanonicalRoot,true)&&c.Snapshot.Notice.Contains("只读"),"safe read-only handover unavailable");
            await c.SelectHistoryAsync(c.Snapshot.History.Single());Check(c.Snapshot.Markdown.Length>0&&File.ReadAllText(path)=="corrupt synthetic ledger","read-only handover lost history or overwrote corruption");
            try{await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true);throw new Exception("read-only connection accepted credentials");}catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Storage,"wrong blocked-credentials error");}
            await c.ClearIndexAsync(true);Check(c.Snapshot.History.Count==0&&c.Snapshot.Requests==1&&f.Services.Calls==0,"explicit recovery changed budget or called services");
        }));
        yield return ("canonical plugin refuses a damaged task ledger while a recovery policy cannot be revoked",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture(seed:true);var statePath=Path.Combine(f.CanonicalRoot,"state.dpapi");var original=File.ReadAllBytes(statePath);
            File.WriteAllText(Path.Combine(f.CanonicalRoot,"analysis-attempts.dpapi"),"corrupt synthetic ledger");
            try{await f.Controller.ConnectAsync(f.CanonicalRoot,true);throw new Exception("unrevoked recovery policy was accepted");}
            catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Storage,"wrong blocked-handover error");}
            Check(!f.Controller.Observation.StoreOwned&&original.SequenceEqual(File.ReadAllBytes(statePath))&&f.Services.Calls==0,"failed handover retained authority or wrote the canonical state");
        }));
    }
    static Task<ModelResult> Fake(MessageBatch batch,string task,CancellationToken _)=>Task.FromResult(new ModelResult(new AnalysisReport(batch,[],[],task),1,1));
    sealed class MemoryStore:IStateStore
    {
        string json="{}";
        public InsightState Load()=>JsonSerializer.Deserialize<InsightState>(json)!;
        public void Save(InsightState state)=>json=JsonSerializer.Serialize(state);
    }
    sealed class SelectiveCipher:IDataCipher
    {
        public bool FailState,FailAttempts;
        public byte[] Protect(byte[] data)
        {
            using var doc=JsonDocument.Parse(data);
            if(FailState&&doc.RootElement.TryGetProperty("Supervisor",out _)||FailAttempts&&doc.RootElement.TryGetProperty("Sources",out _))throw new IOException("SYNTHETIC_WRITE_FAILURE");
            return [71,14,..data];
        }
        public byte[] Unprotect(byte[] data)=>data.Length>=2&&data[0]==71&&data[1]==14?data[2..]:throw new IOException("SYNTHETIC_BAD_CIPHERTEXT");
    }
    sealed class MutableClock:TimeProvider
    {
        public DateTimeOffset Now=RuntimeTests.Now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
