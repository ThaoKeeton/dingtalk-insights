using System.Security.Cryptography;
using System.Text.Json;
using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

internal enum ReportCommitPhase { Staged, CanonicalReplaced, ArchiveMerged }
internal enum IndexClearPhase { IntentSaved, CanonicalCleared, JournalCleared, AttemptsCleared, ReviewsCleared }

/// <summary>One legacy-compatible state-file lease owns state, send attempts and optional review evidence.</summary>
public sealed class BusinessStore: IReportCommitStore,IDisposable
{
    readonly EncryptedStateStore state;
    readonly TimeProvider clock;
    readonly string root;
    bool disposed;
    HashSet<string>? persistedReports;
    IReadOnlyList<SavedReport>? persistedReportRows;
    bool privacyPrunePending;
    bool committingReport;
    string? committingId;
    public ISendAttemptStore SendAttempts {get;}
    public ReportReviewArchive Reviews{get;}
    public AnalysisAttemptArchive AnalysisAttempts{get;}
    public ReportCommitJournal ReportCommits{get;}
    public IndexClearJournal IndexClears{get;}
    public bool ReviewsAvailable{get;private set;}=true;
    public bool ReviewMaintenanceBlocked{get;private set;}
    public bool AnalysisMaintenanceBlocked{get;private set;}
    public bool ReportCommitBlocked{get;private set;}
    public bool CommitUnavailable=>ReportCommitBlocked;
    public bool MaintenanceBlocked=>ReviewMaintenanceBlocked||AnalysisMaintenanceBlocked||ReportCommitBlocked;
    // Tests can terminate a synthetic child at the actual production persistence boundaries.
    internal Action<ReportCommitPhase>? CommitTestBoundary{get;set;}
    internal Action<IndexClearPhase>? ClearTestBoundary{get;set;}
    public BusinessStore(string root,IDataCipher cipher,TimeProvider? clock=null,int? startupRetentionDays=null,Action<string>? persistReviewBarrier=null)
    {
        root=Path.GetFullPath(root);
        this.root=root;
        this.clock=clock??TimeProvider.System;
        Reviews=new(Path.Combine(root,"report-reviews.dpapi"),cipher,EnsureOpen,persistReviewBarrier,()=>ReportCommitBlocked);
        AnalysisAttempts=new(Path.Combine(root,"analysis-attempts.dpapi"),cipher,EnsureOpen);
        ReportCommits=new(Path.Combine(root,"report-commit.dpapi"),cipher,EnsureOpen);
        IndexClears=new(Path.Combine(root,"index-clear.dpapi"),cipher,EnsureOpen);
        state=new EncryptedStateStore(Path.Combine(root,"state.dpapi"),cipher,clock,startupRetentionDays,
            beforePersist:MaintainBeforePersist);
        SendAttempts=new SendStore(this,new EncryptedDocument<SendLedger>(Path.Combine(root,"send-attempts.dpapi"),cipher,4*1024*1024));
    }
    public InsightState Load()
    {
        EnsureOpen();ReviewMaintenanceBlocked=false;AnalysisMaintenanceBlocked=false;ReportCommitBlocked=false;InsightState value;IndexClearIntent? clear=null;var clearPending=IndexClears.Exists;
        try
        {
            if(clearPending){clear=IndexClears.Read()??throw new MonitoringFault(MonitorFailure.Storage);CompleteClear(clear);}
            value=state.Load();if(!ReviewMaintenanceBlocked)MaintainBeforePersist(value,trimmed:false);
            Remember(value);ResolvePending(value,recovered:true);
            if(value.Reports.Any(r=>r.CommitId is not null))
            {
                try{Reviews.EnsureCommittedSnapshots(value,null);}
                catch(Exception){ReportCommitBlocked=true;throw new MonitoringFault(MonitorFailure.Storage);}
            }
        }
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage&&(MaintenanceBlocked||clearPending))
        {
            if(clearPending)ReportCommitBlocked=true;
            value=state.ReadRetained();if(clearPending){if(clear is not null)value.Trim(clear.RetentionReference,Math.Min(value.RetentionDays,clear.RetentionDays));value.ClearIndex();}
            if(AnalysisMaintenanceBlocked)value.AnalysisAttemptsAvailable=false;
        }
        Remember(value);return value;
    }
    void MaintainBeforePersist(InsightState value,bool trimmed)
    {
        try{ReportCommits.PruneExpired(value.RetentionCutoff);}
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage){ReportCommitBlocked=true;throw;}
        if(committingReport&&!value.Reports.Any(r=>r.CommitId==committingId))throw new MonitoringFault(MonitorFailure.Retention);
        try{AnalysisAttempts.Maintain(value);AnalysisMaintenanceBlocked=false;}
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage){AnalysisMaintenanceBlocked=true;throw;}
        var next=value.Reports.Select(ReportReviewSnapshot.Key).ToHashSet();
        var required=trimmed||privacyPrunePending||persistedReports?.Except(next).Any()==true;
        try
        {
            ReviewsAvailable=Reviews.Maintain(value,required,committingReport?persistedReportRows:null);ReviewMaintenanceBlocked=false;privacyPrunePending=false;
        }
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage)
        {ReviewsAvailable=false;ReviewMaintenanceBlocked=true;privacyPrunePending=required;throw;}
    }
    public void Save(InsightState value)
    {
        EnsureOpen();
        if(ReportCommitBlocked)throw new MonitoringFault(MonitorFailure.Storage);
        // Persist invalidation (or its fail-closed barrier) before a new source revision can
        // become canonical. A failed state write may lose certainty, never resurrect it.
        state.Save(value);Remember(value);
    }
    void Remember(InsightState value)
    {persistedReportRows=value.Reports.ToArray();persistedReports=value.Reports.Select(ReportReviewSnapshot.Key).ToHashSet();}
    public void PrepareReport(InsightState value)
    {
        EnsureOpen();if(ReportCommitBlocked)throw new MonitoringFault(MonitorFailure.Storage);
        try{ResolvePending(value,recovered:true);Reviews.EnsureCommittedSnapshots(value,null);}
        catch(Exception){ReportCommitBlocked=true;throw new MonitoringFault(MonitorFailure.Storage);}
    }
    void ResolvePending(InsightState value,bool recovered)
    {
        try
        {
            var pending=ReportCommits.Read();if(pending is null)return;
            var key=ReportReviewSnapshot.Key(pending.Saved);
            var matches=value.Reports.Where(r=>ReportReviewSnapshot.Key(r)==key).ToArray();
            if(matches.Length==0){ReportCommits.Clear();return;}
            if(matches.Length!=1||matches[0].CommitId!=pending.Saved.CommitId)throw new MonitoringFault(MonitorFailure.Storage);
            Reviews.MergePending(value,pending,recovered);CommitTestBoundary?.Invoke(ReportCommitPhase.ArchiveMerged);ReportCommits.Clear();
        }
        catch(Exception){ReportCommitBlocked=true;throw new MonitoringFault(MonitorFailure.Storage);}
    }
    public void CommitReport(InsightState value,SavedReport saved,AnalysisReport report)
    {
        EnsureOpen();if(ReportCommitBlocked||persistedReportRows is null)throw new MonitoringFault(MonitorFailure.Storage);
        ReportCommits.Stage(saved,report);CommitTestBoundary?.Invoke(ReportCommitPhase.Staged);committingReport=true;committingId=saved.CommitId;
        try
        {
            try{state.Save(value);CommitTestBoundary?.Invoke(ReportCommitPhase.CanonicalReplaced);Remember(value);}
            catch(Exception)
            {
                var absent=false;
                try
                {
                    var actual=state.ReadUntrimmedForCommit();var key=ReportReviewSnapshot.Key(saved);
                    var sameKey=actual.Reports.Where(r=>ReportReviewSnapshot.Key(r)==key).ToArray();
                    if(sameKey.Length==1&&sameKey[0].CommitId==saved.CommitId)Remember(actual);
                    else if(sameKey.Length==0){ReportCommits.Clear();absent=true;}
                    else{ReportCommitBlocked=true;throw new MonitoringFault(MonitorFailure.Storage);}
                }
                catch(Exception){ReportCommitBlocked=true;throw new MonitoringFault(MonitorFailure.Storage);}
                if(absent)throw;
            }
        }
        finally{committingReport=false;committingId=null;}
        // A stalled replacement can cross the source-retention boundary. Do not publish
        // the result or merge its evidence after it has expired.
        value.Trim(clock.GetUtcNow(),value.RetentionDays);
        if(!value.Reports.Contains(saved))
        {
            try{state.Save(value);Remember(value);}catch(Exception){ReportCommitBlocked=true;}
            return;
        }
        try{ResolvePending(value,recovered:false);}
        catch(MonitoringFault){ReportCommitBlocked=true;} // The canonical report and encrypted log are already durable.
    }
    public void SaveRevocation(InsightState value)
    {
        if(!value.UserStopped||value.Recovery is not null)throw new MonitoringFault(MonitorFailure.Configuration);
        EnsureOpen();if(!MaintenanceBlocked){Save(value);return;}
        // Never overwrite an uncertain report with stale in-memory state while stopping.
        // Sidecar repair may itself be blocked: preserve every byte of it and change only
        // the recovery authority on the latest canonical state without a retention rewrite.
        var now=clock.GetUtcNow();var reference=now>value.RetentionReference?now:value.RetentionReference;
        var canonical=state.RevokeRecovery(reference,value.RetentionDays);Remember(canonical);
    }
    public void ClearIndex(InsightState value)
    {
        EnsureOpen();
        var now=clock.GetUtcNow();var reference=now>value.RetentionReference?now:value.RetentionReference;
        InsightState canonical;
        try
        {
            // Make the retention high-water mark and revocation durable even if the
            // subsequent clear intent is interrupted or later becomes unreadable.
            var before=state.RevokeRecovery(reference,value.RetentionDays);
            var after=JsonSerializer.Deserialize<InsightState>(JsonSerializer.SerializeToUtf8Bytes(before))??throw new MonitoringFault(MonitorFailure.Storage);
            after.Trim(reference,Math.Min(after.RetentionDays,value.RetentionDays));after.ClearIndex();
            var intent=new IndexClearIntent(1,before.RetentionReference,before.RetentionDays,StateDigest(before),StateDigest(after),
                FileDigest("report-commit.dpapi"),FileDigest("analysis-attempts.dpapi"),FileDigest("report-reviews.dpapi"),FileDigest("report-reviews.dpapi.blocked"));
            // A new, explicit confirmation supersedes an earlier unreadable or interrupted intent.
            IndexClears.Stage(intent,replace:true);ClearTestBoundary?.Invoke(IndexClearPhase.IntentSaved);canonical=CompleteClear(intent);
        }
        catch(Exception){ReportCommitBlocked=true;throw;}
        value.ClearIndex();value.Supervisor=canonical.Supervisor;value.RetentionDays=canonical.RetentionDays;value.RetentionReference=canonical.RetentionReference;
        value.BudgetDay=canonical.BudgetDay;value.ReservedUsd=canonical.ReservedUsd;value.Requests=canonical.Requests;value.InputTokens=canonical.InputTokens;value.OutputTokens=canonical.OutputTokens;
        Remember(value);
        value.AnalysisAttemptsAvailable=true;
    }
    InsightState CompleteClear(IndexClearIntent intent)
    {
        try
        {
            var current=state.ReadUntrimmedForCommit();var identity=StateDigest(current);
            if(current.RetentionReference<intent.RetentionReference||current.RetentionDays>intent.RetentionDays||
                identity!=intent.CanonicalBefore&&identity!=intent.CanonicalAfter)throw new MonitoringFault(MonitorFailure.Storage);
            // An older binary can ignore this intent. Never erase intervening work or a
            // new attempt that it wrote to a sidecar before committing canonical state.
            if(!SameOrEmpty(intent.ReportJournal,FileDigest("report-commit.dpapi"),()=>FileDigest("report-commit.dpapi")=="absent")||
                !SameOrEmpty(intent.Attempts,FileDigest("analysis-attempts.dpapi"),AnalysisAttempts.IsEmpty)||
                !SameOrEmpty(intent.Reviews,FileDigest("report-reviews.dpapi"),Reviews.IsEmpty)||
                !SameOrEmpty(intent.ReviewBarrier,FileDigest("report-reviews.dpapi.blocked"),()=>FileDigest("report-reviews.dpapi.blocked")=="absent"))
                throw new MonitoringFault(MonitorFailure.Storage);
            // Commit the explicitly requested clear before deleting the only report snapshot.
            var canonical=identity==intent.CanonicalAfter?current:state.ClearIndex(intent.RetentionReference,intent.RetentionDays);
            if(StateDigest(canonical)!=intent.CanonicalAfter)throw new MonitoringFault(MonitorFailure.Storage);
            ClearTestBoundary?.Invoke(IndexClearPhase.CanonicalCleared);
            ReportCommits.Clear();ClearTestBoundary?.Invoke(IndexClearPhase.JournalCleared);
            AnalysisAttempts.Clear();ClearTestBoundary?.Invoke(IndexClearPhase.AttemptsCleared);
            Reviews.Clear();ClearTestBoundary?.Invoke(IndexClearPhase.ReviewsCleared);IndexClears.Clear();
            ReportCommitBlocked=false;AnalysisMaintenanceBlocked=false;ReviewMaintenanceBlocked=false;privacyPrunePending=false;ReviewsAvailable=true;return canonical;
        }
        catch(Exception){ReportCommitBlocked=true;throw new MonitoringFault(MonitorFailure.Storage);}
    }
    static bool SameOrEmpty(string expected,string actual,Func<bool> empty)=>expected==actual||empty();
    static string StateDigest(InsightState value)
    {
        // Stop may advance the retention high-water mark while a clear waits for
        // repair. Compare all other fields, then validate the monotonic bounds above.
        var plain=JsonSerializer.SerializeToUtf8Bytes(value);byte[]? normalized=null;
        try
        {
            using var doc=JsonDocument.Parse(plain);using var stream=new MemoryStream();
            using(var writer=new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();foreach(var property in doc.RootElement.EnumerateObject())
                    if(property.Name is not ("RetentionReference" or "RetentionDays"))property.WriteTo(writer);
                writer.WriteEndObject();
            }
            normalized=stream.ToArray();return Convert.ToHexString(SHA256.HashData(normalized));
        }
        finally{CryptographicOperations.ZeroMemory(plain);if(normalized is not null)CryptographicOperations.ZeroMemory(normalized);}
    }
    string FileDigest(string name)
    {
        var path=Path.Combine(root,name);if(Directory.Exists(path))return "directory";if(!File.Exists(path))return "absent";
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexString(SHA256.HashData(stream));
    }
    void EnsureOpen()=>ObjectDisposedException.ThrowIf(disposed,this);
    public void Dispose(){if(disposed)return;disposed=true;state.Dispose();}
    sealed class SendStore(BusinessStore owner,EncryptedDocument<SendLedger> document):ISendAttemptStore
    {
        public SendLedger Load(){owner.EnsureOpen();var ledger=document.Read()??new();ledger.Validate();return ledger;}
        public void Save(SendLedger ledger){owner.EnsureOpen();ledger.Validate();document.Save(ledger);}
    }
}

/// <summary>Bounded DPAPI/cipher document. Caller owns its enclosing business/workspace lifetime.</summary>
public sealed class EncryptedDocument<T>(string path,IDataCipher cipher,int maximumBytes=1024*1024)
{
    public T? Read()
    {
        if(!File.Exists(path))return default;
        try
        {
            if(new FileInfo(path).Length>maximumBytes)throw new IOException();
            var plain=cipher.Unprotect(File.ReadAllBytes(path));
            try{return JsonSerializer.Deserialize<T>(plain)??throw new JsonException();}
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
    }
    public void Save(T value)
    {
        var plain=JsonSerializer.SerializeToUtf8Bytes(value);var temporary=path+"."+Guid.NewGuid()+".tmp";
        try
        {
            if(plain.Length>maximumBytes-1024)throw new IOException();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {file.Write(cipher.Protect(plain));file.Flush(true);}
            File.Move(temporary,path,true);
        }
        catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}
        }
    }
}
