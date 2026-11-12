using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Hashing;

namespace ChickenCommander.Core.Tools;

public enum ChecksumKind { Crc32, Md5, Sha1, Sha256, Sha512 }

public enum VerifyState { Ok, Mismatch, Missing, Error }

public sealed record VerifyResult(string FileName, string Expected, string? Actual, VerifyState State, string? Message = null);

/// <summary>
/// Creates and verifies checksum files in the common formats: "hash *name" (md5sum / sha256sum),
/// BSD "SHA256 (name) = hash", and SFV "name crc32".
/// </summary>
public static partial class Checksums
{
    public static string Extension(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Crc32 => ".sfv",
        ChecksumKind.Md5 => ".md5",
        ChecksumKind.Sha1 => ".sha1",
        ChecksumKind.Sha256 => ".sha256",
        _ => ".sha512",
    };

    public static ChecksumKind? KindFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sfv" => ChecksumKind.Crc32,
        ".md5" => ChecksumKind.Md5,
        ".sha1" => ChecksumKind.Sha1,
        ".sha256" => ChecksumKind.Sha256,
        ".sha512" => ChecksumKind.Sha512,
        _ => null,
    };

    /// <summary>Lower-case hex hash of a file; <paramref name="progress"/> receives bytes read so far.</summary>
    public static async Task<string> ComputeAsync(string path, ChecksumKind kind, Action<long>? progress = null, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[1024 * 1024];
        long total = 0;

        if (kind == ChecksumKind.Crc32)
        {
            var crc = new Crc32();
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                crc.Append(buffer.AsSpan(0, n));
                progress?.Invoke(total += n);
            }
            // SFV writes CRC32 big-endian, as 8 hex digits.
            return crc.GetCurrentHashAsUInt32().ToString("x8");
        }

        using var hash = IncrementalHash.CreateHash(kind switch
        {
            ChecksumKind.Md5 => HashAlgorithmName.MD5,
            ChecksumKind.Sha1 => HashAlgorithmName.SHA1,
            ChecksumKind.Sha256 => HashAlgorithmName.SHA256,
            _ => HashAlgorithmName.SHA512,
        });
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            progress?.Invoke(total += read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>One line of a checksum file: "hash *name" (binary mode), or "name crc" for SFV.</summary>
    public static string FormatLine(ChecksumKind kind, string hash, string relativeName) =>
        kind == ChecksumKind.Crc32 ? $"{relativeName} {hash}" : $"{hash} *{relativeName}";

    /// <summary>Parses a checksum file into (file name, expected hash) pairs. Comments (';' or '#') and blank lines are skipped.</summary>
    public static IReadOnlyList<(string FileName, string Hash)> Parse(string content, ChecksumKind kind)
    {
        var result = new List<(string, string)>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            var bsd = BsdLine().Match(line);
            if (bsd.Success)
            {
                result.Add((bsd.Groups["name"].Value, bsd.Groups["hash"].Value.ToLowerInvariant()));
                continue;
            }
            if (kind == ChecksumKind.Crc32)
            {
                var space = line.LastIndexOf(' ');
                if (space > 0) result.Add((line[..space].Trim(), line[(space + 1)..].ToLowerInvariant()));
                continue;
            }
            var gnu = GnuLine().Match(line);
            if (gnu.Success) result.Add((gnu.Groups["name"].Value, gnu.Groups["hash"].Value.ToLowerInvariant()));
        }
        return result;
    }

    /// <summary>Verifies every file listed in <paramref name="checksumFile"/> (names are relative to its folder).</summary>
    public static async Task<IReadOnlyList<VerifyResult>> VerifyAsync(string checksumFile, IProgress<string>? current = null, CancellationToken ct = default)
    {
        var kind = KindFromExtension(checksumFile) ?? throw new NotSupportedException("Unknown checksum file type: " + Path.GetExtension(checksumFile));
        var folder = Path.GetDirectoryName(Path.GetFullPath(checksumFile))!;
        var results = new List<VerifyResult>();
        foreach (var (name, expected) in Parse(await File.ReadAllTextAsync(checksumFile, Encoding.UTF8, ct).ConfigureAwait(false), kind))
        {
            ct.ThrowIfCancellationRequested();
            current?.Report(name);
            var path = Path.Combine(folder, name.Replace('/', '\\'));
            if (!File.Exists(path))
            {
                results.Add(new VerifyResult(name, expected, null, VerifyState.Missing));
                continue;
            }
            try
            {
                var actual = await ComputeAsync(path, kind, null, ct).ConfigureAwait(false);
                results.Add(new VerifyResult(name, expected, actual, actual == expected ? VerifyState.Ok : VerifyState.Mismatch));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new VerifyResult(name, expected, null, VerifyState.Error, ex.Message));
            }
        }
        return results;
    }

    [GeneratedRegex(@"^(?:MD5|SHA1|SHA256|SHA512|CRC32)\s*\((?<name>.+)\)\s*=\s*(?<hash>[0-9a-fA-F]+)$")]
    private static partial Regex BsdLine();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{8,128})\s[ *]?(?<name>.+)$")]
    private static partial Regex GnuLine();
}
