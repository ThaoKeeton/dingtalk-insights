using System.Security.Cryptography;
using System.Text.Json;

namespace DingTalkInsights.Core;

public interface IDataCipher
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}
public interface IStateStore { InsightState Load(); void Save(InsightState state); }

public sealed class EncryptedStateStore : IStateStore,IDisposable
{
    readonly string path;readonly IDataCipher cipher;readonly FileStream processLock;
    public EncryptedStateStore(string path,IDataCipher cipher)
    {
        this.path=Path.GetFullPath(path);this.cipher=cipher;Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        try{processLock=new FileStream(this.path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new SafetyException("本机数据正在被另一个应用实例使用。");}
    }
    public InsightState Load()
    {
        if(!File.Exists(path))return new InsightState();
        try
        {
            if(new FileInfo(path).Length>32*1024*1024)throw new SafetyException("本机加密数据超过 32 MiB，请先备份和调整保留范围。");
            var plain=cipher.Unprotect(File.ReadAllBytes(path));
            try{var state=JsonSerializer.Deserialize<InsightState>(plain)??throw new SafetyException("数据为空。");state.Validate();return state;}
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        catch(Exception e)when(e is not SafetyException){throw new SafetyException("本机数据无法解密或格式无效。已保留原文件，未自动覆盖。");}
    }
    public void Save(InsightState state)
    {
        state.Validate();byte[] plain=JsonSerializer.SerializeToUtf8Bytes(state);string temp=path+"."+Guid.NewGuid()+".tmp";
        try
        {
            if(plain.Length>30*1024*1024)throw new SafetyException("本机数据接近 32 MiB 上限，请调整消息保留时间后继续。");
            var encrypted=cipher.Protect(plain);
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(encrypted);file.Flush(true);}
            File.Move(temp,path,true);
        }
        catch(SafetyException){throw;}
        catch(Exception){throw new SafetyException("加密状态未能安全保存，已停止后续外部操作。");}
        finally{CryptographicOperations.ZeroMemory(plain);if(File.Exists(temp))File.Delete(temp);}
    }
    public void Dispose()=>processLock.Dispose();
}

public sealed record StoredMessage(string Profile,SourceMessage Message);
public sealed record CoveredRange(DateTimeOffset Start,DateTimeOffset End);
public sealed record SavedReport(DateTimeOffset CreatedAt,string ScopeKey,string Task,string Markdown,bool IsDemo=false);

public sealed class InsightState
{
    public int Version {get;set;}=1;
    public Dictionary<string,StoredMessage> Messages {get;set;}=[];
    public Dictionary<string,DateTimeOffset> Watermarks {get;set;}=[];
    public Dictionary<string,List<CoveredRange>> Coverage {get;set;}=[];
    public HashSet<string> Attempted {get;set;}=[];
    public List<SavedReport> Reports {get;set;}=[];
    public string BudgetDay {get;set;}="";
    public decimal ReservedUsd {get;set;}
    public int Requests {get;set;}
    public long InputTokens {get;set;}
    public long OutputTokens {get;set;}
    public void Validate()
    {
        if(Version!=1||Messages is null||Watermarks is null||Coverage is null||Attempted is null||Reports is null||ReservedUsd<0||Requests<0||Messages.Count>50000)
            throw new SafetyException("本机数据版本或容量不受支持。");
    }
    public static string MessageKey(QueryScope scope,SourceMessage m)=>scope.Key+":"+m.MessageId;
    public static string AttemptKey(QueryScope scope,SourceMessage m)=>Guard.Hash(scope.Key+":"+m.MessageId+":"+m.Revision);
    public int Ingest(MessageBatch batch)
    {
        var changes=0;
        foreach(var m in batch.Messages)
        {
            if(m.ConversationId!=batch.Scope.ConversationId||m.CreatedAt<batch.Scope.Start||m.CreatedAt>=batch.Scope.End)throw new SafetyException("不能保存未授权范围的消息。");
            var key=MessageKey(batch.Scope,m);
            if(Messages.TryGetValue(key,out var old)&&old.Message==m)continue;
            if(!Messages.ContainsKey(key)&&Messages.Count>=50000)throw new SafetyException("本机索引达到 50000 条上限，监控暂停；请缩小保留时间。");
            Messages[key]=new StoredMessage(batch.Scope.Profile,m);changes++;
        }
        if(batch.Complete)
        {
            var ranges=Coverage.GetValueOrDefault(batch.Scope.Key,[]).Append(new CoveredRange(batch.Scope.Start,batch.Scope.End)).OrderBy(r=>r.Start).ToList();
            var merged=new List<CoveredRange>();
            foreach(var r in ranges)
                if(merged.Count>0&&r.Start<=merged[^1].End)merged[^1]=new CoveredRange(merged[^1].Start,r.End>merged[^1].End?r.End:merged[^1].End);else merged.Add(r);
            Coverage[batch.Scope.Key]=merged;
            Watermarks[batch.Scope.Key]=merged[0].End;
        }
        return changes;
    }
    public IReadOnlyList<SourceMessage> Unanalyzed(QueryScope scope,int limit)=>Messages.Values.Where(v=>v.Profile==scope.Profile&&v.Message.ConversationId==scope.ConversationId&&v.Message.CreatedAt>=scope.Start&&v.Message.CreatedAt<scope.End)
        .Select(v=>v.Message).Where(m=>!Attempted.Contains(AttemptKey(scope,m))).OrderBy(m=>m.CreatedAt).ThenBy(m=>m.MessageId,StringComparer.Ordinal).Take(limit).ToArray();
    public void MarkAttempted(QueryScope scope,IEnumerable<SourceMessage> messages){foreach(var m in messages)Attempted.Add(AttemptKey(scope,m));}
    public void Reserve(ModelSettings settings,decimal reservation,DateTimeOffset now)
    {
        settings.Validate();if(reservation<=0)throw new SafetyException("无法确定费用预留。");
        var day=now.UtcDateTime.ToString("yyyy-MM-dd");
        if(BudgetDay.Length>0&&string.CompareOrdinal(day,BudgetDay)<0)throw new SafetyException("本机 UTC 日期回退，费用预留未重置；请先核对系统时间。");
        if(BudgetDay!=day){BudgetDay=day;ReservedUsd=0;Requests=0;InputTokens=0;OutputTokens=0;}
        if(Requests>=settings.MaxRequestsPerDay||ReservedUsd+reservation>settings.DailyBudgetUsd)throw new SafetyException("已达本日预算或请求上限；本次未请求模型。可在设置中调整，或等下个 UTC 日。");
        ReservedUsd+=reservation;Requests++;
    }
    public void Trim(DateTimeOffset now,int retentionDays)
    {
        if(retentionDays is <1 or >365)throw new SafetyException("消息保留天数应为 1–365。");
        var cutoff=now.AddDays(-retentionDays);
        foreach(var pair in Messages.Where(p=>p.Value.Message.CreatedAt<cutoff).ToArray())Messages.Remove(pair.Key);
        var valid=Messages.Select(p=>AttemptKey(new QueryScope(p.Value.Profile,p.Value.Message.ConversationId,DateTimeOffset.MinValue,DateTimeOffset.MaxValue),p.Value.Message)).ToHashSet();
        Attempted.IntersectWith(valid);
        Reports=Reports.Where(r=>r.CreatedAt>=cutoff).TakeLast(100).ToList();
    }
}
