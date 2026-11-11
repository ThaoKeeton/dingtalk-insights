using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DingTalkInsights.Core;

public sealed record WebhookTarget(string Name,string Url,string SigningSecret)
{
    public void Validate()
    {
        Guard.Identifier(Name,"请为机器人设置明确的群名称。");
        if(!Uri.TryCreate(Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="oapi.dingtalk.com"||!uri.IsDefaultPort||uri.AbsolutePath!="/robot/send"||uri.Fragment.Length>0||uri.UserInfo.Length>0)
            throw new SafetyException("只允许官方钉钉 HTTPS 自定义机器人地址。");
        var parts=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length!=1||!parts[0].StartsWith("access_token=",StringComparison.Ordinal)||parts[0].Length<=13)
            throw new SafetyException("Webhook 地址必须且只能携带一个 access_token。");
    }
}
public enum DeliveryStatus { Sent, Failed, Unknown }
public sealed class WebhookClient(ConsentGate consent,HttpClient? http=null)
{
    private readonly HttpClient http=http??HttpSafety.Shared;
    public static string Fingerprint(WebhookTarget target,string text)=>Guard.Hash(JsonSerializer.Serialize(target)+"\n"+text);
    public async Task<DeliveryStatus> SendAsync(WebhookTarget target,string text,ConsentToken? token,CancellationToken cancellation=default)
    {
        target.Validate();
        if(string.IsNullOrWhiteSpace(text)||Encoding.UTF8.GetByteCount(text)>18000)throw new SafetyException("Webhook 内容为空或超过 18 KiB，请缩小整理范围。");
        consent.Consume(token,ExternalAction.Send,Fingerprint(target,text));
        var url=target.Url;
        if(!string.IsNullOrEmpty(target.SigningSecret))
        {
            var timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var signature=Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(target.SigningSecret),Encoding.UTF8.GetBytes(timestamp+"\n"+target.SigningSecret)));
            url+="&timestamp="+timestamp+"&sign="+Uri.EscapeDataString(signature);
        }
        try
        {
            var payload=JsonSerializer.Serialize(new{msgtype="markdown",markdown=new{title="钉钉信息整理 · 待核实",text},at=new{isAtAll=false}});
            using var deadline=HttpSafety.Deadline(http,cancellation);
            using var request=new HttpRequestMessage(HttpMethod.Post,url){Content=new StringContent(payload,Encoding.UTF8,"application/json")};
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if(!response.IsSuccessStatusCode)return DeliveryStatus.Failed;
            using var doc=JsonDocument.Parse(await HttpSafety.ReadAsync(response,8192,deadline.Token));
            return doc.RootElement.TryGetProperty("errcode",out var code)&&code.TryGetInt32(out var value)&&value==0?DeliveryStatus.Sent:DeliveryStatus.Failed;
        }
        catch(Exception e)when(e is OperationCanceledException or HttpRequestException or JsonException or SafetyException){return DeliveryStatus.Unknown;}
    }
}
