using System.Text.Json;

namespace DingTalkInsights.Core;

public static class Demo
{
    public static MessageBatch CreateBatch()
    {
        var start=DateTimeOffset.Parse("2026-11-06T09:00:00+08:00");
        var scope=new QueryScope("demo:local","demo-project",start,start.AddHours(3));
        var data=new {contractVersion="im.message-list.v1",messages=new[]{
            new{messageId="demo-1",conversationId=scope.ConversationId,senderId="demo-a",sender="模拟测试甲",text="登录偶发超时，发布前需要确认原因。",createTime=start.AddMinutes(10),messageType="text"},
            new{messageId="demo-2",conversationId=scope.ConversationId,senderId="demo-b",sender="模拟开发乙",text="修复已提交，请在测试环境复测，还没有生产验证。",createTime=start.AddMinutes(25),messageType="text"},
            new{messageId="demo-3",conversationId=scope.ConversationId,senderId="demo-c",sender="模拟产品丙",text="接口中的完成状态是提交完成还是验收通过？目前两边理解不一致。",createTime=start.AddMinutes(40),messageType="text"},
            new{messageId="demo-4",conversationId=scope.ConversationId,senderId="demo-a",sender="模拟测试甲",text="我负责复测登录，验收口径请产品确认，预计今天下午反馈。",createTime=start.AddMinutes(50),messageType="text"}},count=4,complete=true,paginationKnown=true,hasMore=false,pagesFetched=1,failedCount=0,failures=Array.Empty<object>(),partial=false,truncated=false,stopReason="source_complete"};
        return DwsParser.ParseMessages(JsonSerializer.Serialize(new{ok=true,outcome="success",data}),scope) with{IsDemo=true};
    }
    public static AnalysisReport CreateReport()=>Analyze(CreateBatch());
    public static AnalysisReport Analyze(MessageBatch batch,string task="沟通难点检查（模拟）")
    {
        var issues=new List<object>();
        foreach(var m in batch.Messages.Where(m=>m.MessageId is "demo-2" or "demo-3"))
            issues.Add(new{title=m.MessageId=="demo-2"?"登录超时等待复测":"完成状态的验收口径待对齐",summary=m.MessageId=="demo-2"?"修复已提交，尚无生产验证证据。":"双方对完成状态的含义尚未一致。",status="pending_verification",communication="核对验收条件与下一次反馈时间；不据此评价任何人。",nextStep="回原会话核实范围、负责人和验证结果。",evidence=new[]{new{messageId=m.MessageId,quote=m.Text}}});
        return AnalysisValidator.Validate(JsonSerializer.Serialize(new{issues,pendingQuestions=new[]{"是否已有明确的验收标准及验证结果？"}}),batch,task);
    }
}
