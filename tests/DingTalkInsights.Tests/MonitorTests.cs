using DingTalkInsights.Core;

internal static class MonitorTests
{
    static void Assert(bool b){if(!b)throw new Exception("assertion failed");}
    static void Denied(Action f){try{f();}catch(SafetyException){return;}throw new Exception("expected SafetyException");}
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("增量合并重复批次不重复入库",()=>{var s=new InsightState();var b=Demo.CreateBatch();Assert(s.Ingest(b)==4);Assert(s.Ingest(b)==0&&s.Messages.Count==4);});
        yield return ("不完整批次不能推进断点",()=>{var s=new InsightState();var b=Demo.CreateBatch()with{Complete=false};s.Ingest(b);Assert(!s.Watermarks.ContainsKey(b.Scope.Key));});
        yield return ("完整批次推进断点且下一次保留重叠窗口",()=>{var s=new InsightState();var b=Demo.CreateBatch();s.Ingest(b);var next=MonitorEngine.NextScope(s,b.Scope,b.Scope.End.AddHours(1));Assert(s.Watermarks[b.Scope.Key]==b.Scope.End&&next.Start==b.Scope.End.AddMinutes(-2));});
        yield return ("断点积压按一小时追赶不会跳过区间",()=>{var s=new InsightState();var b=Demo.CreateBatch();var next=MonitorEngine.NextScope(s,b.Scope,b.Scope.Start.AddDays(2));Assert(next.Start==b.Scope.Start&&next.End==b.Scope.Start.AddHours(1));});
        yield return ("同消息编辑更新修订而非增加重复记录",()=>{var s=new InsightState();var b=Demo.CreateBatch();s.Ingest(b);var items=b.Messages.ToArray();items[0]=items[0]with{Text="修订后的模拟消息"};Assert(s.Ingest(b with{Messages=items})==1&&s.Messages.Count==4);});
        yield return ("本地搜索仅在所选身份、会话和时间内",()=>{var s=new InsightState();var b=Demo.CreateBatch();s.Ingest(b);var r=LocalSearch.Find(s,b.Scope,"登录");Assert(r.Messages.Count==2&&!r.Complete);var other=b.Scope with{Profile="another:user"};Assert(LocalSearch.Find(s,other,"登录").Messages.Count==0);});
        yield return ("预算达到上限拒绝新请求",()=>{var s=new InsightState();var m=new ModelSettings{DailyBudgetUsd=0.001m};var today=DateTimeOffset.Parse("2026-11-06T10:00:00Z");s.Reserve(m,0.0008m,today);Denied(()=>s.Reserve(m,0.0008m,today));Assert(s.Requests==1&&s.ReservedUsd==0.0008m);});
        yield return ("预算按 UTC 日期更新并保留本日请求数限制",()=>{var s=new InsightState();var m=new ModelSettings{MaxRequestsPerDay=1};var today=DateTimeOffset.Parse("2026-11-06T10:00:00Z");s.Reserve(m,0.001m,today);Denied(()=>s.Reserve(m,0.001m,today));s.Reserve(m,0.001m,today.AddDays(1));Assert(s.Requests==1);});
        yield return ("分析尝试持久标识防止未知结果自动重试",()=>{var s=new InsightState();var b=Demo.CreateBatch();s.Ingest(b);var pending=s.Unanalyzed(b.Scope,40);Assert(pending.Count==4);s.MarkAttempted(b.Scope,pending);Assert(s.Unanalyzed(b.Scope,40).Count==0);});
        yield return ("加密状态可恢复断点且文件不含聊天原文",()=>{var path=Path.Combine(Path.GetTempPath(),"insights-state-test-"+Guid.NewGuid());var cipher=new TestCipher();try{using(var repo=new EncryptedStateStore(path,cipher)){var s=new InsightState();s.Ingest(Demo.CreateBatch());repo.Save(s);Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("登录"));}using(var repo=new EncryptedStateStore(path,cipher)){var s=repo.Load();Assert(s.Messages.Count==4&&s.Watermarks.Count==1);}}finally{File.Delete(path);File.Delete(path+".lock");}});
        yield return ("篡改加密状态不能被当作空状态覆盖",()=>{var path=Path.Combine(Path.GetTempPath(),"insights-corrupt-test-"+Guid.NewGuid());try{File.WriteAllText(path,"bad");using var repo=new EncryptedStateStore(path,new TestCipher());Denied(()=>repo.Load());}finally{File.Delete(path);File.Delete(path+".lock");}});
        yield return ("不连续覆盖区间不能跳过历史缺口",()=>{var s=new InsightState();var b=Demo.CreateBatch();s.Ingest(b with{Scope=b.Scope with{End=b.Scope.Start.AddHours(1)}});s.Ingest(b with{Scope=b.Scope with{Start=b.Scope.Start.AddHours(4),End=b.Scope.Start.AddHours(5)},Messages=Array.Empty<SourceMessage>()});var next=MonitorEngine.NextScope(s,b.Scope,b.Scope.Start.AddHours(6));Assert(next.End==b.Scope.Start.AddHours(2));});
        yield return ("本机时间回退不能重置当日预算",()=>{var s=new InsightState();var model=new ModelSettings();var now=DateTimeOffset.Parse("2026-11-06T10:00:00Z");s.Reserve(model,0.001m,now);Denied(()=>s.Reserve(model,0.001m,now.AddDays(-1)));Assert(s.Requests==1);});
        yield return ("模型未知失败后重启不自动重试同一修订",()=>{var repo=new MemoryStore();var s=new InsightState();var b=Demo.CreateBatch();var engine=new MonitorEngine(s,repo);var calls=0;Task<MessageBatch> Fetch(QueryScope scope,CancellationToken _)=>Task.FromResult(b with{Scope=scope,Messages=b.Messages.Where(m=>m.CreatedAt>=scope.Start&&m.CreatedAt<scope.End).ToArray()});Task<ModelResult> Analyze(MessageBatch batch,string task,CancellationToken _){calls++;Assert(repo.Load().Attempted.Count==batch.Messages.Count);throw new SafetyException("synthetic unknown result");}Denied(()=>engine.TickAsync(b.Scope,Fetch,Analyze,new ModelSettings(),30,b.Scope.End).GetAwaiter().GetResult());var restored=repo.Load();new MonitorEngine(restored,repo).TickAsync(b.Scope,Fetch,Analyze,new ModelSettings(),30,b.Scope.End.AddMinutes(5)).GetAwaiter().GetResult();Assert(calls==1);});
        yield return ("状态保存失败时不能调用模型",()=>{var b=Demo.CreateBatch();var calls=0;var engine=new MonitorEngine(new InsightState(),new FailingStore());Task<MessageBatch> Fetch(QueryScope scope,CancellationToken _)=>Task.FromResult(b with{Scope=scope,Messages=b.Messages.Where(m=>m.CreatedAt>=scope.Start&&m.CreatedAt<scope.End).ToArray()});Task<ModelResult> Analyze(MessageBatch batch,string task,CancellationToken _){calls++;return Task.FromResult(new ModelResult(Demo.Analyze(batch),0,0));}Denied(()=>engine.TickAsync(b.Scope,Fetch,Analyze,new ModelSettings(),30,b.Scope.End).GetAwaiter().GetResult());Assert(calls==0);});
    }
    sealed class TestCipher:IDataCipher
    {
        readonly byte[] key=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] data){var n=System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);var output=new byte[28+data.Length];n.CopyTo(output,0);using var aes=new System.Security.Cryptography.AesGcm(key,16);aes.Encrypt(n,data,output.AsSpan(28),output.AsSpan(12,16));return output;}
        public byte[] Unprotect(byte[] data){var result=new byte[data.Length-28];using var aes=new System.Security.Cryptography.AesGcm(key,16);aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),result);return result;}
    }
    sealed class MemoryStore:IStateStore
    {
        string json="{}";
        public InsightState Load()=>System.Text.Json.JsonSerializer.Deserialize<InsightState>(json)!;
        public void Save(InsightState state)=>json=System.Text.Json.JsonSerializer.Serialize(state);
    }
    sealed class FailingStore:IStateStore
    {
        public InsightState Load()=>new();
        public void Save(InsightState state)=>throw new SafetyException("synthetic disk failure");
    }
}
