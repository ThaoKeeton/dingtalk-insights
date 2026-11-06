using System.Net;

namespace DingTalkInsights.Core;

internal static class HttpSafety
{
    internal static HttpClient Shared {get;}=Create();
    internal static HttpClient Create()=>new(new HttpClientHandler{AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate}){Timeout=TimeSpan.FromSeconds(90)};
    internal static CancellationTokenSource Deadline(HttpClient client,CancellationToken cancellation)
    {
        var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // ResponseHeadersRead ends HttpClient.Timeout at headers; keep our deadline alive through the body.
        var limit=client.Timeout==Timeout.InfiniteTimeSpan?TimeSpan.FromSeconds(90):client.Timeout;
        deadline.CancelAfter(limit<TimeSpan.FromSeconds(90)?limit:TimeSpan.FromSeconds(90));
        return deadline;
    }
    internal static async Task<string> ReadAsync(HttpResponseMessage response,int maxBytes,CancellationToken cancellation)
    {
        if(response.Content.Headers.ContentLength>maxBytes)throw new SafetyException("服务响应超过本机大小上限。");
        await using var stream=await response.Content.ReadAsStreamAsync(cancellation);using var bytes=new MemoryStream();
        var buffer=new byte[8192];int length;
        while((length=await stream.ReadAsync(buffer,cancellation))>0)
        {if(bytes.Length+length>maxBytes)throw new SafetyException("服务响应超过本机大小上限。");bytes.Write(buffer,0,length);}
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
