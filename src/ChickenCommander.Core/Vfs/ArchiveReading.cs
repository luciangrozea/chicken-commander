using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ChickenCommander.Core.Vfs;

/// <summary>
/// Walks the entries of any supported archive in file order, picking the right SharpCompress API:
/// random access for zip/tar/rar, a sequential reader for solid archives and 7z (much faster there),
/// and a stream reader for compressed tarballs (.tar.gz / .tgz / .tar.bz2 / .tar.xz).
/// </summary>
internal static class ArchiveReading
{
    /// <summary>
    /// Just the entries (names, sizes, dates), for browsing. Unlike <see cref="Entries"/> this never decompresses
    /// file data when the archive has a header (zip, 7z, rar, also solid ones). Compressed tarballs have no header
    /// and are read through; <paramref name="ct"/> stops that even in the middle of one huge entry.
    /// </summary>
    public static IEnumerable<IEntry> List(string archivePath, CancellationToken ct = default)
    {
        IArchive? archive = null;
        try
        {
            archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
        }
        catch (Exception ex) when (ex is SharpCompressException or InvalidOperationException or NotSupportedException)
        {
            archive = null;
        }

        if (archive is null)
        {
            using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            using var stream = new CancellableStream(file, ct);
            using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions { LeaveStreamOpen = true });
            while (reader.MoveToNextEntry()) yield return reader.Entry;
            yield break;
        }

        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }
        }
    }

    public static IEnumerable<(IEntry Entry, Func<Stream> Open)> Entries(string archivePath)
    {
        IArchive? archive = null;
        try
        {
            archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
        }
        catch (Exception ex) when (ex is SharpCompressException or InvalidOperationException or NotSupportedException)
        {
            archive = null;
        }

        if (archive is null)
        {
            using var reader = ReaderFactory.OpenReader(archivePath, new ReaderOptions());
            while (reader.MoveToNextEntry()) yield return (reader.Entry, reader.OpenEntryStream);
            yield break;
        }

        using (archive)
        {
            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry()) yield return (reader.Entry, reader.OpenEntryStream);
            }
            else
            {
                foreach (var entry in archive.Entries) yield return (entry, entry.OpenEntryStream);
            }
        }
    }
}
