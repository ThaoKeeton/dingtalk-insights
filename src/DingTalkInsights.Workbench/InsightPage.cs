using System.Globalization;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DingTalkInsights.Workbench;

/// <summary>Only code-created native controls; a page owns its subscription, the plugin owns the work.</summary>
internal sealed class InsightPage:UserControl
{
    readonly PluginSession session;
    readonly InsightController controller;
    readonly StackPanel body=new(){Spacing=12,Padding=new Thickness(20),MaxWidth=1080,HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly TextBlock notice=Named(new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true},"DTI 操作结果");
    readonly TextBlock health=Named(new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true},"DTI 监控与预算");
    readonly TextBox task=Edit("DTI 整理事项",true);
    readonly TextBox profile=Edit("DTI 身份 corpId:userId"),conversation=Edit("DTI 会话 ID"),start=Edit("DTI 开始时间 ISO8601"),end=Edit("DTI 结束时间 ISO8601"),executable=Edit("DTI DWS 可执行文件"),destination=Edit("DTI Webhook 目标名称");
    readonly TextBox poll=Edit("DTI 轮询秒数"),retention=Edit("DTI 保留天数"),model=Edit("DTI 模型"),inputRate=Edit("DTI 输入单价 每百万 token"),outputRate=Edit("DTI 输出单价 每百万 token"),budget=Edit("DTI UTC 每日美元预算"),requests=Edit("DTI UTC 每日请求上限"),output=Edit("DTI 输出 token 上限");
    readonly CheckBox demo=Named(new CheckBox{Content="使用模拟来源（每次启用默认）",IsChecked=true},"DTI 使用模拟来源");
    readonly CheckBox upload=Named(new CheckBox{Content="本次监控允许模型整理（需单独批准凭据）",IsChecked=false},"DTI 监控模型整理");
    readonly CheckBox dwsSearch=Named(new CheckBox{Content="搜索 DWS；未选择时只搜索本机索引"},"DTI 搜索 DWS");
    readonly TextBox keywords=Edit("DTI 搜索词");
    readonly TextBox candidates=ReadOnly("DTI 来源候选"),report=ReadOnly("DTI 待核实结果");
    readonly ComboBox conversations=Named(new ComboBox{Header="本次发现的会话（选择后仍需保存配置）",HorizontalAlignment=HorizontalAlignment.Stretch},"DTI 发现会话");
    readonly ComboBox history=Named(new ComboBox{Header="本机加密历史",HorizontalAlignment=HorizontalAlignment.Stretch},"DTI 历史结果");
    readonly NumberBox issue=Named(new NumberBox{Header="复核的问题序号（从 1 开始）",Minimum=1,Value=1,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline},"DTI 复核问题序号");
    readonly PasswordBox key=Password("DTI 新 OpenAI key"),webhook=Password("DTI 新 Webhook URL"),secret=Password("DTI 新 Webhook 签名密钥");
    readonly ComboBox credentialLocation=Named(new ComboBox{Header="凭据来源",SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch},"DTI 凭据来源");
    readonly CheckBox useModel=Named(new CheckBox{Content="仅模型用途"},"DTI 凭据用于模型"),useWebhook=Named(new CheckBox{Content="仅人工 Webhook 用途"},"DTI 凭据用于 Webhook");
    Button? begin;
    SnapshotBinding? binding;
    bool loaded,rendering,busy;
    long attachment;

    public InsightPage(PluginSession session,bool settings)
    {
        this.session=session;controller=session.Controller;
        Named(this,settings?"DTI 原生设置":"DTI 原生工作区");
        HorizontalContentAlignment=HorizontalAlignment.Stretch;Content=new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=body};
        body.Children.Add(Text(settings?"钉钉沟通事项 · 设置":"钉钉沟通事项",26));
        body.Children.Add(Text("默认模拟。安装、启用和导航不会读取聊天、使用凭据、调用模型或发送。所有候选结论须回原会话核实，不作人员评价。"));
        body.Children.Add(notice);body.Children.Add(health);
        Add(task);task.Text=controller.Snapshot.Draft;
        task.TextChanged+=(_,_)=>{if(!rendering&&loaded&&controller.Snapshot.Active)controller.SetDraft(task.Text);};
        if(settings)BuildSettings();else BuildWorkspace();
        Populate();
        Loaded+=(_,_)=>
        {
            if(loaded)return;loaded=true;attachment++;
            binding=new(controller,action=>DispatcherQueue.TryEnqueue(()=>action()),Render);
        };
        Unloaded+=(_,_)=>{loaded=false;attachment++;binding?.Dispose();binding=null;key.Password="";webhook.Password="";secret.Password="";};
    }
    void BuildWorkspace()
    {
        Add(demo,upload);
        Add(Action("打开模拟本机库","DTI 打开模拟",async()=>{await Work(()=>controller.OpenDemoAsync());}));
        body.Children.Add(Text("身份、会话、时间、DWS 路径与限额在插件设置中明确填写并保存。真实来源须先连接标准独立版账本，再按每次预览授权。"));
        Add(Action("读取一次…","DTI 读取一次",Read),Action("预览并开始监控…","DTI 开始监控",Begin,out begin),Action("停止并等待退役","DTI 停止监控",async()=>{await Work(controller.StopMonitoringAsync);}));
        Add(keywords,dwsSearch,Action("搜索…","DTI 搜索",Search));
        Add(candidates,Action("预览并整理候选…","DTI 整理候选",Analyze),report);
        Add(issue,Action("复核证据并确认解决…","DTI 人工确认解决",Resolve),Action("复制 Markdown","DTI 复制 Markdown",Copy));
        Add(history);history.SelectionChanged+=async(_,_)=>await Safe(async()=>
        {if(!rendering&&history.SelectedItem is HistoryRow selected)await Work(()=>controller.SelectHistoryAsync(selected.Value));});
        Add(Action("预览人工发送…","DTI 人工发送",Send));
    }
    void BuildSettings()
    {
        body.Children.Add(Text("配置载入不会恢复采集、模型或发送权限。身份、会话、时间与任务草稿只存入本机加密文件；宿主 JSON 只存非敏感限额与模型偏好。"));
        Add(Action("载入已保存配置","DTI 载入设置",async()=>{await session.LoadAsync();Populate();notice.Text="已载入配置；尚未连接或开始。";}),Action("保存本机配置","DTI 保存设置",async()=>{CaptureSettings();await session.SaveAsync();notice.Text="配置已保存；未授予业务或凭据权限。";}));
        Add(profile,conversation,start,end,executable,poll,retention,model,inputRate,outputRate,output,budget,requests,destination);
        Add(Action("预览并发现会话…","DTI 发现会话操作",Discover),conversations);
        conversations.SelectionChanged+=(_,_)=>{if(!rendering&&conversations.SelectedItem is ConversationRow row)conversation.Text=row.Value.ConversationId;};
        Add(Action("连接标准独立版账本…","DTI 连接标准账本",Connect),Action("断开并等待释放","DTI 断开",async()=>{await Work(controller.DisconnectAsync);}));
        body.Children.Add(Text("连接同一物理 state.dpapi 与旧版锁；不会复制活动账本、强制解锁或导入恢复授权。断开/停用并等候退役后，可回独立版明确开始。旧 0.2 的人工发送无法识别发送侧账，回滚期间请勿发送。"));
        credentialLocation.Items.Add("插件本机 DPAPI 凭据");credentialLocation.Items.Add("已连接标准独立版 DPAPI 凭据");credentialLocation.SelectedIndex=0;
        Add(credentialLocation,useModel,useWebhook,Action("仅批准所选凭据用途…","DTI 批准凭据用途",UseCredentials));
        body.Children.Add(Text("输入框始终从空值开始，既有值永不回显。保存新值与批准用途是两次独立操作；保存会替换插件本机三个值并撤销旧批准，不改独立版保险箱。"));
        Add(key,webhook,secret,Action("加密保存新输入凭据…","DTI 保存新凭据",SaveCredentials));
        Add(Action("清理本机内容索引…","DTI 清理索引",async()=>
        {if(await Confirm("清理索引","仅清理当前连接的本机消息、结果与断点；当日预算、请求和发送尝试保留。不会修改钉钉原始聊天。"))await Work(()=>controller.ClearIndexAsync(true));}));
        body.Children.Add(Text("帮助与限制",20));
        body.Children.Add(Text("监控为官方 DWS 会话消息轮询，不是桌面捕获。只读模式不调用模型；未知/不完整来源不会推进完整断点。模型失败、预算延后、权限和迁移失败均显示安全类别。关闭/睡眠/注销期间不保证实时采集。API1 没有停止原因、后台恢复、密钥服务或卸载事务：停用/退出均彻底退役，重新启用须重新确认。程序集保持加载至进程退出；请退出宿主后才移除包。人工 Webhook 结果不确定时不自动重发，重发可能重复。费用单价是用户输入的本地估算，不是实时报价。"));
    }
    void Populate()
    {
        rendering=true;
        try
        {
            var d=session.Draft;var p=session.Preferences;task.Text=controller.Snapshot.Draft;profile.Text=d.Profile;conversation.Text=d.ConversationId;
            start.Text=d.Start.ToString("O");end.Text=d.End.ToString("O");executable.Text=d.Executable;destination.Text=d.DestinationName;
            poll.Text=p.PollSeconds.ToString(CultureInfo.InvariantCulture);retention.Text=p.RetentionDays.ToString(CultureInfo.InvariantCulture);model.Text=p.Model.Model;
            inputRate.Text=F(p.Model.InputUsdPerMillion);outputRate.Text=F(p.Model.OutputUsdPerMillion);budget.Text=F(p.Model.DailyBudgetUsd);
            requests.Text=p.Model.MaxRequestsPerDay.ToString(CultureInfo.InvariantCulture);output.Text=p.Model.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        }
        finally{rendering=false;}
    }
    void CaptureSettings()
    {
        if(!DateTimeOffset.TryParse(start.Text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||
            !DateTimeOffset.TryParse(end.Text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var to)||
            !HasOffset(start.Text)||!HasOffset(end.Text)||to<=from)throw new RuntimeFault(RuntimeFailure.Configuration);
        var prefs=new Preferences{PollSeconds=I(poll.Text),RetentionDays=I(retention.Text),Model=new(){Model=model.Text.Trim(),InputUsdPerMillion=D(inputRate.Text),OutputUsdPerMillion=D(outputRate.Text),DailyBudgetUsd=D(budget.Text),MaxRequestsPerDay=I(requests.Text),MaxOutputTokens=I(output.Text)}};
        prefs.Validate();session.Preferences=prefs;
        session.Draft=new(){Profile=profile.Text.Trim(),ConversationId=conversation.Text.Trim(),Start=from,End=to,Executable=executable.Text.Trim(),DestinationName=destination.Text.Trim(),Task=task.Text};
        controller.SetDraft(task.Text);
    }
    void Render(RuntimeSnapshot snapshot)
    {
        if(!loaded||!snapshot.Active)return;
        rendering=true;
        try
        {
            if(task.Text!=snapshot.Draft)task.Text=snapshot.Draft;
            notice.Text=snapshot.Notice;
            health.Text=$"来源：{snapshot.Connection} · {(snapshot.IsMonitoring?"监控运行中":"已停止")} · {snapshot.Health.Status}\n{MonitorNotices.For(snapshot.Health.Failure)}\n下次检查：{snapshot.Health.NextReadAt:O} · 模型延后：{snapshot.Health.ModelDeferredUntil:O}\nUTC 日：{snapshot.BudgetDay} · 预留 ${F(snapshot.ReservedUsd)} · 请求 {snapshot.Requests} · 保留 {snapshot.RetentionDays} 天";
            candidates.Text=string.Join("\n\n",snapshot.Candidates.Select(m=>$"[{m.MessageId}] {m.CreatedAt:O} · {m.Sender}\n{m.Text}"));
            report.Text=snapshot.Markdown;
            conversations.ItemsSource=snapshot.Conversations.Select(c=>new ConversationRow(c)).ToArray();
            var selected=(history.SelectedItem as HistoryRow)?.Value;
            history.ItemsSource=snapshot.History.Reverse().Select(h=>new HistoryRow(h)).ToArray();
            if(selected is not null)history.SelectedItem=((IEnumerable<HistoryRow>)history.ItemsSource).FirstOrDefault(h=>h.Value==selected);
            if(begin is not null)begin.IsEnabled=!snapshot.IsMonitoring&&!snapshot.IsBusy;
        }
        finally{rendering=false;}
    }
    async Task Read()
    {
        var request=session.CaptureRequest(demo.IsChecked==true,false);
        var preview=controller.PreviewMonitoring(request);
        if(await Confirm("读取本次范围",$"{request.Source}\n本次确切有界窗口：{preview.NextRead.Display}\nDWS：{request.Executable}\n原文只写入本机加密账本，本次不调用模型。"))await Work(()=>controller.ReadOnceAsync(preview,true));
    }
    async Task Begin()
    {
        var request=session.CaptureRequest(demo.IsChecked==true,upload.IsChecked==true);
        var preview=controller.PreviewMonitoring(request);
        if(await Confirm("本次监控预览",$"来源 {preview.Request.Source} · {preview.Request.Profile} / {preview.Request.ConversationId}\n下一窗口：{preview.NextRead.Display}\n既有连续进度：{preview.PreviousCoveredThrough:O}\n放弃缺口：{preview.AbandonedGap}\n保留期内：{preview.WithinRetention} · 将过期消息 {preview.ExpiringMessages} / 结果 {preview.ExpiringReports}\nDWS：{request.Executable}\n每 {request.PollSeconds} 秒；保留 {request.RetentionDays} 天。模型：{(request.UploadToModel?request.Model.Model:"只读，不调用")}\n输入 ${F(request.Model.InputUsdPerMillion)} / 输出 ${F(request.Model.OutputUsdPerMillion)} 每百万 token\n预算 ${F(request.Model.DailyBudgetUsd)} / UTC 日，{request.Model.MaxRequestsPerDay} 请求；输出上限 {request.Model.MaxOutputTokens}。\n不自动 Webhook，停用和退出后须重新批准。"))await Work(()=>controller.StartMonitoringAsync(preview,true));
    }
    async Task Search()
    {
        var d=session.Draft;var local=demo.IsChecked==true;var request=session.CaptureRequest(local,false);
        var scope=new QueryScope(request.Profile,request.ConversationId,local?request.Start:d.Start,local?DateTimeOffset.UtcNow:d.End);
        var text=keywords.Text;var useDws=dwsSearch.IsChecked==true;var path=request.Executable;
        if(await Confirm("搜索本次范围",$"{scope.Display}\n搜索词：{text}\n来源：{(useDws?"DWS，仅本次查询":"本机加密索引")}\nDWS：{path}\n本次不调用模型。"))await Work(()=>controller.SearchAsync(path,scope,text,useDws,true));
    }
    async Task Discover()
    {
        CaptureSettings();var d=session.Draft;
        var path=d.Executable;var identity=d.Profile;var from=d.Start;var to=d.End;
        if(await Confirm("发现本次范围的会话",$"身份：{identity}\n{from:O} 至 {to:O}（不含结束）\nDWS：{path}\n仅发现会话，不采集原文或调用模型。"))await Work(()=>controller.DiscoverAsync(path,identity,from,to,true));
    }
    async Task Analyze()
    {
        var preview=await Work(()=>controller.PreviewAnalysisAsync(session.Preferences.Model));if(preview is null)return;
        var exact=OpenAiClient.BuildRequest(preview.Batch,preview.Task,preview.Model);
        if(await Confirm(preview.Batch.IsDemo?"模拟整理预览（不联网）":"上传模型预览",
            $"精确范围：{preview.Batch.Scope.Display}\n模型 {preview.Model.Model}；预估上限预留 ${F(OpenAiClient.Reservation(exact,preview.Model))}；UTC 日预算 ${F(preview.Model.DailyBudgetUsd)} / {preview.Model.MaxRequestsPerDay} 请求\n以下为本次固定请求正文，确认后使用同一预览：\n{exact}"))await Work(()=>controller.AnalyzeCandidatesAsync(preview,true));
    }
    async Task Resolve()
    {
        var index=(int)issue.Value-1;var approved=controller.Snapshot.Markdown;
        if(await Confirm("复核原文证据",$"问题序号：{index+1}\n确认你已回原会话核对证据及解决结果；修复提交或有人回复不等于解决。\n{approved}"))
        {
            if(controller.Snapshot.Markdown!=approved)throw new RuntimeFault(RuntimeFailure.Configuration);
            await Work(()=>controller.ConfirmResolvedAsync(index,true));
        }
    }
    Task Copy()
    {
        var content=controller.Snapshot.Markdown;if(content.Length==0)throw new RuntimeFault(RuntimeFailure.Retention);
        var data=new DataPackage();data.SetText(content);Clipboard.SetContent(data);notice.Text="已复制当前有效结果；请在分享前复核证据与范围。";return Task.CompletedTask;
    }
    async Task Send()
    {
        var name=session.Draft.DestinationName;var preview=await Work(()=>controller.PreviewSendAsync(name));if(preview is null)return;
        if(!await Confirm("人工发送精确预览",$"目标名称 {preview.DestinationName}\n目标指纹（遮罩） …{preview.DestinationFingerprint}\n已有尝试 {preview.Attempts} / {preview.PreviousStatus}\n精确正文：\n{preview.Markdown}"))return;
        var retry=false;
        if(preview.AlreadyAttempted)
        {
            retry=await Confirm("重复送达风险：独立人工重试确认","同一目标与正文已有发送尝试。请先到接收群核对；即使先前显示失败或未知，本次人工重试仍可能重复送达。明确确认后才尝试一次。","承担重复风险并重试一次");
            if(!retry)return;
        }
        await Work(()=>controller.SendReportAsync(preview,true,retry));
    }
    async Task Connect()
    {
        if(!await Confirm("审查标准独立版连接","继续后才解析当前用户的标准独立版 DingTalkInsights 目录。随后将显示确切路径并再次确认；只允许标准目录，任意副本无法阻止标准旧应用同时采集。请先停止并退出独立版。"))return;
        var root=session.Location.Resolve(true)!;
        if(await Confirm("连接此确切标准账本",$"{root}\n连接同一物理账本与旧版锁，保留断点、尝试、UTC 预算和发送侧账；撤销旧恢复授权，不自动读取来源、不导入或使用凭据。先断开模拟库。"))
            await Work(()=>session.Location.ConnectAsync(controller,root,true));
    }
    async Task UseCredentials()
    {
        var location=credentialLocation.SelectedIndex==0?CredentialLocation.Plugin:CredentialLocation.ConnectedStore;
        var purposes=(useModel.IsChecked==true?CredentialUse.Model:CredentialUse.None)|(useWebhook.IsChecked==true?CredentialUse.Webhook:CredentialUse.None);
        if(await Confirm("单独批准凭据用途",$"来源：{location}；仅用于：{purposes}\n现有密文不会回显或复制到另一保险箱。保存新值、连接、信任插件和开始监控均不能替代本次批准。"))await Work(()=>controller.UseCredentialsAsync(location,purposes,true));
    }
    async Task SaveCredentials()
    {
        var entered=new Credentials(key.Password,webhook.Password,secret.Password);
        try
        {
            if(await Confirm("加密保存新输入","将用本次输入替换插件本机三个凭据字段；空字段也会替换为空。不会更改独立版保险箱，不授予使用权限。输入值不会显示在此预览或日志。"))await Work(()=>controller.SaveCredentialsAsync(entered,true));
        }
        finally{key.Password="";webhook.Password="";secret.Password="";}
    }
    async Task<bool> Confirm(string title,string content,string primary="确认本次操作")
    {
        var epoch=attachment;
        var text=ReadOnly("DTI 本次授权正文");text.Text=content;text.MinHeight=180;text.MaxHeight=450;
        var dialog=new ContentDialog{XamlRoot=XamlRoot,Title=title,Content=text,PrimaryButtonText=primary,CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
        var result=await dialog.ShowAsync();return loaded&&attachment==epoch&&controller.Snapshot.Active&&result==ContentDialogResult.Primary;
    }
    async Task Safe(Func<Task> action)
    {
        if(!loaded||busy||!controller.Snapshot.Active)return;
        var epoch=attachment;busy=true;
        try{await action();}
        catch(SafetyException error){if(loaded&&attachment==epoch)notice.Text=error.Message;}
        catch(Exception){if(loaded&&attachment==epoch)notice.Text="本机操作未完成；未显示传输正文或凭据，请核对配置或存储。";}
        finally{busy=false;}
    }
    static Task<T> Work<T>(Func<Task<T>> action)=>PluginSession.Worker(action);
    static Task Work(Func<Task> action)=>PluginSession.Worker(action);
    Button Action(string label,string name,Func<Task> action)
    {var button=Named(new Button{Content=label,HorizontalAlignment=HorizontalAlignment.Left},name);button.Click+=async(_,_)=>await Safe(action);return button;}
    Button Action(string label,string name,Func<Task> action,out Button? button)=>button=Action(label,name,action);
    void Add(params UIElement[] elements){foreach(var element in elements)body.Children.Add(element);}
    static T Named<T>(T value,string name)where T:DependencyObject{AutomationProperties.SetName(value,name);return value;}
    static TextBlock Text(string text,double size=14)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
    static TextBox Edit(string name,bool multiline=false)=>Named(new TextBox{Header=name[4..],AcceptsReturn=multiline,TextWrapping=TextWrapping.Wrap,MaxLength=multiline?2000:8192,HorizontalAlignment=HorizontalAlignment.Stretch},name);
    static TextBox ReadOnly(string name)=>Named(new TextBox{Header=name[4..],IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=110,MaxHeight=450,HorizontalAlignment=HorizontalAlignment.Stretch},name);
    static PasswordBox Password(string name)=>Named(new PasswordBox{Header=name[4..],Password="",MaxLength=8192,HorizontalAlignment=HorizontalAlignment.Stretch},name);
    static string F(decimal value)=>value.ToString(CultureInfo.InvariantCulture);
    static int I(string value)=>int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)?n:throw new RuntimeFault(RuntimeFailure.Configuration);
    static decimal D(string value)=>decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)?n:throw new RuntimeFault(RuntimeFailure.Configuration);
    static bool HasOffset(string text)=>System.Text.RegularExpressions.Regex.IsMatch(text,@"(Z|[+-]\d\d:\d\d)$");
    sealed record ConversationRow(Conversation Value){public override string ToString()=>$"{Value.Name} · {Value.ConversationId}";}
    sealed record HistoryRow(SavedReport Value){public override string ToString()=>$"{Value.CreatedAt:yyyy-MM-dd HH:mm} · {(Value.IsDemo?"模拟 · ":"")}{Value.Task}";}
}
