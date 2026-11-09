using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed record PendingReportCommit(int Version,SavedReport Saved,ReportReviewSnapshot Snapshot);

/// <summary>One encrypted write-ahead report, protected by the owning BusinessStore lease.</summary>
public sealed class ReportCommitJournal(string path,IDataCipher cipher,Action ensureOpen)
{
    readonly EncryptedDocument<PendingReportCommit> document=new(path,cipher,2*1024*1024);
    public PendingReportCommit? Read()
    {
        ensureOpen();
        try
        {
            if(Directory.Exists(path))throw new MonitoringFault(MonitorFailure.Storage);
            var pending=document.Read();if(pending is null)return null;
            if(pending.Version!=1||pending.Saved is null||pending.Snapshot is null||
                !Guid.TryParseExact(pending.Saved.CommitId,"N",out _)||pending.Snapshot.Decisions.Length!=0||
                pending.Snapshot.Invalidated||pending.Snapshot.InputsInvalidated||pending.Snapshot.Ambiguous||pending.Snapshot.InputRevisions is null)
                throw new MonitoringFault(MonitorFailure.Storage);
            _=pending.Snapshot.Original(pending.Saved);return pending;
        }
        catch(MonitoringFault){throw;}
        catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
    }
    public void Stage(SavedReport saved,AnalysisReport report)
    {
        ensureOpen();if(Read() is not null)throw new MonitoringFault(MonitorFailure.Storage);
        var snapshot=ReportReviewSnapshot.Capture(saved,report);document.Save(new(1,saved,snapshot));
    }
    public void Clear()
    {
        ensureOpen();
        try{if(Directory.Exists(path))throw new IOException();File.Delete(path);}
        catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
    }
    public void PruneExpired(DateTimeOffset cutoff)
    {
        var pending=Read();
        if(pending is not null&&(pending.Saved.CreatedAt<cutoff||pending.Saved.OldestSourceAt is not {} oldest||oldest<cutoff))Clear();
    }
}
