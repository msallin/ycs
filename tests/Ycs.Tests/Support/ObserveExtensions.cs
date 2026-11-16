using System;

namespace Ycs;

internal static class ObserveExtensions
{
    /// <summary>Observes a shared type of any kind, for a test that walks the types of a document.</summary>
    public static IDisposable ObserveAny(this YType type, Action<YEvent> handler) => type switch
    {
        YArray array => array.Observe(handler),
        YMap map => map.Observe(handler),
        YText text => text.Observe(handler),
        YXmlFragment fragment => fragment.Observe(handler),
        _ => throw new ArgumentException("not a shared type", nameof(type)),
    };
}
