using Microsoft.Extensions.Logging;
using Ycs.Server.Rooms;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

[TestClass]
public class RoomLoggingTests
{
    [TestMethod]
    public void Receive_UpdateThatDropsWaitingStructs_LogsAWarningWithoutContent()
    {
        var forger = new YDoc(new YDocOptions { ClientId = 7 });
        var missing = HostileUpdates.Edit(forger, d => d.GetArray("array").Insert(0, [1]));
        var deep = HostileUpdates.Edit(forger, d =>
        {
            var array = d.GetArray("array");
            for (var i = 0; i < 40; i++)
            {
                var inner = new YArray();
                array.Insert(0, [inner]);
                array = inner;
            }
        });
        var logger = new CapturingLogger();
        var room = new Room("r", limits: new UpdateLimits { MaxDepth = 32 }, logger: logger);
        var session = new Session(room);
        session.Start();

        session.Receive(Messages.Update(deep));
        session.Receive(Messages.Update(missing));

        var entry = logger.Entries.Single();
        Assert.AreEqual(
            (LogLevel.Warning, "r", 40L, 1, false),
            (entry.Level, entry.Values["Room"], entry.Values["Structs"], entry.Values["Clients"], session.IsClosed));
    }
}
