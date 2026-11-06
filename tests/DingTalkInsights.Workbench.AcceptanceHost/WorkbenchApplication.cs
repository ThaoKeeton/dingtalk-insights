using Microsoft.UI.Xaml;

namespace Workbench.App;

// Same full name as the original App.xaml; generated Main/resources/metadata remain SDK-owned.
public sealed partial class WorkbenchApplication:Application
{
    readonly HarnessOptions harness;
    readonly RunOptions options;
    MainWindow? window;
    public WorkbenchApplication()
    {
        harness=HarnessOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        options=new RunOptions(false,harness.UserRoot,harness.Report);
        options.TraceStartup("isolated-sdk-generated-main");
        UnhandledException+=(_,args)=>{Environment.ExitCode=1;options.WriteFailure("unhandled-native-ui",args.Exception,args.Message);};
        options.TraceStartup("initializing-original-App.xaml");InitializeComponent();
        options.TraceStartup("original-application-resources-ready");
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window=new MainWindow(options);window.Activate();await window.InitializeAsync();
            if(window.SmokeStartupFailure is {} failure)throw new InvalidOperationException("Source-linked presenter startup failed.",failure);
            await AcceptanceDriver.Run(window,options,harness);
        }
        catch(Exception error)
        {
            Environment.ExitCode=1;options.WriteFailure("native-plugin-acceptance",error);
            if(window is not null)await window.FinishSmokeAsync();else Exit();
        }
    }
}
