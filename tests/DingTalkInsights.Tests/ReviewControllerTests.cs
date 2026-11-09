using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

internal static class ReviewControllerTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    public static IEnumerable<(string,Action)> All()
    {
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
            using var f=new RuntimeTests.Fixture(seed:true);var statePath=Path.Combine(f.CanonicalRoot,"state.dpapi");var before=File.ReadAllBytes(statePath);
            File.WriteAllText(Path.Combine(f.CanonicalRoot,"report-reviews.dpapi"),"corrupt synthetic review");f.Clock.Advance(TimeSpan.FromDays(31));
            try{await f.Controller.ConnectAsync(f.CanonicalRoot,true);throw new Exception("unrevoked handover accepted");}catch(RuntimeFault fault){Check(fault.Category==RuntimeFailure.Storage,"wrong handover refusal");}
            Check(!f.Controller.Observation.StoreOwned&&before.SequenceEqual(File.ReadAllBytes(statePath))&&f.Services.Calls==0,"failed handover retained authority or rewrote canonical state");
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
