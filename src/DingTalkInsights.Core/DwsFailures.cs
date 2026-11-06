using System.Text.Json;

namespace DingTalkInsights.Core;

/// <summary>Pinned v1.0.67 wire keys only; localized message/hint/body are never classification inputs.</summary>
public static class DwsFailures
{
    internal const int MaxDiagnosticChars=16*1024;
    internal static MonitoringFault ClassifyProcessFailure(string output,string? diagnostic,int exitCode)
    {
        // DualValidate +chat-messages emits the legacy error on stderr. Business/partial
        // stdout or conflicting channels cannot be promoted to a retryable empty read.
        if(diagnostic is null||!string.IsNullOrWhiteSpace(output)&&!string.IsNullOrWhiteSpace(diagnostic))return new(MonitorFailure.Unknown);
        return Classify(string.IsNullOrWhiteSpace(output)?diagnostic:output,exitCode);
    }
    public static MonitoringFault Classify(string json,int exitCode)
    {
        try
        {
            if(json.Length>MaxDiagnosticChars)return new(MonitorFailure.Unknown);
            using var document=DwsParser.Parse(json);var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!Unique(root)||!root.TryGetProperty("error",out var error)
                ||error.ValueKind!=JsonValueKind.Object||!Unique(error))return new(MonitorFailure.Unknown);
            var type=Guard.Optional(error,"type");var category=Guard.Optional(error,"category");
            var http=error.TryGetProperty("http_status",out var h)&&h.TryGetInt32(out var status)?status:0;
            if(type is "auth" or "permission"||category is "auth" or "permission"||exitCode is 2 or 4||http is 401 or 403)return new(MonitorFailure.Authorization);
            if(type=="validation"||category=="validation"||exitCode==3)return new(MonitorFailure.Validation);
            if(Guard.Optional(root,"outcome")=="partial_failure"||category=="partial_failure"||exitCode==7)return new(MonitorFailure.Incomplete);
            if(exitCode!=1||!Guard.True(error,"retryable"))return new(MonitorFailure.Unknown);
            if(error.TryGetProperty("category",out var legacy))
            {
                if(legacy.ValueKind!=JsonValueKind.String||category!="api"
                    ||!error.TryGetProperty("code",out var legacyCode)||!legacyCode.TryGetInt32(out var legacyExit)||legacyExit!=exitCode
                    ||error.TryGetProperty("type",out var legacyType)&&(legacyType.ValueKind!=JsonValueKind.String||type!=category))return new(MonitorFailure.Unknown);
            }
            else if(type!="api")return new(MonitorFailure.Unknown);
            if(root.TryGetProperty("outcome",out var outcome)&&(outcome.ValueKind!=JsonValueKind.String||outcome.GetString()!="failure")
                ||root.TryGetProperty("ok",out var ok)&&ok.ValueKind!=JsonValueKind.False
                ||root.TryGetProperty("data",out var data)&&data.ValueKind!=JsonValueKind.Null)return new(MonitorFailure.Unknown);
            if(error.TryGetProperty("exit_code",out var wireExit)&&(!wireExit.TryGetInt32(out var code)||code!=exitCode))return new(MonitorFailure.Unknown);
            if(error.TryGetProperty("http_status",out h)&&(h.ValueKind!=JsonValueKind.Number||!h.TryGetInt32(out status)||status is <100 or >599))return new(MonitorFailure.Unknown);
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
    static bool Unique(JsonElement value)
    {
        var names=new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property=>names.Add(property.Name));
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
