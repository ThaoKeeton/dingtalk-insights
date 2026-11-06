using System.Security.Cryptography;
using System.Text.Json;
using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

/// <summary>One legacy-compatible state-file lease owns both state and the hash-only send sidecar.</summary>
public sealed class BusinessStore: IStateStore,IDisposable
{
    readonly EncryptedStateStore state;
    bool disposed;
    public ISendAttemptStore SendAttempts {get;}
    public BusinessStore(string root,IDataCipher cipher,TimeProvider? clock=null,int? startupRetentionDays=null)
    {
        root=Path.GetFullPath(root);
        state=new EncryptedStateStore(Path.Combine(root,"state.dpapi"),cipher,clock,startupRetentionDays);
        SendAttempts=new SendStore(this,new EncryptedDocument<SendLedger>(Path.Combine(root,"send-attempts.dpapi"),cipher,4*1024*1024));
    }
    public InsightState Load(){EnsureOpen();return state.Load();}
    public void Save(InsightState value){EnsureOpen();state.Save(value);}
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
