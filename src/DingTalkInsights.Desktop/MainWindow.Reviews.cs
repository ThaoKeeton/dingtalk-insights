using DingTalkInsights.Core;

namespace DingTalkInsights.Desktop;

public sealed partial class MainWindow
{
    const string ReviewStorageNotice="结果已保存，但人工核实快照未能保存；请检查本机存储后重试核实。";
    bool ReviewRecoveryBlocked=>store.MaintenanceBlocked;
    bool SetReport(AnalysisReport report)
    {
        review=null;currentReport=report;displayedSavedReport=null;markdown=ReportRenderer.Markdown(report);displayedDemo=report.Batch.IsDemo;
        reportBox.Text=markdown;issuePicker.ItemsSource=report.Issues;if(report.Issues.Count>0)issuePicker.SelectedIndex=0;
        RefreshHistory(clearSelection:true);
        try{ApplyReview(store.Reviews.Capture(state,report));return true;}
        catch(Exception){StatusText.Text=ReviewStorageNotice;return false;}
    }
    void ApplyReview(ReportReviewView value)
    {
        var selected=issuePicker.SelectedIndex;
        review=value;currentReport=value.Report;displayedSavedReport=value.Saved;markdown=value.Markdown;displayedDemo=value.Saved.IsDemo;
        reportBox.Text=markdown;issuePicker.ItemsSource=value.Report?.Issues;
        if(value.Report?.Issues.Count>0)issuePicker.SelectedIndex=Math.Clamp(selected,0,value.Report.Issues.Count-1);
    }
    void RefreshReview()
    {
        if(review is not {} previous)return;
        var next=store.ReviewsAvailable?previous.Refresh(state):ReportReviewView.Legacy(previous.Saved,unavailable:true);
        if(next.Fingerprint!=previous.Fingerprint)ApplyReview(next);
    }
    Task ConfirmResolvedAsync()=>ReviewIssueAsync(true);
    Task ReopenResolvedAsync()=>ReviewIssueAsync(false);
    async Task ReviewIssueAsync(bool resolved)
    {
        if(review is null&&currentReport is not null)ApplyReview(store.Reviews.Capture(state,currentReport));
        var preview=review??throw new SafetyException("旧历史没有可核实的证据快照，请重新采集并整理。");
        var index=issuePicker.SelectedIndex;var report=preview.Report;
        if(report is null||index<0||index>=report.Issues.Count||resolved&&!preview.CanReview)throw new SafetyException("请选择仍在保留期且原文未改变的报告与问题。");
        var issue=report.Issues[index];var token=ActiveToken;
        var text=issue.Title+"\n\n"+string.Join("\n\n",issue.Evidence.Select(e=>$"{e.Message.MessageId} / {e.Message.CreatedAt:O}\n{e.Quote}"));
        text+=resolved?"\n\n仅在你已回原会话确认验证结果与验收范围后继续。提交修复或有人回复本身不足以确认解决。核实仅保存到此报告。":"\n\n撤销此报告该问题的人工确认，恢复原报告状态；本次不调用模型，也不修改其他报告。";
        if(!await Confirm(resolved?"人工确认解决":"撤销人工确认",text,resolved?"我已核实，确认解决":"撤销此项人工确认"))return;
        token.ThrowIfCancellationRequested();EnforceRetention();
        if(review?.Fingerprint!=preview.Fingerprint)throw new SafetyException("当前报告或证据已改变，请重新预览后核实。");
        var saved=store.Reviews.Decide(state,preview,index,resolved,true);
        ApplyReview(saved);StatusText.Text=resolved?"人工核实已加密保存，重开同一报告可恢复；仅适用于这些原文修订。":"人工确认已撤销并保存，恢复原报告的待核实状态。";
    }
}
