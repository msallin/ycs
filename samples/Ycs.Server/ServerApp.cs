using Ycs.Server.Awareness;
using Ycs.Server.Rooms;
using Ycs.Server.Transports;

namespace Ycs.Server;

internal static class ServerApp
{
    /// <summary>
    /// The server, built and not started. It listens where the standard ASP.NET settings say (<c>--urls</c>,
    /// <c>ASPNETCORE_URLS</c>); the WebSocket settings come from the section <c>WebSocket</c>, whose message limit
    /// also bounds the SignalR hub (<c>/hub</c>). It serves the files of its web root (<c>wwwroot</c>, or
    /// <c>--webroot</c>, e.g. a published Blazor client) as static files.
    /// </summary>
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = WebSocketEndpointOptions.From(builder.Configuration);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RoomRegistry>();
        builder.Services.AddSyncHub(options);
        builder.Services.AddHostedService<AwarenessSweep>();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = options.KeepAliveInterval,
            KeepAliveTimeout = options.KeepAliveTimeout,
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapWebSocketEndpoint();
        app.MapSyncHub();
        return app;
    }
}
