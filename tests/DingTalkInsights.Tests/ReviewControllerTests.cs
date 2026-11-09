using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using System.Text.Json;

internal static class ReviewControllerTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("failed snapshot merge blocks new work and recovers the model assessment without repeating it",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.ConnectAsync(f.CanonicalRoot,true);
            await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true);await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true);
            f.Services.Analyze=(batch,task,_)=>
            {
                var raw=JsonSerializer.Serialize(new{issues=new[]{new{title="Synthetic",summary="Synthetic summary",status="resolved",communication="",nextStep="",evidence=new[]{new{messageId=batch.Messages[0].MessageId,quote=batch.Messages[0].Text}}}},pendingQuestions=Array.Empty<string>()});
                f.Cipher.FailReviewArchive=true;return Task.FromResult(new ModelResult(AnalysisValidator.Validate(raw,batch,task),1,1));
            };
            Check(await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true),"synthetic analysis failed");
            Check(c.Snapshot.Markdown.Contains("来源状态未明")&&!c.Snapshot.Markdown.Contains("模型输入与本机已保存的来源版本一致"),"failed snapshot claimed current sources");
            f.Cipher.FailReviewArchive=false;
            try{await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true);throw new Exception("new work bypassed the pending commit");}catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Storage,"wrong pending-commit refusal");}
            await c.DisconnectAsync();await c.ConnectAsync(f.CanonicalRoot,true);await c.SelectHistoryAsync(c.Snapshot.History.Single());
            Check(c.Snapshot.Markdown.Contains("模型判断已解决")&&!c.Snapshot.Markdown.Contains("人工确认已解决")&&f.Services.Models==1&&f.Services.Sends==0,"restart lost or repeated the recovered model assessment");
        }));
        yield return ("legacy unknown send cannot replay after the same report gets a new display format",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.ConnectAsync(f.CanonicalRoot,true);
            await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true);await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true);
            await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);var saved=c.Snapshot.History.Single();await c.DisconnectAsync();
            var calls=0;using(var legacy=new BusinessStore(f.CanonicalRoot,f.Cipher,new FixedTime(RuntimeTests.Now)))
            {
                var target=new WebhookTarget("Synthetic room",f.Vault.Read().WebhookUrl,"");
                var sender=new DurableWebhookSender(legacy.SendAttempts,(_,_,_)=>{calls++;return Task.FromResult(DeliveryStatus.Unknown);});
                Check((await sender.SendAsync(target,saved.Markdown,true)).Status==DeliveryStatus.Unknown&&calls==1,"fixture did not record a legacy unknown send");
            }
            await c.ConnectAsync(f.CanonicalRoot,true);await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Webhook,true);await c.SelectHistoryAsync(saved);
            var preview=await c.PreviewSendAsync("Synthetic room");
            Check(preview is not null&&preview.AlreadyAttempted&&preview.PreviousStatus==DeliveryStatus.Unknown,"legacy unknown send was hidden by new report formatting");
            Check(!await c.SendReportAsync(preview!,true)&&f.Services.Sends==0,"legacy unknown send replayed without separate approval");
            preview=await c.PreviewSendAsync("Synthetic room");Check(await c.SendReportAsync(preview!,true,true)&&f.Services.Sends==1,"explicit local-fake retry did not dispatch once");
        }));
        yield return ("human decision history requires a distinct legacy-send acknowledgement even without a matching payload",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.ConnectAsync(f.CanonicalRoot,true);
            await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true);await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model|CredentialUse.Webhook,true);
            f.Services.Analyze=(batch,task,_)=>
            {
                var raw=JsonSerializer.Serialize(new{issues=new[]{new{title="Synthetic",summary="Synthetic",status="resolved",communication="",nextStep="",evidence=new[]{new{messageId=batch.Messages[0].MessageId,quote=batch.Messages[0].Text}}}},pendingQuestions=Array.Empty<string>()});
                return Task.FromResult(new ModelResult(AnalysisValidator.Validate(raw,batch,task),1,1));
            };
            await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);
            await c.ConfirmResolvedAsync((await c.PreviewResolutionAsync(0,true))!,true);
            await c.ConfirmResolvedAsync((await c.PreviewResolutionAsync(0,false))!,true);
            var preview=(await c.PreviewSendAsync("Synthetic room"))!;
            Check(preview.LegacyFormatRisk&&preview.AlreadyAttempted&&preview.PreviousStatus is null,"prior human rendering was assumed never sent");
            Check(!await c.SendReportAsync(preview,true)&&f.Services.Sends==0,"missing old payload bypassed the risk acknowledgement");
            preview=(await c.PreviewSendAsync("Synthetic room"))!;
            Check(await c.SendReportAsync(preview,true,true)&&f.Services.Sends==1,"confirmed local fake did not dispatch once");
        }));
        yield return ("routine demo analysis shows model assessment without human review and survives handover",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.OpenDemoAsync();await c.ReadOnceAsync(c.PreviewMonitoring(f.DemoRequest),true);
            Check(await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true),"demo analysis failed");
            Check(c.Snapshot.Markdown.Contains("模型判断已解决")&&!c.Snapshot.Markdown.Contains("人工确认已解决")&&c.Snapshot.Requests==0&&f.Services.Sends==0,"routine result required review or dispatched a real service");
            var saved=c.Snapshot.History.Single();await c.SelectHistoryAsync(saved);
            Check(c.Snapshot.Markdown.Contains("模型判断已解决")&&!c.Snapshot.Markdown.Contains("人工确认已解决"),"history lost the model assessment");
            await c.DeactivateAsync();using var standalone=new BusinessStore(Path.Combine(f.PluginRoot,"demo"),f.Cipher,new FixedTime(RuntimeTests.Now));var state=standalone.Load();
            var view=standalone.Reviews.Open(state,state.Reports.Single());
            Check(view.Markdown.Contains("模型判断已解决")&&!view.Report!.Issues.Any(i=>i.HumanConfirmed),"standalone handover fabricated or lost the assessment");
        }));
        yield return ("review controller saves explicit decisions and a new plugin/standalone owner restores them",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.OpenDemoAsync();await c.ReadOnceAsync(c.PreviewMonitoring(f.DemoRequest),true);
            c.SetDraft("Synthetic durable review");await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);
            var resolution=await c.PreviewResolutionAsync(0,true);Check(!await c.ConfirmResolvedAsync(resolution!,false),"unconfirmed resolution accepted");
            Check(await c.ConfirmResolvedAsync(resolution!,true)&&c.Snapshot.Markdown.Contains("人工确认已解决"),"confirmed resolution not shown");
            var saved=c.Snapshot.History.Single();await c.SelectHistoryAsync(saved);Check(c.Snapshot.CanReviewReport&&c.Snapshot.Markdown.Contains("人工确认已解决"),"history dropped decision");
            await c.DeactivateAsync();using var standalone=new BusinessStore(Path.Combine(f.PluginRoot,"demo"),f.Cipher,new FixedTime(RuntimeTests.Now));var state=standalone.Load();
            var view=standalone.Reviews.Open(state,state.Reports.Single());Check(view.Report!.Issues[0].HumanConfirmed,"standalone handover dropped plugin review");
            standalone.Reviews.Decide(state,view,0,false,true);standalone.Dispose();
            await using var next=new InsightController(f.PluginRoot,"synthetic-next-review-owner",f.Cipher,f.Services,f.Clock);
            await next.OpenDemoAsync();await next.SelectHistoryAsync(next.Snapshot.History.Single());
            Check(next.Snapshot.CanReviewReport&&!next.Snapshot.Markdown.Contains("人工确认已解决")&&next.Snapshot.Markdown.Contains("已撤销"),"reopen was not durable");
        }));
        yield return ("review controller Stop and selection changes reject the original dialog preview",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.OpenDemoAsync();await c.ReadOnceAsync(c.PreviewMonitoring(f.DemoRequest),true);
            await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);var old=await c.PreviewResolutionAsync(0,true);await c.StopMonitoringAsync();
            try{await c.ConfirmResolvedAsync(old!,true);throw new Exception("stopped preview accepted");}catch(RuntimeFault){}
            var selected=await c.PreviewResolutionAsync(0,true);await c.SelectHistoryAsync(c.Snapshot.History.Single());
            try{await c.ConfirmResolvedAsync(selected!,true);throw new Exception("selection-stale preview accepted");}catch(RuntimeFault){}
            Check(!c.Snapshot.Markdown.Contains("人工确认已解决"),"stale preview changed certainty");
        }));
        yield return ("review controller confirmation preview evidence cannot be changed by its caller",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.OpenDemoAsync();await c.ReadOnceAsync(c.PreviewMonitoring(f.DemoRequest),true);
            await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);var preview=(await c.PreviewResolutionAsync(0,true))!;
            var first=preview.Evidence[0];
            if(preview.Evidence is IList<Evidence> editable)
            {
                try{editable.Clear();throw new Exception("confirmation preview evidence is mutable");}catch(NotSupportedException){}
            }
            Check(preview.Evidence.Count>0&&preview.Evidence[0]==first&&await c.ConfirmResolvedAsync(preview,true),"immutable approved evidence was not used");
        }));
        yield return ("review controller corrupt optional archive allows history and explicit clear without false certainty",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.OpenDemoAsync();await c.ReadOnceAsync(c.PreviewMonitoring(f.DemoRequest),true);
            await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);await c.ConfirmResolvedAsync((await c.PreviewResolutionAsync(0,true))!,true);
            var original=c.Snapshot.History.Single();await c.DisconnectAsync();
            var path=Path.Combine(f.PluginRoot,"demo","report-reviews.dpapi");File.WriteAllText(path,"corrupt synthetic review");
            await c.OpenDemoAsync();await c.SelectHistoryAsync(c.Snapshot.History.Single());
            Check(c.Snapshot.Markdown.Contains("demo-2")&&!c.Snapshot.CanReviewReport&&!c.Snapshot.Markdown.Contains("人工确认已解决"),"original lost or false certainty shown");
            Check(File.ReadAllText(path)=="corrupt synthetic review"&&c.Snapshot.History.Single()==original,"corruption overwritten or original changed");
            await c.ClearIndexAsync(true);Check(c.Snapshot.History.Count==0&&c.Snapshot.Markdown=="","explicit recovery unavailable");
        }));
        yield return ("review unavailable demo history in a canonical store cannot become a send preview",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture(seed:true);
            using(var standalone=new BusinessStore(f.CanonicalRoot,f.Cipher,new FixedTime(RuntimeTests.Now)))
            {
                var state=standalone.Load();state.Reports.Add(new(RuntimeTests.Now,"synthetic-scope","Synthetic demo history","Synthetic demo original",true,RuntimeTests.Now.AddMinutes(-10)));standalone.Save(state);
            }
            File.WriteAllText(Path.Combine(f.CanonicalRoot,"report-reviews.dpapi"),"corrupt synthetic review");
            var c=f.Controller;await c.ConnectAsync(f.CanonicalRoot,true);await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Webhook,true);
            await c.SelectHistoryAsync(c.Snapshot.History.Single(r=>r.IsDemo));
            try{await c.PreviewSendAsync("Synthetic destination");throw new Exception("demo fallback became sendable");}catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Connection,"demo rejected for the wrong reason");}
            Check(f.Services.Sends==0&&f.Services.Calls==0,"demo history test dispatched a service");
        }));
        yield return ("review cleanup failure never grants a canonical handover without durable recovery revocation",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture(seed:true);var statePath=Path.Combine(f.CanonicalRoot,"state.dpapi");
            File.WriteAllText(Path.Combine(f.CanonicalRoot,"report-reviews.dpapi"),"corrupt synthetic review");f.Clock.Advance(TimeSpan.FromDays(31));
            Check(await f.Controller.ConnectAsync(f.CanonicalRoot,true)&&f.Controller.Snapshot.Notice.Contains("只读"),"safe read-only handover was unavailable");
            Check(f.Services.Calls==0&&f.Controller.Snapshot.History.Count==0,"handover exposed expired data or called a service");await f.Controller.DisconnectAsync();
            using var raw=new EncryptedStateStore(statePath,f.Cipher,new FixedTime(RuntimeTests.Now));var state=raw.ReadRetained();
            Check(state.Recovery is null&&state.UserStopped&&state.Reports.Count==0&&File.ReadAllText(Path.Combine(f.CanonicalRoot,"report-reviews.dpapi"))=="corrupt synthetic review","handover failed to persist revocation/retention or modified the damaged archive");
        }));
        yield return ("review privacy recovery only permits retained history and explicit clear before new work",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var root=Path.Combine(f.PluginRoot,"demo");
            using(var standalone=new BusinessStore(root,f.Cipher,new FixedTime(RuntimeTests.Now)))
            {
                var state=standalone.Load();state.Reports.Add(new(RuntimeTests.Now,"synthetic","Synthetic expired","Expired body",true,RuntimeTests.Now.AddDays(-29)));
                state.Reports.Add(new(RuntimeTests.Now,"synthetic","Synthetic retained","Retained body",true,RuntimeTests.Now.AddMinutes(-10)));standalone.Save(state);
            }
            File.WriteAllText(Path.Combine(root,"report-reviews.dpapi"),"corrupt synthetic review");f.Clock.Advance(TimeSpan.FromDays(2));
            var path=Path.Combine(root,"state.dpapi");var before=File.ReadAllBytes(path);var c=f.Controller;
            await c.OpenDemoAsync();await c.SelectHistoryAsync(c.Snapshot.History.Single());
            Check(c.Snapshot.Markdown.Contains("Retained body")&&!c.Snapshot.Markdown.Contains("Expired body")&&before.SequenceEqual(File.ReadAllBytes(path)),"read-only history wrote state or leaked expired text");
            try{await c.ReadOnceAsync(f.DemoRequest,true);throw new Exception("new work bypassed failed cleanup");}catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Storage,"wrong cleanup refusal");}
            await c.ClearIndexAsync(true);Check(c.Snapshot.History.Count==0&&c.Snapshot.Markdown==""&&f.Services.Calls==0,"explicit clear did not recover without service calls");
        }));
        yield return ("review controller source revisions revoke visible certainty without a new model",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var c=f.Controller;await c.ConnectAsync(f.CanonicalRoot,true);
            await c.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true);
            f.Services.Analyze=(batch,task,_)=>Task.FromResult(new ModelResult(new AnalysisReport(batch,
                [new("Synthetic issue","Needs confirmation",IssueStatus.PendingVerification,"","",[new(batch.Messages[0],batch.Messages[0].Text)])],[],task),2,3));
            await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true);await c.AnalyzeCandidatesAsync((await c.PreviewAnalysisAsync(new()))!,true);
            await c.ConfirmResolvedAsync((await c.PreviewResolutionAsync(0,true))!,true);var saved=c.Snapshot.History.Single();
            f.Services.Read=(scope,_)=>Task.FromResult(RuntimeTests.FakeServices.Batch(scope) with{Messages=[RuntimeTests.FakeServices.Batch(scope).Messages[0] with{Text="New source revision"}]});
            await c.ReadOnceAsync(c.PreviewMonitoring(f.Request),true);await c.SelectHistoryAsync(saved);
            Check(!c.Snapshot.CanReviewReport&&!c.Snapshot.Markdown.Contains("人工确认已解决")&&c.Snapshot.Markdown.Contains("失效")&&f.Services.Models==1,"changed evidence inherited human certainty");
        }));
    }
}
