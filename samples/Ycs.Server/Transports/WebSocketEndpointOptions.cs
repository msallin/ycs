namespace Ycs.Server.Transports;

/// <summary>The settings of the WebSocket transport, from the configuration section <c>WebSocket</c>.</summary>
internal sealed record WebSocketEndpointOptions
{
    public const int DefaultMaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>The largest message a client may send; a larger one closes the connection with 1009.</summary>
    public int MaxMessageBytes { get; init; } = DefaultMaxMessageBytes;

    /// <summary>How often the server pings an idle client (y-websocket's server: every 30 s).</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the server waits for the pong before it drops the connection.</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a closed session's connection may take to finish the send under way and to have its close frame
    /// answered, before the server drops it.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The least <see cref="MaxMessageBytes"/>: a step 1 of a document of a few clients.</summary>
    public const int MinMessageBytes = 64;

    public static WebSocketEndpointOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("WebSocket");
        var defaults = new WebSocketEndpointOptions();
        var options = new WebSocketEndpointOptions
        {
            MaxMessageBytes = section.GetValue(nameof(MaxMessageBytes), defaults.MaxMessageBytes),
            KeepAliveInterval = section.GetValue(nameof(KeepAliveInterval), defaults.KeepAliveInterval),
            KeepAliveTimeout = section.GetValue(nameof(KeepAliveTimeout), defaults.KeepAliveTimeout),
            CloseTimeout = section.GetValue(nameof(CloseTimeout), defaults.CloseTimeout),
        };
        return options.MaxMessageBytes >= MinMessageBytes
            ? options
            : throw new InvalidOperationException($"WebSocket:MaxMessageBytes is {options.MaxMessageBytes}; it must be at least {MinMessageBytes}.");
    }
}
