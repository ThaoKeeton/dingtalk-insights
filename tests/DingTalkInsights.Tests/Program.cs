using System.Text.Json;
using System.Text.Json.Nodes;
using DingTalkInsights.Core;

if(SyntheticDws.TryRun(args) is {} syntheticExit)return syntheticExit;

var tests = new List<(string Name, Action Test)>();
var scope = new QueryScope("corp:user", "cid-demo", DateTimeOffset.Parse("2026-11-06T09:00:00+08:00"), DateTimeOffset.Parse("2026-11-06T11:00:00+08:00"));
string Payload() => """
{"ok":true,"outcome":"success","data":{"contractVersion":"im.message-list.v1","messages":[{"messageId":"m1","conversationId":"cid-demo","senderId":"u1","sender":"测试甲","text":"登录失败，提示超时。","createTime":"2026-11-06 09:10:00","messageType":"text"},{"messageId":"m2","conversationId":"cid-demo","senderId":"u2","sender":"测试乙","text":"修复已提交，请复测。","createTime":"2026-11-06T09:30:00+08:00","messageType":"text"}],"count":2,"complete":true,"paginationKnown":true,"hasMore":false,"pagesFetched":1,"failedCount":0,"failures":[],"partial":false,"truncated":false,"stopReason":"source_complete","queryRange":{"startTime":"2026-11-06T09:00:00+08:00","endTime":"2026-11-06T11:00:00+08:00","order":"asc","semantics":"[start,end)"}}}
""";
string Change(string key, JsonNode? value) { var n = JsonNode.Parse(Payload())!; n["data"]![key] = value; return n.ToJsonString(); }
MessageBatch Batch() => DwsParser.ParseMessages(Payload(), scope);
string Analysis(string status="in_progress", string quote="修复已提交，请复测。", string id="m2") => JsonSerializer.Serialize(new {
    issues = new[] {new {title="登录超时",summary="已提交修复，等待复测",status,communication="需要明确复测负责人",nextStep="测试人员复测",evidence=new[]{new {messageId=id,quote}}}},
    pendingQuestions=new[]{"修复是否生效？"}
});
void Add(string name, Action test) => tests.Add((name,test));
void True(bool condition) { if(!condition) throw new Exception("assertion failed"); }
void Throws(Action action) { try { action(); } catch (SafetyException) {return;} throw new Exception("expected SafetyException"); }

Add("DWS 稳定契约及无时区时间按 UTC+8 解析", () => {var b=Batch();True(b.Messages.Count==2 && b.Complete && b.Messages[0].CreatedAt.Offset==TimeSpan.FromHours(8));});
Add("缺 complete 不能报告完整", () => {var n=JsonNode.Parse(Payload())!;n["data"]!.AsObject().Remove("complete");True(!DwsParser.ParseMessages(n.ToJsonString(),scope).Complete);});
Add("缺分页证据不能报告完整", () => True(!DwsParser.ParseMessages(Change("paginationKnown",null),scope).Complete));
Add("截断、部分失败和有更多页均保留不完整", () => {foreach(var key in new[]{"hasMore","partial","truncated"})True(!DwsParser.ParseMessages(Change(key,JsonValue.Create(true)),scope).Complete);});
Add("非零退出不能报告完整", () => True(!DwsParser.ParseMessages(Payload(),scope,7).Complete));
Add("混入其他会话立即拒绝", () => Throws(()=>DwsParser.ParseMessages(Payload().Replace("cid-demo","cid-other"),scope)));
Add("消息时间越界立即拒绝", () => Throws(()=>DwsParser.ParseMessages(Payload().Replace("09:10:00","11:00:00"),scope)));
Add("相同 ID 内容冲突立即拒绝", () => Throws(()=>DwsParser.ParseMessages(Payload().Replace("\"m2\"","\"m1\""),scope)));
Add("同一消息重复可去重并说明", () => {var n=JsonNode.Parse(Payload())!;var a=n["data"]!["messages"]!.AsArray();a.Add(a[0]!.DeepClone());n["data"]!["count"]=3;var b=DwsParser.ParseMessages(n.ToJsonString(),scope);True(b.Messages.Count==2 && b.Warnings.Count>0);});
Add("缺消息正文保留无法读取提示", () => {var n=JsonNode.Parse(Payload())!;n["data"]!["messages"]![0]!["text"]="";var b=DwsParser.ParseMessages(n.ToJsonString(),scope);True(!b.Complete && b.Warnings.Count>0);});
Add("未知契约不猜字段", () => Throws(()=>DwsParser.ParseMessages(Change("contractVersion",JsonValue.Create("future.v2")),scope)));
Add("非法时间范围在查询前拒绝", () => Throws(()=>new QueryScope("corp:user","cid",scope.End,scope.Start).Validate()));
Add("DWS 失败信封不作为空成功", () => Throws(()=>DwsParser.ParseMessages("{\"ok\":false,\"outcome\":\"failure\",\"error\":{\"message\":\"SYNTHETIC_SECRET\"}}",scope)));
Add("模型证据逐字匹配后允许展示", () => {var r=AnalysisValidator.Validate(Analysis(),Batch());True(r.Issues.Count==1 && r.Issues[0].Evidence[0].Message.MessageId=="m2");});
Add("模型伪造 ID 被拒绝", () => Throws(()=>AnalysisValidator.Validate(Analysis(id:"invented"),Batch())));
Add("模型伪造引文被拒绝", () => Throws(()=>AnalysisValidator.Validate(Analysis(quote:"已经复测通过"),Batch())));
Add("模型 resolved 与人工确认分别记录", () => {var r=AnalysisValidator.Validate(Analysis(status:"resolved"),Batch());True(r.Issues[0].ModelReportedResolved&&r.Issues[0].Status==IssueStatus.PendingVerification&&!r.Issues[0].HumanConfirmed);});
Add("人工确认仅在复核证据后生效", () => {var r=AnalysisValidator.Validate(Analysis(status:"resolved"),Batch());Throws(()=>r.ConfirmResolved(0,false));r.ConfirmResolved(0,true);True(r.Issues[0].Status==IssueStatus.Resolved && r.Issues[0].HumanConfirmed);});
Add("缺证据和未知状态被拒绝", () => {var n=JsonNode.Parse(Analysis())!;n["issues"]![0]!["evidence"]=new JsonArray();Throws(()=>AnalysisValidator.Validate(n.ToJsonString(),Batch()));Throws(()=>AnalysisValidator.Validate(Analysis(status:"whatever"),Batch()));});
Add("Markdown 包含来源与不完整提示且不生成活动链接", () => {var b=DwsParser.ParseMessages(Change("complete",JsonValue.Create(false)),scope);var r=AnalysisValidator.Validate(Analysis(),b);var md=ReportRenderer.Markdown(r);True(md.Contains("m2")&&md.Contains("不完整")&&!md.Contains("](http"));});
Add("模拟报告不将修复提交当作已解决", () => {var r=Demo.CreateReport();True(r.Batch.IsDemo && r.Issues.All(i=>i.Status!=IssueStatus.Resolved));});
Add("矛盾终止原因不能推进完整断点",()=>True(!DwsParser.ParseMessages(Change("stopReason",JsonValue.Create("page_limit")),scope).Complete));
Add("已存在的截断字段必须为正确布尔类型",()=>True(!DwsParser.ParseMessages(Change("truncatedByPageLimit",JsonValue.Create("false")),scope).Complete));
Add("空结果的返回时间范围不匹配时仍属不完整",()=>{var n=JsonNode.Parse(Payload())!;n["data"]!["messages"]=new JsonArray();n["data"]!["count"]=0;n["data"]!["queryRange"]=JsonSerializer.SerializeToNode(new{startTime=scope.Start.AddHours(1),endTime=scope.End,order="asc",semantics="[start,end)"});True(!DwsParser.ParseMessages(n.ToJsonString(),scope).Complete);});

Add("官方升序范围终止原因允许完整",()=>True(DwsParser.ParseMessages(Change("stopReason",JsonValue.Create("range_end")),scope).Complete));
Add("未知或缺失终止原因不能推进断点",()=>{foreach(var value in new JsonNode?[]{null,JsonValue.Create("future_reason"),JsonValue.Create("range_start")})True(!DwsParser.ParseMessages(Change("stopReason",value),scope).Complete);});
Add("缺失或错误范围元数据不能推进断点",()=>{foreach(var field in new[]{"startTime","endTime","order","semantics"}){var n=JsonNode.Parse(Payload())!;n["data"]!["queryRange"]![field]="unknown";True(!DwsParser.ParseMessages(n.ToJsonString(),scope).Complete);}True(!DwsParser.ParseMessages(Change("queryRange",null),scope).Complete);});
Add("补充完整性字段未知类型或真值均不完整",()=>{foreach(var field in new[]{"truncatedByPageLimit","truncatedByResultLimit"})foreach(var value in new JsonNode?[]{null,JsonValue.Create("false"),JsonValue.Create(true)})True(!DwsParser.ParseMessages(Change(field,value),scope).Complete);});

tests.AddRange(DeliveryTests.All());
tests.AddRange(ReportReviewTests.All());
tests.AddRange(ReviewControllerTests.All());
tests.AddRange(TaskAwareAnalysisTests.All());
tests.AddRange(RuntimeTests.All());
tests.AddRange(WorkbenchAdapterTests.All());
tests.AddRange(NativeAdapterTests.All());
tests.AddRange(ServiceTests.All());
tests.AddRange(MonitorTests.All());
tests.AddRange(BackgroundTests.All());
tests.AddRange(HostStartupTests.All());
tests.AddRange(DwsProcessTests.All());
var failed=0;
foreach(var (name,test) in tests)try { test();Console.WriteLine("PASS "+name); }catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetType().Name+" "+e.Message);}
Console.WriteLine($"{tests.Count-failed}/{tests.Count} passed");
return failed==0?0:1;
