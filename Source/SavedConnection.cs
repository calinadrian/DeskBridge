using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DeskBridge;

// Windows encrypts saved credentials for the signed-in user.
internal static class SavedConnection
{
    static string DirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeskBridge");
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]
    static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Protect(byte[] bytes,bool encrypt)
    {
        var input=new Blob { Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length) }; Blob output=default;
        try {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool ok=encrypt ? CryptProtectData(ref input,"DeskBridge",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output)
                : CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result=new byte[output.Length]; Marshal.Copy(output.Data,result,0,result.Length); return result;
        } finally { Marshal.FreeHGlobal(input.Data); if(output.Data!=IntPtr.Zero) LocalFree(output.Data); }
    }
    static void Save(string name,byte[] bytes)
    {
        Directory.CreateDirectory(DirectoryPath); string path=Path.Combine(DirectoryPath,name);
        File.WriteAllBytes(path+".tmp",Protect(bytes,true)); File.Move(path+".tmp",path,true);
    }
    static byte[]? Load(string name)
    {
        string path=Path.Combine(DirectoryPath,name);
        return File.Exists(path) ? Protect(File.ReadAllBytes(path),false) : null;
    }
    public static string SenderCode()=>Load("sender.dat") is byte[] bytes ? Encoding.UTF8.GetString(bytes) : "";
    public static void SaveSender(string code)=>Save("sender.dat",Encoding.UTF8.GetBytes(code));
    sealed record Identity(string Certificate,string Secret);
    public static (X509Certificate2 Certificate,byte[] Secret) Receiver(bool remember)
    {
        if(remember && Load("receiver.dat") is byte[] bytes) {
            var identity=JsonSerializer.Deserialize<Identity>(bytes) ?? throw new IOException("Saved pairing is invalid.");
            return (new X509Certificate2(Convert.FromBase64String(identity.Certificate)),Convert.FromBase64String(identity.Secret));
        }
        var cert=Connection.Certificate(); var secret=RandomNumberGenerator.GetBytes(32);
        try {
            if(remember) Save("receiver.dat",JsonSerializer.SerializeToUtf8Bytes(new Identity(Convert.ToBase64String(cert.Export(X509ContentType.Pfx)),Convert.ToBase64String(secret))));
            return (cert,secret);
        } catch { cert.Dispose(); throw; }
    }
    public static void Forget()
    {
        foreach(string name in new[] { "sender.dat","receiver.dat" }) File.Delete(Path.Combine(DirectoryPath,name));
    }
    public static void SetStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(enabled) key.SetValue("DeskBridge","\""+Environment.ProcessPath+"\" --startup");
        else key.DeleteValue("DeskBridge",false);
    }
}
