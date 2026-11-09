namespace DownloadDock.Core.Tests;

/// <summary>Timing-sensitive tests (timers, file watchers) run alone so a busy thread pool can't starve them.</summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public sealed class TimingCollection;
