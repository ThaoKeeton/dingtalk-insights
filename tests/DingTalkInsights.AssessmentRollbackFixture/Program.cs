using System.Security.Cryptography;
using System.Text;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

if(args.Length!=4)throw new ArgumentException("Expected mode, guarded root, cipher and expected assessment.");
var mode=args[0];var root=Path.GetFullPath(args[1]);var id=Path.GetFileName(root);
var expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DingTalkInsights-AssessmentRollback",id));
if(!Guid.TryParseExact(id,"N",out _)||root!=expected||!File.Exists(Path.Combine(root,".fixture-owner")))throw new IOException("Only guarded synthetic fixture roots are allowed.");
for(var part=root;part is not null;part=Path.GetDirectoryName(part))if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Fixture links forbidden.");
IDataCipher cipher=args[2] switch{"dpapi" when OperatingSystem.IsWindows()=>new DpapiCipher(),"synthetic"=>new SyntheticCipher(),_=>throw new ArgumentException("Unknown fixture cipher.")};
if(args[3] is not("model" or "legacy"))throw new ArgumentException("Unknown assessment expectation.");
var now=DateTimeOffset.Parse("2035-04-03T10:00:00Z");var scope=new QueryScope("fixture:person","synthetic-assessment-room",now.AddHours(-2),now.AddHours(-1));
var message=new SourceMessage("fixture-1",scope.ConversationId,"fixture-person","Synthetic sender","Synthetic evidence: acceptance passed",scope.Start.AddMinutes(2));
var batch=new MessageBatch(scope,[message],true,["Synthetic bounded scope"],new Dictionary<string,System.Text.Json.JsonElement>());
var report=AnalysisValidator.Validate("""{"issues":[{"title":"Synthetic assessment","summary":"Acceptance passed","status":"resolved","communication":"","nextStep":"","evidence":[{"messageId":"fixture-1","quote":"acceptance passed"}]}],"pendingQuestions":[]}""",batch,"Synthetic assessment task");
using var store=new BusinessStore(Path.Combine(root,args[2]+"-"+args[3]),cipher,new FixedClock(now));
if(mode=="write")
{
    var state=store.Load();state.Ingest(batch);state.Reserve(new ModelSettings(),0.01m,now);
    state.Reports.Add(new(now,scope.Key,report.Task,ReportRenderer.Markdown(report),false,message.CreatedAt));store.Save(state);
    store.Reviews.Capture(state,report);
    var destination=Guard.Hash("synthetic-destination");var reportKey=ReportReviewSnapshot.Key(state.Reports.Single());
    var sends=new SendLedger();sends.Attempts[Guard.Hash(destination+"\nreport:"+reportKey)]=new(destination,Guard.Hash("report:"+reportKey),now,SendAttemptStatus.Unknown,1);store.SendAttempts.Save(sends);
}
else if(mode is "legacy-rewrite" or "read")
{
    var state=store.Load();var view=store.Reviews.Open(state,state.Reports.Single());
    var ledger=store.SendAttempts.Load();var destination=Guard.Hash("synthetic-destination");var reportKey=ReportReviewSnapshot.Key(state.Reports.Single());
    if(state.Requests!=1||state.ReservedUsd!=0.01m||!ledger.Attempts.TryGetValue(Guard.Hash(destination+"\nreport:"+reportKey),out var attempt)||attempt.Status!=SendAttemptStatus.Unknown)throw new Exception("Budget or unknown-send report alias changed.");
    if(view.Report is null||view.Report.Issues[0].HumanConfirmed||view.Report.Issues[0].Status!=IssueStatus.PendingVerification)throw new Exception("The model was represented as a human decision.");
    if(mode=="legacy-rewrite")
    {
        // This binary is built from the pinned previous release's real Core and Runtime.
        if(view.Markdown.Contains("模型判断已解决"))throw new Exception("The old renderer changed unexpectedly.");
        view=store.Reviews.Decide(state,view,0,true,true);view=store.Reviews.Decide(state,view,0,false,true);
        if(view.Report!.Issues[0].HumanConfirmed)throw new Exception("Legacy revocation failed.");
        store.SendAttempts.Save(ledger); // The previous binary round-trips the existing report alias as an ordinary hash entry.
    }
    else
    {
        if(view.Markdown.Contains("模型判断已解决")!=(args[3]=="model")||view.Markdown.Contains("人工确认已解决"))throw new Exception("New reader lost or inferred a model assessment.");
        if(!view.Markdown.Contains("来源状态未明")||view.Markdown.Contains("模型输入与本机已保存的来源版本一致"))throw new Exception("Legacy rewrite incorrectly claimed complete current input coverage.");
    }
}
else throw new ArgumentException("Unknown fixture mode.");
Console.WriteLine($"PASS {mode} {args[2]} {args[3]}: synthetic assessment, optional human decision, budget and unknown send");
return 0;

sealed class FixedClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
sealed class SyntheticCipher:IDataCipher
{
    static readonly byte[] Prefix=Encoding.UTF8.GetBytes("DTI-synthetic-assessment-rollback:");
    public byte[] Protect(byte[] value)=>Prefix.Concat(value).ToArray();
    public byte[] Unprotect(byte[] value)=>value.AsSpan().StartsWith(Prefix)?value[Prefix.Length..]:throw new CryptographicException();
}
