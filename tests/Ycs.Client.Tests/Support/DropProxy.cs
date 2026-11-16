using System.Net;
using System.Net.Sockets;

namespace Ycs.Client.Tests.Support;

/// <summary>
/// A TCP relay on a free loopback port in front of the server, which can drop every connection through it and refuse
/// new ones: the server going away, as a client sees it, while the server keeps its rooms.
/// </summary>
internal sealed class DropProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly int target;
    private readonly Lock gate = new();
    private readonly List<(TcpClient Client, TcpClient Server)> pairs = [];
    private readonly Task accepting;
    private bool refusing;

    public DropProxy(int target)
    {
        this.target = target;
        listener.Start();
        accepting = AcceptLoopAsync();
    }

    /// <summary>The URL a provider is given: <c>ws://127.0.0.1:port/ws</c> of this proxy.</summary>
    public Uri WebSocketBase => new($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/ws");

    /// <summary>Drops every open connection and refuses new ones until <see cref="Allow"/>.</summary>
    public void DropAndRefuse()
    {
        lock (gate)
        {
            refusing = true;
            foreach (var (client, server) in pairs)
            {
                // A reset, not a close: the client sees the connection break. A pair may have ended already.
                client.Client?.LingerState = new LingerOption(true, 0);
                client.Dispose();
                server.Dispose();
            }

            pairs.Clear();
        }
    }

    public void Allow()
    {
        lock (gate)
        {
            refusing = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        DropAndRefuse();
        listener.Stop();
        await accepting;
    }

    private static async Task PumpAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
        }
        finally
        {
            from.Dispose();
            to.Dispose();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return;
            }

            var server = new TcpClient();
            try
            {
                await server.ConnectAsync(IPAddress.Loopback, target);
            }
            catch (SocketException)
            {
                client.Dispose();
                server.Dispose();
                continue;
            }

            lock (gate)
            {
                if (refusing)
                {
                    client.Dispose();
                    server.Dispose();
                    continue;
                }

                pairs.Add((client, server));
            }

            _ = PumpAsync(client, server);
            _ = PumpAsync(server, client);
        }
    }
}
