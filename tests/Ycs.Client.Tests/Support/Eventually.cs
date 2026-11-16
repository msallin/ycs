namespace Ycs.Client.Tests.Support;

internal static class Eventually
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, checked under the provider's gate every 10 ms; fails with
    /// <paramref name="what"/> after 10 s.
    /// </summary>
    public static async Task HoldsAsync(WebsocketProvider provider, Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            lock (provider.Gate)
            {
                if (condition())
                {
                    return;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Not within {Timeout.TotalSeconds} s: {what}.");
            }

            await Task.Delay(10);
        }
    }
}
