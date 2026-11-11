using DingTalkInsights.Core;

namespace DingTalkInsights.Desktop;

internal sealed record UserSettings
{
    public string DwsPath{get;init;}="";
    public string Profile{get;init;}="";
    public string ConversationId{get;init;}="";
    public DateTimeOffset? MonitoringStart{get;init;}
    public string WebhookName{get;init;}="";
    public string Theme{get;init;}="system";
    public ModelSettings Model{get;init;}=new();
    public bool CloseToTray{get;init;}
    public bool UploadToModel{get;init;}
    public int PollSeconds{get;init;}=300;
    public int RetentionDays{get;init;}=30;
}
