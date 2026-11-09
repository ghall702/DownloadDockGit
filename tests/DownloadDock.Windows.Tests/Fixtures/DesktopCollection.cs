namespace DownloadDock.Windows.Tests;

/// <summary>
/// Tests that show windows or move the real mouse run one at a time, so an injected drag can't
/// wander over another test's window.
/// </summary>
[CollectionDefinition("Desktop", DisableParallelization = true)]
public sealed class DesktopCollection;
