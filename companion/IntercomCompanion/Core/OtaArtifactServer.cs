using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IntercomCompanion.Core;

/// <summary>Short-lived, single-artifact HTTP endpoint. It exists only while
/// an operator runs an OTA batch; devices never listen for HTTP themselves.</summary>
internal sealed class OtaArtifactServer : IAsyncDisposable
{
    private readonly OtaPackage package;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<IPAddress, byte> allowedClients = new();
    private readonly TaskCompletionSource<string> firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // 64 unpredictable bits are more than sufficient for a short-lived,
    // source-IP-scoped LAN endpoint and leave room for the longest valid
    // variable-length P-256 DER signature in the 320-byte PTT1 offer.
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    private readonly Task acceptTask;

    public OtaArtifactServer(IPAddress localAddress, OtaPackage package)
    {
        this.package = package;
        listener = new TcpListener(localAddress, 0);
        listener.Start(4);
        acceptTask = Task.Run(AcceptLoopAsync);
    }

    public Uri UrlFor(Peer peer)
    {
        allowedClients.TryAdd(peer.Endpoint.Address, 0);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        return new Uri($"http://{endpoint.Address}:{endpoint.Port}/{token}/firmware.bin");
    }

    /// <summary>Completes once the offered device has actually connected to the
    /// temporary server. Acceptance of the UDP offer alone is not sufficient:
    /// it only says that the device queued the work.</summary>
    public Task<string> WaitForFirstRequestAsync(CancellationToken cancellationToken) =>
        firstRequest.Task.WaitAsync(cancellationToken);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                _ = Task.Run(() => ServeAsync(client, stopping.Token));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            using var stream = client.GetStream();
            string? requestLine;
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                requestLine = await reader.ReadLineAsync(cancellationToken);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken))) { }
            }
            catch (IOException) { return; }
            var expectedPath = $"/{token}/firmware.bin";
            var good = allowedClients.ContainsKey(remote) && requestLine is not null &&
                       requestLine.Equals($"GET {expectedPath} HTTP/1.1", StringComparison.Ordinal);
            if (!good)
            {
                await WriteHeaderAsync(stream, "404 Not Found", 0, cancellationToken);
                return;
            }
            firstRequest.TrySetResult(remote.ToString());
            await WriteHeaderAsync(stream, "200 OK", package.Size, cancellationToken);
            await using var image = new FileStream(package.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await image.CopyToAsync(stream, 32 * 1024, cancellationToken);
        }
    }

    private static Task WriteHeaderAsync(Stream stream, string status, long length, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n"), cancellationToken).AsTask();

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();
        firstRequest.TrySetCanceled(stopping.Token);
        try { await acceptTask; } catch (OperationCanceledException) { }
        stopping.Dispose();
    }
}
