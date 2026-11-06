namespace DingTalkInsights.Workbench;

/// <summary>Serialize ordinary page actions while allowing one observed Stop to retire pending work.</summary>
public sealed class NativeActionGate
{
    readonly object sync=new();
    bool business,stopping;
    public bool IsBusy{get{lock(sync)return business||stopping;}}
    public Task<bool> RunAsync(Func<Task> action,bool stop=false)
    {
        lock(sync)
        {
            if(stopping||!stop&&business)return Task.FromResult(false);
            if(stop)stopping=true;else business=true;
        }
        return Invoke();
        async Task<bool> Invoke()
        {
            try{await action();return true;}
            finally{lock(sync){if(stop)stopping=false;else business=false;}}
        }
    }
}
