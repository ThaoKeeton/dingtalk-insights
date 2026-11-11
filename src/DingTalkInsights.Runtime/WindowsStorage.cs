using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

public sealed class DpapiCipher:IDataCipher
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
public sealed record Credentials(string OpenAiKey="",string WebhookUrl="",string WebhookSecret="");
public sealed class CredentialVault(string path,IDataCipher cipher)
{
    const int Maximum=128*1024;
    public bool Exists=>File.Exists(path);
    public string Revision
    {
        get
        {
            try
            {
                if(!File.Exists(path))return Guard.Hash("no-app-credentials");
                if(new FileInfo(path).Length>Maximum)throw new IOException();
                return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
        }
    }
    public Credentials Read()
    {
        var values=new EncryptedDocument<Credentials>(path,cipher,Maximum).Read()??new();Validate(values);return values;
    }
    public void Save(Credentials values)
    {
        Validate(values);new EncryptedDocument<Credentials>(path,cipher,Maximum).Save(values);
    }
    static void Validate(Credentials values)
    {
        if(values.OpenAiKey is null||values.WebhookUrl is null||values.WebhookSecret is null||
            values.OpenAiKey.Length>8192||values.WebhookUrl.Length>8192||values.WebhookSecret.Length>8192)throw new MonitoringFault(MonitorFailure.Storage);
    }
    public void Clear()
    {
        try{if(File.Exists(path))File.Delete(path);}catch(Exception){throw new MonitoringFault(MonitorFailure.Storage);}
    }
}
