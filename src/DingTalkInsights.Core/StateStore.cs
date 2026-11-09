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
    readonly TimeProvider clock;readonly int? startupRetentionDays;
    readonly Action<InsightState,bool>? beforePersist;
    public EncryptedStateStore(string path,IDataCipher cipher,TimeProvider? clock=null,int? startupRetentionDays=null,Action<InsightState,bool>? beforePersist=null)
    {
        this.clock=clock??TimeProvider.System;this.startupRetentionDays=startupRetentionDays;this.beforePersist=beforePersist;
        this.path=Path.GetFullPath(path);this.cipher=cipher;Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        try{processLock=new FileStream(this.path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new SafetyException("本机数据正在被另一个应用实例使用。");}
    }
    public InsightState Load()=>Read(persist:true);
    /// <summary>Retained in-memory view for optional-archive recovery. Never rewrites the canonical file.</summary>
    public InsightState ReadRetained()=>Read(persist:false);
    InsightState Read(bool persist)
    {
        if(!File.Exists(path))
        {
            var empty=new InsightState();empty.Trim(clock.GetUtcNow(),startupRetentionDays??empty.RetentionDays);return empty;
        }
        try
        {
            if(new FileInfo(path).Length>32*1024*1024)throw new SafetyException("本机加密数据超过 32 MiB，请先备份和调整保留范围。");
            var plain=cipher.Unprotect(File.ReadAllBytes(path));
            try
            {
                var state=JsonSerializer.Deserialize<InsightState>(plain)??throw new SafetyException("数据为空。");state.Validate();
                if(persist){state.RetentionDays=startupRetentionDays??state.RetentionDays;Save(state);}
                else state.Trim(clock.GetUtcNow(),startupRetentionDays??state.RetentionDays);
                return state;
            }
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        catch(Exception e)when(e is not SafetyException){throw new SafetyException("本机数据无法解密或格式无效。已保留原文件，未自动覆盖。");}
    }
    public void Save(InsightState state)
    {
        var messages=state.Messages.Count;var reports=state.Reports.Count;
        state.Trim(clock.GetUtcNow(),state.RetentionDays);state.Validate();beforePersist?.Invoke(state,state.Messages.Count<messages||state.Reports.Count<reports);
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(state);string temp=path+"."+Guid.NewGuid()+".tmp";
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
public sealed record SavedReport(DateTimeOffset CreatedAt,string ScopeKey,string Task,string Markdown,bool IsDemo=false,DateTimeOffset? OldestSourceAt=null);
public sealed record AttemptSource(string MessageKey,DateTimeOffset CreatedAt);
public sealed record MonitoringOrigin(string Profile,string ConversationId,DateTimeOffset Start)
{
    public QueryScope Scope(DateTimeOffset end)=>new(Profile,ConversationId,Start,end);
}
public sealed record CollectionReceipt(QueryScope Scope,DateTimeOffset CollectedAt,bool Complete,
    IReadOnlyList<string> Warnings,IReadOnlyDictionary<string,JsonElement> Ledger,bool MetadataOmitted=false);

public sealed class InsightState
{
    public int Version {get;set;}=1;
    public RecoveryPolicy? Recovery {get;set;}
    public bool UserStopped {get;set;}
    public SupervisorHealth Supervisor {get;set;}=new();
    public Dictionary<string,StoredMessage> Messages {get;set;}=[];
    public Dictionary<string,DateTimeOffset> Watermarks {get;set;}=[];
    public Dictionary<string,List<CoveredRange>> Coverage {get;set;}=[];
    public HashSet<string> Attempted {get;set;}=[];
    public Dictionary<string,TaskSourceAttempts> TaskAttempts {get;set;}=[];
    public bool TaskAttemptSidecarRequired {get;set;}
    [System.Text.Json.Serialization.JsonIgnore] public bool AnalysisAttemptsAvailable {get;set;}=true;
    public List<SavedReport> Reports {get;set;}=[];
    [System.Text.Json.Serialization.JsonIgnore] public HashSet<string> AmbiguousReports {get;set;}=[];
    public Dictionary<string,AttemptSource> AttemptSources {get;set;}=[];
    public DateTimeOffset? LegacyAttemptsExpireAt {get;set;}
    public MonitoringOrigin? Monitoring {get;set;}
    public List<CollectionReceipt> Receipts {get;set;}=[];
    public int RetentionDays {get;set;}=30;
    public DateTimeOffset RetentionReference {get;set;}=DateTimeOffset.MinValue;
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset RetentionCutoff=>RetentionReference==DateTimeOffset.MinValue?DateTimeOffset.MinValue:RetentionReference.AddDays(-RetentionDays);
    public string BudgetDay {get;set;}="";
    public decimal ReservedUsd {get;set;}
    public int Requests {get;set;}
    public long InputTokens {get;set;}
    public long OutputTokens {get;set;}
    public void Validate()
    {
        if(Supervisor is null)throw new MonitoringFault(MonitorFailure.Configuration);
        Supervisor.Validate();
        if(Version!=1||Messages is null||Watermarks is null||Coverage is null||Attempted is null||Reports is null||AttemptSources is null||Receipts is null||RetentionDays is <1 or >365||ReservedUsd<0||Requests<0||Messages.Count>50000)
            throw new SafetyException("本机数据版本或容量不受支持。");
        AnalysisAttemptPolicy.Validate(TaskAttempts);
    }
    public static string MessageKey(QueryScope scope,SourceMessage m)=>scope.Key+":"+m.MessageId;
    public static string AttemptKey(QueryScope scope,SourceMessage m)=>Guard.Hash(scope.Key+":"+m.MessageId+":"+m.Revision);
    public int Ingest(MessageBatch batch)
    {
        EnsureRetained(batch);
        var changes=0;
        foreach(var m in batch.Messages)
        {
            if(m.ConversationId!=batch.Scope.ConversationId||m.CreatedAt<batch.Scope.Start||m.CreatedAt>=batch.Scope.End)throw new SafetyException("不能保存未授权范围的消息。");
            var key=MessageKey(batch.Scope,m);
            if(Messages.TryGetValue(key,out var old)&&old.Message==m)continue;
            if(!Messages.ContainsKey(key)&&Messages.Count>=50000)throw new SafetyException("本机索引达到 50000 条上限，监控暂停；请缩小保留时间。");
            Messages[key]=new StoredMessage(batch.Scope.Profile,m);changes++;
        }
        var ledger=batch.Ledger;
        var omitted=JsonSerializer.SerializeToUtf8Bytes(ledger).Length>32768;
        Receipts.Add(new CollectionReceipt(batch.Scope,RetentionReference==DateTimeOffset.MinValue?DateTimeOffset.UtcNow:RetentionReference,
            batch.Complete,batch.Warnings.Take(100).Select(w=>w.Length>512?w[..512]:w).ToArray(),omitted?new Dictionary<string,JsonElement>():ledger.ToDictionary(p=>p.Key,p=>p.Value.Clone()),omitted));
        Receipts=Receipts.TakeLast(100).ToList();
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
    public void MarkAttempted(QueryScope scope,IEnumerable<SourceMessage> messages)
    {
        foreach(var m in messages)
        {
            var key=AttemptKey(scope,m);Attempted.Add(key);
            AttemptSources[key]=new AttemptSource(MessageKey(scope,m),m.CreatedAt);
        }
    }
    public void EnsureScopeRetained(QueryScope scope)
    {
        scope.Validate();
        if(scope.Start<RetentionCutoff)throw new MonitoringFault(MonitorFailure.Retention);
    }
    public void EnsureRetained(MessageBatch batch)
    {
        if(batch.Messages.Any(m=>m.CreatedAt<RetentionCutoff))throw new SafetyException("候选正文已超过保留期，请重新选择范围并读取；本次未使用过期正文。");
    }
    public void ClearIndex()
    {
        BackgroundPolicy.Revoke(this);
        Messages.Clear();Coverage.Clear();Watermarks.Clear();Attempted.Clear();AttemptSources.Clear();TaskAttempts.Clear();TaskAttemptSidecarRequired=false;Reports.Clear();Receipts.Clear();Monitoring=null;LegacyAttemptsExpireAt=null;
        // Reservations and request/token counters are deliberately retained.
    }
    public void Reserve(ModelSettings settings,decimal reservation,DateTimeOffset now)
    {
        settings.Validate();if(reservation<=0)throw new SafetyException("无法确定费用预留。");
        var day=now.UtcDateTime.ToString("yyyy-MM-dd");
        if(BudgetDay.Length>0&&string.CompareOrdinal(day,BudgetDay)<0)throw new SafetyException("本机 UTC 日期回退，费用预留未重置；请先核对系统时间。");
        if(BudgetDay!=day){BudgetDay=day;ReservedUsd=0;Requests=0;InputTokens=0;OutputTokens=0;}
        if(Requests>=settings.MaxRequestsPerDay||ReservedUsd+reservation>settings.DailyBudgetUsd)throw new MonitoringFault(MonitorFailure.Budget,deferredUntil:new DateTimeOffset(now.UtcDateTime.Date.AddDays(1),TimeSpan.Zero));
        ReservedUsd+=reservation;Requests++;
    }
    public void Trim(DateTimeOffset now,int retentionDays)
    {
        if(retentionDays is <1 or >365)throw new SafetyException("消息保留天数应为 1–365。");
        RetentionDays=retentionDays;
        if(now>RetentionReference)RetentionReference=now;
        var cutoff=RetentionCutoff;
        foreach(var pair in Messages.Where(p=>p.Value.Message.CreatedAt<cutoff).ToArray())Messages.Remove(pair.Key);
        // Keep every attempted revision until its source ages out, including A -> B -> A.
        foreach(var pair in AttemptSources.Where(p=>p.Value.CreatedAt<cutoff).ToArray())
        {Attempted.Remove(pair.Key);AttemptSources.Remove(pair.Key);}
        foreach(var pair in TaskAttempts.Where(p=>p.Value.CreatedAt<cutoff).ToArray())TaskAttempts.Remove(pair.Key);
        // Version-1 hashes cannot be reversed to recover source age. Keep unknown legacy hashes conservatively
        // for the maximum supported retention interval after migration, even if the user later expands it.
        if(Attempted.Any(k=>!AttemptSources.ContainsKey(k)))
        {
            LegacyAttemptsExpireAt??=RetentionReference.AddDays(365);
            if(RetentionReference>=LegacyAttemptsExpireAt)Attempted.RemoveWhere(k=>!AttemptSources.ContainsKey(k));
        }
        Reports=Reports.Where(r=>r.CreatedAt>=cutoff&&r.OldestSourceAt is {} oldest&&oldest>=cutoff).TakeLast(100).ToList();
        Receipts=Receipts.Where(r=>r.Scope.End>=cutoff).TakeLast(100).ToList();
    }
}
