using Microsoft.AspNetCore.SignalR;

namespace Ycs.Server.Transports;

internal static class SyncHubSetup
{
    /// <summary>SignalR with the JSON protocol, its frame limit derived from <see cref="WebSocketEndpointOptions.MaxMessageBytes"/>.</summary>
    public static IServiceCollection AddSyncHub(this IServiceCollection services, WebSocketEndpointOptions options)
    {
        services.AddSignalR(hub =>
        {
            hub.MaximumReceiveMessageSize = SyncHub.MaxFrameBytes(options.MaxMessageBytes);
            hub.MaximumParallelInvocationsPerClient = 1;
        });
        return services;
    }

    /// <summary>
    /// Maps the hub at <paramref name="pattern"/>. A request without a valid room (negotiate or connect) is answered
    /// 400 before SignalR sees it.
    /// </summary>
    public static HubEndpointConventionBuilder MapSyncHub(this IEndpointRouteBuilder endpoints, string pattern = "/hub")
    {
        var builder = endpoints.MapHub<SyncHub>(pattern);
        builder.Add(endpoint =>
        {
            var next = endpoint.RequestDelegate!;
            endpoint.RequestDelegate = context =>
            {
                if (SyncHub.RoomName(context) is null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Task.CompletedTask;
                }

                return next(context);
            };
        });
        return builder;
    }
}
