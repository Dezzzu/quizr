using Microsoft.Extensions.Hosting;

// Composition root. Generic host, not ASP.NET Core — nothing listens on a port
// in phase 1. See STACK.md before adding anything here.
var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();
await host.RunAsync();
