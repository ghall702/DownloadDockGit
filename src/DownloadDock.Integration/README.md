# DownloadDock.Integration

Versioned, dependency-free named-pipe IPC contract for integrating with **DownloadDock** on Windows
(used by Finder for Windows). It contains the protocol constants, message and payload models, error
codes, framing, a hardened server, a client and an auto-reconnecting client, Windows path validation
and registry self-registration helpers.

```csharp
await using var dock = new ResilientIntegrationClient(new IntegrationClientOptions
{
    PipeName = IntegrationEndpoints.DownloadDockPipeName(),
    ClientName = IntegrationProtocol.FinderClientName,
    ClientVersion = "1.0.0",
}, subscribeTo: IntegrationEvents.All);
dock.EventReceived += e => Console.WriteLine($"{e.Name} {e.Payload}");
dock.Start();
// later: await dock.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan);
```

Full reference: `docs/FINDER_INTEGRATION.md` and `docs/FINDER_HANDOFF.md` in the DownloadDock repository.
