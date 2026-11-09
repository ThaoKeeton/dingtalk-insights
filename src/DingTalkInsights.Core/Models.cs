using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DingTalkInsights.Core;

public class SafetyException(string message) : Exception(message);

public sealed record QueryScope(string Profile, string ConversationId, DateTimeOffset Start, DateTimeOffset End)
{
    public void Validate()
    {
        ValidateOrigin();
        if (End - Start > TimeSpan.FromDays(31))
            throw new SafetyException("身份或时间范围无效；每次查询最多 31 天，结束时间不包含在范围中。");
    }
    // A saved monitoring origin is not a single request: only each fetch has the 31-day query bound.
    public void ValidateOrigin()
    {
        Guard.Identifier(Profile, "请选择明确的 DWS 身份（corpId:userId）。");
        Guard.Identifier(ConversationId, "请选择明确的会话 ID。");
        if(!Profile.Contains(':')||End<=Start)throw new SafetyException("监控身份或开始时间无效。");
    }
    [JsonIgnore] public string Key => Guard.Hash(Profile + "\n" + ConversationId);
    [JsonIgnore] public string Fingerprint => Guard.Hash(JsonSerializer.Serialize(this));
    [JsonIgnore] public string Display => $"{Profile} / {ConversationId}\n{Start:yyyy-MM-dd HH:mm:ss zzz} 至 {End:yyyy-MM-dd HH:mm:ss zzz}（不含结束时间）";
}

public sealed record SourceMessage(string MessageId, string ConversationId, string SenderId, string Sender,
    string Text, DateTimeOffset CreatedAt, string MessageType = "text", string UpdateTime = "")
{
    [JsonIgnore] public string Revision => Guard.Hash(JsonSerializer.Serialize(this));
}

public sealed record MessageBatch(QueryScope Scope, IReadOnlyList<SourceMessage> Messages, bool Complete,
    IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, JsonElement> Ledger, bool IsDemo = false)
{
    [JsonIgnore] public string Fingerprint => Guard.Hash(Scope.Fingerprint + string.Join("", Messages.Select(m => m.Revision)) + Complete);
}

public enum IssueStatus { Open, InProgress, PendingVerification, Resolved }
public sealed record Evidence(SourceMessage Message, string Quote);
public sealed class Issue(string title, string summary, IssueStatus status, string communication, string nextStep, IReadOnlyList<Evidence> evidence,bool modelReportedResolved=false)
{
    public string Title { get; } = title;
    public string Summary { get; } = summary;
    public IssueStatus Status { get; private set; } = status;
    public string Communication { get; } = communication;
    public string NextStep { get; } = nextStep;
    public IReadOnlyList<Evidence> Evidence { get; } = evidence;
    // A model assessment is not an operator's verification or a change to the human status.
    public bool ModelReportedResolved { get; } = modelReportedResolved;
    public bool HumanConfirmed { get; private set; }
    public string StatusLabel => Status switch { IssueStatus.Open => "待处理 · 待核实", IssueStatus.InProgress => "沟通中 · 待核实", IssueStatus.Resolved => "人工确认已解决", _ => "待验证 · 待核实" };
    public string AssessmentLabel => HumanConfirmed ? "人工确认已解决" : ModelReportedResolved ? "模型判断已解决（未人工确认）" : Status switch
    { IssueStatus.Open => "模型判断待处理（未人工确认）", IssueStatus.InProgress => "模型判断沟通中（未人工确认）", IssueStatus.Resolved => "解决状态未人工确认", _ => "待验证（未人工确认）" };
    internal void Confirm() { Status = IssueStatus.Resolved; HumanConfirmed = true; }
}

public sealed class AnalysisReport(MessageBatch batch, IReadOnlyList<Issue> issues, IReadOnlyList<string> pendingQuestions, string task = "沟通难点检查")
{
    public MessageBatch Batch { get; } = batch;
    public IReadOnlyList<Issue> Issues { get; } = issues;
    public IReadOnlyList<string> PendingQuestions { get; } = pendingQuestions;
    public string Task { get; } = task;
    public void ConfirmResolved(int index, bool evidenceReviewed)
    {
        if (!evidenceReviewed || index < 0 || index >= Issues.Count) throw new SafetyException("请先复核当前问题的原文证据。");
        Issues[index].Confirm();
    }
}

public static class Guard
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static void Identifier(string? value, string error)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw new SafetyException(error);
    }
    public static string Text(JsonElement obj, string key, int max = 4000, bool empty = false)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) throw new SafetyException("数据缺少所需文本字段。");
        var s = v.GetString()!;
        if ((!empty && string.IsNullOrWhiteSpace(s)) || s.Length > max || s.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
            throw new SafetyException("文本为空、过长或含不支持的控制字符。");
        return s;
    }
    public static bool True(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public static bool False(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.False;
    public static string Optional(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    public static DateTimeOffset Time(JsonElement e)
    {
        var s = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
        if (long.TryParse(s, out var epoch))
        {
            try { return epoch > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { throw new SafetyException("消息时间无效。"); }
        }
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(8));
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(Z|[+-]\d\d:\d\d)$") && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return time;
        throw new SafetyException("消息时间无效或缺少可确认的时区。");
    }
}
