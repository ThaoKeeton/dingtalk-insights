using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

internal sealed record PreviewAuthority(Guid Id,string Owner,long Generation,int RetentionDays,DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,CredentialUse Use,string CredentialRevision);
public sealed class ResolutionPreview
{
    internal ResolutionPreview(PreviewAuthority authority,ReportReviewView view,int index,bool resolved)
    {Authority=authority;View=view;Index=index;Resolved=resolved;Evidence=Array.AsReadOnly(view.Report!.Issues[index].Evidence.ToArray());}
    internal PreviewAuthority Authority{get;}
    internal ReportReviewView View{get;}
    public int Index{get;}
    public bool Resolved{get;}
    public string Title=>View.Report!.Issues[Index].Title;
    public string Markdown=>View.Markdown;
    public IReadOnlyList<Evidence> Evidence{get;}
}
public sealed class MonitoringPreview
{
    internal MonitoringPreview(PreviewAuthority authority,MonitoringRequest request,QueryScope next,DateTimeOffset? covered,CoveredRange? abandoned,int messages,int reports,bool retained)
    {Authority=authority;Request=request;NextRead=next;PreviousCoveredThrough=covered;AbandonedGap=abandoned;ExpiringMessages=messages;ExpiringReports=reports;WithinRetention=retained;}
    internal PreviewAuthority Authority{get;}
    public long Generation=>Authority.Generation;
    public DateTimeOffset CreatedAt=>Authority.CreatedAt;
    public DateTimeOffset ExpiresAt=>Authority.ExpiresAt;
    public MonitoringRequest Request{get;}
    public QueryScope NextRead{get;}
    public DateTimeOffset? PreviousCoveredThrough{get;}
    public CoveredRange? AbandonedGap{get;}
    public int ExpiringMessages{get;}
    public int ExpiringReports{get;}
    public bool WithinRetention{get;}
}
public sealed class AnalysisPreview
{
    internal AnalysisPreview(PreviewAuthority authority,MessageBatch batch,string candidateFingerprint,string task,ModelSettings model)
    {Authority=authority;Batch=batch with{Messages=Array.AsReadOnly(batch.Messages.ToArray()),Warnings=Array.AsReadOnly(batch.Warnings.ToArray())};CandidateFingerprint=candidateFingerprint;Task=task;Model=model;}
    internal PreviewAuthority Authority{get;}
    internal string CandidateFingerprint{get;}
    public MessageBatch Batch{get;}
    public string Task{get;}
    public ModelSettings Model{get;}
}
public sealed class SendPreview
{
    internal SendPreview(PreviewAuthority authority,string name,string destinationFingerprint,string markdown,string fingerprint,SendAttempt? attempt)
    {
        Authority=authority;DestinationName=name;DestinationFingerprint=destinationFingerprint;Markdown=markdown;Fingerprint=fingerprint;
        AlreadyAttempted=attempt is not null;Attempts=attempt?.Attempts??0;
        PreviousStatus=attempt is null?null:attempt.Status switch{SendAttemptStatus.Sent=>DeliveryStatus.Sent,SendAttemptStatus.Failed=>DeliveryStatus.Failed,_=>DeliveryStatus.Unknown};
    }
    internal PreviewAuthority Authority{get;}
    internal string Fingerprint{get;}
    public string DestinationName{get;}
    public string DestinationFingerprint{get;}
    public string Markdown{get;}
    public bool AlreadyAttempted{get;}
    public int Attempts{get;}
    public DeliveryStatus? PreviousStatus{get;}
}
