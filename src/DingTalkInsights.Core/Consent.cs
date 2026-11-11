namespace DingTalkInsights.Core;

public enum ExternalAction { Discover, Read, Search, Analyze, Send }
public sealed class ConsentToken
{
    internal ConsentToken(Guid id){Id=id;}
    internal Guid Id {get;}
}

/// <summary>UI issues a token only after showing and accepting the exact operation.</summary>
public sealed class ConsentGate
{
    private readonly Dictionary<Guid,(ExternalAction Action,string Fingerprint,DateTimeOffset Expires)> tokens=[];
    public ConsentToken Issue(ExternalAction action,string fingerprint)
    {
        lock(tokens){var id=Guid.NewGuid();tokens[id]=(action,fingerprint,DateTimeOffset.UtcNow.AddMinutes(5));return new ConsentToken(id);}
    }
    public void Consume(ConsentToken? token,ExternalAction action,string fingerprint)
    {
        lock(tokens)
        {
            if(token is null||!tokens.Remove(token.Id,out var grant)||grant.Action!=action||grant.Fingerprint!=fingerprint||grant.Expires<DateTimeOffset.UtcNow)
                throw new SafetyException("本次操作未确认或内容已改变，请重新预览并确认。");
        }
    }
}
