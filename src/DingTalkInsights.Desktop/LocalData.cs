using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DingTalkInsights.Core;

namespace DingTalkInsights.Desktop;

internal sealed class DpapiCipher:IDataCipher
{
    [StructLayout(LayoutKind.Sequential)]private struct Blob{public int Size;public IntPtr Data;}
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]private static extern bool CryptProtectData(ref Blob data,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob result);
    [DllImport("crypt32.dll",SetLastError=true)]private static extern bool CryptUnprotectData(ref Blob data,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob result);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    public byte[] Protect(byte[] data)=>Transform(data,true);
    public byte[] Unprotect(byte[] data)=>Transform(data,false);
    private static byte[] Transform(byte[] bytes,bool protect)
    {
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob result=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            var ok=protect?CryptProtectData(ref input,"DingTalk Insights",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out result):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out result);
            if(!ok)throw new SafetyException("Windows 无法加密或解密本机数据。");
            var output=new byte[result.Size];Marshal.Copy(result.Data,output,0,output.Length);return output;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length],0,input.Data,bytes.Length);Marshal.FreeHGlobal(input.Data);
            if(result.Data!=IntPtr.Zero){Marshal.Copy(new byte[result.Size],0,result.Data,result.Size);LocalFree(result.Data);}
        }
    }
}
internal sealed record Credentials(string OpenAiKey="",string WebhookUrl="",string WebhookSecret="");
internal sealed class CredentialVault(string path,IDataCipher cipher)
{
    public bool Exists=>File.Exists(path);
    public Credentials Read()
    {
        if(!File.Exists(path))return new Credentials();
        try{var bytes=cipher.Unprotect(File.ReadAllBytes(path));try{return JsonSerializer.Deserialize<Credentials>(bytes)??new Credentials();}finally{CryptographicOperations.ZeroMemory(bytes);}}
        catch(Exception){throw new SafetyException("本机凭据无法解密，请在设置中重新配置；未展示任何密钥。");}
    }
    public void Save(Credentials values)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(values);var temp=path+".tmp";
        try{File.WriteAllBytes(temp,cipher.Protect(bytes));File.Move(temp,path,true);}
        finally{CryptographicOperations.ZeroMemory(bytes);if(File.Exists(temp))File.Delete(temp);}
    }
    public void Clear(){if(File.Exists(path))File.Delete(path);}
}
internal sealed record UserSettings
{
    public string DwsPath{get;init;}="";
    public string Profile{get;init;}="";
    public string ConversationId{get;init;}="";
    public DateTimeOffset? MonitoringStart{get;init;}
    public string WebhookName{get;init;}="";
    public string Theme{get;init;}="system";
    public ModelSettings Model{get;init;}=new();
    public int PollSeconds{get;init;}=300;
    public int RetentionDays{get;init;}=30;
}
