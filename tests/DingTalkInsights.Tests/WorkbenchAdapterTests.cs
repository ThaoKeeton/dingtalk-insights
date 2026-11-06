using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using DingTalkInsights.Workbench;

internal static class WorkbenchAdapterTests
{
    static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("adapter defers canonical known-folder resolution and rejects copied-root authority",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var resolutions=0;
            var location=new CanonicalStoreLocation(()=>{resolutions++;return f.CanonicalRoot;});
            var session=new PluginSession(f.Controller,f.PluginRoot,f.Cipher,location,_=>Task.FromResult(new Preferences()),_=>Task.CompletedTask);
            Assert(resolutions==0&&!Directory.Exists(f.PluginRoot),"adapter construction resolved real root or wrote data");
            Assert(location.Resolve(false) is null&&resolutions==0,"unconfirmed review resolved known folder");
            var reviewed=location.Resolve(true)!;Assert(resolutions==1,"confirmed review did not resolve");
            try{await location.ConnectAsync(f.Controller,f.Temp.Child("copied-root"),true);throw new Exception("copied root accepted");}catch(RuntimeFault e){Assert(e.Category==RuntimeFailure.Connection,"wrong copied-root failure");}
            Assert(await location.ConnectAsync(f.Controller,reviewed,true),"canonical handover rejected");
            Assert(f.Services.Calls==0,"connection invoked business services");await session.DeactivateAsync();
        }));
        yield return ("adapter nonsecret preferences and encrypted connection draft roundtrip without resume authority",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var saved=new Preferences();
            var session=new PluginSession(f.Controller,f.PluginRoot,f.Cipher,new(()=>f.CanonicalRoot),_=>Task.FromResult(saved),p=>{saved=p;return Task.CompletedTask;});
            session.Preferences=new(){PollSeconds=60,RetentionDays=7};
            session.Draft=new(){Profile="fixture:person",ConversationId="fixture-room",Task="Synthetic saved draft"};
            f.Controller.SetDraft("Synthetic saved draft");await session.SaveAsync();var raw=System.Text.Json.JsonSerializer.Serialize(saved);
            Assert(!raw.Contains("fixture-room")&&!raw.Contains("Synthetic saved draft"),"sensitive configuration leaked into host JSON");
            Assert(File.Exists(Path.Combine(f.PluginRoot,"connection-draft.dpapi")),"draft not encrypted");
            session.Draft=new();await session.LoadAsync();
            Assert(session.Draft.ConversationId=="fixture-room"&&f.Controller.Snapshot.Draft=="Synthetic saved draft","encrypted draft lost");
            Assert(!f.Controller.Snapshot.IsMonitoring&&f.Services.Calls==0,"loading preferences restored business authority");await session.DeactivateAsync();
        }));
        yield return ("adapter deactivation awaits pending preference persistence before returning",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
            var session=new PluginSession(f.Controller,f.PluginRoot,f.Cipher,new(()=>f.CanonicalRoot),_=>Task.FromResult(new Preferences()),async p=>{entered.SetResult();await release.Task;});
            var save=session.SaveAsync();await entered.Task;var stop=session.DeactivateAsync();
            Assert(!stop.IsCompleted,"Stop returned with pending adapter persistence");release.SetResult();await save;await stop;
            try{await session.LoadAsync();throw new Exception("retired adapter accepted persistence");}catch(RuntimeFault e){Assert(e.Category==RuntimeFailure.Inactive,"wrong retired error");}
        }));
        yield return ("adapter snapshot subscription drops superseded queued and unloaded notifications",()=>Run(async()=>
        {
            using var f=new RuntimeTests.Fixture();var queued=new Queue<Action>();var seen=new List<RuntimeSnapshot>();
            var binding=new SnapshotBinding(f.Controller,a=>{queued.Enqueue(a);return true;},seen.Add);
            f.Controller.SetDraft("older");f.Controller.SetDraft("latest");
            while(queued.TryDequeue(out var action))action();
            Assert(seen.Count==1&&seen[0].Draft=="latest","stale dispatched snapshot applied");
            f.Controller.SetDraft("after unload");binding.Dispose();while(queued.TryDequeue(out var action))action();
            Assert(seen.Count==1&&f.Controller.Observation.Subscriptions==0,"unloaded page retained subscription or callback");
            await f.Controller.DeactivateAsync();Assert(f.Controller.Observation.PendingOperations==0&&!f.Controller.Observation.StoreOwned,"retirement left resources");
        }));
    }
}
