using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>The server in this process on a free loopback port, as <see cref="ServerApp.Create"/> builds it.</summary>
internal sealed class ServerHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private ServerHost(WebApplication app, Uri webSocketBase)
    {
        this.app = app;
        WebSocketBase = webSocketBase;
    }

    /// <summary>The URL a y-websocket provider is given: <c>ws://127.0.0.1:port/ws</c>.</summary>
    public Uri WebSocketBase { get; }

    public RoomRegistry Rooms => app.Services.GetRequiredService<RoomRegistry>();

    public static async Task<ServerHost> StartAsync(params string[] settings)
    {
        var app = ServerApp.Create(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning", .. settings]);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new ServerHost(app, new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws"));
    }

    public Uri RoomUri(string room) => new($"{WebSocketBase}/{room}");

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
