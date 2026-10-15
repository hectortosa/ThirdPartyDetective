using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ThirdPartyDetective.Cli.Services.Registry;

// A text file read from a package archive. Path is relative to the archive's
// package root ("LICENSE", "licenses/gpl-3-0.txt").
public sealed record ArchiveFile(string Path, string Text)
{
    public bool IsNotice => PackageArchive.FileName(Path).StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase);
}

// Downloads package archives (.nupkg, npm tarballs, wheels, sdists) and reads
// the license files in them. Archives come from public registries and are
// untrusted: the download, the decompressed stream, the number of files and
// each file's text are all capped.
internal static class PackageArchive
{
    public const long MaxDownloadBytes = 50L * 1024 * 1024;

    // A tar.gz has no index, so finding its license files means decompressing the
    // whole stream; a small download can expand to gigabytes.
    private const long MaxDecompressedBytes = 512L * 1024 * 1024;

    public const int MaxFileChars = 256 * 1024;
    private const int MaxFiles = 20;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] LicenseFilePrefixes = { "LICENSE", "LICENCE", "COPYING", "NOTICE", "UNLICENSE" };

    // "license.js" is a module, not a license file.
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".cjs", ".mjs", ".ts", ".cts", ".mts", ".jsx", ".tsx", ".map", ".json", ".py", ".pyi", ".pyc",
        ".html", ".htm", ".css", ".sh", ".c", ".h", ".cs", ".dll", ".so", ".png", ".svg", ".xml",
    };

    // LICENSE, LICENCE, COPYING, NOTICE and UNLICENSE, with or without a suffix
    // (LICENSE.md, LICENSE-MIT, LICENSE.APACHE, COPYING.LESSER), and MIT-LICENSE.
    public static bool IsLicenseFileName(string fileName)
    {
        if (CodeExtensions.Contains(Path.GetExtension(fileName))) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return LicenseFilePrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
               stem.EndsWith("-LICENSE", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_LICENSE", StringComparison.OrdinalIgnoreCase);
    }

    public static string FileName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    // Downloads an archive into memory, or returns null when it is missing, too
    // large or too slow. The caller's cancellation propagates.
    public static async Task<byte[]?> DownloadAsync(HttpClient http, string url, ILogger logger, CancellationToken cancellationToken)
    {
        // HttpClient.Timeout stops at the response headers; this deadline also covers the body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);
        var token = deadline.Token;

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Could not download {Url}: {Status}", url, response.StatusCode);
                return null;
            }
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                logger.LogDebug("{Url} exceeds the download cap ({Cap} bytes); skipping", url, MaxDownloadBytes);
                return null;
            }

            using var buffer = new MemoryStream();
            await using var body = await response.Content.ReadAsStreamAsync(token);
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                {
                    logger.LogDebug("{Url} exceeds the download cap ({Cap} bytes); skipping", url, MaxDownloadBytes);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Download of {Url} failed", url);
            return null;
        }
    }

    // Reads the entries `select` picks from a zip (.nupkg, wheel). `select` gets
    // the entry's full path with '/' separators and returns the path to report,
    // or null to skip the entry.
    public static IReadOnlyList<ArchiveFile> ReadZip(byte[] archive, Func<string, string?> select)
    {
        var files = new List<ArchiveFile>();
        using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (files.Count == MaxFiles) break;
            if (entry.FullName.EndsWith('/')) continue;
            var reported = select(entry.FullName.Replace('\\', '/'));
            if (reported == null) continue;

            using var stream = entry.Open();
            if (ReadText(stream) is { } text)
                files.Add(new ArchiveFile(reported, text));
        }
        return Sorted(files);
    }

    // Reads the regular-file entries `select` picks from a tar.gz (npm tarball, sdist).
    public static IReadOnlyList<ArchiveFile> ReadTarGz(byte[] archive, Func<string, string?> select,
        long maxDecompressedBytes = MaxDecompressedBytes)
    {
        var files = new List<ArchiveFile>();
        using var gzip = new GZipStream(new MemoryStream(archive, writable: false), CompressionMode.Decompress);
        using var bounded = new BoundedReadStream(gzip, maxDecompressedBytes);
        using var tar = new TarReader(bounded);
        TarEntry? entry;
        while (files.Count < MaxFiles && (entry = tar.GetNextEntry()) != null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream == null)
                continue;
            var reported = select(entry.Name.TrimStart('.', '/'));
            if (reported == null) continue;

            if (ReadText(entry.DataStream) is { } text)
                files.Add(new ArchiveFile(reported, text));
        }
        return Sorted(files);
    }

    // One text for the package: a single file as it is, several under their names.
    public static string? Combine(IReadOnlyList<ArchiveFile> files)
    {
        if (files.Count == 0) return null;
        if (files.Count == 1) return files[0].Text;
        return string.Join("\n\n", files.Select(f => $"==> {f.Path} <==\n\n{f.Text.TrimEnd()}"));
    }

    // License files (not NOTICE files) are what a license can be identified from.
    public static IReadOnlyList<ArchiveFile> LicenseFilesOnly(IReadOnlyList<ArchiveFile> files) =>
        files.Where(f => !f.IsNotice).ToList();

    // Wheels can carry the same file twice (dist-info/LICENSE and dist-info/licenses/LICENSE).
    private static IReadOnlyList<ArchiveFile> Sorted(List<ArchiveFile> files) =>
        files.DistinctBy(f => f.Path, StringComparer.Ordinal).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    // UTF-8 (BOM-aware), capped at MaxFileChars; null for binary content.
    private static string? ReadText(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var chars = new char[MaxFileChars];
        var read = reader.ReadBlock(chars, 0, chars.Length);
        var text = new string(chars, 0, read);
        return text.Contains('\0') || string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // Fails the read once more than `limit` bytes have come through.
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _total;

        public BoundedReadStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));

        private int Count(int read)
        {
            _total += read;
            if (_total > _limit)
                throw new InvalidDataException($"Archive expands beyond {_limit} bytes");
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
