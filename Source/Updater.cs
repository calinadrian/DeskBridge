using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeskBridge;

internal sealed record ReleaseUpdate(Version Version, string Tag, Uri Binary, Uri Checksum);
internal sealed record UpdateJob(int ParentId, long ParentStarted, string Target, string Sha256);

internal static class Updater
{
    public const string Repository="calinadrian/DeskBridge";
    public const string Asset="DeskBridge-win-x64.exe";
    static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeskBridge","Updates");
    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0,0,0);
    public static string DisplayVersion => $"{Current.Major}.{Current.Minor}.{Current.Build}";
    static readonly HttpClient Http=CreateHttp();
    static HttpClient CreateHttp()
    {
        var client=new HttpClient { Timeout=TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskBridge/"+DisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    public static async Task<ReleaseUpdate?> Check(CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        // Public release assets do not consume GitHub's unauthenticated API quota.
        using var manifest=await Http.GetAsync($"https://github.com/{Repository}/releases/latest/download/DeskBridge-update.json",HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        if(manifest.IsSuccessStatusCode) {
            if(manifest.Content.Headers.ContentLength>16384) throw new IOException("Invalid update manifest.");
            await using var input=await manifest.Content.ReadAsStreamAsync(timeout.Token);
            var buffer=new byte[16385]; int length=0;
            while(length<buffer.Length) {
                int count=await input.ReadAsync(buffer.AsMemory(length),timeout.Token);
                if(count==0) break;
                length+=count;
            }
            if(length>16384) throw new IOException("Invalid update manifest.");
            using var info=JsonDocument.Parse(buffer.AsMemory(0,length));
            var entry=info.RootElement;
            var releaseTag=entry.GetProperty("tag").GetString() ?? "";
            if(!Version.TryParse(entry.GetProperty("version").GetString(),out var releaseVersion) || releaseTag!="v"+releaseVersion.ToString())
                throw new IOException("Invalid update version.");
            if(releaseVersion<=Current) return null;
            return new ReleaseUpdate(releaseVersion,releaseTag,
                ValidateUrl($"https://github.com/{Repository}/releases/download/{releaseTag}/{Asset}",releaseTag,Asset),
                ValidateUrl($"https://github.com/{Repository}/releases/download/{releaseTag}/{Asset}.sha256",releaseTag,Asset+".sha256"));
        }
        if(manifest.StatusCode!=HttpStatusCode.NotFound) manifest.EnsureSuccessStatusCode();
        // Older releases had no manifest. Keep a fallback for that transition.
        using var response=await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",timeout.Token);
        if(response.StatusCode==HttpStatusCode.NotFound) throw new IOException("No published release is available yet.");
        if(response.StatusCode==HttpStatusCode.Forbidden || (int)response.StatusCode==429)
            throw new IOException("GitHub's request limit was reached. Try again later.");
        response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root=json.RootElement;
        if(root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag=root.GetProperty("tag_name").GetString() ?? "";
        if(!Version.TryParse(tag.TrimStart('v'),out var version) || version<=Current) return null;
        string? binary=null,checksum=null;
        foreach(var asset in root.GetProperty("assets").EnumerateArray()) {
            var name=asset.GetProperty("name").GetString();
            if(name==Asset) binary=asset.GetProperty("browser_download_url").GetString();
            if(name==Asset+".sha256") checksum=asset.GetProperty("browser_download_url").GetString();
        }
        if(binary==null || checksum==null) throw new IOException("The latest release is missing its executable or checksum. Try again after publishing finishes.");
        return new ReleaseUpdate(version,tag,ValidateUrl(binary,tag,Asset),ValidateUrl(checksum,tag,Asset+".sha256"));
    }
    static Uri ValidateUrl(string value,string tag,string name)
    {
        var expected=$"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri) || !string.Equals(uri.AbsoluteUri,expected,StringComparison.Ordinal))
            throw new IOException("GitHub returned an unexpected download address.");
        return uri;
    }
    public static async Task<string> Download(ReleaseUpdate release,IProgress<int> progress,CancellationToken ct)
    {
        var folder=Path.Combine(Root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string staged=Path.Combine(folder,"update.exe");
        using var checksumResponse=await Http.GetAsync(release.Checksum,HttpCompletionOption.ResponseHeadersRead,ct);
        checksumResponse.EnsureSuccessStatusCode();
        if(checksumResponse.Content.Headers.ContentLength>4096) throw new IOException("Invalid checksum file.");
        await using var checksumStream=await checksumResponse.Content.ReadAsStreamAsync(ct);
        var checksumBytes=new byte[4097]; int checksumLength=0;
        while(checksumLength<checksumBytes.Length) {
            var n=await checksumStream.ReadAsync(checksumBytes.AsMemory(checksumLength),ct);
            if(n==0) break;
            checksumLength+=n;
        }
        if(checksumLength>4096) throw new IOException("Invalid checksum file.");
        var checksumParts=System.Text.Encoding.UTF8.GetString(checksumBytes,0,checksumLength).Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        var checksum=checksumParts.Length>0 ? checksumParts[0] : "";
        if(checksum.Length!=64 || !checksum.All(Uri.IsHexDigit)) throw new IOException("Invalid checksum file.");
        using var response=await Http.GetAsync(release.Binary,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        long total=response.Content.Headers.ContentLength ?? 0;
        if(total>400_000_000) throw new IOException("The update exceeds the supported size.");
        await using(var input=await response.Content.ReadAsStreamAsync(ct))
        await using(var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)) {
            byte[] buffer=new byte[81920]; long count=0; int n;
            while((n=await input.ReadAsync(buffer,ct))!=0) {
                count+=n;
                if(count>400_000_000) throw new IOException("The update exceeds the supported size.");
                await output.WriteAsync(buffer.AsMemory(0,n),ct);
                progress.Report(total>0 ? (int)Math.Min(100,count*100/total) : -1);
            }
        }
        if(!string.Equals(await Hash(staged,ct),checksum,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The downloaded update failed verification. Your installed app was not changed.");
        var fileVersion=FileVersionInfo.GetVersionInfo(staged).FileVersion;
        if(!Version.TryParse(fileVersion,out var downloadedVersion) || downloadedVersion!=Normalize(release.Version))
            throw new IOException("The downloaded app version does not match the release.");
        var target=Environment.ProcessPath ?? throw new IOException("Cannot locate the current executable.");
        if(!string.Equals(Path.GetFileName(target),"DeskBridge.exe",StringComparison.OrdinalIgnoreCase))
            throw new IOException("Keep the app named DeskBridge.exe to enable automatic replacement.");
        // Copy the running version as a helper so the downloaded executable remains replaceable.
        File.Copy(target,Path.Combine(folder,"helper.exe"));
        using var process=Process.GetCurrentProcess();
        var job=new UpdateJob(process.Id,process.StartTime.ToUniversalTime().Ticks,target,checksum);
        var jobPath=Path.Combine(folder,"job.json");
        await File.WriteAllTextAsync(jobPath,JsonSerializer.Serialize(job),ct);
        return jobPath;
    }
    static Version Normalize(Version v) => new(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
    static async Task<string> Hash(string path,CancellationToken ct)
    {
        await using var file=File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file,ct));
    }
    public static void Launch(string jobPath)
    {
        var start=new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(jobPath)!,"helper.exe")) { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(jobPath);
        if(Process.Start(start)==null) throw new IOException("Could not start the updater.");
    }
    public static void Apply(string jobPath)
    {
        try {
            var folder=Path.GetDirectoryName(Path.GetFullPath(jobPath))!;
            if(!string.Equals(Path.GetDirectoryName(folder),Path.GetFullPath(Root),StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(folder),"N",out _)
                || !string.Equals(Environment.ProcessPath,Path.Combine(folder,"helper.exe"),StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(jobPath)!="job.json") throw new IOException("Invalid updater location.");
            var job=JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(jobPath)) ?? throw new IOException("Invalid update job.");
            var target=Path.GetFullPath(job.Target);
            if(!Path.IsPathFullyQualified(job.Target) || !string.Equals(Path.GetFileName(target),"DeskBridge.exe",StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid update destination.");
            var staged=Path.Combine(folder,"update.exe");
            if(!string.Equals(Hash(staged,CancellationToken.None).GetAwaiter().GetResult(),job.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged update failed verification.");
            // The exact process must exit; never kill a running app to install an update.
            Process? parent=null;
            try { parent=Process.GetProcessById(job.ParentId); } catch(ArgumentException) { }
            using(parent) {
                if(parent!=null) {
                    try {
                        if(!parent.HasExited && parent.StartTime.ToUniversalTime().Ticks==job.ParentStarted) {
                            if(!string.Equals(parent.MainModule?.FileName,target,StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected parent application.");
                            if(!parent.WaitForExit(45000)) throw new IOException("DeskBridge did not close. Your installed app was not changed.");
                        }
                    } catch(InvalidOperationException) when(parent.HasExited) { }
                    catch(System.ComponentModel.Win32Exception) when(parent.HasExited) { }
                }
            }
            var backup=target+".previous";
            Exception? last=null;
            for(int attempt=0;attempt<60;attempt++) {
                try {
                    File.Replace(staged,target,backup,true); last=null; break;
                } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { last=ex; Thread.Sleep(500); }
            }
            if(last!=null) throw new IOException("Could not replace DeskBridge. Close other copies and keep the app in a writable folder. The existing app is still available.",last);
            try {
                if(Process.Start(new ProcessStartInfo(target) { UseShellExecute=true, WorkingDirectory=Path.GetDirectoryName(target)! })==null)
                    throw new IOException("Could not restart DeskBridge.");
            } catch {
                File.Copy(backup,target,true);
                throw new IOException("Could not restart the update. The previous executable has been restored; open DeskBridge again.");
            }
        } catch(Exception ex) {
            MessageBox.Show(ex.Message,"DeskBridge update",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }
    public static void CleanOldDownloads()
    {
        // Only remove our own abandoned staging folders, never installation folders or backups.
        _=Task.Run(() => {
            try {
                if(!Directory.Exists(Root)) return;
                foreach(var folder in Directory.GetDirectories(Root)) {
                    if(!Guid.TryParseExact(Path.GetFileName(folder),"N",out _)) continue;
                    if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0) continue;
                    if(Directory.GetLastWriteTimeUtc(folder)>DateTime.UtcNow.AddDays(-2)) continue;
                    try {
                        foreach(var name in new[] { "helper.exe","update.exe","job.json" }) File.Delete(Path.Combine(folder,name));
                        Directory.Delete(folder,false);
                    } catch { }
                }
            } catch { }
        });
    }
}
