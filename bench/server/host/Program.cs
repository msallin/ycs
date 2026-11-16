using Ycs.Server;
using Ycs.Server.Rooms;

var app = ServerApp.Create(args);
var rooms = app.Services.GetRequiredService<RoomRegistry>();

app.MapGet("/bench/memory", () =>
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    var info = GC.GetGCMemoryInfo();
    return Results.Text(
        $"{{\"managedBytes\":{GC.GetTotalMemory(false)},\"heapSizeBytes\":{info.HeapSizeBytes},"
        + $"\"committedBytes\":{info.TotalCommittedBytes},\"rooms\":{rooms.Rooms.Count()}}}",
        "application/json");
});

app.Run();
