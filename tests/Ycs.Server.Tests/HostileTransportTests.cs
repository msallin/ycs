using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Configuration;
using Ycs.Server.Rooms;
using Ycs.Server.Tests.Support;
using Ycs.Server.Transports;

namespace Ycs.Server.Tests;

/// <summary>Clients that stop reading, and requests the transports refuse.</summary>
[TestClass]
public class HostileTransportTests
{
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task RunAsync_SessionClosedWhileASendWaits_AbortsTheSocketAfterTheCloseTimeout()
    {
        var room = new Room("r");
        var socket = new HostileWebSocket();
        var session = new Session(room);
        var connection = new WebSocketEndpoint.Connection(socket, session, 1024, TimeSpan.FromMilliseconds(100));
        var running = connection.RunAsync(CancellationToken.None);
        while (socket.Sends == 0)
        {
            await Task.Delay(10);
        }

        session.Close();

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(socket.Aborted);
    }

    // Kestrel routes "/ws/y/." as "/ws/y/" and the raw target's last segment is "."; other dot segments, encoded
    // or not, are removed before routing and not found.
    [TestMethod]
    public async Task Connect_DotSegmentAsRoomName_IsRefused()
    {
        const string path = "/ws/y/.";
        await using var server = await ServerHost.StartAsync();
        using var client = new ClientWebSocket();
        client.Options.CollectHttpResponseDetails = true;
        var uri = new Uri(server.WebSocketBase.GetLeftPart(UriPartial.Authority) + path, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });

        await Assert.ThrowsExactlyAsync<WebSocketException>(() => client.ConnectAsync(uri, CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.BadRequest, client.HttpStatusCode);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("63")]
    public void From_MaxMessageBytesBelow64_Throws(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("WebSocket:MaxMessageBytes", value)]).Build();

        Assert.ThrowsExactly<InvalidOperationException>(() => WebSocketEndpointOptions.From(configuration));
    }
}
