using System.Security.Cryptography;
using System.Text.Json;
using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

/// <summary>One legacy-compatible state-file lease owns state, send attempts and optional review evidence.</summary>
public sealed class BusinessStore: IStateStore,IDisposable
{
    readonly EncryptedStateStore state;
    bool disposed;
    HashSet<string>? persistedReports;
    bool privacyPrunePending;
    public ISendAttemptStore SendAttempts {get;}
    public ReportReviewArchive Reviews{get;}
    public AnalysisAttemptArchive AnalysisAttempts{get;}
    public bool ReviewsAvailable{get;private set;}=true;
    public bool ReviewMaintenanceBlocked{get;private set;}
    public bool AnalysisMaintenanceBlocked{get;private set;}
    public bool MaintenanceBlocked=>ReviewMaintenanceBlocked||AnalysisMaintenanceBlocked;
    public BusinessStore(string root,IDataCipher cipher,TimeProvider? clock=null,int? startupRetentionDays=null,Action<string>? persistReviewBarrier=null)
    {
        root=Path.GetFullPath(root);
        Reviews=new(Path.Combine(root,"report-reviews.dpapi"),cipher,EnsureOpen,persistReviewBarrier);
        AnalysisAttempts=new(Path.Combine(root,"analysis-attempts.dpapi"),cipher,EnsureOpen);
        state=new EncryptedStateStore(Path.Combine(root,"state.dpapi"),cipher,clock,startupRetentionDays,
            beforePersist:MaintainBeforePersist);
        SendAttempts=new SendStore(this,new EncryptedDocument<SendLedger>(Path.Combine(root,"send-attempts.dpapi"),cipher,4*1024*1024));
    }
    public InsightState Load()
    {
        EnsureOpen();ReviewMaintenanceBlocked=false;AnalysisMaintenanceBlocked=false;InsightState value;
        try{value=state.Load();if(!ReviewMaintenanceBlocked)MaintainBeforePersist(value,trimmed:false);}
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage&&MaintenanceBlocked)
        {value=state.ReadRetained();if(AnalysisMaintenanceBlocked)value.AnalysisAttemptsAvailable=false;}
        persistedReports=value.Reports.Select(ReportReviewSnapshot.Key).ToHashSet();return value;
    }
    void MaintainBeforePersist(InsightState value,bool trimmed)
    {
        try{AnalysisAttempts.Maintain(value);AnalysisMaintenanceBlocked=false;}
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage){AnalysisMaintenanceBlocked=true;throw;}
        var next=value.Reports.Select(ReportReviewSnapshot.Key).ToHashSet();
        var required=trimmed||privacyPrunePending||persistedReports?.Except(next).Any()==true;
        try
        {
            ReviewsAvailable=Reviews.Maintain(value,required);ReviewMaintenanceBlocked=false;privacyPrunePending=false;
        }
        catch(MonitoringFault fault)when(fault.Category==MonitorFailure.Storage)
        {ReviewsAvailable=false;ReviewMaintenanceBlocked=true;privacyPrunePending=required;throw;}
    }
    public void Save(InsightState value)
    {
        EnsureOpen();
        // Persist invalidation (or its fail-closed barrier) before a new source revision can
        // become canonical. A failed state write may lose certainty, never resurrect it.
        state.Save(value);persistedReports=value.Reports.Select(ReportReviewSnapshot.Key).ToHashSet();
    }
    public void ClearIndex(InsightState value)
    {
        EnsureOpen();value.ClearIndex();AnalysisAttempts.Clear();AnalysisMaintenanceBlocked=false;value.AnalysisAttemptsAvailable=true;Reviews.Clear();privacyPrunePending=false;Save(value);ReviewsAvailable=true;
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
