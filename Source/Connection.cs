using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace DeskBridge;

internal enum Kind : byte { Ping=1, Hello=2, Start=3, Release=4, Move=5, Key=6, Button=7, Wheel=8, Cursor=9, Display=10 }
internal record struct Packet(Kind Kind, int A=0, int B=0, int C=0, int D=0);

internal sealed class Connection : IDisposable
{
    public const int Port=45839;
    readonly TcpClient tcp;
    readonly SslStream stream;
    readonly CancellationTokenSource cancel=new();
    readonly Channel<Packet> queue=Channel.CreateBounded<Packet>(new BoundedChannelOptions(2048) {
        SingleReader=true, FullMode=BoundedChannelFullMode.Wait });
    long lastRead=Stopwatch.GetTimestamp();
    int disposed;
    public event Action<Packet>? Received;
    public event Action? Closed;
    public bool Alive => Volatile.Read(ref disposed)==0;
    Connection(TcpClient client, SslStream ssl) { tcp=client; stream=ssl; }

    public static X509Certificate2 Certificate()
    {
        using var rsa=RSA.Create(2048);
        var request=new CertificateRequest("CN=DeskBridge", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx),(string?)null,X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
    public static string PairCode(X509Certificate2 cert, byte[] secret) =>
        "DB1-"+Convert.ToHexString(SHA256.HashData(cert.RawData))+"-"+Convert.ToHexString(secret);

    public static async Task<Connection> Accept(TcpClient tcp, X509Certificate2 cert, byte[] secret, CancellationToken ct)
    {
        tcp.NoDelay=true;
        var ssl=new SslStream(tcp.GetStream(), false);
        try {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate=cert, EnabledSslProtocols=SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
            var presented=new byte[32];
            await ssl.ReadExactlyAsync(presented, timeout.Token);
            if (!CryptographicOperations.FixedTimeEquals(presented,secret)) throw new AuthenticationException("Pairing code did not match.");
            await ssl.WriteAsync(new byte[] { 1 }, timeout.Token);
            return new Connection(tcp,ssl);
        } catch { ssl.Dispose(); tcp.Dispose(); throw; }
    }
    public static async Task<Connection> Connect(string host, string code, CancellationToken ct)
    {
        var parts=code.Trim().Split('-');
        if(parts.Length!=3 || parts[0]!="DB1" || parts[1].Length!=64 || parts[2].Length!=64)
            throw new ArgumentException("Paste the complete pairing code from the Legion Go.");
        byte[] fingerprint, secret;
        try { fingerprint=Convert.FromHexString(parts[1]); secret=Convert.FromHexString(parts[2]); }
        catch { throw new ArgumentException("The pairing code contains invalid characters."); }
        var tcp=new TcpClient { NoDelay=true };
        SslStream? ssl=null;
        try {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await tcp.ConnectAsync(host.Trim(),Port,timeout.Token);
            ssl=new SslStream(tcp.GetStream(),false,(_,cert,_,_) => cert!=null &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()),fingerprint));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost="DeskBridge", EnabledSslProtocols=SslProtocols.Tls12 | SslProtocols.Tls13 },timeout.Token);
            await ssl.WriteAsync(secret, timeout.Token);
            var response=new byte[1];
            await ssl.ReadExactlyAsync(response,timeout.Token);
            if(response[0]!=1) throw new AuthenticationException("Pairing failed.");
            return new Connection(tcp,ssl);
        } catch { ssl?.Dispose(); tcp.Dispose(); throw; }
    }
    public void Start() { _=ReadLoop(); _=WriteLoop(); _=Watchdog(); }
    public bool Send(Packet packet)
    {
        if(!Alive) return false;
        if(queue.Writer.TryWrite(packet)) return true;
        Dispose(); return false;
    }
    async Task ReadLoop()
    {
        var bytes=new byte[17];
        try {
            while(!cancel.IsCancellationRequested) {
                await stream.ReadExactlyAsync(bytes,cancel.Token);
                Interlocked.Exchange(ref lastRead,Stopwatch.GetTimestamp());
                var p=new Packet((Kind)bytes[0], BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(5)),BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(9)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(13)));
                if(!Enum.IsDefined(p.Kind)) throw new IOException("Unsupported message.");
                if(p.Kind!=Kind.Ping) Received?.Invoke(p);
            }
        } catch { } finally { Dispose(); }
    }
    async Task WriteLoop()
    {
        var bytes=new byte[17];
        try {
            await foreach(var p in queue.Reader.ReadAllAsync(cancel.Token)) {
                bytes[0]=(byte)p.Kind;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1),p.A);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5),p.B);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9),p.C);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13),p.D);
                await stream.WriteAsync(bytes,cancel.Token);
            }
        } catch { } finally { Dispose(); }
    }
    async Task Watchdog()
    {
        try {
            while(!cancel.IsCancellationRequested) {
                await Task.Delay(800,cancel.Token);
                if(Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRead))>TimeSpan.FromSeconds(4)) break;
                Send(new Packet(Kind.Ping));
            }
        } catch { } finally { Dispose(); }
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0) return;
        cancel.Cancel(); queue.Writer.TryComplete(); tcp.Dispose(); stream.Dispose();
        Closed?.Invoke();
    }
}
