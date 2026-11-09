using System.IO.Enumeration;

namespace DownloadDock.Core.Downloads;

/// <summary>
/// Enumerates a folder and returns the newest completed downloads.
/// Uses <see cref="FileSystemEnumerable{T}"/> so file times come straight from the
/// directory listing (no extra stat per file) and keeps only the top-K entries in a
/// heap, which keeps scans of folders with tens of thousands of files fast and cheap.
/// </summary>
public static class FolderScanner
{
    public sealed record ScanResult(IReadOnlyList<DownloadEntry> Newest, int TotalVisibleCount);

    public static ScanResult Scan(string folder, int maxCount, bool includeSubfolders, CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0 || string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return new ScanResult(Array.Empty<DownloadEntry>(), 0);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary,
            ReturnSpecialDirectories = false,
            // Limit recursion so a huge tree (e.g. an extracted SDK) can't stall the scan.
            MaxRecursionDepth = includeSubfolders ? 4 : 0,
        };

        var enumerable = new FileSystemEnumerable<DownloadEntry>(
            folder,
            (ref FileSystemEntry e) => new DownloadEntry(
                e.ToFullPath(),
                e.FileName.ToString(),
                e.Length,
                e.CreationTimeUtc.UtcDateTime,
                e.LastWriteTimeUtc.UtcDateTime),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory && DownloadFileFilter.ShouldShow(e.FileName.ToString(), e.Attributes),
        };

        // Min-heap on AddedUtc holding the newest maxCount entries.
        var heap = new PriorityQueue<DownloadEntry, long>(maxCount + 1);
        var total = 0;
        try
        {
            foreach (var entry in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;
                var key = entry.AddedUtc.Ticks;
                if (heap.Count < maxCount)
                {
                    heap.Enqueue(entry, key);
                }
                else if (heap.TryPeek(out _, out var minKey) && key > minKey)
                {
                    heap.EnqueueDequeue(entry, key);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Folder deleted mid-scan; return what we have.
        }
        catch (IOException)
        {
            // Device removed / network share dropped; return what we have.
        }

        var result = new List<DownloadEntry>(heap.Count);
        while (heap.Count > 0)
            result.Add(heap.Dequeue());

        result.Reverse(); // newest first
        return new ScanResult(SortNewestFirst(result), total);
    }

    /// <summary>Stable newest-first ordering with name as a deterministic tie-breaker.</summary>
    public static IReadOnlyList<DownloadEntry> SortNewestFirst(IEnumerable<DownloadEntry> entries)
        => entries
            .OrderByDescending(e => e.AddedUtc)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
