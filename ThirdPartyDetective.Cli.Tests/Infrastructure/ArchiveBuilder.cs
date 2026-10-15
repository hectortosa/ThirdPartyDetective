using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace ThirdPartyDetective.Cli.Tests.Infrastructure;

// Builds small package archives (npm tarballs and sdists as tar.gz, wheels as
// zip) for lookup tests.
internal static class ArchiveBuilder
{
    public static byte[] TarGz(params (string Path, string Text)[] files)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (path, text) in files)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text))
                });
            }
        }
        return output.ToArray();
    }

    public static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }
}
