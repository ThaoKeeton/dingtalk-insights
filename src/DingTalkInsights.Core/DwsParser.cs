using System.Text;
using System.Text.Json;

namespace DingTalkInsights.Core;

public sealed record Conversation(string ConversationId, string Name, string Type)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? ConversationId : $"{Name}  ·  {ConversationId}";
}

public static class DwsParser
{
    public static MessageBatch ParseMessages(string json, QueryScope scope, int exitCode = 0)
    {
        scope.Validate();
        try
        {
            using var doc = Parse(json);
            var data = Payload(doc.RootElement);
            if (Guard.Optional(data, "contractVersion") != "im.message-list.v1") throw new SafetyException("DWS 消息契约不受支持，请核对版本和 schema。");
            if (!data.TryGetProperty("messages", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 5000)
                throw new SafetyException("DWS 消息列表缺失或超过本次 5000 条上限。");
            var warnings = new List<string>();
            var complete = Guard.True(data, "complete") && Guard.True(data, "paginationKnown") && Guard.False(data, "hasMore")
                && Guard.False(data, "partial") && Guard.False(data, "truncated") && exitCode == 0
                && !Guard.True(data,"truncatedByPageLimit") && !Guard.True(data,"truncatedByResultLimit");
            if (!data.TryGetProperty("failures", out var failures) || failures.ValueKind != JsonValueKind.Array || failures.GetArrayLength() > 0) complete = false;
            if (!data.TryGetProperty("failedCount", out var fc) || !fc.TryGetInt32(out var count) || count != 0) complete = false;
            if (Guard.Optional(doc.RootElement,"outcome") is "partial_failure" or "pending") complete = false;
            var messages = new Dictionary<string, SourceMessage>();
            foreach (var raw in array.EnumerateArray())
            {
                var id = Guard.Text(raw, "messageId", 512);
                var cid = Guard.Text(raw, "conversationId", 512);
                if (cid != scope.ConversationId) throw new SafetyException("DWS 返回了未选择的会话，已停止读取。");
                if (!raw.TryGetProperty("createTime", out var ct)) throw new SafetyException("消息缺少时间，无法确认范围。");
                var created = Guard.Time(ct);
                if (created < scope.Start || created >= scope.End) throw new SafetyException("DWS 消息超出所选时间范围，已停止读取。");
                var text = Guard.Optional(raw, "text");
                if (text.Length > 64000) throw new SafetyException("单条消息超过本机内容上限。");
                var kind = Guard.Optional(raw, "messageType");
                if (string.IsNullOrWhiteSpace(text) || kind is not ("text" or "markdown" or "" ) || text.Contains("加密消息",StringComparison.Ordinal))
                { complete = false; warnings.Add($"消息 {id} 可能含未读取正文、附件或加密内容。"); }
                if(raw.TryGetProperty("quotedMessage",out var quoted) && quoted.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    warnings.Add($"消息 {id} 的引用上下文未展开，请回原会话核实。");
                var item = new SourceMessage(id, cid, Guard.Optional(raw,"senderId"), Guard.Optional(raw,"sender"), text, created, kind, Guard.Optional(raw,"updateTime"));
                if (messages.TryGetValue(id, out var prior))
                {
                    if (prior != item) throw new SafetyException("同一消息 ID 出现冲突，已停止合并。");
                    warnings.Add("已去除重复消息。");
                }
                else messages.Add(id, item);
            }
            if (!data.TryGetProperty("count",out var cnt) || !cnt.TryGetInt32(out var declared) || declared != array.GetArrayLength())
            { complete=false;warnings.Add("DWS 返回条数与列表不一致。"); }
            if (!complete) warnings.Insert(0, "采集不完整或完整性未知；不能据此断言没有问题或问题已解决。");
            warnings.Add("分页状态仅描述已返回的数据；附件、撤回和服务端可见历史仍可能缺失。");
            var ledgerKeys = new[] {"contractVersion","queryRange","count","pagesFetched","paginationKnown","complete","hasMore","nextPage","stopReason","truncated","truncatedByPageLimit","truncatedByResultLimit","failedCount","failures","partial","scope","resourceDownloads"};
            var ledger = data.EnumerateObject().Where(p=>ledgerKeys.Contains(p.Name)).ToDictionary(p=>p.Name,p=>p.Value.Clone());
            return new MessageBatch(scope, messages.Values.OrderBy(m=>m.CreatedAt).ThenBy(m=>m.MessageId,StringComparer.Ordinal).ToArray(), complete, warnings.Distinct().ToArray(),ledger);
        }
        catch (SafetyException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new SafetyException("DWS 数据格式无效；未采用不明数据。"); }
    }

    public static IReadOnlyList<Conversation> ParseConversations(string json, int exitCode = 0)
    {
        try
        {
            using var doc = Parse(json);
            var data = Payload(doc.RootElement);
            if (exitCode != 0 || !Guard.True(data,"complete")) throw new SafetyException("会话发现尚未完整完成；请缩小时间范围或检查 DWS 权限。");
            var rows = data.GetProperty("conversations");
            return rows.EnumerateArray().Select(r=>new Conversation(Guard.Text(r,"conversationId",512),Guard.Optional(r,"name"),Guard.Optional(r,"type")))
                .DistinctBy(r=>r.ConversationId).Take(1000).ToArray();
        }
        catch (SafetyException) { throw; }
        catch (Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new SafetyException("会话列表格式不受支持。"); }
    }

    internal static JsonDocument Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json)>4*1024*1024) throw new SafetyException("DWS 输出超过 4 MiB，已停止处理。");
        return JsonDocument.Parse(json, new JsonDocumentOptions{MaxDepth=40});
    }
    internal static JsonElement Payload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new SafetyException("DWS 未返回对象。");
        if (root.TryGetProperty("outcome",out var outcome))
        {
            if (outcome.GetString() is not ("success" or "partial_failure")) throw new SafetyException("DWS 未成功返回数据，请在本机检查授权和命令版本。");
            if (!root.TryGetProperty("data",out var data) || data.ValueKind!=JsonValueKind.Object) throw new SafetyException("DWS 缺少业务数据。");
            return data;
        }
        return root;
    }
}
