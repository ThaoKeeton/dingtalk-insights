using System.Globalization;
using System.Text;
using System.Text.Json;
using DingTalkInsights.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DingTalkInsights.Desktop;

public sealed partial class MainWindow:Window
{
    readonly ConsentGate gate=new();
    readonly EncryptedStateStore store;
    readonly CredentialVault vault;
    readonly InsightState state;
    readonly MonitorEngine engine;
    readonly string dataRoot;
    readonly SemaphoreSlim operations=new(1,1);
    readonly Dictionary<string,FrameworkElement> pages=[];
    readonly List<Button> actions=[];
    CancellationTokenSource? active,monitorCancellation;
    Task? monitorTask;
    MessageBatch? candidates;
    AnalysisReport? currentReport;
    SavedReport? displayedSavedReport;
    UserSettings savedSettings=new();
    readonly MessageBatch demoBatch=Demo.CreateBatch();
    string markdown="";
    bool displayedDemo=true,smokeStarted,refreshingHistory;
    readonly bool smoke=Environment.GetCommandLineArgs().Contains("--smoke-test");

    readonly ComboBox source=new(){Header="数据来源",ItemsSource=new[]{"模拟数据（不连接服务）","官方 DWS（本机已登录身份）"},SelectedIndex=0};
    readonly TextBox dwsPath=new(){Header="官方 DWS 可执行文件绝对路径",PlaceholderText=@"C:\Users\you\...\dws.exe"};
    readonly TextBox profile=new(){Header="明确的 DWS 身份",PlaceholderText="corpId:userId"};
    readonly TextBox conversation=new(){Header="选定会话的稳定 ID",PlaceholderText="openConversationId"};
    readonly ComboBox conversations=new(){Header="所选时间内有新消息的会话",DisplayMemberPath="Display",HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly TextBox since=new(){Header="监控从何时开始（ISO 8601，含时区）"};
    readonly TextBox askStart=new(){Header="搜索开始时间（包含，含时区）"};
    readonly TextBox askEnd=new(){Header="搜索结束时间（不包含，含时区）"};
    readonly NumberBox interval=new(){Header="检查间隔（秒，60–3600）",Minimum=60,Maximum=3600,Value=300,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
    readonly NumberBox retention=new(){Header="本机消息保留天数（1–365）",Minimum=1,Maximum=365,Value=30,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
    readonly ToggleSwitch enableAi=new(){Header="将监控新增消息交给 OpenAI",OffContent="只采集到本机",OnContent="正文将发送到 OpenAI"};
    readonly TextBox taskText=new(){Header="你需要整理什么",Text="整理登录超时的沟通进展、验收口径和待确认事项。",AcceptsReturn=true,MinHeight=88,TextWrapping=TextWrapping.Wrap};
    readonly TextBox keywords=new(){Header="检索关键词（先核对关键词与范围）",Text="登录",PlaceholderText="本地搜索支持空格或逗号分词；DWS 使用整段关键词"};
    readonly TextBox candidateBox=new(){Header="实际候选消息 · 发送到模型前可核对",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=160,MaxHeight=320};
    readonly TextBox reportBox=new(){Header="当前整理结果与原文证据",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=280,MaxHeight=650};
    readonly ComboBox issuePicker=new(){Header="人工核实当前问题",DisplayMemberPath="Title",HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly ComboBox history=new(){Header="本机最近整理结果",HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly TextBlock monitorSummary=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock budgetSummary=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBox modelName=new(){Header="模型 ID",Text="gpt-6-luna"};
    readonly TextBox inputRate=new(){Header="输入单价 USD / 100 万 tokens",Text="0.10"};
    readonly TextBox outputRate=new(){Header="输出单价 USD / 100 万 tokens",Text="0.50"};
    readonly TextBox dailyBudget=new(){Header="每日费用预留上限 USD（UTC 日）",Text="0.50"};
    readonly NumberBox dailyRequests=new(){Header="每日最多请求数",Value=200,Minimum=1,Maximum=10000};
    readonly PasswordBox apiKey=new(){Header="OpenAI API 密钥（留空保留现有值）",PasswordRevealMode=PasswordRevealMode.Hidden};
    readonly PasswordBox webhookUrl=new(){Header="钉钉 Webhook 地址（含 token，留空保留）",PasswordRevealMode=PasswordRevealMode.Hidden};
    readonly PasswordBox webhookSecret=new(){Header="机器人加签 Secret（留空保留）",PasswordRevealMode=PasswordRevealMode.Hidden};
    readonly TextBox webhookName=new(){Header="接收机器人对应的群名称",PlaceholderText="请使用你能核对的具体群名"};
    readonly ComboBox theme=new(){Header="界面主题",ItemsSource=new[]{"跟随系统","浅色","深色"},SelectedIndex=0};
    Control? settingsPage;
    Button? startButton,stopButton;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240,900));
        dataRoot=smoke?Path.Combine(AppContext.BaseDirectory,"smoke-output","isolated-data"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DingTalkInsights");
        Directory.CreateDirectory(dataRoot);var cipher=new DpapiCipher();
        LoadSettings();
        store=new EncryptedStateStore(Path.Combine(dataRoot,"state.dpapi"),cipher,startupRetentionDays:(int)retention.Value);vault=new CredentialVault(Path.Combine(dataRoot,"credentials.dpapi"),cipher);
        state=store.Load();engine=new MonitorEngine(state,store);
        var now=DateTimeOffset.Now;since.Text=now.AddHours(-24).ToString("O");askStart.Text=since.Text;askEnd.Text=now.ToString("O");
        BuildPages();SetDemoFields();RefreshStatus();
        conversations.SelectionChanged+=(_,_)=>{if(conversations.SelectedItem is Conversation c)conversation.Text=c.ConversationId;};
        theme.SelectionChanged+=(_,_)=>Root.RequestedTheme=theme.SelectedIndex switch{1=>ElementTheme.Light,2=>ElementTheme.Dark,_=>ElementTheme.Default};
        source.SelectionChanged+=(_,_)=>{if(source.SelectedIndex==0)SetDemoFields();else LoadConnectionFields();};
        history.SelectionChanged+=HistorySelectionChanged;
        RefreshHistory();if(history.Items.Count>0)history.SelectedIndex=0;
        Navigation.SelectedItem=Navigation.MenuItems[0];
        Closed+=(_,_)=>{monitorCancellation?.Cancel();active?.Cancel();store.Dispose();};
        Activated+=async(_,_)=>{if(smoke&&!smokeStarted){smokeStarted=true;await RunSmokeAsync();}};
    }

    void BuildPages()
    {
        var monitor=Panel();
        monitor.Children.Add(Card("持续发现待核实事项",Label("在选定身份和会话内按时间增量读取。完整批次才推进断点，重叠两分钟去重；重新打开后不会自行开始读取。窗口关闭或电脑休眠时不会继续采集，恢复后再次开始会从断点追赶。")));
        monitor.Children.Add(Card("监控范围",Stack(since,interval,enableAi)));
        startButton=Action("开始持续检查","start-monitor",StartMonitoringAsync);
        stopButton=new Button{Content="停止",IsEnabled=false};AutomationProperties.SetAutomationId(stopButton,"stop-monitor");stopButton.Click+=(_,_)=>{monitorCancellation?.Cancel();active?.Cancel();StatusText.Text="正在停止；已保存断点和费用预留，不自动重试发送。";};
        monitor.Children.Add(Row(startButton,Action("检查一次并预览原文","read-once",ReadOnceAsync),stopButton));
        monitor.Children.Add(Card("运行状态",Stack(monitorSummary,budgetSummary)));
        monitor.Children.Add(Label("结果在“结果与发送”页查看。模型只提出带原文的候选；没有候选不代表没有问题。"));
        pages["monitor"]=Scroll(monitor);

        var ask=Panel();ask.Children.Add(Card("从你的事项出发搜集证据",Label("范围沿用“连接与设置”的身份与会话。先核对关键词和时间，搜索候选原文，再选择是否交给模型。不会跨到未选择的会话，也不会进行人员评价。")));
        ask.Children.Add(Stack(taskText,keywords,askStart,askEnd));
        ask.Children.Add(Row(Action("搜索本机索引","search-local",SearchLocalAsync),Action("通过 DWS 搜索","search-dws",SearchDwsAsync),Action("整理当前候选","analyze-candidates",AnalyzeCandidatesAsync)));
        ask.Children.Add(candidateBox);pages["ask"]=Scroll(ask);

        var results=Panel();results.Children.Add(Card("带来源的整理结果",Label("结论均为待核实候选。原文只以文本显示，正文中的命令与链接不会执行。发送前请核对接收群、正文、范围和不完整提示。")));
        results.Children.Add(Label("保存的历史结果可直接查看与导出。人工确认仅影响当前预览及其导出，不更新历史文件；重新打开历史仍需重新核实。"));
        results.Children.Add(history);results.Children.Add(reportBox);results.Children.Add(issuePicker);
        results.Children.Add(Row(Action("复核证据并确认解决","confirm-resolved",ConfirmResolvedAsync),Action("导出 Markdown…","export-report",ExportAsync),Action("预览并发送一次…","preview-send",SendAsync)));
        pages["results"]=Scroll(results);

        var settings=Panel();settings.Children.Add(Card("数据连接",Stack(source,dwsPath,profile,conversation,conversations,Action("列出时间范围内的会话…","discover",DiscoverAsync))));
        settings.Children.Add(Card("模型与费用上限",Stack(modelName,inputRate,outputRate,dailyBudget,dailyRequests,Label("默认低价模型的单价参考 2026-11-06 官方价目。单价可修改，请与实际模型核对。每次请求前保守预留费用，不因失败或重启返还；这是本应用的本机限额，不包含其他应用用量，也不是平台账单保证。"))));
        settings.Children.Add(Card("本机与外观",Stack(theme,retention,Label("修改保留天数在保存普通设置或确认开始真实监控后生效；缩短会立即清理到期正文及含其引用的结果。"),Action("保存普通设置","save-settings",SaveSettingsAsync))));
        settings.Children.Add(Card("仅在本机 Windows 加密保存",Stack(apiKey,webhookName,webhookUrl,webhookSecret,Label("已保存的秘密不回显。DWS 登录由官方 CLI 独立管理。密钥创建仍通过安全设置流程，不要粘贴到聊天或仓库。"),Row(Action("确认并保存凭据…","save-credentials",SaveCredentialsAsync),Action("移除本应用凭据…","clear-credentials",ClearCredentialsAsync)))));
        settings.Children.Add(Action("删除本机消息与结果索引…","clear-index",ClearIndexAsync));
        settingsPage=Scroll(settings);pages["settings"]=settingsPage;
        var help=Panel();help.Children.Add(Card("接入条件",Label("1. 独立安装官方 DWS，并在自己的电脑完成 dws auth login。企业未开通 CLI 时仍需管理员授权。\n2. 用 dws profile list 核对 corpId:userId；把明确身份与会话 ID 配置到本应用。\n3. 首先用一个会话与短时间范围核对消息数、时间和内容。\n4. 确认公司政策允许后，再配置新应用专用 OpenAI 密钥并开启上传。开发测试无需真实密钥。")));
        help.Children.Add(Card("首版的范围",Label("支持当前窗口运行期间的持续检查、本机加密消息索引、断点追赶、关键词搜索、候选整理、人工核实、导出与手动 Webhook。\n暂不提供后台 Windows 服务、托盘、自启动、实时事件订阅、自动更新或附件正文解析。轮询可能漏掉查询窗口外的编辑、撤回或平台不可见历史；这类内容必须回钉钉核实。\n缺页、解析失败、授权失败和预算耗尽会显示原因并暂停监控，不跨过未知缺口。单次读取最多 1000 条，密集会话请缩小起始范围。\n消息默认保留 30 天，最大 50000 条；加密状态上限 32 MiB。超限会暂停，不静默丢弃。最近保留 100 份结果。\nWebhook 超时会标为结果不确定，先检查钉钉再决定重试。")));
        help.Children.Add(Card("本机数据位置",Label(dataRoot+"\nstate.dpapi：消息、断点、尝试记录、费用预留和结果。\ncredentials.dpapi：本应用凭据。\nsettings.json：不含密钥的普通设置。\nWindows DPAPI 仅保护静态文件；同一 Windows 用户下的恶意进程不在本应用隔离能力内。")));
        pages["help"]=Scroll(help);
    }

    void NavigationChanged(NavigationView sender,NavigationViewSelectionChangedEventArgs args)
    {
        if(args.SelectedItem is NavigationViewItem item&&item.Tag is string key&&pages.TryGetValue(key,out var page)){PageHost.Content=page;PageTitle.Text=item.Content.ToString();}
    }
    void Navigate(string key){Navigation.SelectedItem=Navigation.MenuItems.OfType<NavigationViewItem>().First(i=>(string)i.Tag==key);}
    bool IsDemo=>source.SelectedIndex==0;
    void SetDemoFields(){var b=demoBatch;profile.Text=b.Scope.Profile;conversation.Text=b.Scope.ConversationId;since.Text=b.Scope.Start.ToString("O");askStart.Text=b.Scope.Start.ToString("O");askEnd.Text=b.Scope.End.ToString("O");}
    QueryScope Scope(bool forSearch=false,bool monitoring=false)
    {
        var from=ParseTime(forSearch?askStart.Text:since.Text);var to=forSearch?ParseTime(askEnd.Text):IsDemo?demoBatch.Scope.End:DateTimeOffset.Now;
        var scope=new QueryScope(profile.Text.Trim(),conversation.Text.Trim(),from,to);
        if(monitoring)scope.ValidateOrigin();else scope.Validate();return scope;
    }
    void EnforceRetention()
    {
        var previousReports=state.Reports;
        state.Trim(DateTimeOffset.UtcNow,state.RetentionDays);
        // Invalidate expired in-memory content before persistence, even when the write fails.
        if(candidates is not null&&candidates.Messages.Any(m=>m.CreatedAt<state.RetentionCutoff)){candidates=null;candidateBox.Text="候选正文已到保留期，请重新读取或搜索。";}
        if((currentReport is not null&&currentReport.Batch.Messages.Any(m=>m.CreatedAt<state.RetentionCutoff))
            ||(displayedSavedReport is not null&&!state.Reports.Contains(displayedSavedReport)))
        {currentReport=null;displayedSavedReport=null;markdown="";reportBox.Text="结果引用的来源已到保留期，已清理当前预览。";issuePicker.ItemsSource=null;}
        if(!state.Reports.SequenceEqual(previousReports))RefreshHistory();
        store.Save(state);
    }
    static DateTimeOffset ParseTime(string text)
    {
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(text));return Guard.Time(doc.RootElement);
    }
    ModelSettings Model()
    {
        decimal Parse(string s)=>decimal.TryParse(s,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)?value:throw new SafetyException("请输入以小数点分隔的 USD 金额。");
        var model=new ModelSettings{Model=modelName.Text.Trim(),InputUsdPerMillion=Parse(inputRate.Text),OutputUsdPerMillion=Parse(outputRate.Text),DailyBudgetUsd=Parse(dailyBudget.Text),MaxRequestsPerDay=(int)dailyRequests.Value};model.Validate();return model;
    }
    DwsClient Dws()=>new(dwsPath.Text.Trim(),gate);
    async Task Run(Func<Task> action,bool quiet=false,bool monitorTick=false)
    {
        if(!await operations.WaitAsync(0)){if(!quiet)StatusText.Text="已有操作正在执行，请稍后再试。";return;}
        active=new CancellationTokenSource();BusyBar.Visibility=Visibility.Visible;foreach(var button in actions)button.IsEnabled=false;
        try{EnforceRetention();await action();EnforceRetention();}
        catch(SafetyException e){StatusText.Text=e.Message;if(monitorTick)monitorCancellation?.Cancel();}
        catch(OperationCanceledException){StatusText.Text="操作已停止；已有断点和费用预留保留。";}
        catch(Exception){StatusText.Text="操作未完成，未展示服务响应或凭据。请检查本机配置与可用空间。";if(monitorTick)monitorCancellation?.Cancel();}
        finally{active.Dispose();active=null;BusyBar.Visibility=Visibility.Collapsed;foreach(var button in actions)button.IsEnabled=true;operations.Release();RefreshStatus();UpdateMonitorControls();}
    }
    CancellationToken ActiveToken=>active?.Token??CancellationToken.None;
    async Task<bool> Confirm(string title,string text,string accept="确认本次操作")
    {
        var box=new TextBox{Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=480,MinWidth=420};
        var dialog=new ContentDialog{Title=title,Content=box,PrimaryButtonText=accept,CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,XamlRoot=Root.XamlRoot,RequestedTheme=Root.ActualTheme};
        return await dialog.ShowAsync()==ContentDialogResult.Primary;
    }
    async Task DiscoverAsync()
    {
        if(IsDemo){conversations.ItemsSource=new[]{new Conversation("demo-project","模拟项目群","group")};conversations.SelectedIndex=0;StatusText.Text="已载入模拟会话。";return;}
        var start=ParseTime(askStart.Text);var end=ParseTime(askEnd.Text);var identity=profile.Text.Trim();
        if(!await Confirm("读取会话名称和 ID",$"身份：{identity}\n时间：{start:O} 至 {end:O}\n只发现此范围内有消息的会话；不会开始模型整理。"))return;
        conversations.ItemsSource=await Dws().DiscoverAsync(identity,start,end,gate.Issue(ExternalAction.Discover,DwsClient.DiscoveryFingerprint(identity,start,end)),ActiveToken);StatusText.Text="会话列表已更新，请明确选择一个会话。";
    }
    async Task ReadOnceAsync()
    {
        var initial=Scope(monitoring:true);var scope=IsDemo?demoBatch.Scope:MonitorEngine.NextScope(state,initial,DateTimeOffset.UtcNow);MessageBatch batch;
        state.EnsureScopeRetained(scope);
        if(IsDemo)batch=demoBatch;
        else
        {
            if(!await Confirm("只读取选定会话",scope.Display+"\n读取结果将以 Windows DPAPI 加密保存在本机索引；本次不会上传到模型。"))return;
            EnforceRetention();state.EnsureScopeRetained(scope);
            batch=await Dws().ReadAsync(scope,gate.Issue(ExternalAction.Read,scope.Fingerprint),ActiveToken);
        }
        state.Ingest(batch);store.Save(state);SetCandidates(batch);Navigate("ask");StatusText.Text=$"已读取 {batch.Messages.Count} 条；{(batch.Complete?"已返回分页结束":"范围不完整") }。可核对原文后再整理。";
    }
    Task SearchLocalAsync()
    {
        if(IsDemo){state.Ingest(demoBatch);store.Save(state);}
        SetCandidates(LocalSearch.Find(state,Scope(true),keywords.Text));StatusText.Text=$"本机命中 {candidates!.Messages.Count} 条（最多 40 条）；未请求外部服务。";return Task.CompletedTask;
    }
    async Task SearchDwsAsync()
    {
        if(IsDemo){await SearchLocalAsync();return;}
        var scope=Scope(true);var query=keywords.Text.Trim();state.EnsureScopeRetained(scope);
        if(!await Confirm("按关键词搜索所选会话",scope.Display+"\n关键词："+query+"\n仅检索此会话，结果保存到本机；不会自动上传模型。"))return;
        EnforceRetention();state.EnsureScopeRetained(scope);
        var batch=await Dws().SearchAsync(scope,query,gate.Issue(ExternalAction.Search,DwsClient.SearchFingerprint(scope,query)),ActiveToken);
        state.Ingest(batch);store.Save(state);SetCandidates(batch);StatusText.Text=$"DWS 搜索返回 {batch.Messages.Count} 条；关键词检索可能遗漏其他表述。";
    }
    void SetCandidates(MessageBatch batch)
    {
        candidates=batch.Messages.Count>40?batch with{Messages=batch.Messages.Take(40).ToArray(),Complete=false,Warnings=batch.Warnings.Concat(["模型候选仅取本次最早 40 条，请缩小范围或另行查询余下内容。"]).ToArray()}:batch;
        currentReport=null;displayedSavedReport=null;markdown="";reportBox.Text="";issuePicker.ItemsSource=null;
        candidateBox.Text=candidates.Scope.Display+"\n\n"+string.Join("\n\n",candidates.Messages.Select(m=>$"[{m.MessageId}] {m.CreatedAt:yyyy-MM-dd HH:mm:ss zzz} {m.Sender}\n{m.Text}"))+"\n\n"+string.Join("\n",candidates.Warnings);
    }
    async Task AnalyzeCandidatesAsync()
    {
        var batch=candidates??throw new SafetyException("请先读取或搜索候选消息。");if(batch.Messages.Count==0)throw new SafetyException("当前没有候选消息，请调整搜索词或时间范围。");
        var task=taskText.Text.Trim();var model=Model();string key="";
        if(!batch.IsDemo)
        {
            var request=OpenAiClient.BuildRequest(batch,task,model);var reserve=OpenAiClient.Reservation(request,model);
            if(!await Confirm("本次向 OpenAI 上传候选正文",$"{batch.Scope.Display}\n{batch.Messages.Count} 条；模型 {model.Model}\n事项：{task}\n本次保守预留：USD {reserve:F6}\n\n将发送上方预览中的姓名、消息 ID、时间与正文。请先确认公司政策允许。不会发送 Webhook。","同意上传并整理一次"))return;
            key=vault.Read().OpenAiKey;if(string.IsNullOrWhiteSpace(key))throw new SafetyException("请先在设置中配置本应用的专用 OpenAI 密钥。");
        }
        var result=await engine.AnalyzeOnceAsync(batch,task,model,Analyzer(batch.IsDemo,key,model),DateTimeOffset.UtcNow,ActiveToken);
        SetReport(result.Report);Navigate("results");StatusText.Text="整理完成，候选结论与证据请逐项核实。";
    }
    Func<MessageBatch,string,CancellationToken,Task<ModelResult>> Analyzer(bool demo,string key,ModelSettings model)=>
        demo?(b,t,_)=>Task.FromResult(new ModelResult(Demo.Analyze(b,t),0,0)):
        (b,t,c)=>new OpenAiClient(gate).AnalyzeAsync(key,b,t,model,gate.Issue(ExternalAction.Analyze,OpenAiClient.Fingerprint(b,t,model)),c);
    async Task StartMonitoringAsync()
    {
        if(monitorCancellation is not null)throw new SafetyException("请先停止现有监控。");
        var scope=Scope(monitoring:true);var model=Model();var isDemo=IsDemo;var useAi=enableAi.IsOn||isDemo;
        var poll=(int)interval.Value;var days=isDemo?state.RetentionDays:(int)retention.Value;
        if(poll is <60 or >3600||days is <1 or >365)throw new SafetyException("检查间隔或保留天数无效。");
        var now=DateTimeOffset.UtcNow;
        var next=MonitorEngine.NextScope(state,scope,now);
        if(next.Start<now.AddDays(-days))throw new SafetyException("下一采集窗口早于所选保留期；请明确增加保留天数，或改晚起点并在开始确认中核对放弃的缺口。");
        var resumeNote="";
        if(!isDemo&&state.Monitoring is {} previous&&previous.Scope(now).Key==scope.Key)
        {
            var priorCursor=MonitorEngine.ResumeCursor(state,previous.Scope(now),now);
            resumeNote=$"\n上次授权起点：{previous.Start:O}；从覆盖断点恢复的下一起点：{priorCursor:O}。";
            if(scope.Start>priorCursor)resumeNote+=$"\n你已改晚起点：确认开始将明确放弃此前尚未检查的缺口 [{priorCursor:O}, {scope.Start:O})。";
        }
        if(!isDemo&&!await Confirm("开始持续检查",scope.Display+resumeNote+$"\n每 {poll} 秒检查，保持此窗口运行，直到你停止或退出。\n原文在本机加密保存 {days} 天。\n"+(useAi?$"允许增量正文上传到 OpenAI：{model.Model}；每日预留上限 USD {model.DailyBudgetUsd}，请求上限 {model.MaxRequestsPerDay}。每次最多 40 条，结果待核实。":"不上传到模型，仅采集到本机。")+"\n不会自动发送 Webhook。","授权以上范围并开始"))return;
        var key=useAi&&!isDemo?vault.Read().OpenAiKey:"";if(useAi&&!isDemo&&string.IsNullOrWhiteSpace(key))throw new SafetyException("开启模型检查前，请配置本应用的 OpenAI 密钥。");
        if(!isDemo){state.Monitoring=new MonitoringOrigin(scope.Profile,scope.ConversationId,scope.Start);await SaveSettingsAsync();store.Save(state);}
        var client=Dws();Func<QueryScope,CancellationToken,Task<MessageBatch>> fetch=isDemo?(s,_)=>Task.FromResult(demoBatch with{Scope=s,Messages=demoBatch.Messages.Where(m=>m.CreatedAt>=s.Start&&m.CreatedAt<s.End).ToArray()}):(s,c)=>client.ReadAsync(s,gate.Issue(ExternalAction.Read,s.Fingerprint),c);
        var cancellation=new CancellationTokenSource();monitorCancellation=cancellation;UpdateMonitorControls();
        monitorTask=MonitorLoop(scope,fetch,useAi?Analyzer(isDemo,key,model):null,model,poll,days,cancellation);
        StatusText.Text=isDemo?"模拟持续检查已开始；不连接任何服务。":"持续检查已开始，仅限已确认的身份和会话。";
    }
    async Task MonitorLoop(QueryScope scope,Func<QueryScope,CancellationToken,Task<MessageBatch>> fetch,Func<MessageBatch,string,CancellationToken,Task<ModelResult>>? analyze,ModelSettings model,int poll,int days,CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            while(!cancellation.IsCancellationRequested)
            {
                await Run(async()=>
                {
                    var result=await engine.TickAsync(scope,fetch,analyze,model,days,DateTimeOffset.UtcNow,cancellation.Token);
                    if(result.Report is not null)SetReport(result.Report);
                    StatusText.Text=$"本轮新增或修订 {result.Changed} 条；本机 {result.Stored} 条。";
                    if(!result.Complete){StatusText.Text+=" 采集不完整，断点未推进；监控已暂停，请缩小范围或核对权限后恢复。";cancellation.Cancel();}
                },quiet:true,monitorTick:true);
                await Task.Delay(TimeSpan.FromSeconds(poll),cancellation.Token);
            }
        }
        catch(OperationCanceledException){}
        finally{if(ReferenceEquals(monitorCancellation,cancellation)){monitorCancellation=null;UpdateMonitorControls();}cancellation.Dispose();}
    }
    void UpdateMonitorControls()
    {
        var running=monitorCancellation is not null;
        if(startButton is not null)startButton.IsEnabled=!running;if(stopButton is not null)stopButton.IsEnabled=running||active is not null;
        if(settingsPage is not null)settingsPage.IsEnabled=!running;since.IsEnabled=!running;interval.IsEnabled=!running;enableAi.IsEnabled=!running;
    }
    void SetReport(AnalysisReport report){currentReport=report;displayedSavedReport=null;markdown=ReportRenderer.Markdown(report);displayedDemo=report.Batch.IsDemo;reportBox.Text=markdown;issuePicker.ItemsSource=report.Issues;if(report.Issues.Count>0)issuePicker.SelectedIndex=0;RefreshHistory(clearSelection:true);}
    void RefreshHistory(bool clearSelection=false)
    {
        var selected=clearSelection?null:(history.SelectedItem as HistoryItem)?.Report;
        refreshingHistory=true;
        try
        {
            var items=state.Reports.AsEnumerable().Reverse().Select(r=>new HistoryItem(r)).ToArray();
            history.ItemsSource=items;history.SelectedItem=items.FirstOrDefault(i=>i.Report==selected);
        }
        finally{refreshingHistory=false;}
    }
    void HistorySelectionChanged(object sender,SelectionChangedEventArgs args)
    {
        if(refreshingHistory||history.SelectedItem is not HistoryItem item)return;
        try
        {
            EnforceRetention();
            if(!state.Reports.Contains(item.Report)){RefreshHistory();return;}
            currentReport=null;displayedSavedReport=item.Report;markdown=item.Report.Markdown;displayedDemo=item.Report.IsDemo;
            reportBox.Text=markdown;issuePicker.ItemsSource=null;
        }
        catch(Exception e)
        {
            // Viewing history is a manual action: storage failure must not escape XAML or stop monitoring.
            currentReport=null;displayedSavedReport=null;markdown="";reportBox.Text="历史结果未能载入，请检查本机存储后重新选择。";issuePicker.ItemsSource=null;
            RefreshHistory(clearSelection:true);
            StatusText.Text=e is SafetyException?e.Message:"历史结果未能载入；未展示服务响应或凭据，请检查本机存储。";
        }
    }
    async Task ConfirmResolvedAsync()
    {
        var report=currentReport??throw new SafetyException("历史文本不可直接改变状态，请重新核对当前结构化结果。");var index=issuePicker.SelectedIndex;
        if(index<0)throw new SafetyException("请选择当前需要人工核实的问题。");var issue=report.Issues[index];
        if(!await Confirm("人工确认解决",issue.Title+"\n\n"+string.Join("\n\n",issue.Evidence.Select(e=>$"{e.Message.MessageId} / {e.Message.CreatedAt:O}\n{e.Quote}"))+"\n\n仅在你已回原会话确认验证结果与验收范围后继续。提交修复或有人回复本身不足以确认解决。","我已核实，确认解决"))return;
        EnforceRetention();if(currentReport!=report)throw new SafetyException("来源已到保留期，请重新读取。");report.ConfirmResolved(index,true);SetReport(report);StatusText.Text="当前预览已标为人工确认；此标记未写入历史，当前预览可单独导出或发送。";
    }
    async Task ExportAsync()
    {
        if(markdown.Length==0)throw new SafetyException("还没有可导出的结果。");
        var picker=new FileSavePicker{SuggestedFileName="dingtalk-insights-"+DateTime.Now.ToString("yyyyMMdd-HHmm")};picker.FileTypeChoices.Add("Markdown",new[]{".md"});
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var file=await picker.PickSaveFileAsync();if(file is not null){EnforceRetention();if(markdown.Length==0)throw new SafetyException("来源已到保留期，结果已清理；本次未导出。");await File.WriteAllTextAsync(file.Path,markdown,new UTF8Encoding(false));StatusText.Text="结果已导出到所选位置；导出文件含正文，请按公司政策保管。";}
    }
    async Task SendAsync()
    {
        if(markdown.Length==0)throw new SafetyException("还没有可发送的结果。");var content=markdown;
        if(displayedDemo){await Confirm("模拟发送预览（不会发到真实群）",content,"完成模拟预览");StatusText.Text="模拟预览结束，未请求 Webhook。";return;}
        var credential=vault.Read();var target=new WebhookTarget(webhookName.Text.Trim(),credential.WebhookUrl,credential.WebhookSecret);target.Validate();
        if(!await Confirm("向选定钉钉群发送一次",$"接收群：{target.Name}\n目标指纹：{Guard.Hash(target.Url)[..12]}\n\n{content}","确认发送这一份内容"))return;
        EnforceRetention();if(markdown.Length==0||markdown!=content)throw new SafetyException("结果已过期或改变，请重新预览；本次未发送。");
        var status=await new WebhookClient(gate).SendAsync(target,content,gate.Issue(ExternalAction.Send,WebhookClient.Fingerprint(target,content)),ActiveToken);
        StatusText.Text=status switch{DeliveryStatus.Sent=>"钉钉已确认接收本次消息。",DeliveryStatus.Failed=>"钉钉未确认接收，请核对机器人设置；未自动重试。",_=>"发送结果不确定。请先到钉钉检查是否收到，未自动重试。"};
    }
    void RefreshStatus()
    {
        monitorSummary.Text=$"本机索引：{state.Messages.Count} 条 · 断点：{state.Watermarks.Count} 个会话 · 保存结果：{state.Reports.Count} 份\n最近采集回执：{state.Receipts.Count} 份，其中 {state.Receipts.Count(r=>!r.Complete)} 份不完整；原始元数据仅在本机加密保存。\n{(monitorCancellation is not null?"当前监控运行中":"监控尚未启动或已停止；点击开始才会采集")}";
        budgetSummary.Text=$"费用日：{(state.BudgetDay.Length==0?"尚无真实模型请求":state.BudgetDay+" UTC")} · 已预留 USD {state.ReservedUsd:F6} · 请求 {state.Requests} 次\n模型已返回用量：输入 {state.InputTokens} / 输出 {state.OutputTokens} tokens。失败与结果不明的预留保留，不自动重试同一修订。";
    }
    void LoadSettings()
    {
        if(!File.Exists(Path.Combine(dataRoot,"settings.json")))return;
        try
        {
            var s=JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(dataRoot,"settings.json")))??new();
            s.Model.Validate();if(s.RetentionDays is <1 or >365||s.PollSeconds is <60 or >3600)throw new SafetyException("普通设置范围无效。");savedSettings=s;
            dwsPath.Text=s.DwsPath;webhookName.Text=s.WebhookName;modelName.Text=s.Model.Model;inputRate.Text=s.Model.InputUsdPerMillion.ToString(CultureInfo.InvariantCulture);outputRate.Text=s.Model.OutputUsdPerMillion.ToString(CultureInfo.InvariantCulture);dailyBudget.Text=s.Model.DailyBudgetUsd.ToString(CultureInfo.InvariantCulture);dailyRequests.Value=s.Model.MaxRequestsPerDay;interval.Value=s.PollSeconds;retention.Value=s.RetentionDays;theme.SelectedIndex=s.Theme switch{"light"=>1,"dark"=>2,_=>0};Root.RequestedTheme=theme.SelectedIndex switch{1=>ElementTheme.Light,2=>ElementTheme.Dark,_=>ElementTheme.Default};
        }
        catch(Exception){StatusText.Text="普通设置无法读取，已使用默认值；原设置文件保留，未自动覆盖。";}
    }
    void LoadConnectionFields()
    {
        var now=DateTimeOffset.Now;var prior=state.Monitoring;
        profile.Text=prior?.Profile??savedSettings.Profile;conversation.Text=prior?.ConversationId??savedSettings.ConversationId;
        since.Text=(prior?.Start??savedSettings.MonitoringStart??now.AddHours(-24)).ToString("O");
        askStart.Text=now.AddHours(-24).ToString("O");askEnd.Text=now.ToString("O");
    }
    Task SaveSettingsAsync()
    {
        var model=Model();var settings=new UserSettings{DwsPath=dwsPath.Text.Trim(),Profile=IsDemo?savedSettings.Profile:profile.Text.Trim(),ConversationId=IsDemo?savedSettings.ConversationId:conversation.Text.Trim(),MonitoringStart=IsDemo?savedSettings.MonitoringStart:ParseTime(since.Text),WebhookName=webhookName.Text.Trim(),Model=model,Theme=theme.SelectedIndex switch{1=>"light",2=>"dark",_=>"system"},PollSeconds=(int)interval.Value,RetentionDays=(int)retention.Value};
        if(settings.RetentionDays is <1 or >365||settings.PollSeconds is <60 or >3600)throw new SafetyException("检查间隔或保留天数无效。");
        state.Trim(DateTimeOffset.UtcNow,settings.RetentionDays);store.Save(state);
        var path=Path.Combine(dataRoot,"settings.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);savedSettings=settings;EnforceRetention();RefreshHistory();StatusText.Text="普通设置已保存。每次启动仍从模拟模式开始，监控不会自动运行。";return Task.CompletedTask;
    }
    async Task SaveCredentialsAsync()
    {
        if(!await Confirm("持久保存本应用凭据","将新输入的 OpenAI 密钥、Webhook 地址和加签 Secret 用 Windows DPAPI 加密，保存在当前 Windows 用户的 LocalAppData。\n\n仅本应用使用；不会上传到仓库，旧值不回显。留空字段保留现有值。","同意加密保存"))return;
        var old=vault.Read();var next=new Credentials(apiKey.Password.Length>0?apiKey.Password:old.OpenAiKey,webhookUrl.Password.Length>0?webhookUrl.Password:old.WebhookUrl,webhookSecret.Password.Length>0?webhookSecret.Password:old.WebhookSecret);
        if(next.WebhookUrl.Length>0)new WebhookTarget(webhookName.Text.Trim(),next.WebhookUrl,next.WebhookSecret).Validate();
        vault.Save(next);apiKey.Password="";webhookUrl.Password="";webhookSecret.Password="";await SaveSettingsAsync();StatusText.Text="凭据已在本机加密保存，未测试或请求真实服务。";
    }
    async Task ClearCredentialsAsync(){if(await Confirm("移除本应用凭据","删除本应用保存的加密凭据文件。不会注销 DWS，也不会撤销 OpenAI 平台密钥；如需撤销，请在对应平台操作。","移除本机凭据")){vault.Clear();apiKey.Password="";webhookUrl.Password="";webhookSecret.Password="";StatusText.Text="已移除本应用的本机凭据文件。";}}
    async Task ClearIndexAsync(){if(await Confirm("删除本机消息和结果索引","删除本应用的消息、断点、尝试记录与结果；当日费用预留和请求数保留，避免清理后绕过预算。不会影响钉钉原始聊天。","删除本机索引")){state.ClearIndex();store.Save(state);candidates=null;currentReport=null;displayedSavedReport=null;markdown="";candidateBox.Text="";reportBox.Text="";issuePicker.ItemsSource=null;RefreshHistory();StatusText.Text="本机索引已清理，当日费用预留保留。";}}

    Button Action(string text,string id,Func<Task> handler){var button=new Button{Content=text};AutomationProperties.SetAutomationId(button,id);button.Click+=async(_,_)=>await Run(handler);actions.Add(button);return button;}
    static StackPanel Panel()=>new(){Spacing=18,Padding=new Thickness(0,0,4,16)};
    static TextBlock Label(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,LineHeight=23};
    static StackPanel Stack(params UIElement[] elements){var panel=new StackPanel{Spacing=12};foreach(var e in elements)panel.Children.Add(e);return panel;}
    static StackPanel Row(params UIElement[] elements){var panel=new StackPanel{Spacing=8,Orientation=Orientation.Horizontal};foreach(var e in elements)panel.Children.Add(e);return panel;}
    static Border Card(string title,UIElement body)=>new(){Style=(Style)Application.Current.Resources["SettingCardStyle"],Child=Stack(new TextBlock{Text=title,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,FontSize=16},body)};
    static ScrollViewer Scroll(UIElement child)=>new(){Content=child,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
    sealed record HistoryItem(SavedReport Report){public override string ToString()=>$"{Report.CreatedAt.ToLocalTime():MM-dd HH:mm} · {(Report.IsDemo?"模拟 · ":"")}{Report.Task}";}
}
