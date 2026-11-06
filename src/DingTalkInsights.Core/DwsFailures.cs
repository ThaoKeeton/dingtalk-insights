using System.Text.Json;

namespace DingTalkInsights.Core;

/// <summary>Pinned v1.0.67 wire keys only; localized message/hint/body are never classification inputs.</summary>
public static class DwsFailures
{
    public static MonitoringFault Classify(string json,int exitCode)
    {
        try
        {
            using var document=DwsParser.Parse(json);var root=document.RootElement;
            if(!root.TryGetProperty("error",out var error)||error.ValueKind!=JsonValueKind.Object)return new(MonitorFailure.Unknown);
            var type=Guard.Optional(error,"type");
            var http=error.TryGetProperty("http_status",out var h)&&h.TryGetInt32(out var status)?status:0;
            if(type is "auth" or "permission"||exitCode is 2 or 4||http is 401 or 403)return new(MonitorFailure.Authorization);
            if(type=="validation"||exitCode==3)return new(MonitorFailure.Validation);
            if(Guard.Optional(root,"outcome")=="partial_failure"||exitCode==7)return new(MonitorFailure.Incomplete);
            if(type!="api"||exitCode!=1||!Guard.True(error,"retryable"))return new(MonitorFailure.Unknown);
            if(error.TryGetProperty("exit_code",out var wireExit)&&(!wireExit.TryGetInt32(out var code)||code!=exitCode))return new(MonitorFailure.Unknown);
            TimeSpan? retry=null;
            if(error.TryGetProperty("retry_after_seconds",out var seconds))
            {
                if(!seconds.TryGetInt64(out var value)||value is <0 or >86400)return new(MonitorFailure.Unknown);
                retry=TimeSpan.FromSeconds(value);
            }
            // Absolute hints cannot be safely interpreted without the supervisor clock: don't retry early.
            if(error.TryGetProperty("next_retry_at",out _))return new(MonitorFailure.Unknown);
            return new(MonitorFailure.TransientRead,retry);
        }
        catch(Exception){return new(MonitorFailure.Unknown);}
    }
}
public sealed record DwsReadiness(bool Ready,string CredentialRevision)
{
    public static DwsReadiness Parse(string json,int exitCode,string expectedProfile)
    {
        try
        {
            using var document=DwsParser.Parse(json);var root=document.RootElement;
            var identity=Guard.Optional(root,"corp_id")+":"+Guard.Optional(root,"user_id");
            var ready=exitCode==0&&Guard.True(root,"success")&&Guard.True(root,"authenticated")
                &&(Guard.True(root,"token_valid")||Guard.True(root,"refresh_token_valid"))&&(!root.TryGetProperty("reason",out var reason)||reason.ValueKind==JsonValueKind.String&&reason.GetString()=="")
                &&identity==expectedProfile&&identity.Split(':') is [var corp,var user]&&corp.Length>0&&user.Length>0;
            // DWS does not expose a login-generation identifier. OAuth expiry timestamps are not revisions.
            return new(ready,ready?Guard.Hash(identity):"");
        }
        catch(Exception){return new(false,"");}
    }
}
