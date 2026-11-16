using Ycs.Client.Tests.Support;

namespace Ycs.Client.Tests;

[TestClass]
public class WebsocketProviderTests
{
    private static ServerProcess? server;

    private static ServerProcess Server => server ?? throw new InvalidOperationException("The server has not started.");

    [ClassInitialize]
    public static async Task StartServerAsync(TestContext context) => server = await ServerProcess.StartAsync();

    [ClassCleanup]
    public static void StopServer() => server?.Dispose();

    [TestMethod]
    public async Task Connect_EmptyRoom_IsConnectedAndSynced()
    {
        await using var client = Open(Server.WebSocketBase, NewRoom());

        await Eventually.HoldsAsync(client, () => client.Status == ConnectionStatus.Connected && client.Synced, "connected and synced");
    }

    [TestMethod]
    public async Task Connect_RoomWithText_ReceivesIt()
    {
        var room = NewRoom();
        await using var writer = Open(Server.WebSocketBase, room);
        await Eventually.HoldsAsync(writer, () => writer.Synced, "the writer synced");
        Insert(writer, 0, "hello");
        await using var reader = Open(Server.WebSocketBase, room);

        await Eventually.HoldsAsync(reader, () => reader.Synced && Text(reader) == "hello", "the reader has the text");
    }

    [TestMethod]
    public async Task Edit_TwoClients_Converge()
    {
        var room = NewRoom();
        await using var a = Open(Server.WebSocketBase, room);
        await using var b = Open(Server.WebSocketBase, room);
        await Eventually.HoldsAsync(a, () => a.Synced, "a synced");
        await Eventually.HoldsAsync(b, () => b.Synced, "b synced");

        Insert(a, 0, "from a;");
        Insert(b, 0, "from b;");

        await Eventually.HoldsAsync(a, () => Text(a).Length == 14, "a has both edits");
        await Eventually.HoldsAsync(b, () => Text(b) == Text(a), "b equals a");
    }

    [TestMethod]
    public async Task Received_RemoteEdit_IsRaisedAfterTheDocumentChanged()
    {
        var room = NewRoom();
        await using var a = Open(Server.WebSocketBase, room);
        await using var b = Open(Server.WebSocketBase, room, connect: false);
        var seen = new List<string>();
        b.Received += () => seen.Add(b.Doc.GetText(SharedText.Name).ToString());
        b.Connect();
        await Eventually.HoldsAsync(b, () => b.Synced, "b synced");

        Insert(a, 0, "x");

        await Eventually.HoldsAsync(b, () => seen.Contains("x"), "b's event saw the text");
    }

    [TestMethod]
    public async Task SetLocalState_TwoClients_EachSeesTheOthersUser()
    {
        var room = NewRoom();
        await using var a = Open(Server.WebSocketBase, room);
        await using var b = Open(Server.WebSocketBase, room);

        a.SetLocalState(Presence.State("ann", "#aa0000"));
        b.SetLocalState(Presence.State("bob", "#0000bb"));

        await Eventually.HoldsAsync(a, () => Users(a).SequenceEqual([new PresenceUser(b.ClientId, "bob", "#0000bb")]), "a sees bob alone");
        await Eventually.HoldsAsync(b, () => Users(b).SequenceEqual([new PresenceUser(a.ClientId, "ann", "#aa0000")]), "b sees ann alone");
    }

    [TestMethod]
    public async Task DisposeAsync_ClientLeaves_TheOtherNoLongerSeesIt()
    {
        var room = NewRoom();
        var a = Open(Server.WebSocketBase, room);
        await using var b = Open(Server.WebSocketBase, room);
        a.SetLocalState(Presence.State("ann", "#aa0000"));
        await Eventually.HoldsAsync(b, () => Users(b).Count == 1, "b sees ann");

        await a.DisposeAsync();

        await Eventually.HoldsAsync(b, () => Users(b).Count == 0, "b no longer sees ann");
    }

    [TestMethod]
    public async Task ServerDrops_EditsWhileOffline_MergeOnReconnect()
    {
        var room = NewRoom();
        await using var proxy = new DropProxy(Server.Port);
        await using var a = Open(proxy.WebSocketBase, room);
        await using var b = Open(proxy.WebSocketBase, room);
        await Eventually.HoldsAsync(a, () => a.Synced, "a synced");
        await Eventually.HoldsAsync(b, () => b.Synced, "b synced");
        Insert(a, 0, "shared;");
        await Eventually.HoldsAsync(b, () => Text(b) == "shared;", "b has a's first edit");

        proxy.DropAndRefuse();
        await Eventually.HoldsAsync(a, () => a.Status != ConnectionStatus.Connected, "a noticed the drop");
        await Eventually.HoldsAsync(b, () => b.Status != ConnectionStatus.Connected, "b noticed the drop");
        Insert(a, 7, "offline a;");
        Insert(b, 0, "offline b;");
        proxy.Allow();

        await Eventually.HoldsAsync(a, () => a.Synced && Text(a).Length == 27, "a has every edit");
        await Eventually.HoldsAsync(b, () => b.Synced && Text(b) == Text(a), "b equals a");
    }

    [TestMethod]
    public async Task ServerDrops_Presence_ComesBackOnReconnect()
    {
        var room = NewRoom();
        await using var proxy = new DropProxy(Server.Port);
        await using var a = Open(proxy.WebSocketBase, room);
        await using var b = Open(proxy.WebSocketBase, room);
        a.SetLocalState(Presence.State("ann", "#aa0000"));
        b.SetLocalState(Presence.State("bob", "#0000bb"));
        await Eventually.HoldsAsync(b, () => Users(b).Count == 1, "b sees ann");

        proxy.DropAndRefuse();
        await Eventually.HoldsAsync(b, () => b.Status != ConnectionStatus.Connected && Users(b).Count == 0, "b forgot ann on the drop");
        proxy.Allow();

        await Eventually.HoldsAsync(a, () => Users(a).Count == 1, "a sees bob again");
        await Eventually.HoldsAsync(b, () => Users(b).Count == 1, "b sees ann again");
    }

    private static string NewRoom() => "client-tests-" + Guid.NewGuid().ToString("N");

    private static WebsocketProvider Open(Uri server, string room, bool connect = true)
    {
        var provider = new WebsocketProvider(server, room, new YDoc());
        if (connect)
        {
            provider.Connect();
        }

        return provider;
    }

    private static void Insert(WebsocketProvider provider, int index, string text)
    {
        lock (provider.Gate)
        {
            provider.Doc.GetText(SharedText.Name).Insert(index, text);
        }
    }

    // Under the gate (Eventually holds it).
    private static string Text(WebsocketProvider provider) => provider.Doc.GetText(SharedText.Name).ToString();

    private static IReadOnlyList<PresenceUser> Users(WebsocketProvider provider) => Presence.Users(provider.RemoteStates);
}
