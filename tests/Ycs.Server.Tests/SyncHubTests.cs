using System.Net;
using System.Net.WebSockets;
using Ycs.Server.Tests.Support;
using Ycs.Server.Transports;

namespace Ycs.Server.Tests;

[TestClass]
public class SyncHubTests
{
    [TestMethod]
    public async Task Connect_TwoHubClients_TextTypedInOneAppearsInTheOther()
    {
        await using var server = await ServerHost.StartAsync();
        await using var a = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 1);
        await using var b = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 2);

        a.Insert(0, "hello");

        await b.WaitUntilAsync(c => c.Text == "hello");
    }

    [TestMethod]
    public async Task Connect_HubAndWebSocketClientInOneRoom_TheySyncBothWays()
    {
        await using var server = await ServerHost.StartAsync();
        await using var hub = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 1);
        await using var socket = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);

        hub.Insert(0, "hub ");
        await socket.WaitUntilAsync(c => c.Text == "hub ");
        socket.Insert(4, "socket");

        await hub.WaitUntilAsync(c => c.Text == "hub socket");
    }

    [TestMethod]
    public async Task Connect_RoomWithContent_NewHubClientSyncsIt()
    {
        await using var server = await ServerHost.StartAsync();
        await using var a = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);
        a.Insert(0, "before");
        await WaitForRoomTextAsync(server, "doc", "before");

        await using var b = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 2);

        await b.WaitUntilAsync(c => c.Text == "before");
    }

    [TestMethod]
    public async Task Connect_EncodedRoomName_IsTheDecodedRoom()
    {
        await using var server = await ServerHost.StartAsync();
        await using var client = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "a/b c&d"), 1);
        client.Insert(0, "x");

        await WaitForRoomTextAsync(server, "a/b c&d", "x");
    }

    [TestMethod]
    public async Task Abort_HubClientVanishes_ItsAwarenessIsRemovedAndItLeavesTheRoom()
    {
        await using var server = await ServerHost.StartAsync();
        await using var a = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 1);
        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);
        a.Send(Messages.Awareness(1, 1, "{\"user\":\"a\"}"));
        await b.WaitUntilAsync(c => c.HasAwareness(1, out var s) && s == "{\"user\":\"a\"}");

        a.Abort();

        await b.WaitUntilAsync(c => !c.HasAwareness(1, out _));
        Assert.AreEqual(1, server.Rooms.GetOrCreate("doc").SessionCount);
    }

    [TestMethod]
    public async Task Send_MessageAtTheLimit_IsAccepted()
    {
        await using var server = await ServerHost.StartAsync("--WebSocket:MaxMessageBytes", "1024");
        await using var a = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 1);
        await using var b = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 2);
        var filler = 1024 - Messages.Awareness(1, 1, "\"\"").Length;

        // The two length prefixes grow by one byte each past 127.
        var message = Messages.Awareness(1, 1, "\"" + new string('x', filler - 2) + "\"");
        Assert.AreEqual(1024, message.Length);

        a.Send(message);

        await b.WaitUntilAsync(c => c.HasAwareness(1, out _));
    }

    [TestMethod]
    [DataRow(1025)]
    [DataRow(4096)]
    public async Task Send_OverTheLimit_ClosesTheConnection(int size)
    {
        await using var server = await ServerHost.StartAsync("--WebSocket:MaxMessageBytes", "1024");
        await using var client = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 1);

        client.Send(new byte[size]);

        await client.Closed.WaitAsync(SocketClient.Timeout);
    }

    [TestMethod]
    public async Task Send_NullMessage_ClosesTheConnection()
    {
        await using var server = await ServerHost.StartAsync();
        await using var client = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 1);

        client.SendRaw("{\"type\":1,\"target\":\"Send\",\"arguments\":[null]}");

        await client.Closed.WaitAsync(SocketClient.Timeout);
    }

    [TestMethod]
    public async Task Send_Garbage_ClosesOnlyItsConnection()
    {
        await using var server = await ServerHost.StartAsync();
        await using var bad = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 1);
        await using var a = await SignalRClient.ConnectAsync(SignalRClient.HubUri(server, "doc"), 2);
        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 3);

        bad.Send([0, 2, 5, 0xFF, 0xFF]);
        await bad.Closed.WaitAsync(SocketClient.Timeout);
        a.Insert(0, "still");

        await b.WaitUntilAsync(c => c.Text == "still");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?room=")]
    [DataRow("?room=a&room=b")]
    [DataRow("?other=a")]
    public async Task Negotiate_WithoutOneValidRoom_Is400(string query)
    {
        await using var server = await ServerHost.StartAsync();
        using var http = new HttpClient();

        using var response = await http.PostAsync(HttpUri(server, "/hub/negotiate" + query), null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow(1, HttpStatusCode.OK)]
    [DataRow(WebSocketEndpoint.MaxRoomNameLength, HttpStatusCode.OK)]
    [DataRow(WebSocketEndpoint.MaxRoomNameLength + 1, HttpStatusCode.BadRequest)]
    public async Task Negotiate_RoomNameLength_IsCheckedAfterDecoding(int length, HttpStatusCode expected)
    {
        await using var server = await ServerHost.StartAsync();
        using var http = new HttpClient();

        // "%41" is one character, "A", once decoded.
        var room = string.Concat(Enumerable.Repeat("%41", length));
        using var response = await http.PostAsync(HttpUri(server, $"/hub/negotiate?negotiateVersion=1&room={room}"), null);

        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task Connect_WithoutRoom_IsRefused()
    {
        await using var server = await ServerHost.StartAsync();

        var refused = await Assert.ThrowsExactlyAsync<WebSocketException>(
            () => SignalRClient.ConnectAsync(new Uri(server.WebSocketBase, "/hub"), 1));

        Assert.AreEqual(WebSocketError.NotAWebSocket, refused.WebSocketErrorCode);
    }

    [TestMethod]
    [DataRow(1023, 1364 + 1024)]
    [DataRow(1024, 1368 + 1024)]
    [DataRow(16 * 1024 * 1024, 22_369_624 + 1024)]
    public void MaxFrameBytes_CoversTheBase64OfTheLimit(int maxMessageBytes, long expected) =>
        Assert.AreEqual(expected, SyncHub.MaxFrameBytes(maxMessageBytes));

    private static Uri HttpUri(ServerHost server, string pathAndQuery) =>
        new(new Uri(server.WebSocketBase.ToString().Replace("ws://", "http://", StringComparison.Ordinal)), pathAndQuery);

    private static async Task WaitForRoomTextAsync(ServerHost server, string room, string text)
    {
        using var timeout = new CancellationTokenSource(SocketClient.Timeout);
        while (Messages.TextOf(server.Rooms.GetOrCreate(room).EncodeStateAsUpdate()) != text)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
