using System.Text;
using System.Text.Json;

namespace DingTalkInsights.Core;

public static class AnalysisValidator
{
    public static AnalysisReport Validate(string json, MessageBatch batch, string task="沟通难点检查")
    {
        try
        {
            using var doc=DwsParser.Parse(json);var root=doc.RootElement;
            Exact(root,"issues","pendingQuestions");
            var rows=root.GetProperty("issues");
            if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>30)throw new SafetyException("整理条目超限或格式无效。");
            var source=batch.Messages.ToDictionary(m=>m.MessageId);
            var issues=new List<Issue>();
            foreach(var row in rows.EnumerateArray())
            {
                Exact(row,"title","summary","status","communication","nextStep","evidence");
                var modelStatus=Guard.Text(row,"status",40);
                var status=modelStatus switch
                {"open"=>IssueStatus.Open,"in_progress"=>IssueStatus.InProgress,"pending_verification" or "resolved"=>IssueStatus.PendingVerification,_=>throw new SafetyException("模型返回了未知状态。")};
                var evidence=new List<Evidence>();var es=row.GetProperty("evidence");
                if(es.ValueKind!=JsonValueKind.Array||es.GetArrayLength() is <1 or >20)throw new SafetyException("每个问题必须关联原文证据。");
                foreach(var e in es.EnumerateArray())
                {
                    Exact(e,"messageId","quote");var id=Guard.Text(e,"messageId",512);var quote=Guard.Text(e,"quote",2000);
                    if(!source.TryGetValue(id,out var message)||!message.Text.Contains(quote,StringComparison.Ordinal))throw new SafetyException("模型引用的消息或引文无法核验，整理结果已拒绝。");
                    evidence.Add(new Evidence(message,quote));
                }
                issues.Add(new Issue(Guard.Text(row,"title",160),Guard.Text(row,"summary",1000),status,Guard.Text(row,"communication",500,true),Guard.Text(row,"nextStep",500,true),evidence,modelStatus=="resolved"));
            }
            var qs=root.GetProperty("pendingQuestions");
            if(qs.ValueKind!=JsonValueKind.Array||qs.GetArrayLength()>30)throw new SafetyException("待确认事项格式无效。");
            var questions=qs.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String&&v.GetString()!.Length<=500?v.GetString()!:throw new SafetyException("待确认事项无效。")).ToArray();
            return new AnalysisReport(batch,issues,questions,task);
        }
        catch(SafetyException){throw;}
        catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException){throw new SafetyException("模型输出未通过结构校验。");}
    }
    private static void Exact(JsonElement e,params string[] names)
    {
        if(e.ValueKind!=JsonValueKind.Object)throw new SafetyException("整理结构无效。");
        var actual=e.EnumerateObject().Select(p=>p.Name).ToArray();
        if(actual.Length!=names.Length||!actual.Order().SequenceEqual(names.Order()))throw new SafetyException("整理结果字段不符合约定。");
    }
}

public enum ReportInputContext { Current,Unknown,Historical }
public static class ReportRenderer
{
    public static string Plain(string text)=>new(text.Select(c=>c switch{'['=>'［',']'=>'］','<'=>'＜','>'=>'＞','`'=>'｀','@'=>'＠',_=>c}).ToArray());
    // The canonical representation is kept byte-compatible with older review archives.
    public static string Markdown(AnalysisReport report)=>Render(report,false,ReportInputContext.Current);
    public static string Display(AnalysisReport report,ReportInputContext context=ReportInputContext.Current)=>Render(report,true,context);
    static string Render(AnalysisReport report,bool display,ReportInputContext context)
    {
        var b=new StringBuilder(!display?"# 钉钉信息整理 · 待核实\n\n":context switch
        {ReportInputContext.Current=>"# 钉钉信息整理 · 模型结果\n\n",ReportInputContext.Historical=>"# 钉钉信息整理 · 历史模型结果\n\n",_=>"# 钉钉信息整理 · 来源状态未明\n\n"});
        if(report.Batch.IsDemo)b.AppendLine("**模拟数据演示**\n");
        if(display)b.AppendLine(context switch
        {ReportInputContext.Current=>"模型输入与本机已保存的来源版本一致；人工核实为可选操作，模型判断不等于人工确认。\n",
         ReportInputContext.Historical=>"引用原文或其他输入已修订或缺失，本报告只反映当时来源，不代表当前状态。\n",
         _=>"无法核对本报告全部输入是否仍为当前版本；以下反映当时的模型判断。\n"});
        b.AppendLine(Plain(report.Task)).AppendLine().AppendLine(Plain(report.Batch.Scope.Display)).AppendLine();
        b.AppendLine(report.Batch.Complete?"已返回消息分页结束；可见范围和附件仍需核实。":"**采集不完整或未知，结论范围受限。**").AppendLine();
        foreach(var i in report.Issues)
        {
            var label=!display?i.StatusLabel:context switch{ReportInputContext.Current=>i.AssessmentLabel,ReportInputContext.Historical=>"历史："+i.AssessmentLabel,_=>"来源状态未明："+i.AssessmentLabel};
            b.AppendLine("## "+Plain(i.Title)).AppendLine().AppendLine("状态："+label).AppendLine().AppendLine(Plain(i.Summary)).AppendLine();
            if(i.Communication.Length>0)b.AppendLine((display?"沟通要点：":"沟通待核实点：")+Plain(i.Communication)).AppendLine();
            if(i.NextStep.Length>0)b.AppendLine("下一步："+Plain(i.NextStep)).AppendLine();
            foreach(var e in i.Evidence)b.AppendLine($"> {Plain(e.Quote).Replace("\n","\n> ")}\n> 来源：{Plain(e.Message.ConversationId)} / {Plain(e.Message.MessageId)} · {e.Message.CreatedAt:yyyy-MM-dd HH:mm:ss zzz} · {Plain(e.Message.Sender)}\n");
        }
        if(report.Issues.Count==0)b.AppendLine("本批未生成问题候选；不代表不存在沟通难点。\n");
        if(report.PendingQuestions.Count>0)b.AppendLine("## 待确认\n");
        foreach(var q in report.PendingQuestions)b.AppendLine("- "+Plain(q));
        b.AppendLine().AppendLine("## 范围与限制\n");foreach(var w in report.Batch.Warnings)b.AppendLine("- "+Plain(w));
        return b.ToString();
    }
}
