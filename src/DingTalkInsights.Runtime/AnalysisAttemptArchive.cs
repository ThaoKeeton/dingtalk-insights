using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed class AnalysisAttemptLedger
{
    public int Version{get;set;}=1;
    public Dictionary<string,TaskSourceAttempts> Sources{get;set;}=[];
}

/// <summary>Monotonic hash-only attempt ledger; the enclosing BusinessStore owns its canonical lease.</summary>
public sealed class AnalysisAttemptArchive(string path,IDataCipher cipher,Action ensureOpen)
{
    readonly EncryptedDocument<AnalysisAttemptLedger> document=new(path,cipher,32*1024*1024);
    static TaskSourceAttempts Copy(TaskSourceAttempts source)=>new(){CreatedAt=source.CreatedAt,Tasks=source.Tasks.ToHashSet()};
    public void Maintain(InsightState state)
    {
        ensureOpen();
        try
        {
            var saved=document.Read();
            if(saved is null&&state.TaskAttemptSidecarRequired)throw new MonitoringFault(MonitorFailure.Storage);
            var ledger=saved??new();
            if(ledger.Version!=1)throw new MonitoringFault(MonitorFailure.Storage);
            AnalysisAttemptPolicy.Validate(ledger.Sources);AnalysisAttemptPolicy.Validate(state.TaskAttempts);
            var changed=false;
            var needMessageKeys=ledger.Sources.Where(p=>p.Value.CreatedAt>=state.RetentionCutoff&&!state.AttemptSources.ContainsKey(p.Key)).Select(p=>p.Key).ToHashSet();
            if(needMessageKeys.Count>0)
            {
                foreach(var stored in state.Messages.Values)
                {
                    var message=stored.Message;var scopeKey=Guard.Hash(stored.Profile+"\n"+message.ConversationId);
                    var sourceKey=Guard.Hash(scopeKey+":"+message.MessageId+":"+message.Revision);
                    if(needMessageKeys.Contains(sourceKey))state.AttemptSources[sourceKey]=new(scopeKey+":"+message.MessageId,message.CreatedAt);
                }
            }
            foreach(var pair in ledger.Sources.ToArray())
            {
                if(pair.Value.CreatedAt<state.RetentionCutoff){ledger.Sources.Remove(pair.Key);changed=true;continue;}
                if(!state.TaskAttempts.TryGetValue(pair.Key,out var current))state.TaskAttempts[pair.Key]=Copy(pair.Value);
                else
                {
                    if(current.CreatedAt!=pair.Value.CreatedAt)throw new MonitoringFault(MonitorFailure.Storage);
                    current.Tasks.UnionWith(pair.Value.Tasks);
                }
                state.Attempted.Add(pair.Key);
            }
            foreach(var pair in state.TaskAttempts)
            {
                if(!ledger.Sources.TryGetValue(pair.Key,out var savedSource)){ledger.Sources[pair.Key]=Copy(pair.Value);changed=true;}
                else
                {
                    if(savedSource.CreatedAt!=pair.Value.CreatedAt)throw new MonitoringFault(MonitorFailure.Storage);
                    var count=savedSource.Tasks.Count;savedSource.Tasks.UnionWith(pair.Value.Tasks);changed|=count!=savedSource.Tasks.Count;
                }
            }
            AnalysisAttemptPolicy.Validate(ledger.Sources);AnalysisAttemptPolicy.Validate(state.TaskAttempts);
            if(changed)document.Save(ledger); // Must precede the canonical state and every model dispatch.
            state.TaskAttemptSidecarRequired=ledger.Sources.Count>0;state.AnalysisAttemptsAvailable=true;
        }
        catch(MonitoringFault){state.AnalysisAttemptsAvailable=false;throw;}
        catch(Exception){state.AnalysisAttemptsAvailable=false;throw new MonitoringFault(MonitorFailure.Storage);}
    }
    public void Clear()
    {
        ensureOpen();
        try{document.Save(new());}
        catch(MonitoringFault){throw;}
        catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
    }
}
