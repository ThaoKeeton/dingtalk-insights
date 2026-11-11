using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

internal static class ReportReviewTests
{
    static readonly DateTimeOffset Now=DateTimeOffset.Parse("2034-03-02T10:00:00Z");
    static void Check(bool condition,string message="review assertion failed"){if(!condition)throw new Exception(message);}
    static void Denied(Action action){try{action();throw new Exception("expected safe review refusal");}catch(SafetyException){}}
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("model resolution is visible without a human review and survives reopening",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);
            Check(view.Markdown.Contains("模型判断已解决")&&!view.Markdown.Contains("人工确认已解决"),"the model resolution was hidden or presented as a human decision");
            Check(!view.Report!.Issues[0].HumanConfirmed&&view.Report.Issues[0].Status==IssueStatus.PendingVerification,"the model was granted human certainty");
            f.Reopen();view=f.Store.Reviews.Open(f.State,f.State.Reports.Single());
            Check(view.Markdown.Contains("模型判断已解决")&&!view.Markdown.Contains("人工确认已解决"),"reopening lost the model assessment");
            view=f.Store.Reviews.Decide(f.State,view,0,true,true);
            Check(view.Markdown.Contains("人工确认已解决"),"explicit human confirmation was lost");
            view=f.Store.Reviews.Decide(f.State,view,0,false,true);
            Check(view.Markdown.Contains("模型判断已解决")&&!view.Markdown.Contains("人工确认已解决"),"revoking human confirmation lost the original model assessment");
        });
        yield return ("human confirmation and revocation make unenumerable old send formats a separate risk",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);
            Check(!view.SendIdentity.LegacyRisk,"new unreviewed report unnecessarily requires legacy-format acknowledgement");
            view=f.Store.Reviews.Decide(f.State,view,0,true,true);Check(view.SendIdentity.LegacyRisk,"human confirmation did not flag prior old-format risk");
            view=f.Store.Reviews.Decide(f.State,view,0,false,true);Check(view.SendIdentity.LegacyRisk,"revocation forgot earlier human-format risk");
        });
        yield return ("a revised source makes the model assessment explicitly historical",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Capture(f.State,f.Report);
            var revised=f.Report.Batch.Messages[0] with{Text="Synthetic revised source"};
            f.State.Ingest(f.Report.Batch with{Messages=[revised]});f.Store.Save(f.State);
            var view=f.Store.Reviews.Open(f.State,f.Saved);
            Check(!view.CanReview&&!view.Report!.Issues[0].HumanConfirmed&&view.Markdown.Contains("历史模型结果")&&view.Markdown.Contains("不代表当前状态")&&view.Markdown.Contains("历史：模型判断已解决"),"old model assessment appeared current");
        });
        foreach(var noIssues in new[]{false,true})
        {
            var empty=noIssues;
            yield return ($"model report becomes historical when an unquoted batch source changes (empty={empty})",()=>
            {
                using var f=new Fixture();var extra=f.Report.Batch.Messages[0] with{MessageId="other-source",Text="Synthetic unquoted context",CreatedAt=Now.AddMinutes(-5)};
                var batch=f.Report.Batch with{Messages=[f.Report.Batch.Messages[0],extra]};f.State.Ingest(batch);
                var raw=empty?"""{"issues":[],"pendingQuestions":[]}""":"""{"issues":[{"title":"Synthetic review","summary":"Needs verification","status":"resolved","communication":"Confirm acceptance","nextStep":"Retest","evidence":[{"messageId":"review-source","quote":"fix submitted"}]}],"pendingQuestions":[]}""";
                var report=AnalysisValidator.Validate(raw,batch,"Synthetic full-batch task");
                var saved=new SavedReport(Now,batch.Scope.Key,report.Task,ReportRenderer.Markdown(report),false,batch.Messages.Min(m=>m.CreatedAt));
                f.State.Reports.Add(saved);f.Store.Save(f.State);var live=f.Store.Reviews.Capture(f.State,report);
                if(!empty)live=f.Store.Reviews.Decide(f.State,live,0,true,true);
                f.State.Ingest(batch with{Messages=[extra with{Text="Synthetic changed unquoted context"}]});f.Store.Save(f.State);
                live=live.Refresh(f.State);var historical=f.Store.Reviews.Open(f.State,saved);
                Check(historical.Markdown.Contains("历史模型结果")&&historical.Markdown.Contains("不代表当前状态"),"unquoted or empty-evidence batch was falsely presented as current");
                if(!empty)Check(historical.Report!.Issues[0].HumanConfirmed,"unquoted input incorrectly invalidated an evidence-bound human decision");
                f.State.Ingest(batch with{Messages=[extra]});f.Store.Save(f.State);
                Check(live.Refresh(f.State).Markdown.Contains("历史模型结果")&&f.Store.Reviews.Open(f.State,saved).Markdown.Contains("历史模型结果"),"A B A resurrected current model context");
            });
        }
        yield return ("legacy snapshot without the model resolution remains readable without guessing",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Capture(f.State,f.Report);
            var ledger=JsonSerializer.Deserialize<ReportReviewLedger>(f.Cipher.Unprotect(File.ReadAllBytes(f.ReviewsPath)))!;
            var key=ledger.Reports.Keys.Single();var snapshot=ledger.Reports[key];
            ledger.Reports[key]=snapshot with{ModelJson=snapshot.ModelJson.Replace("\"status\":\"resolved\"","\"status\":\"pending_verification\"")};
            File.WriteAllBytes(f.ReviewsPath,f.Cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(ledger)));f.Reopen();
            var view=f.Store.Reviews.Open(f.State,f.Saved);
            Check(view.CanReview&&view.Markdown.Contains("待验证")&&!view.Markdown.Contains("模型判断已解决")&&!view.Markdown.Contains("人工确认已解决"),"legacy missing model status was inferred");
        });
        yield return ("identical legacy report text cannot merge different model assessments",()=>
        {
            using var f=new Fixture();var first=f.Store.Reviews.Capture(f.State,f.Report);
            var raw=JsonSerializer.Deserialize<ReportReviewLedger>(f.Cipher.Unprotect(File.ReadAllBytes(f.ReviewsPath)))!.Reports.Values.Single().ModelJson;
            var other=AnalysisValidator.Validate(raw.Replace("\"status\":\"resolved\"","\"status\":\"pending_verification\""),f.Report.Batch,f.Report.Task);
            Check(ReportRenderer.Markdown(other)==f.Saved.Markdown,"fixture does not reproduce the legacy text collision");
            Denied(()=>f.Store.Reviews.Capture(f.State,other));
            Check(f.Store.Reviews.Open(f.State,f.Saved).Markdown==first.Markdown,"collision changed the stored assessment");
        });
        yield return ("identical legacy report text cannot merge different evidence revisions",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Capture(f.State,f.Report);
            var revised=f.Report.Batch.Messages[0] with{Text="Synthetic changed context; fix submitted"};var batch=f.Report.Batch with{Messages=[revised]};
            f.State.Ingest(batch);f.Store.Save(f.State);
            var raw="""{"issues":[{"title":"Synthetic review","summary":"Needs verification","status":"resolved","communication":"Confirm acceptance","nextStep":"Retest","evidence":[{"messageId":"review-source","quote":"fix submitted"}]}],"pendingQuestions":[]}""";
            var changed=AnalysisValidator.Validate(raw,batch,f.Report.Task);
            Check(ReportRenderer.Markdown(changed)==f.Saved.Markdown,"fixture did not reproduce the identical legacy Markdown collision");
            Denied(()=>f.Store.Reviews.Capture(f.State,changed));
        });
        yield return ("a failed assessment snapshot leaves original history readable without inventing a model status",()=>
        {
            using var f=new Fixture();f.Cipher.FailReviews=true;Denied(()=>f.Store.Reviews.Capture(f.State,f.Report));f.Cipher.FailReviews=false;
            f.Reopen();var history=f.Store.Reviews.Open(f.State,f.Saved);
            Check(history.Report is null&&!history.CanReview&&history.Markdown==f.Saved.Markdown&&!history.Markdown.Contains("模型判断已解决")&&!history.Markdown.Contains("人工确认已解决"),"failed snapshot fabricated a durable assessment");
        });
        yield return ("review confirmed and reopened decisions survive a new canonical owner",()=>
        {
            using var f=new Fixture();var first=f.Store.Reviews.Capture(f.State,f.Report);var original=f.Saved.Markdown;
            var resolved=f.Store.Reviews.Decide(f.State,first,0,true,true);
            Check(resolved.Report!.Issues[0].HumanConfirmed&&resolved.Markdown.Contains("人工确认已解决"));
            Check(f.State.Reports.Single().Markdown==original&&f.State.Requests==3&&f.State.ReservedUsd==0.04m,"review rewrote original report/budget");
            f.Reopen();var restored=f.Store.Reviews.Open(f.State,f.State.Reports.Single());
            Check(restored.Report!.Issues[0].HumanConfirmed&&restored.Decisions.Single().RecordedAt==Now,"restart lost human verification");
            var opened=f.Store.Reviews.Decide(f.State,restored,0,false,true);Check(!opened.Report!.Issues[0].HumanConfirmed&&opened.Markdown.Contains("已撤销"));
            f.Reopen();Check(!f.Store.Reviews.Open(f.State,f.State.Reports.Single()).Report!.Issues[0].HumanConfirmed,"reopened state was temporary");
        });
        yield return ("review rejects no confirmation and preserves the exact view on write failure",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);var before=File.ReadAllBytes(f.ReviewsPath);
            Denied(()=>f.Store.Reviews.Decide(f.State,view,0,true,false));Check(before.SequenceEqual(File.ReadAllBytes(f.ReviewsPath)));
            f.Cipher.Fail=true;Denied(()=>f.Store.Reviews.Decide(f.State,view,0,true,true));
            Check(!view.Report!.Issues[0].HumanConfirmed&&before.SequenceEqual(File.ReadAllBytes(f.ReviewsPath)),"failed save mutated visible/stored certainty");
        });
        yield return ("review duplicate identical decision is idempotent and stale view cannot overwrite it",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);var decided=f.Store.Reviews.Decide(f.State,view,0,true,true);var bytes=File.ReadAllBytes(f.ReviewsPath);
            var again=f.Store.Reviews.Decide(f.State,decided,0,true,true);Check(again.Fingerprint==decided.Fingerprint&&bytes.SequenceEqual(File.ReadAllBytes(f.ReviewsPath)));
            Denied(()=>f.Store.Reviews.Decide(f.State,view,0,false,true));
        });
        yield return ("review cannot transfer confirmation to another report or infer it from model resolved",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);f.Store.Reviews.Decide(f.State,view,0,true,true);
            var other=f.Saved with{CreatedAt=Now.AddTicks(1)};f.State.Reports.Add(other);f.Store.Save(f.State);
            Check(!f.Store.Reviews.Open(f.State,other).CanReview,"different report inherited evidence snapshot");
            Check(!f.Report.Issues[0].HumanConfirmed&&f.Report.Issues[0].Status==IssueStatus.PendingVerification,"model resolved became human approval");
            Denied(()=>f.Store.Reviews.Decide(f.State,f.Store.Reviews.Open(f.State,other),0,true,true));
        });
        yield return ("review evidence revision invalidates certainty durably through A B A and old state rewriting",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);f.Store.Reviews.Decide(f.State,view,0,true,true);
            var source=f.Report.Batch.Messages[0];f.State.Ingest(f.Report.Batch with{Messages=[source with{Text="Revised synthetic evidence"}]});f.Store.Save(f.State);
            var stale=f.Store.Reviews.Open(f.State,f.Saved);Check(!stale.CanReview&&!stale.Report!.Issues[0].HumanConfirmed&&stale.Markdown.Contains("失效"));
            Denied(()=>f.Store.Reviews.Decide(f.State,stale,0,true,true));
            f.State.Ingest(f.Report.Batch);f.Store.Save(f.State);f.Reopen();
            Check(!f.Store.Reviews.Open(f.State,f.State.Reports.Single()).CanReview,"A B A resurrected certainty");
        });
        yield return ("review retained proof survives legacy state rewrite without copying an active ledger",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);f.Store.Dispose();
            using(var legacy=new EncryptedStateStore(Path.Combine(f.Root,"state.dpapi"),f.Cipher,f.Clock)){var state=legacy.Load();legacy.Save(state);}
            f.Open();Check(f.Store.Reviews.Open(f.State,f.State.Reports.Single()).Report!.Issues[0].HumanConfirmed&&f.State.Requests==3);
        });
        yield return ("review failed invalidation cannot resurrect certainty after durable B and legacy A rewrite",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);
            f.State.Ingest(f.Report.Batch with{Messages=[f.Report.Batch.Messages[0] with{Text="Synthetic revised B"}]});
            f.Cipher.FailReviews=true;f.Store.Save(f.State);f.Cipher.FailReviews=false;f.Store.Dispose();
            using(var legacy=new EncryptedStateStore(Path.Combine(f.Root,"state.dpapi"),f.Cipher,f.Clock))
            {
                var changed=legacy.Load();Check(changed.Messages.Values.Single().Message.Text=="Synthetic revised B","canonical B was not durable");
                changed.Ingest(f.Report.Batch);legacy.Save(changed);
            }
            f.Open();var readable=f.Store.Reviews.OpenReadable(f.State,f.Saved);
            Check(!readable.CanReview&&readable.Report is null&&readable.Unavailable&&!readable.Markdown.Contains("人工确认已解决"),"interrupted A B A resurrected certainty");
            Denied(()=>f.Store.Reviews.Open(f.State,f.Saved));
        });
        yield return ("review failed proof and barrier writes cannot commit a canonical source revision",()=>
        {
            using var f=new Fixture(markerFailure:true);f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);
            var path=Path.Combine(f.Root,"state.dpapi");var before=File.ReadAllBytes(path);
            f.State.Ingest(f.Report.Batch with{Messages=[f.Report.Batch.Messages[0] with{Text="Uncommitted synthetic B"}]});f.Cipher.FailReviews=true;
            Denied(()=>f.Store.Save(f.State));
            Check(before.SequenceEqual(File.ReadAllBytes(path))&&f.Store.ReviewMaintenanceBlocked&&!File.Exists(f.ReviewsPath+".blocked"),"source committed without durable invalidation or barrier");
        });
        yield return ("review legacy Markdown stays readable and cannot manufacture structured evidence",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Open(f.State,f.Saved);
            Check(!view.CanReview&&view.Report is null&&view.Markdown==f.Saved.Markdown&&!File.Exists(f.ReviewsPath));
        });
        yield return ("review snapshots prune with retention or explicit index clearing and keep attempts",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);
            f.Clock.Now=Now.AddDays(31);f.Store.Save(f.State);Denied(()=>f.Store.Reviews.Open(f.State,f.Saved));
            Check(!File.ReadAllText(f.ReviewsPath).Contains("Synthetic review body"),"expired evidence survived archive pruning");
            f.Clock.Now=Now;f.Reopen();Check(f.State.Requests==3&&f.State.ReservedUsd==0.04m,"retention reset costs");
        });
        yield return ("review final trim and startup rewrite prune the same exact canonical instant",()=>
        {
            foreach(var startup in new[]{false,true})
            {
                using var f=new Fixture();f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);
                var boundary=f.Report.Batch.Messages[0].CreatedAt.AddDays(30);f.Clock.Sequence.Enqueue(boundary);f.Clock.Sequence.Enqueue(boundary.AddTicks(1));
                if(startup)f.Reopen();else f.Store.Save(f.State);
                var ledger=JsonSerializer.Deserialize<ReportReviewLedger>(f.Cipher.Unprotect(File.ReadAllBytes(f.ReviewsPath)))!;
                Check(ledger.Reports.Count==f.State.Reports.Count&&!ledger.Reports.Values.SelectMany(s=>s.Sources).Any(s=>s.CreatedAt<f.State.RetentionCutoff),"canonical trim committed before matching archive cleanup");
            }
        });
        yield return ("review required privacy prune fails the canonical write but retained history remains readable",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);
            var statePath=Path.Combine(f.Root,"state.dpapi");var before=File.ReadAllBytes(statePath);
            f.Cipher.FailReviews=true;f.Clock.Now=Now.AddDays(31);Denied(()=>f.Store.Save(f.State));
            Check(before.SequenceEqual(File.ReadAllBytes(statePath)),"failed privacy prune committed expired canonical removal");
            f.Reopen();Check(f.State.Reports.Count==0&&!f.Store.ReviewsAvailable&&f.Store.ReviewMaintenanceBlocked,"startup did not return a safe read-only trimmed state");
            Check(before.SequenceEqual(File.ReadAllBytes(statePath)),"read-only fallback rewrote the original state");
            f.Cipher.FailReviews=false;f.Store.ClearIndex(f.State);f.Reopen();Check(f.State.Reports.Count==0&&f.Store.ReviewsAvailable);
        });
        yield return ("review corrupt snapshot fails closed without overwriting existing bytes",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Capture(f.State,f.Report);File.WriteAllText(f.ReviewsPath,"broken synthetic data");
            Denied(()=>f.Store.Reviews.Open(f.State,f.Saved));Check(File.ReadAllText(f.ReviewsPath)=="broken synthetic data");
        });
        yield return ("review optional archive corruption preserves readable originals and explicit clear recovery",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);
            File.WriteAllText(f.ReviewsPath,"corrupt synthetic review");f.Reopen();
            Check(f.State.Reports.Single().Markdown==f.Saved.Markdown,"optional archive blocked the original report");
            var readable=f.Store.Reviews.OpenReadable(f.State,f.State.Reports.Single());
            Check(readable.Report is null&&!readable.CanReview&&readable.Unavailable&&!readable.Markdown.Contains("人工确认已解决"));
            Check(File.ReadAllText(f.ReviewsPath)=="corrupt synthetic review","corruption silently overwritten");
            f.Store.Save(f.State);f.Store.ClearIndex(f.State);f.Reopen();Check(f.State.Reports.Count==0&&f.State.Requests==3);
        });
        yield return ("review modified proof or duplicate decision indices cannot claim verification",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Decide(f.State,f.Store.Reviews.Capture(f.State,f.Report),0,true,true);
            var doc=JsonSerializer.Deserialize<ReportReviewLedger>(f.Cipher.Unprotect(File.ReadAllBytes(f.ReviewsPath)))!;
            var key=doc.Reports.Keys.Single();var entry=doc.Reports[key];doc.Reports[key]=entry with{Decisions=[entry.Decisions[0] with{IssueFingerprint=new string('A',64)}]};
            File.WriteAllBytes(f.ReviewsPath,f.Cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(doc)));Denied(()=>f.Store.Reviews.Open(f.State,f.Saved));
        });
        yield return ("duplicate legacy report rows remain readable but cannot share an unproven snapshot",()=>
        {
            using var f=new Fixture();f.State.Reports.Add(f.Saved);f.Store.Save(f.State);
            Denied(()=>f.Store.Reviews.Capture(f.State,f.Report));var view=f.Store.Reviews.Open(f.State,f.Saved);
            Check(view.Report is null&&!view.CanReview&&view.Markdown.Contains("Synthetic review")&&!view.Markdown.Contains("模型判断已解决"),"duplicate rows inherited an unproven model assessment");
            f.State.Reports.RemoveAt(0);f.Store.Save(f.State);f.Reopen();view=f.Store.Reviews.Open(f.State,f.Saved);
            Check(view.Report is null&&!view.CanReview&&!view.Markdown.Contains("模型判断已解决"),"removing one duplicate resurrected an ambiguous assessment");
        });
        yield return ("duplicate report rows cannot transfer an existing human decision",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);f.Store.Reviews.Decide(f.State,view,0,true,true);
            f.State.Reports.Add(f.Saved);f.Store.Save(f.State);view=f.Store.Reviews.Open(f.State,f.Saved);
            Check(view.Report is null&&!view.Markdown.Contains("人工确认已解决"),"duplicate row inherited an existing human decision");
            var legacy=System.Text.Json.Nodes.JsonNode.Parse(f.Cipher.Unprotect(File.ReadAllBytes(f.ReviewsPath)))!;
            foreach(var item in legacy["Reports"]!.AsObject())
            {item.Value!.AsObject().Remove("Ambiguous");item.Value.AsObject().Remove("InputRevisions");item.Value.AsObject().Remove("InputsInvalidated");}
            File.WriteAllBytes(f.ReviewsPath,f.Cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(legacy)));
            f.State.Reports.RemoveAt(0);f.Store.Save(f.State);f.Reopen();view=f.Store.Reviews.Open(f.State,f.Saved);
            Check(view.Report is null&&!view.Markdown.Contains("人工确认已解决"),"legacy snapshot rewrite or removing one duplicate revived a possibly different human decision");
        });
        yield return ("new report gets a distinct identity when the legacy fields collide at the same clock tick",()=>
        {
            using var f=new Fixture();var engine=new MonitorEngine(f.State,f.Store);
            engine.AnalyzeOnceAsync(f.Report.Batch,f.Report.Task,new(),(b,t,c)=>Task.FromResult(new ModelResult(f.Report,0,0)),Now,utcNow:()=>Now).GetAwaiter().GetResult();
            Check(f.State.Reports.Count==2&&f.State.Reports[0].CreatedAt!=f.State.Reports[1].CreatedAt,"two distinct reports acquired the same legacy identity");
            var view=f.Store.Reviews.Capture(f.State,f.Report);var decided=f.Store.Reviews.Decide(f.State,view,0,true,true);
            Check(decided.Report!.Issues[0].HumanConfirmed,"distinct-tick report could not store an explicit decision");
            f.Clock.Now=f.Report.Batch.Messages[0].CreatedAt.AddDays(30).AddTicks(1);f.Store.Save(f.State);
            Check(f.State.Reports.Count==0,"distinct report tick extended source retention");
        });
        yield return ("review oversized or duplicate proof records fail closed",()=>
        {
            using var f=new Fixture();var view=f.Store.Reviews.Capture(f.State,f.Report);f.Store.Reviews.Decide(f.State,view,0,true,true);
            var good=File.ReadAllBytes(f.ReviewsPath);
            foreach(var huge in new[]{true,false})
            {
                var doc=JsonSerializer.Deserialize<ReportReviewLedger>(f.Cipher.Unprotect(good))!;var key=doc.Reports.Keys.Single();var entry=doc.Reports[key];
                doc.Reports[key]=huge?entry with{ModelJson=new string('x',ReportReviewSnapshot.MaximumBytes+1)}:entry with{Decisions=[entry.Decisions[0],entry.Decisions[0]]};
                File.WriteAllBytes(f.ReviewsPath,f.Cipher.Protect(JsonSerializer.SerializeToUtf8Bytes(doc)));Denied(()=>f.Store.Reviews.Open(f.State,f.Saved));
            }
        });
        yield return ("review index clear removes corrupt archive only after explicit clearing and keeps send attempts",()=>
        {
            using var f=new Fixture();f.Store.Reviews.Capture(f.State,f.Report);
            var sends=new SendLedger();sends.Attempts[new string('A',64)]=new(new string('B',64),new string('C',64),Now,SendAttemptStatus.Unknown,1);f.Store.SendAttempts.Save(sends);
            var sent=File.ReadAllBytes(Path.Combine(f.Root,"send-attempts.dpapi"));
            File.WriteAllText(f.ReviewsPath,"corrupt synthetic review");f.Store.ClearIndex(f.State);
            Denied(()=>f.Store.Reviews.Open(f.State,f.Saved));
            Check(f.State.Reports.Count==0&&f.State.Requests==3&&f.State.ReservedUsd==0.04m&&sent.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root,"send-attempts.dpapi"))));
            f.Reopen();Check(f.State.Reports.Count==0&&f.Store.SendAttempts.Load().Attempts.Single().Value.Status==SendAttemptStatus.Unknown);
        });
    }
    sealed class Fixture:IDisposable
    {
        readonly bool markerFailure;
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"DTI-review-"+Guid.NewGuid().ToString("N"));
        public string ReviewsPath=>Path.Combine(Root,"report-reviews.dpapi");
        public ReviewCipher Cipher{get;}=new();public ReviewClock Clock{get;}=new();
        public BusinessStore Store{get;private set;}=null!;public InsightState State{get;private set;}=null!;public AnalysisReport Report{get;}public SavedReport Saved{get;}
        public Fixture(bool markerFailure=false)
        {
            this.markerFailure=markerFailure;
            var scope=new QueryScope("synthetic:user","synthetic-review",Now.AddHours(-1),Now);
            var batch=new MessageBatch(scope,[new("review-source",scope.ConversationId,"user","Synthetic sender","Synthetic review body; fix submitted",Now.AddMinutes(-10))],true,[],new Dictionary<string,JsonElement>());
            Report=AnalysisValidator.Validate("""{"issues":[{"title":"Synthetic review","summary":"Needs verification","status":"resolved","communication":"Confirm acceptance","nextStep":"Retest","evidence":[{"messageId":"review-source","quote":"fix submitted"}]}],"pendingQuestions":[]}""",batch,"Synthetic review task");
            Saved=new(Now,scope.Key,Report.Task,ReportRenderer.Markdown(Report),false,batch.Messages[0].CreatedAt);
            Open();State.Ingest(batch);State.Reports.Add(Saved);State.Requests=3;State.ReservedUsd=0.04m;Store.Save(State);
        }
        public void Open(){Store=new(Root,Cipher,Clock,persistReviewBarrier:markerFailure?_=>throw new IOException("SYNTHETIC_MARKER_FAILURE"):null);State=Store.Load();}
        public void Reopen(){Store.Dispose();Open();}
        public void Dispose(){Cipher.Fail=false;Store.Dispose();Directory.Delete(Root,true);}
    }
    sealed class ReviewCipher:IDataCipher
    {
        public bool Fail,FailReviews;
        public byte[] Protect(byte[] bytes)
        {
            using var doc=JsonDocument.Parse(bytes);
            if(Fail||FailReviews&&!doc.RootElement.TryGetProperty("Supervisor",out _))throw new IOException("SYNTHETIC_PRIVATE_STORAGE_ERROR");
            return [71,14,..bytes];
        }
        public byte[] Unprotect(byte[] bytes){if(bytes.Length<2||bytes[0]!=71||bytes[1]!=14)throw new IOException("SYNTHETIC_PRIVATE_STORAGE_ERROR");return bytes[2..];}
    }
    sealed class ReviewClock:TimeProvider
    {
        public DateTimeOffset Now=ReportReviewTests.Now;public Queue<DateTimeOffset> Sequence{get;}=[];
        public override DateTimeOffset GetUtcNow(){if(Sequence.TryDequeue(out var value))Now=value;return Now;}
    }
}
