using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ReportAtomicCommitTests
{
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    static ReportReviewLedger ReadLedger(SyntheticCipher cipher,string root)=>JsonSerializer.Deserialize<ReportReviewLedger>(cipher.Unprotect(File.ReadAllBytes(Path.Combine(root,"report-reviews.dpapi"))))??throw new Exception("synthetic review ledger was empty");
    static string SyntheticModel(MessageBatch batch)=>JsonSerializer.Serialize(new{issues=new[]{new{title="Synthetic",summary="Synthetic summary",status="resolved",communication="",nextStep="",evidence=new[]{new{messageId=batch.Messages[0].MessageId,quote=batch.Messages[0].Text}}}},pendingQuestions=Array.Empty<string>()});
    static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    const string SyntheticTask="Synthetic atomic task";
    public static int? TryRunChild(string[] args)
    {
        if(args.Length!=3||args[0] is not ("--synthetic-report-crash" or "--synthetic-clear-crash"))return null;
        var now=RuntimeTests.Now;
        using var store=new BusinessStore(args[1],new SyntheticCipher(),new FixedTime(now));
        var state=store.Load();var batch=Demo.CreateBatch(now) with{IsDemo=false};state.Ingest(batch);store.Save(state);
        if(args[0]=="--synthetic-report-crash")
        {
            var phase=Enum.Parse<ReportCommitPhase>(args[2]);store.CommitTestBoundary=p=>{if(p==phase)Environment.Exit(71);};
        }
        var engine=new MonitorEngine(state,store);
        engine.AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7)),now,utcNow:()=>now).GetAwaiter().GetResult();
        if(args[0]=="--synthetic-clear-crash")
        {
            store.ReportCommits.Stage(state.Reports.Single(),Demo.Analyze(batch,SyntheticTask));
            store.SendAttempts.Save(new SendLedger{Attempts={[Guard.Hash("synthetic-send-key")]=new(Guard.Hash("synthetic-destination"),Guard.Hash("synthetic-payload"),now,SendAttemptStatus.Unknown,1)}});
            state.Trim(now.AddDays(5),state.RetentionDays);var phase=Enum.Parse<IndexClearPhase>(args[2]);
            store.ClearTestBoundary=p=>{if(p==phase)Environment.Exit(71);};store.ClearIndex(state);
        }
        return 72; // The selected production boundary was not reached.
    }
    static void CrashChild(string root,object phase)
    {
        var host=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info=new ProcessStartInfo(string.IsNullOrWhiteSpace(host)?Environment.ProcessPath!:host){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!string.IsNullOrWhiteSpace(host)||Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(ReportAtomicCommitTests).Assembly.Location);
        info.ArgumentList.Add(phase is IndexClearPhase?"--synthetic-clear-crash":"--synthetic-report-crash");info.ArgumentList.Add(root);info.ArgumentList.Add(phase.ToString()!);
        using var process=Process.Start(info)??throw new Exception("failed to start synthetic crash child");
        if(!process.WaitForExit(15000)){process.Kill(true);throw new Exception("synthetic crash child timed out");}
        Check(process.ExitCode==71,"synthetic crash child missed boundary: "+process.StandardError.ReadToEnd());
    }
    sealed class FailCanonicalCipher:SyntheticCipherProxy
    {
        public bool Fail,FailJournal,FailClear,FailClearIntent;
        public override byte[] Protect(byte[] data)
        {
            using var json=JsonDocument.Parse(data);
            if(FailClearIntent&&json.RootElement.TryGetProperty("CanonicalBefore",out _))throw new IOException("synthetic clear intent failure");
            if(FailJournal&&json.RootElement.TryGetProperty("Saved",out _)&&json.RootElement.TryGetProperty("Snapshot",out _))throw new IOException("synthetic journal failure");
            if(FailClear&&json.RootElement.TryGetProperty("Supervisor",out _)&&json.RootElement.TryGetProperty("Reports",out var cleared)&&cleared.GetArrayLength()==0)throw new IOException("synthetic clear failure");
            if(Fail&&json.RootElement.TryGetProperty("Supervisor",out _)&&json.RootElement.TryGetProperty("Reports",out var reports)&&
                reports.EnumerateArray().Any(r=>r.GetProperty("Task").GetString()==SyntheticTask))throw new IOException("synthetic canonical failure");
            return base.Protect(data);
        }
    }
    class SyntheticCipherProxy:IDataCipher
    {
        readonly SyntheticCipher inner=new();
        public virtual byte[] Protect(byte[] data)=>inner.Protect(data);
        public byte[] Unprotect(byte[] data)=>inner.Unprotect(data);
    }
    sealed class MutableTime(DateTimeOffset now):TimeProvider
    {
        public DateTimeOffset Now{get;set;}=now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    public static IEnumerable<(string,Action)> All()
    {
        foreach(var automatic in new[]{false,true})
        {
            var monitoring=automatic;
            yield return ($"{(monitoring?"monitor":"on-demand")} model assessment survives exit before any UI snapshot capture",()=>Run(async()=>
            {
                using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var now=RuntimeTests.Now;var demo=Demo.CreateBatch(now) with{IsDemo=false};var calls=0;
                using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
                {
                    var state=store.Load();state.Ingest(demo);store.Save(state);var engine=new MonitorEngine(state,store);
                    Task<ModelResult> Model(MessageBatch batch,string task,CancellationToken _){calls++;return Task.FromResult(new ModelResult(Demo.Analyze(batch,task),5,7));}
                    if(monitoring)
                    {
                        Task<MessageBatch> Read(QueryScope scope,CancellationToken _)=>Task.FromResult(demo with{Scope=scope,Messages=demo.Messages.Where(m=>m.CreatedAt>=scope.Start&&m.CreatedAt<scope.End).ToArray()});
                        await engine.TickAsync(demo.Scope,Read,Model,new(),30,now,utcNow:()=>now);
                    }
                    else await engine.AnalyzeOnceAsync(demo,"Synthetic atomic task",new(),Model,now,utcNow:()=>now);
                    Check(state.Reports.Count==1&&calls==1,"model result did not reach the canonical report boundary");
                    // Deliberately exit without controller, view, Changed callback or Reviews.Capture.
                }
                using var reopened=new BusinessStore(temp.Path,cipher,new FixedTime(now));var restored=reopened.Load();
                var view=reopened.Reviews.Open(restored,restored.Reports.Single());
                Check(view.Report is not null&&view.Markdown.Contains("模型判断已解决")&&!view.Markdown.Contains("人工确认已解决"),"saved report lost its original model assessment before UI capture");
                Check(restored.Requests==1&&restored.ReservedUsd>0&&calls==1,"recovery changed the reserved budget or repeated the model");
            }));
        }
        foreach(var boundary in Enum.GetValues<ReportCommitPhase>())
        {
            var phase=boundary;
            yield return ($"terminated child recovers safely at {phase}",()=>
            {
                using var temp=new RuntimeTemp();CrashChild(temp.Path,phase);
                using var store=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(RuntimeTests.Now));var state=store.Load();
                Check(!store.MaintenanceBlocked&&store.ReportCommits.Read() is null,"journal did not reconcile");
                Check(state.Requests==1&&state.ReservedUsd>0,"recovery changed pre-dispatch budget");
                if(phase==ReportCommitPhase.Staged)Check(state.Reports.Count==0,"uncommitted report appeared");
                else
                {
                    var view=store.Reviews.Open(state,state.Reports.Single());
                    Check(view.Markdown.Contains("模型判断已解决")&&!view.Markdown.Contains("人工确认已解决"),"recovery lost the model status or invented review");
                    if(phase==ReportCommitPhase.CanonicalReplaced)Check(!view.CanReview,"interrupted evidence incorrectly presented as current");
                }
                var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};var calls=0;
                try{Run(()=>new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now));}
                catch(SafetyException){}
                Check(calls==0&&state.Requests==1,"crash recovery repeated a model attempt");
            });
        }
        foreach(var boundary in Enum.GetValues<IndexClearPhase>())
        {
            var phase=boundary;
            yield return ($"interrupted explicit clear resumes all sidecars at {phase}",()=>
            {
                using var temp=new RuntimeTemp();CrashChild(temp.Path,phase);
                using var store=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(RuntimeTests.Now));var state=store.Load();
                Check(!store.MaintenanceBlocked&&!store.IndexClears.Exists&&store.ReportCommits.Read() is null&&state.Reports.Count==0&&state.Attempted.Count==0&&state.TaskAttempts.Count==0,"interrupted clear retained or resurrected indexed data");
                Check(state.RetentionReference>=RuntimeTests.Now.AddDays(5)&&state.Requests==1&&state.ReservedUsd>0,"clear lost monotonic retention or budget");
                Check(store.SendAttempts.Load().Attempts.Values.Single().Status==SendAttemptStatus.Unknown,"clear lost the unknown send attempt");
            });
        }
        foreach(var boundary in new[]{IndexClearPhase.IntentSaved,IndexClearPhase.CanonicalCleared})
        {
            var phase=boundary;
            yield return ($"old-format work after interrupted clear is preserved at {phase}",()=>
            {
                using var temp=new RuntimeTemp();CrashChild(temp.Path,phase);var cipher=new SyntheticCipher();var now=RuntimeTests.Now.AddDays(5);
                var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};
                using(var legacy=new EncryptedStateStore(Path.Combine(temp.Path,"state.dpapi"),cipher,new FixedTime(now)))
                {
                    var state=legacy.Load();state.Ingest(batch);AnalysisAttemptPolicy.Record(state,batch,"Synthetic later old-format matter");state.Reserve(new(),0.01m,now);legacy.Save(state);
                }
                using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
                {
                    var visible=store.Load();Check(store.ReportCommitBlocked&&store.IndexClears.Exists&&visible.Reports.Count==0,"changed canonical state was silently cleared");
                    try{store.PrepareReport(visible);throw new Exception("intervening work allowed model replay");}catch(MonitoringFault){}
                }
                using var raw=new EncryptedStateStore(Path.Combine(temp.Path,"state.dpapi"),cipher,new FixedTime(now));var restored=raw.ReadUntrimmedForCommit();
                Check(AnalysisAttemptPolicy.Select(restored,batch,"Synthetic later old-format matter").SkippedSameTask==batch.Messages.Count&&restored.Requests==1,"intervening attempt or budget was deleted");
            });
        }
        yield return ("a later sidecar-only attempt prevents interrupted clear from deleting it",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,IndexClearPhase.IntentSaved);var cipher=new SyntheticCipher();
            var path=Path.Combine(temp.Path,"analysis-attempts.dpapi");var doc=new EncryptedDocument<AnalysisAttemptLedger>(path,cipher,32*1024*1024);var ledger=doc.Read()!;
            ledger.Sources[Guard.Hash("later synthetic revision")]=new(){CreatedAt=RuntimeTests.Now,Tasks=[Guard.Hash("later synthetic task")]};doc.Save(ledger);var bytes=File.ReadAllBytes(path);
            using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var state=store.Load();
            Check(store.ReportCommitBlocked&&store.IndexClears.Exists&&bytes.SequenceEqual(File.ReadAllBytes(path)),"clear erased an intervening sidecar attempt");
            try{store.PrepareReport(state);throw new Exception("sidecar conflict allowed model work");}catch(MonitoringFault){}
        });
        yield return ("explicit reconfirmed clear recovers a corrupt clear intent without losing budget or retention",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,IndexClearPhase.IntentSaved);var cipher=new SyntheticCipher();
            File.WriteAllText(Path.Combine(temp.Path,"index-clear.dpapi"),"synthetic corrupt clear intent");
            using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var state=store.Load();Check(store.ReportCommitBlocked,"corrupt clear intent did not block work");
            store.ClearIndex(state);Check(!store.MaintenanceBlocked&&!store.IndexClears.Exists&&state.RetentionReference>=RuntimeTests.Now.AddDays(5)&&state.Requests==1&&state.ReservedUsd>0,"explicit clear lost retention or budget");
        });
        yield return ("waiting clear resumes after a later revocation advances retention",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,IndexClearPhase.IntentSaved);var cipher=new SyntheticCipher();
            var path=Path.Combine(temp.Path,"report-commit.dpapi");var original=File.ReadAllBytes(path);File.Delete(path);Directory.CreateDirectory(path);
            var later=RuntimeTests.Now.AddDays(10);
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(later)))
            {
                var state=store.Load();Check(store.ReportCommitBlocked&&store.IndexClears.Exists,"temporary sidecar conflict was not blocked");
                state.RetentionDays=1;BackgroundPolicy.Revoke(state);store.SaveRevocation(state);
            }
            Directory.Delete(path);File.WriteAllBytes(path,original);
            using var resumed=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var restored=resumed.Load();
            Check(!resumed.MaintenanceBlocked&&!resumed.IndexClears.Exists&&restored.Reports.Count==0&&restored.RetentionReference>=later&&restored.RetentionDays==1,"revocation made the original clear unrecoverable or weakened retention");
        });
        yield return ("failed replacement of a confirmed clear keeps the previous intent",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,IndexClearPhase.IntentSaved);var cipher=new FailCanonicalCipher();var path=Path.Combine(temp.Path,"index-clear.dpapi");
            using(var old=new EncryptedStateStore(Path.Combine(temp.Path,"state.dpapi"),cipher,new FixedTime(RuntimeTests.Now)))
            {var state=old.Load();state.Reserve(new(),0.01m,RuntimeTests.Now.AddDays(5));old.Save(state);}
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now)))
            {
                var state=store.Load();Check(store.ReportCommitBlocked,"intervening state did not block clear");var before=File.ReadAllBytes(path);cipher.FailClearIntent=true;
                try{store.ClearIndex(state);throw new Exception("failed intent replacement accepted");}catch(SafetyException){}
                Check(before.SequenceEqual(File.ReadAllBytes(path)),"failed replacement deleted or changed the previous intent");cipher.FailClearIntent=false;
            }
            using var reopened=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));_ = reopened.Load();
            Check(reopened.ReportCommitBlocked&&reopened.IndexClears.Exists,"failed reconfirmation silently abandoned the original clear");
        });
        yield return ("exception after canonical replacement is reconciled without rollback or duplicate model",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();using var store=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(RuntimeTests.Now));
            var state=store.Load();var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};state.Ingest(batch);store.Save(state);var calls=0;
            store.CommitTestBoundary=p=>{if(p==ReportCommitPhase.CanonicalReplaced)throw new IOException("synthetic post-replace error");};
            await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);
            Check(calls==1&&state.Requests==1&&state.Reports.Count==1&&store.ReportCommits.Read() is null&&!store.MaintenanceBlocked,"post-replace error rolled back a committed report");
            Check(store.Reviews.Open(state,state.Reports.Single()).Markdown.Contains("模型判断已解决"),"post-replace reconciliation lost the assessment");
        }));
        yield return ("report that expires after canonical replacement is never published",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var clock=new MutableTime(RuntimeTests.Now);using var store=new BusinessStore(temp.Path,new SyntheticCipher(),clock);
            var state=store.Load();var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};state.Ingest(batch);store.Save(state);var calls=0;
            store.CommitTestBoundary=p=>{if(p==ReportCommitPhase.CanonicalReplaced)clock.Now=RuntimeTests.Now.AddDays(31);};
            try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);throw new Exception("expired result was published");}
            catch(MonitoringFault e){Check(e.Category==MonitorFailure.Retention,"wrong expired-result error");}
            Check(calls==1&&state.Reports.Count==0&&store.ReportCommits.Read() is null,"expired report or recovery evidence was retained");
        }));
        yield return ("failed canonical replace at 100 reports preserves oldest human review",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new FailCanonicalCipher();using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));
            var state=store.Load();var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};state.Ingest(batch);
            for(var i=0;i<100;i++)
            {
                var report=Demo.Analyze(batch,"Synthetic old "+i);state.Reports.Add(new(RuntimeTests.Now.AddTicks(i),batch.Scope.Key,report.Task,ReportRenderer.Markdown(report),false,batch.Messages.Min(m=>m.CreatedAt)));
            }
            store.Save(state);var first=state.Reports[0];var captured=store.Reviews.Capture(state,Demo.Analyze(batch,"Synthetic old 0"));
            store.Reviews.Decide(state,captured,0,true,true);cipher.Fail=true;var calls=0;
            try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);throw new Exception("canonical failure accepted");}catch(SafetyException){}
            cipher.Fail=false;Check(calls==1&&state.Reports.Count==100&&store.ReportCommits.Read() is null,"failed report changed bounded history");store.Dispose();
            using var reopened=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var restored=reopened.Load();
            Check(restored.Reports.Count==100&&restored.Reports.Contains(first)&&reopened.Reviews.Open(restored,first).Report!.Issues[0].HumanConfirmed,"failed replacement removed the oldest confirmed review");
            Check(restored.Requests==1&&restored.ReservedUsd>0,"failed canonical commit released budget");
        }));
        yield return ("journal write failure cannot publish report or release a reserved model attempt",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new FailCanonicalCipher();using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));
            var state=store.Load();var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};state.Ingest(batch);store.Save(state);cipher.FailJournal=true;var calls=0;
            try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);throw new Exception("journal failure accepted");}catch(SafetyException){}
            Check(calls==1&&state.Reports.Count==0&&state.Requests==1&&state.ReservedUsd>0,"journal failure published an undurable result or released its reservation");
            cipher.FailJournal=false;store.Dispose();using var reopened=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var restored=reopened.Load();
            try{await new MonitorEngine(restored,reopened).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);throw new Exception("failed commit was automatically repeated");}catch(SafetyException){}
            Check(calls==1&&restored.Requests==1,"failed journal allowed another model call");
        }));
        foreach(var corrupt in new[]{false,true})
        {
            var badArchive=corrupt;
            yield return ($"{(badArchive?"corrupt archive":"review barrier")} is refused before model reservation and dispatch",()=>Run(async()=>
            {
                using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var now=RuntimeTests.Now;
                using(var setup=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
                {var initial=setup.Load();initial.Ingest(Demo.CreateBatch(now) with{IsDemo=false});setup.Save(initial);}
                File.WriteAllText(Path.Combine(temp.Path,badArchive?"report-reviews.dpapi":"report-reviews.dpapi.blocked"),"synthetic unavailable");
                using var store=new BusinessStore(temp.Path,cipher,new FixedTime(now));var state=store.Load();var calls=0;var batch=Demo.CreateBatch(now) with{IsDemo=false};
                try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},now,utcNow:()=>now);throw new Exception("unavailable snapshot accepted");}catch(MonitoringFault){}
                Check(calls==0&&state.Requests==0&&state.ReservedUsd==0&&state.TaskAttempts.Count==0,"model or reservation occurred despite known unavailable snapshot storage");
            }));
        }
        yield return ("journal reconciliation preserves an existing human decision",()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var now=RuntimeTests.Now;
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var state=store.Load();var batch=Demo.CreateBatch(now) with{IsDemo=false};state.Ingest(batch);store.Save(state);
                var report=Demo.Analyze(batch,SyntheticTask);var saved=new SavedReport(now,batch.Scope.Key,SyntheticTask,ReportRenderer.Markdown(report),false,batch.Messages.Min(m=>m.CreatedAt)){CommitId=Guid.NewGuid().ToString("N")};
                store.ReportCommits.Stage(saved,report);state.Reports.Add(saved);store.Save(state);store.Reviews.MergePending(state,store.ReportCommits.Read()!,false);
                var view=store.Reviews.Open(state,saved);store.Reviews.Decide(state,view,0,true,true);
                // Simulate termination after archive replacement, before removing the journal.
            }
            using var restoredStore=new BusinessStore(temp.Path,cipher,new FixedTime(now));var restored=restoredStore.Load();var restoredView=restoredStore.Reviews.Open(restored,restored.Reports.Single());
            Check(restoredStore.ReportCommits.Read() is null&&restoredView.Report!.Issues[0].HumanConfirmed&&restoredView.Decisions.Count==1,"reconciliation overwrote an existing human decision");
        });
        yield return ("failed canonical clear preserves the snapshot until the confirmed clear can resume",()=>
        {
            using var temp=new RuntimeTemp();var cipher=new FailCanonicalCipher();var now=RuntimeTests.Now;
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var state=store.Load();var batch=Demo.CreateBatch(now) with{IsDemo=false};state.Ingest(batch);store.Save(state);
                var report=Demo.Analyze(batch,SyntheticTask);var saved=new SavedReport(now,batch.Scope.Key,SyntheticTask,ReportRenderer.Markdown(report),false,batch.Messages.Min(m=>m.CreatedAt)){CommitId=Guid.NewGuid().ToString("N")};
                store.ReportCommits.Stage(saved,report);state.Reports.Add(saved);store.Save(state);cipher.FailClear=true;
                try{store.ClearIndex(state);throw new Exception("failed clear was accepted");}catch(SafetyException){}
                Check(store.ReportCommits.Read() is not null,"failed canonical clear deleted the only pending snapshot");cipher.FailClear=false;
            }
            using var reopened=new BusinessStore(temp.Path,cipher,new FixedTime(now));var restored=reopened.Load();
            Check(restored.Reports.Count==0&&reopened.ReportCommits.Read() is null&&!reopened.IndexClears.Exists,"confirmed clear did not finish on restart");
        });
        yield return ("corrupt pending journal blocks work but allows canonical recovery revocation",()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var now=RuntimeTests.Now;
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var state=store.Load();var config=new MonitoringConfiguration("dws","corp:synthetic","synthetic-room",now.AddHours(-1),Path.Combine(temp.Path,"synthetic-dws"),Guard.Hash("synthetic-exe"),true,new(),60,30,Guard.Hash("synthetic-credential"));
                BackgroundPolicy.Approve(state,config,now,true);store.Save(state);
            }
            var journal=Path.Combine(temp.Path,"report-commit.dpapi");File.WriteAllText(journal,"corrupt synthetic bytes");
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var state=store.Load();Check(store.ReportCommitBlocked,"corrupt journal did not block work");BackgroundPolicy.Revoke(state);store.SaveRevocation(state);
                try{store.Save(state);throw new Exception("ordinary save bypassed corrupt journal");}catch(MonitoringFault){}
                Check(File.ReadAllText(journal)=="corrupt synthetic bytes","revocation modified the corrupt journal");
            }
            using var raw=new EncryptedStateStore(Path.Combine(temp.Path,"state.dpapi"),cipher,new FixedTime(now));var restored=raw.ReadUntrimmedForCommit();
            Check(restored.UserStopped&&restored.Recovery is null,"recovery authority survived revocation");
        });
        yield return ("blocked revocation preserves advanced retention when the clock later moves back",()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var now=RuntimeTests.Now;
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {var state=store.Load();state.Ingest(Demo.CreateBatch(now));store.Save(state);}
            File.WriteAllText(Path.Combine(temp.Path,"report-commit.dpapi"),"synthetic corrupt journal");
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now.AddDays(31))))
            {var state=store.Load();Check(store.ReportCommitBlocked&&state.Messages.Count==0,"expected retention and journal block");BackgroundPolicy.Revoke(state);store.SaveRevocation(state);}
            using var raw=new EncryptedStateStore(Path.Combine(temp.Path,"state.dpapi"),cipher,new FixedTime(now));var restored=raw.ReadRetained();
            Check(restored.RetentionReference>=now.AddDays(31)&&restored.Messages.Count==0,"clock rollback revived an expired message after revocation");
        });
        foreach(var hadRecovery in new[]{false,true})
        {
            var priorRecovery=hadRecovery;
            yield return ($"plugin safely connects a corrupt commit journal with prior recovery {priorRecovery}",()=>Run(async()=>
            {
                using var fixture=new RuntimeTests.Fixture(seed:priorRecovery);
                File.WriteAllText(Path.Combine(fixture.CanonicalRoot,"report-commit.dpapi"),"synthetic corrupt journal");
                var controller=fixture.Controller;Check(await controller.ConnectAsync(fixture.CanonicalRoot,true)&&controller.Snapshot.Notice.Contains("只读"),"safe read-only connection was unavailable");
                try{await controller.ReadOnceAsync(controller.PreviewMonitoring(fixture.Request),true);throw new Exception("read bypassed journal failure");}catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Storage,"wrong journal refusal");}
                Check(fixture.Services.Calls==0,"blocked connection dispatched a service");
                Check(await controller.ClearIndexAsync(true)&&controller.Snapshot.History.Count==0,"explicit clear did not recover the corrupt report journal");
                await controller.DisconnectAsync();using var raw=new EncryptedStateStore(Path.Combine(fixture.CanonicalRoot,"state.dpapi"),fixture.Cipher,new FixedTime(RuntimeTests.Now));
                var restored=raw.ReadUntrimmedForCommit();Check(restored.Recovery is null&&restored.UserStopped,"plugin connected without durable revocation");
            }));
        }
        yield return ("old-format rewrite of a pending canonical report stays blocked and keeps the journal",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,ReportCommitPhase.CanonicalReplaced);var cipher=new SyntheticCipher();var path=Path.Combine(temp.Path,"state.dpapi");
            var node=JsonNode.Parse(cipher.Unprotect(File.ReadAllBytes(path)))!;node["Reports"]![0]!.AsObject().Remove("CommitId");File.WriteAllBytes(path,cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(node)));
            using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var state=store.Load();
            Check(store.ReportCommitBlocked&&store.ReportCommits.Read() is not null&&store.Reviews.OpenReadable(state,state.Reports.Single()).Report is null,"legacy identity loss was treated as proof of a matching commit");
            try{store.Save(state);throw new Exception("legacy ambiguity did not block writing");}catch(MonitoringFault){}
        });
        yield return ("missing committed snapshot and journal block later work",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,ReportCommitPhase.ArchiveMerged);
            File.Delete(Path.Combine(temp.Path,"report-commit.dpapi"));File.Delete(Path.Combine(temp.Path,"report-reviews.dpapi"));
            using var store=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(RuntimeTests.Now));var state=store.Load();
            Check(store.ReportCommitBlocked&&store.Reviews.OpenReadable(state,state.Reports.Single()).Report is null,"missing committed evidence appeared complete");
            try{store.PrepareReport(state);throw new Exception("missing committed evidence permitted new model work");}catch(MonitoringFault){}
        });
        yield return ("expired pending report and evidence are removed during recovery",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,ReportCommitPhase.CanonicalReplaced);
            using var store=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(RuntimeTests.Now.AddDays(31)));var state=store.Load();
            Check(!store.MaintenanceBlocked&&state.Reports.Count==0&&store.ReportCommits.Read() is null,"expired journal or report survived retention");
        });
        yield return ("successful 100-report commit prunes the evicted review only after replacement",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));
            var state=store.Load();var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};state.Ingest(batch);
            for(var i=0;i<100;i++)
            {
                var old=Demo.Analyze(batch,"Synthetic old "+i);
                state.Reports.Add(new(RuntimeTests.Now.AddTicks(i),batch.Scope.Key,old.Task,ReportRenderer.Markdown(old),false,batch.Messages.Min(m=>m.CreatedAt)));
            }
            store.Save(state);var oldest=state.Reports[0];var kept=state.Reports[1];
            store.Reviews.Decide(state,store.Reviews.Capture(state,Demo.Analyze(batch,"Synthetic old 0")),0,true,true);
            store.Reviews.Decide(state,store.Reviews.Capture(state,Demo.Analyze(batch,"Synthetic old 1")),0,true,true);
            var oldestKey=ReportReviewSnapshot.Key(oldest);var keptKey=ReportReviewSnapshot.Key(kept);var sawReplace=false;
            store.CommitTestBoundary=p=>
            {
                if(p!=ReportCommitPhase.CanonicalReplaced)return;
                sawReplace=true;
                using var canonical=JsonDocument.Parse(cipher.Unprotect(File.ReadAllBytes(Path.Combine(temp.Path,"state.dpapi"))));
                var tasks=canonical.RootElement.GetProperty("Reports").EnumerateArray().Select(r=>r.GetProperty("Task").GetString()).ToArray();
                Check(!tasks.Contains("Synthetic old 0")&&tasks.Contains("Synthetic old 1")&&tasks.Contains(SyntheticTask),"canonical replacement did not drop only the oldest report");
                var during=ReadLedger(cipher,temp.Path);var fresh=state.Reports.Single(r=>r.Task==SyntheticTask);
                Check(during.Reports.ContainsKey(oldestKey)&&during.Reports.ContainsKey(keptKey)&&!during.Reports.ContainsKey(ReportReviewSnapshot.Key(fresh))&&File.Exists(Path.Combine(temp.Path,"report-commit.dpapi")),"evicted review was removed before the replacement was durable");
            };
            var calls=0;
            await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);
            var freshSaved=state.Reports.Single(r=>r.Task==SyntheticTask);var freshView=store.Reviews.Open(state,freshSaved);var keptView=store.Reviews.Open(state,kept);var after=ReadLedger(cipher,temp.Path);
            Check(sawReplace&&calls==1&&state.Reports.Count==100&&!state.Reports.Contains(oldest)&&state.Reports.Contains(kept)&&store.ReportCommits.Read() is null,"successful eviction changed the wrong history or left a journal");
            Check(freshView.CanReview&&freshView.Markdown.Contains("模型判断已解决")&&!freshView.Markdown.Contains("人工确认已解决"),"new model assessment was lost or presented as a human decision");
            Check(keptView.Report!.Issues[0].HumanConfirmed&&keptView.Decisions.Count==1,"retained human confirmation was dropped with the evicted report");
            Check(!after.Reports.ContainsKey(oldestKey)&&after.Reports.ContainsKey(keptKey)&&after.Reports.ContainsKey(ReportReviewSnapshot.Key(freshSaved)),"evicted review survived a confirmed replacement or the new snapshot was not merged");
            store.Dispose();
            using var reopened=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var restored=reopened.Load();
            Check(!reopened.MaintenanceBlocked&&reopened.ReportCommits.Read() is null&&restored.Reports.Count==100&&!restored.Reports.Contains(oldest)&&reopened.Reviews.Open(restored,kept).Report!.Issues[0].HumanConfirmed,"reopen lost the retained confirmation or resurrected the evicted report");
        }));
        foreach(var duplicate in new[]{false,true})
        {
            var copied=duplicate;
            yield return ($"{(copied?"duplicate report rows":"rewritten commit id")} conflict keeps the journal and blocks guessing",()=>
            {
                using var temp=new RuntimeTemp();CrashChild(temp.Path,ReportCommitPhase.CanonicalReplaced);var cipher=new SyntheticCipher();
                var path=Path.Combine(temp.Path,"state.dpapi");var journalPath=Path.Combine(temp.Path,"report-commit.dpapi");
                var node=JsonNode.Parse(cipher.Unprotect(File.ReadAllBytes(path)))!;
                if(copied)node["Reports"]!.AsArray().Add(node["Reports"]![0]!.DeepClone());else node["Reports"]![0]!["CommitId"]=Guid.NewGuid().ToString("N");
                File.WriteAllBytes(path,cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(node)));var journal=File.ReadAllBytes(journalPath);
                using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var state=store.Load();
                Check(store.ReportCommitBlocked&&journal.SequenceEqual(File.ReadAllBytes(journalPath)),"conflicting commit identity was merged or the journal was rewritten");
                Check(store.Reviews.OpenReadable(state,state.Reports[0]).Report is null,"conflict invented a model assessment");
                try{store.PrepareReport(state);throw new Exception("conflicting journal allowed another model preparation");}catch(MonitoringFault){}
                Check(state.Requests==1&&state.Reports.Count==(copied?2:1),"conflict recovery changed budget or deleted the canonical rows");
            });
        }
        yield return ("a committed report removed before recovery clears the orphan journal and is not resurrected",()=>
        {
            using var temp=new RuntimeTemp();CrashChild(temp.Path,ReportCommitPhase.CanonicalReplaced);var cipher=new SyntheticCipher();
            var path=Path.Combine(temp.Path,"state.dpapi");var node=JsonNode.Parse(cipher.Unprotect(File.ReadAllBytes(path)))!;
            node["Reports"]=new JsonArray();File.WriteAllBytes(path,cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(node)));
            using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var state=store.Load();
            Check(!store.MaintenanceBlocked&&state.Reports.Count==0&&store.ReportCommits.Read() is null&&!File.Exists(Path.Combine(temp.Path,"report-commit.dpapi")),"orphan journal blocked recovery or recreated the deleted report");
            Check(state.Requests==1&&state.ReservedUsd>0,"orphan cleanup released the reserved attempt");
            var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};var calls=0;
            try{Run(()=>new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now));throw new Exception("orphan cleanup allowed the same attempt to repeat");}
            catch(SafetyException){}
            Check(calls==0&&state.Requests==1,"missing report was treated as a new model attempt");
        });
        yield return ("journal reconciliation keeps a sticky invalidation and its human decision",()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();var now=RuntimeTests.Now;
            using(var store=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var state=store.Load();var batch=Demo.CreateBatch(now) with{IsDemo=false};state.Ingest(batch);store.Save(state);
                var report=Demo.Analyze(batch,SyntheticTask);var saved=new SavedReport(now,batch.Scope.Key,SyntheticTask,ReportRenderer.Markdown(report),false,batch.Messages.Min(m=>m.CreatedAt)){CommitId=Guid.NewGuid().ToString("N")};
                store.ReportCommits.Stage(saved,report);state.Reports.Add(saved);store.Save(state);store.Reviews.MergePending(state,store.ReportCommits.Read()!,false);
                store.Reviews.Decide(state,store.Reviews.Open(state,saved),0,true,true);
            }
            var ledger=ReadLedger(cipher,temp.Path);var key=ledger.Reports.Keys.Single();ledger.Reports[key]=ledger.Reports[key] with{Invalidated=true};
            File.WriteAllBytes(Path.Combine(temp.Path,"report-reviews.dpapi"),cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(ledger)));
            using(var restoredStore=new BusinessStore(temp.Path,cipher,new FixedTime(now)))
            {
                var restored=restoredStore.Load();var restoredView=restoredStore.Reviews.Open(restored,restored.Reports.Single());
                Check(restoredStore.ReportCommits.Read() is null&&restoredView.Decisions.Count==1&&restoredView.Decisions[0].Resolved&&!restoredView.CanReview&&restoredView.Markdown.Contains("失效")&&!restoredView.Report!.Issues[0].HumanConfirmed,"recovery cleared sticky invalidation or dropped the recorded decision");
            }
            using var again=new BusinessStore(temp.Path,cipher,new FixedTime(now));var second=again.Load();var secondView=again.Reviews.Open(second,second.Reports.Single());
            Check(secondView.Decisions.Count==1&&secondView.Decisions[0].Resolved&&!secondView.CanReview&&again.ReportCommits.Read() is null,"a second reopen rewrote the reconciled decision");
        });
        yield return ("cancellation after the model returns keeps the reservation and writes no journal",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();using var store=new BusinessStore(temp.Path,new SyntheticCipher(),new FixedTime(RuntimeTests.Now));
            var state=store.Load();var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};state.Ingest(batch);store.Save(state);
            using var cancel=new CancellationTokenSource();var calls=0;
            try
            {
                await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;cancel.Cancel();return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,cancel.Token,utcNow:()=>RuntimeTests.Now);
                throw new Exception("cancelled model result was committed");
            }
            catch(OperationCanceledException){}
            Check(calls==1&&state.Reports.Count==0&&state.Requests==1&&state.ReservedUsd>0&&store.ReportCommits.Read() is null,"cancellation published a report or released its reservation");
            try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(b,t,_)=>{calls++;return Task.FromResult(new ModelResult(Demo.Analyze(b,t),5,7));},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);throw new Exception("cancelled attempt was repeated");}
            catch(SafetyException){}
            Check(calls==1&&state.Requests==1,"a later pass repeated the cancelled model attempt");
        }));
        yield return ("unknown model result on the commit store leaves no journal and blocks a retry",()=>Run(async()=>
        {
            using var temp=new RuntimeTemp();var cipher=new SyntheticCipher();using var store=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));
            var state=store.Load();var batch=Demo.CreateBatch(RuntimeTests.Now) with{IsDemo=false};state.Ingest(batch);store.Save(state);var calls=0;
            try{await new MonitorEngine(state,store).AnalyzeOnceAsync(batch,SyntheticTask,new(),(_,_,_)=>{calls++;throw new IOException("synthetic unknown result");},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);throw new Exception("unknown result was accepted");}
            catch(SafetyException){}
            Check(calls==1&&state.Reports.Count==0&&state.Requests==1&&state.ReservedUsd>0&&store.ReportCommits.Read() is null&&!File.Exists(Path.Combine(temp.Path,"report-commit.dpapi")),"unknown result published a report or left a commit journal");
            store.Dispose();
            using var reopened=new BusinessStore(temp.Path,cipher,new FixedTime(RuntimeTests.Now));var restored=reopened.Load();
            try{await new MonitorEngine(restored,reopened).AnalyzeOnceAsync(batch,SyntheticTask,new(),(_,_,_)=>{calls++;throw new IOException("synthetic unknown result");},RuntimeTests.Now,utcNow:()=>RuntimeTests.Now);throw new Exception("unknown result was retried after restart");}
            catch(SafetyException){}
            Check(calls==1&&restored.Requests==1&&restored.Reports.Count==0,"restart repeated or forgot the unknown attempt");
        }));
        yield return ("plugin canonical analysis recovers the model snapshot without a second call or leftover journal",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;
            Check(await c.ConnectAsync(f.CanonicalRoot,true)&&await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true)&&await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true),"synthetic canonical analysis could not prepare");
            f.Services.Analyze=(batch,task,_)=>Task.FromResult(new ModelResult(AnalysisValidator.Validate(SyntheticModel(batch),batch,task),1,1));
            Check(await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true),"synthetic canonical analysis failed");
            Check(!File.Exists(Path.Combine(f.CanonicalRoot,"report-commit.dpapi"))&&c.Snapshot.Markdown.Contains("模型判断已解决")&&!c.Snapshot.Markdown.Contains("人工确认已解决")&&!c.Snapshot.Markdown.Contains("历史模型结果")&&c.Snapshot.CanReviewReport,"successful plugin commit left a journal or hid the current model assessment");
            await c.DisconnectAsync();
            await using var next=new InsightController(f.PluginRoot,"synthetic-next-commit-owner",f.Cipher,f.Services,f.Clock);
            Check(await next.ConnectAsync(f.CanonicalRoot,true)&&await next.SelectHistoryAsync(next.Snapshot.History.Single()),"reconnected plugin could not open the committed report");
            Check(next.Snapshot.Markdown.Contains("模型判断已解决")&&!next.Snapshot.Markdown.Contains("人工确认已解决")&&!next.Snapshot.Markdown.Contains("历史模型结果")&&next.Snapshot.CanReviewReport&&f.Services.Models==1&&f.Services.Sends==0,"plugin reopen lost the model assessment or repeated the model");
        }));
        yield return ("plugin stop during an in-flight model leaves no commit journal and keeps the reservation",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;
            Check(await c.ConnectAsync(f.CanonicalRoot,true)&&await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true)&&await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true),"synthetic stop fixture could not prepare");
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Services.Analyze=async(batch,task,_)=>{entered.TrySetResult();await release.Task;return new ModelResult(AnalysisValidator.Validate(SyntheticModel(batch),batch,task),1,1);};
            var running=c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping=c.StopMonitoringAsync();release.TrySetResult();
            Check(!await running.WaitAsync(TimeSpan.FromSeconds(5)),"stopped model result was published");
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Services.Models==1&&f.Services.Sends==0&&c.Snapshot.Requests==1&&c.Snapshot.ReservedUsd>0&&c.Snapshot.History.Count==0&&!File.Exists(Path.Combine(f.CanonicalRoot,"report-commit.dpapi")),"stop after the model returned committed a report or released its reservation");
            await c.DisconnectAsync();
            using var reopened=new BusinessStore(f.CanonicalRoot,f.Cipher,new FixedTime(RuntimeTests.Now));var restored=reopened.Load();
            Check(restored.UserStopped&&restored.Recovery is null&&restored.Requests==1&&restored.Reports.Count==0&&reopened.ReportCommits.Read() is null,"stop persisted a report or dropped the cancelled reservation");
        }));
    }
}
