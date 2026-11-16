using System.Net;
using System.Net.WebSockets;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

[TestClass]
public class WebSocketEndpointTests
{
    [TestMethod]
    public async Task Connect_TwoSockets_TextTypedInOneAppearsInTheOther()
    {
        await using var server = await ServerHost.StartAsync();
        await using var a = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);
        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);

        a.Insert(0, "hello");

        await b.WaitUntilAsync(c => c.Text == "hello");
    }

    [TestMethod]
    public async Task Connect_RoomWithContent_NewSocketSyncsIt()
    {
        await using var server = await ServerHost.StartAsync();
        await using var a = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);
        a.Insert(0, "before");
        await WaitForRoomTextAsync(server, "doc", "before");

        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);

        await b.WaitUntilAsync(c => c.Text == "before");
    }

    [TestMethod]
    public async Task Awareness_StateOfOneReachesTheOtherAndTheSender()
    {
        await using var server = await ServerHost.StartAsync();
        await using var a = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);
        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);

        a.Send(Messages.Awareness(1, 1, "{\"user\":\"a\"}"));

        await Task.WhenAll(
            a.WaitUntilAsync(c => c.HasAwareness(1, out _)),
            b.WaitUntilAsync(c => c.HasAwareness(1, out var s) && s == "{\"user\":\"a\"}"));
    }

    [TestMethod]
    public async Task Abort_ClientVanishes_ItsAwarenessIsRemovedAndItLeavesTheRoom()
    {
        await using var server = await ServerHost.StartAsync();
        await using var a = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);
        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);
        a.Send(Messages.Awareness(1, 1, "{}"));
        await b.WaitUntilAsync(c => c.HasAwareness(1, out _));

        a.Abort();

        await b.WaitUntilAsync(c => !c.HasAwareness(1, out _));
        Assert.AreEqual(1, server.Rooms.GetOrCreate("doc").SessionCount);
    }

    [TestMethod]
    public async Task Receive_OversizeMessage_Closes1009()
    {
        await using var server = await ServerHost.StartAsync("--WebSocket:MaxMessageBytes", "1024");
        await using var client = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);

        client.Send(new byte[1025]);

        Assert.AreEqual(WebSocketCloseStatus.MessageTooBig, await client.Closed.WaitAsync(SocketClient.Timeout));
    }

    [TestMethod]
    public async Task Receive_MessageAtTheLimit_IsAccepted()
    {
        await using var server = await ServerHost.StartAsync("--WebSocket:MaxMessageBytes", "1024");
        await using var a = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);
        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);
        var filler = 1024 - Messages.Awareness(1, 1, "\"\"").Length;

        // The two length prefixes grow by one byte each past 127.
        var message = Messages.Awareness(1, 1, "\"" + new string('x', filler - 2) + "\"");
        Assert.AreEqual(1024, message.Length);

        a.Send(message);

        await b.WaitUntilAsync(c => c.HasAwareness(1, out _));
    }

    [TestMethod]
    public async Task Receive_TextMessage_Closes1003()
    {
        await using var server = await ServerHost.StartAsync();
        await using var client = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);

        client.Send("hello"u8.ToArray(), WebSocketMessageType.Text);

        Assert.AreEqual(WebSocketCloseStatus.InvalidMessageType, await client.Closed.WaitAsync(SocketClient.Timeout));
    }

    [TestMethod]
    public async Task Receive_Garbage_Closes1002AndTheOthersKeepWorking()
    {
        await using var server = await ServerHost.StartAsync();
        await using var bad = await SocketClient.ConnectAsync(server.RoomUri("doc"), 1);
        await using var a = await SocketClient.ConnectAsync(server.RoomUri("doc"), 2);
        await using var b = await SocketClient.ConnectAsync(server.RoomUri("doc"), 3);

        bad.Send([0, 2, 5, 0xFF, 0xFF]);
        var status = await bad.Closed.WaitAsync(SocketClient.Timeout);
        a.Insert(0, "still");
        await b.WaitUntilAsync(c => c.Text == "still");

        Assert.AreEqual(WebSocketCloseStatus.ProtocolError, status);
    }

    [TestMethod]
    [DataRow("/ws/", HttpStatusCode.NotFound)]
    [DataRow("/ws/a", HttpStatusCode.BadRequest)]
    public async Task Get_PlainHttp_IsRefused(string path, HttpStatusCode expected)
    {
        await using var server = await ServerHost.StartAsync();
        using var http = new HttpClient();

        using var response = await http.GetAsync(new Uri(server.WebSocketBase.ToString().Replace("ws://", "http://", StringComparison.Ordinal).Replace("/ws", path, StringComparison.Ordinal)));

        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task Connect_RoomNameOver256Characters_IsRefused()
    {
        await using var server = await ServerHost.StartAsync();

        var refused = await Assert.ThrowsExactlyAsync<WebSocketException>(() => SocketClient.ConnectAsync(server.RoomUri(new string('r', 257)), 1));

        Assert.AreEqual(WebSocketError.NotAWebSocket, refused.WebSocketErrorCode);
    }

    [TestMethod]
    public async Task Connect_EncodedRoomName_IsDecodedOnce()
    {
        await using var server = await ServerHost.StartAsync();
        await using var client = await SocketClient.ConnectAsync(server.RoomUri("a%2Fb%2541"), 1);
        client.Insert(0, "x");

        await WaitForRoomTextAsync(server, "a/b%41", "x");
    }

    private static async Task WaitForRoomTextAsync(ServerHost server, string room, string text)
    {
        using var timeout = new CancellationTokenSource(SocketClient.Timeout);
        while (Messages.TextOf(server.Rooms.GetOrCreate(room).EncodeStateAsUpdate()) != text)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
