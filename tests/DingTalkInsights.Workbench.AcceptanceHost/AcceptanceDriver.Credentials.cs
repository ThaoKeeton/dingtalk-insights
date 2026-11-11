using System.Text;
using Microsoft.UI.Xaml.Controls;

namespace Workbench.App;

internal static partial class AcceptanceDriver
{
    static async Task VerifyHostCredentials(MainWindow window,HarnessOptions options,Microsoft.UI.Xaml.FrameworkElement settings,object plugin,List<object> observations)
    {
        CurrentStep="native-host-credentials";
        var root=Path.Combine(options.UserRoot,"secrets",Id);
        string[] Files()=>Directory.Exists(root)?Directory.GetFiles(root,"*",SearchOption.AllDirectories):[];
        var location=Find<ComboBox>(settings,"DTI 凭据来源");
        Require(location.Items.Count==3&&location.SelectedIndex==0,"Native host credential choice absent.");
        Find<PasswordBox>(settings,"DTI 新 OpenAI key").Password="SYNTHETIC_SECRET_NATIVE";
        await ConfirmAction(window,settings,"DTI 保存新凭据",false);
        await Wait(()=>PageState(plugin).GetProperty("Outcome").GetString()=="completed","Cancelled credential save did not settle.");
        Require(Files().Length==0&&Find<PasswordBox>(settings,"DTI 新 OpenAI key").Password.Length==0,"Cancelled save wrote/retained input.");
        Find<PasswordBox>(settings,"DTI 新 OpenAI key").Password="SYNTHETIC_SECRET_NATIVE";
        await ConfirmAction(window,settings,"DTI 保存新凭据",true);
        await Wait(()=>PageState(plugin).GetProperty("Outcome").GetString()=="completed"&&Files().Length==2,"Native committed host save failed.");
        Require(Files().All(file=>!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("SYNTHETIC_SECRET_NATIVE")),"Actual host ciphertext contains plaintext credential.");
        Find<CheckBox>(settings,"DTI 凭据用于模型").IsChecked=true;
        await ConfirmAction(window,settings,"DTI 批准凭据用途",true);
        await Wait(()=>PageState(plugin).GetProperty("Outcome").GetString()=="completed"&&Find<TextBlock>(settings,"DTI 操作结果").Text.Contains("已批准所选来源"),"Native separate host approval failed.");
        Invoke(Find<Button>(settings,"DTI 撤销凭据用途"));
        await Wait(()=>PageState(plugin).GetProperty("Outcome").GetString()=="completed"&&Find<TextBlock>(settings,"DTI 操作结果").Text.Contains("已撤销凭据用途"),"Native revoke did not retire.");
        Require(Files().Length==2,"Purpose revoke deleted saved credentials.");
        await ConfirmAction(window,settings,"DTI 删除所选凭据",true);
        await Wait(()=>PageState(plugin).GetProperty("Outcome").GetString()=="completed"&&Files().Length==0,"Native host credential deletion failed.");
        // Keep a synthetic committed value for subsequent disable/restart/package-retention phases.
        Find<PasswordBox>(settings,"DTI 新 OpenAI key").Password="SYNTHETIC_SECRET_NATIVE";
        await ConfirmAction(window,settings,"DTI 保存新凭据",true);
        await Wait(()=>PageState(plugin).GetProperty("Outcome").GetString()=="completed"&&Files().Length==2,"Native resave did not finish.");
        Require(Resources(plugin).GetProperty("Dispatches").GetInt64()==0&&!Resources(plugin).GetProperty("StoreOwned").GetBoolean(),"Credential flow performed business IO.");
        observations.Add(new{stage="native-host-credentials",protection="actual current-user Windows DPAPI",cancelWroteNothing=true,purposeSeparate=true,revokeRetainedCiphertext=true,deleteRemovedValues=true,noExternalDispatch=true,retainedCiphertextFiles=Files().Length});
    }
}
