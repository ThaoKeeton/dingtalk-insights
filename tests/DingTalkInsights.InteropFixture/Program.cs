using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

var root=Path.GetFullPath(args[1]);var id=Path.GetFileName(root);
var expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DingTalkInsights-Interop",id));
if(!Guid.TryParseExact(id,"N",out _)||root!=expected||!File.Exists(Path.Combine(root,".fixture-owner")))throw new IOException("Only guarded synthetic fixture roots are allowed.");
for(var part=root;part is not null;part=Path.GetDirectoryName(part))if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Fixture links forbidden.");
IDataCipher cipher=args[2]=="dpapi"?new DpapiCipher():new SyntheticCipher();
var now=DateTimeOffset.Parse("2035-02-03T10:00:00Z");
var clock=new FixedClock(now);var scope=new QueryScope("fixture:person","synthetic-interop-room",now.AddHours(-2),now.AddHours(-1));
var message=new SourceMessage("fixture-1",scope.ConversationId,"fixture-person","Synthetic sender","Synthetic compatibility evidence",scope.Start.AddMinutes(2));
var batch=new MessageBatch(scope,[message],true,["Synthetic warning"],new Dictionary<string,JsonElement>());
var model=new ModelSettings();const string task="Synthetic interoperability task";
var report=AnalysisValidator.Validate("""{"issues":[{"title":"Synthetic interop review","summary":"Needs human verification","status":"resolved","communication":"","nextStep":"","evidence":[{"messageId":"fixture-1","quote":"compatibility evidence"}]}],"pendingQuestions":[]}""",batch,task);
var expectedFacts=new{scopeHash=scope.Fingerprint,revision=message.Revision,batchHash=batch.Fingerprint,attempt=InsightState.AttemptKey(scope,message),requestHash=OpenAiClient.Fingerprint(batch,task,model),foldedTask=AnalysisAttemptPolicy.TaskKey("Maße Synthetic"),day="2035-02-03",requests=1,reserved=0.01m};
if(AnalysisAttemptPolicy.TaskKey("MASSE SYNTHETIC")!=expectedFacts.foldedTask)throw new Exception("Cross-runtime Unicode task folding changed.");
using var store=new BusinessStore(Path.Combine(root,args[2]),cipher,clock);
if(args[0]=="write")
{
    var state=store.Load();state.Ingest(batch);AnalysisAttemptPolicy.Record(state,batch,task);state.Reserve(model,0.01m,now);store.Save(state);
    state.Reports.Add(new(now,scope.Key,task,ReportRenderer.Markdown(report),false,message.CreatedAt));store.Save(state);
    store.Reviews.Decide(state,store.Reviews.Capture(state,report),0,true,true);
    var destination=Guard.Hash("synthetic-destination");var payload=Guard.Hash("synthetic-payload");var ledger=new SendLedger();
    ledger.Attempts[Guard.Hash(destination+"\n"+payload)]=new(destination,payload,now,SendAttemptStatus.Pending,1);store.SendAttempts.Save(ledger);
    File.WriteAllText(Path.Combine(root,args[2]+"-facts.json"),JsonSerializer.Serialize(expectedFacts));
}
else if(args[0]=="read")
{
    if(File.ReadAllText(Path.Combine(root,args[2]+"-facts.json"))!=JsonSerializer.Serialize(expectedFacts))throw new Exception("Persisted source/request/attempt hashes changed across runtimes.");
    var state=store.Load();
    if(state.Requests!=1||state.ReservedUsd!=0.01m||state.BudgetDay!="2035-02-03"||state.Messages.Count!=1||!state.Attempted.Contains(InsightState.AttemptKey(scope,message)))throw new Exception("Persisted attempts/budget changed.");
    if(AnalysisAttemptPolicy.Select(state,batch,task).Batch.Messages.Count!=0||AnalysisAttemptPolicy.Select(state,batch,"Synthetic different task").PreviouslyAttempted!=1)throw new Exception("Cross-runtime task-scoped protection or explicit reuse warning lost.");
    var calls=0;
    if(state.Coverage.Count!=1)throw new Exception("Persisted complete checkpoint changed.");
    // Force an overlap replay after validating the persisted checkpoint, to exercise attempted-revision suppression independently.
    state.Coverage.Clear();
    var result=await new MonitorEngine(state,store).TickAsync(scope,(s,ct)=>Task.FromResult(batch with{Scope=s}),
        (b,t,ct)=>{calls++;throw new Exception("Duplicate paid dispatch");},model,state.RetentionDays,scope.End);
    if(calls!=0||state.Requests!=1||state.ReservedUsd!=0.01m)throw new Exception("Runtime change replayed attempted source.");
    if(store.SendAttempts.Load().Attempts.Single().Value.Status!=SendAttemptStatus.Pending)throw new Exception("Uncertain durable send attempt changed.");
    var review=store.Reviews.Open(state,state.Reports.Single());
    if(!review.CanReview||review.Report?.Issues[0].HumanConfirmed!=true)throw new Exception("Cross-runtime human verification lost.");
    if(Environment.Version.Major>=10)
    {
        var revoked=store.Reviews.Decide(state,review,0,false,true);
        if(revoked.Report!.Issues[0].HumanConfirmed)throw new Exception("Cross-runtime revoke did not restore tentative state.");
        store.Reviews.Decide(state,revoked,0,true,true);
    }
    Console.WriteLine($"PASS {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} {args[2]}: persisted task-scoped attempts/UTC budget, human confirm/revoke/restore and no duplicate analysis");
}
else throw new ArgumentException("Unknown fixture command.");
return 0;
sealed class FixedClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
sealed class SyntheticCipher:IDataCipher
{
    static readonly byte[] Prefix=Encoding.UTF8.GetBytes("DTI-synthetic-interop-only:");
    public byte[] Protect(byte[] value)=>Prefix.Concat(value).ToArray();
    public byte[] Unprotect(byte[] value)=>value.AsSpan().StartsWith(Prefix)?value[Prefix.Length..]:throw new CryptographicException();
}
