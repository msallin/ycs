using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Ycs.Blazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<RoomPage>("#app");
await builder.Build().RunAsync();
