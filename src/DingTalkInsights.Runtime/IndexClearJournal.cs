using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed record IndexClearIntent(int Version,DateTimeOffset RetentionReference,int RetentionDays,
    string CanonicalBefore,string CanonicalAfter,string ReportJournal,string Attempts,string Reviews,string ReviewBarrier);

/// <summary>Durable explicit clear intent; the owning BusinessStore holds the canonical lease.</summary>
public sealed class IndexClearJournal(string path,IDataCipher cipher,Action ensureOpen)
{
    readonly EncryptedDocument<IndexClearIntent> document=new(path,cipher,4096);
    public bool Exists=>File.Exists(path)||Directory.Exists(path);
    public IndexClearIntent? Read()
    {
        ensureOpen();if(Directory.Exists(path))throw new MonitoringFault(MonitorFailure.Storage);
        var intent=document.Read();
        if(intent is not null&&(intent.Version!=1||intent.RetentionReference==DateTimeOffset.MinValue||intent.RetentionDays is <1 or >365||
            !AnalysisAttemptPolicy.ValidHash(intent.CanonicalBefore)||!AnalysisAttemptPolicy.ValidHash(intent.CanonicalAfter)||
            !ValidFileDigest(intent.ReportJournal)||!ValidFileDigest(intent.Attempts)||!ValidFileDigest(intent.Reviews)||!ValidFileDigest(intent.ReviewBarrier)))
            throw new MonitoringFault(MonitorFailure.Storage);
        return intent;
    }
    static bool ValidFileDigest(string? value)=>value is "absent" or "directory"||value is not null&&AnalysisAttemptPolicy.ValidHash(value);
    public void Stage(IndexClearIntent intent,bool replace=false)
    {
        ensureOpen();if(Directory.Exists(path)||Exists&&!replace)throw new MonitoringFault(MonitorFailure.Storage);document.Save(intent);
    }
    public void Clear()
    {
        ensureOpen();try{if(Directory.Exists(path))throw new IOException();File.Delete(path);}
        catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
    }
}
