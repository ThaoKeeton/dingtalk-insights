using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DingTalkInsights.Core;

/// <summary>Only hashed identities and retention time; no task or message text.</summary>
public sealed class TaskSourceAttempts
{
    public DateTimeOffset CreatedAt{get;set;}
    public HashSet<string> Tasks{get;set;}=[];
}
public sealed record AnalysisSelection(MessageBatch Batch,int SkippedSameTask,int PreviouslyAttempted);

public static class AnalysisAttemptPolicy
{
    public const int MaximumSources=100_000,MaximumTasksPerSource=128;
    public static string TaskKey(string task)
    {
        if(string.IsNullOrWhiteSpace(task)||task.Length>2000)throw new SafetyException("请提供整理事项与 1–40 条候选消息；请先缩小范围。");
        try
        {
            var text=Regex.Replace(task.Normalize(NormalizationForm.FormD).Trim(),@"\s+"," ");
            var folded=new StringBuilder(text.Length);
            foreach(var rune in text.EnumerateRunes())folded.Append(UnicodeCaseFold.Map.TryGetValue(rune.Value,out var mapped)?mapped:rune.ToString());
            return Guard.Hash(folded.ToString().Normalize(NormalizationForm.FormC));
        }
        catch(ArgumentException){throw new SafetyException("整理事项包含不支持的字符。");}
    }
    public static bool ValidHash(string value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F');
    public static void Validate(IReadOnlyDictionary<string,TaskSourceAttempts>? sources)
    {
        if(sources is null||sources.Count>MaximumSources||sources.Any(p=>!ValidHash(p.Key)||p.Value is null||p.Value.CreatedAt==DateTimeOffset.MinValue||
            p.Value.Tasks is null||p.Value.Tasks.Count is <1 or >MaximumTasksPerSource||p.Value.Tasks.Any(t=>!ValidHash(t))))
            throw new MonitoringFault(MonitorFailure.Storage);
    }
    public static AnalysisSelection Select(InsightState state,MessageBatch batch,string task)
    {
        if(!state.AnalysisAttemptsAvailable)throw new MonitoringFault(MonitorFailure.Storage);
        batch.Scope.ValidateOrigin();var taskKey=TaskKey(task);var eligible=new List<SourceMessage>();var prior=0;var skipped=0;var seen=new HashSet<string>();
        foreach(var message in batch.Messages)
        {
            if(message.ConversationId!=batch.Scope.ConversationId||message.CreatedAt<batch.Scope.Start||message.CreatedAt>=batch.Scope.End)
                throw new SafetyException("候选包含未授权范围的来源，请重新选择。");
            var source=InsightState.AttemptKey(batch.Scope,message);
            if(!seen.Add(message.MessageId))throw new SafetyException("候选中存在重复消息 ID，请重新选择。");
            if(state.TaskAttempts.TryGetValue(source,out var existing)&&existing.Tasks.Contains(taskKey)){skipped++;continue;}
            eligible.Add(message);if(state.Attempted.Contains(source))prior++;
        }
        var warnings=skipped==0?batch.Warnings.ToArray():batch.Warnings.Concat([$"已排除同一事项此前尝试过的 {skipped} 条来源修订；未知结果不会自动重试。"]).ToArray();
        return new(batch with{Messages=Array.AsReadOnly(eligible.ToArray()),Warnings=Array.AsReadOnly(warnings),Complete=skipped==0&&batch.Complete},skipped,prior);
    }
    public static void RequireExact(InsightState state,MessageBatch batch,string task,bool reuseConfirmed)
    {
        var selected=Select(state,batch,task);
        if(batch.Messages.Count==0||selected.SkippedSameTask!=0)throw new SafetyException("同一事项已有来源尝试；请核对历史，未知结果不会自动重试。");
        if(selected.PreviouslyAttempted>0&&!reuseConfirmed)throw new SafetyException("这些来源此前已尝试；请先单独确认这是不同事项并核对重复计费风险。");
        var taskKey=TaskKey(task);var newSources=0;
        foreach(var message in batch.Messages)
        {
            var key=InsightState.AttemptKey(batch.Scope,message);
            if(state.TaskAttempts.TryGetValue(key,out var source))
            {if(source.Tasks.Count>=MaximumTasksPerSource&&!source.Tasks.Contains(taskKey))throw new MonitoringFault(MonitorFailure.Storage);}
            else newSources++;
        }
        if(state.TaskAttempts.Count+newSources>MaximumSources)throw new MonitoringFault(MonitorFailure.Storage);
    }
    public static void Record(InsightState state,MessageBatch batch,string task)
    {
        var taskKey=TaskKey(task);
        foreach(var message in batch.Messages)
        {
            var sourceKey=InsightState.AttemptKey(batch.Scope,message);
            if(!state.TaskAttempts.TryGetValue(sourceKey,out var source))state.TaskAttempts[sourceKey]=source=new(){CreatedAt=message.CreatedAt};
            source.Tasks.Add(taskKey);
        }
        state.MarkAttempted(batch.Scope,batch.Messages);
    }
}

/// <summary>Default full C/F case folding, pinned to unmodified Unicode 15.1 data.</summary>
internal static class UnicodeCaseFold
{
    public static IReadOnlyDictionary<int,string> Map{get;}=Load();
    static IReadOnlyDictionary<int,string> Load()
    {
        using var stream=typeof(UnicodeCaseFold).Assembly.GetManifestResourceStream("DingTalkInsights.Core.CaseFolding-15.1.0.txt")??throw new InvalidOperationException("Unicode folding data is unavailable.");
        using var bytes=new MemoryStream();stream.CopyTo(bytes);
        if(Convert.ToHexString(SHA256.HashData(bytes.ToArray()))!="4E55ACFDC32825A22E87670E9056A3BF94AD7C5400065778E9E10F8314372BCF")throw new InvalidOperationException("Unicode folding data changed.");
        bytes.Position=0;using var reader=new StreamReader(bytes,Encoding.UTF8);var map=new Dictionary<int,string>();string? line;
        while((line=reader.ReadLine()) is not null)
        {
            if(line.Length==0||line[0]=='#')continue;
            var fields=line.Split(';');if(fields.Length<3)continue;
            var status=fields[1].Trim();if(status is not("C" or "F"))continue;
            var source=int.Parse(fields[0].Trim(),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            var text=new StringBuilder();
            foreach(var point in fields[2].Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
                text.Append(char.ConvertFromUtf32(int.Parse(point,NumberStyles.HexNumber,CultureInfo.InvariantCulture)));
            map.Add(source,text.ToString());
        }
        if(map.Count!=1530)throw new InvalidOperationException("Unicode folding data is incomplete.");
        return map;
    }
}
