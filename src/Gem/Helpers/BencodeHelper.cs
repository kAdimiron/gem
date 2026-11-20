using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Gem.Helpers;

public sealed record TorrentCreationOptions(
    string SourcePath,
    IReadOnlyList<string> Trackers,
    int PieceLengthBytes = 0,
    bool IsPrivate = false,
    string? Comment = null,
    string CreatedBy = "Gem/1.0.1");

public sealed record TorrentCreationResult(
    byte[] TorrentBytes,
    string TorrentName,
    string SeedSavePath,
    long TotalSize,
    int PieceLength,
    int PieceCount,
    int FileCount);

/// <summary>
/// Minimal zero-dependency BEncode reader/writer for reading torrent metadata fields (piece length, private flag)
/// and creating BEP-3 compliant single-file and multi-file .torrent files.
/// </summary>
public static class BencodeHelper
{
    public static (int PieceLength, bool IsPrivate) ReadMetadataHints(ReadOnlySpan<byte> bencodedBytes)
    {
        if (bencodedBytes.IsEmpty)
            return (0, false);

        int offset = 0;
        if (!TryReadElement(bencodedBytes, ref offset, out int rootPieceLength, out bool rootIsPrivate, out int infoPieceLength, out bool infoIsPrivate))
            return (0, false);

        int pieceLength = infoPieceLength > 0 ? infoPieceLength : rootPieceLength;
        bool isPrivate = infoIsPrivate || rootIsPrivate;
        return (pieceLength, isPrivate);
    }

    public static int ChooseRecommendedPieceLength(long totalSize) => totalSize switch
    {
        <= 32L * 1024 * 1024 => 32 * 1024,
        <= 128L * 1024 * 1024 => 64 * 1024,
        <= 512L * 1024 * 1024 => 256 * 1024,
        <= 1024L * 1024 * 1024 => 512 * 1024,
        <= 4L * 1024 * 1024 * 1024 => 1024 * 1024,
        <= 16L * 1024 * 1024 * 1024 => 2 * 1024 * 1024,
        <= 64L * 1024 * 1024 * 1024 => 4 * 1024 * 1024,
        <= 256L * 1024 * 1024 * 1024 => 8 * 1024 * 1024,
        _ => 16 * 1024 * 1024
    };

    public static async Task<TorrentCreationResult> CreateTorrentAsync(
        TorrentCreationOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SourcePath))
            throw new ArgumentException(Services.LocalizationService.Instance["ErrBencodeSpecifySource"], nameof(options));

        string rawPath = options.SourcePath.Trim();
        bool isMultiFile;
        string torrentName;
        string seedSavePath;
        var entries = new List<(string FullPath, string[] PathSegments, long Length)>();

        if (File.Exists(rawPath))
        {
            string fullFile = Path.GetFullPath(rawPath);
            string? parent = Path.GetDirectoryName(fullFile);
            if (string.IsNullOrWhiteSpace(parent))
                throw new InvalidOperationException(Services.LocalizationService.Instance["ErrBencodeParentMissing"]);

            long len = new FileInfo(fullFile).Length;
            if (len <= 0)
                throw new InvalidOperationException(Services.LocalizationService.Instance["ErrBencodeEmptyFile"]);

            isMultiFile = false;
            torrentName = Path.GetFileName(fullFile);
            seedSavePath = parent;
            entries.Add((fullFile, [torrentName], len));
        }
        else if (Directory.Exists(rawPath))
        {
            string fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath));
            string name = Path.GetFileName(fullDir);
            string? parent = Path.GetDirectoryName(fullDir);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parent))
                throw new InvalidOperationException(Services.LocalizationService.Instance["ErrBencodeRootDir"]);

            isMultiFile = true;
            torrentName = name;
            seedSavePath = parent;

            var relFiles = Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories)
                .Select(fp => (FullPath: fp, RelPath: Path.GetRelativePath(fullDir, fp)))
                .OrderBy(x => x.RelPath, StringComparer.Ordinal);

            foreach (var (fp, rel) in relFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long len = new FileInfo(fp).Length;
                string[] segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0)
                    entries.Add((fp, segments, len));
            }

            if (entries.Count == 0)
                throw new InvalidOperationException(Services.LocalizationService.Instance["ErrBencodeEmptyFolder"]);
        }
        else
        {
            throw new FileNotFoundException(Services.LocalizationService.Instance["ErrBencodeNotFound"], rawPath);
        }

        long totalSize = entries.Sum(e => e.Length);
        if (totalSize <= 0)
            throw new InvalidOperationException(Services.LocalizationService.Instance["ErrBencodeEmptySource"]);

        int pieceLength = options.PieceLengthBytes > 0
            ? Math.Max(16 * 1024, options.PieceLengthBytes)
            : ChooseRecommendedPieceLength(totalSize);

        int expectedPieces = (int)((totalSize + pieceLength - 1) / pieceLength);
        byte[] pieceHashes = new byte[expectedPieces * 20];
        byte[] readBuffer = new byte[Math.Min(256 * 1024, pieceLength)];

        int pieceIdx = 0;
        int bytesInPiece = 0;
        long hashedTotal = 0;
        double lastReportedPct = -1;

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length == 0)
                continue;

            await using var fs = new FileStream(
                entry.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                readBuffer.Length,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            while (true)
            {
                int toRead = Math.Min(readBuffer.Length, pieceLength - bytesInPiece);
                int read = await fs.ReadAsync(readBuffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                sha1.AppendData(readBuffer.AsSpan(0, read));
                bytesInPiece += read;
                hashedTotal += read;

                if (bytesInPiece == pieceLength)
                {
                    if (pieceIdx < expectedPieces)
                        sha1.GetHashAndReset(pieceHashes.AsSpan(pieceIdx * 20, 20));
                    pieceIdx++;
                    bytesInPiece = 0;
                }

                double pct = Math.Clamp(hashedTotal * 100.0 / totalSize, 0, 100);
                if (pct - lastReportedPct >= 0.5 || hashedTotal == totalSize)
                {
                    lastReportedPct = pct;
                    progress?.Report(pct);
                }
            }
        }

        if (bytesInPiece > 0)
        {
            if (pieceIdx < expectedPieces)
                sha1.GetHashAndReset(pieceHashes.AsSpan(pieceIdx * 20, 20));
            pieceIdx++;
        }

        if (pieceIdx != expectedPieces)
            Array.Resize(ref pieceHashes, pieceIdx * 20);

        progress?.Report(100);

        var trackers = options.Trackers
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

        using var ms = new MemoryStream();
        WriteByte(ms, (byte)'d');

        if (trackers.Count > 0)
        {
            WriteString(ms, "announce");
            WriteString(ms, trackers[0]);

            WriteString(ms, "announce-list");
            WriteByte(ms, (byte)'l');
            foreach (string tracker in trackers)
            {
                WriteByte(ms, (byte)'l');
                WriteString(ms, tracker);
                WriteByte(ms, (byte)'e');
            }
            WriteByte(ms, (byte)'e');
        }

        if (!string.IsNullOrWhiteSpace(options.Comment))
        {
            WriteString(ms, "comment");
            WriteString(ms, options.Comment.Trim());
        }

        if (!string.IsNullOrWhiteSpace(options.CreatedBy))
        {
            WriteString(ms, "created by");
            WriteString(ms, options.CreatedBy.Trim());
        }

        WriteString(ms, "creation date");
        WriteInt(ms, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        WriteString(ms, "encoding");
        WriteString(ms, "UTF-8");

        WriteString(ms, "info");
        WriteByte(ms, (byte)'d');

        if (isMultiFile)
        {
            WriteString(ms, "files");
            WriteByte(ms, (byte)'l');
            foreach (var entry in entries)
            {
                WriteByte(ms, (byte)'d');
                WriteString(ms, "length");
                WriteInt(ms, entry.Length);
                WriteString(ms, "path");
                WriteByte(ms, (byte)'l');
                foreach (string segment in entry.PathSegments)
                    WriteString(ms, segment);
                WriteByte(ms, (byte)'e');
                WriteByte(ms, (byte)'e');
            }
            WriteByte(ms, (byte)'e');
        }
        else
        {
            WriteString(ms, "length");
            WriteInt(ms, totalSize);
        }

        WriteString(ms, "name");
        WriteString(ms, torrentName);
        WriteString(ms, "piece length");
        WriteInt(ms, pieceLength);
        WriteString(ms, "pieces");
        WriteRawString(ms, pieceHashes);

        if (options.IsPrivate)
        {
            WriteString(ms, "private");
            WriteInt(ms, 1);
        }

        WriteByte(ms, (byte)'e'); // info
        WriteByte(ms, (byte)'e'); // root

        return new TorrentCreationResult(
            ms.ToArray(),
            torrentName,
            seedSavePath,
            totalSize,
            pieceLength,
            pieceIdx,
            entries.Count);
    }

    public static byte[] BuildMinimalTorrent(string fileName, int pieceLength, long fileLength, string? trackerUrl = null)
    {
        int pieceCount = Math.Max(1, (int)((fileLength + pieceLength - 1) / pieceLength));
        using var ms = new MemoryStream();
        WriteByte(ms, (byte)'d');

        if (!string.IsNullOrWhiteSpace(trackerUrl))
        {
            WriteString(ms, "announce");
            WriteString(ms, trackerUrl);
        }

        WriteString(ms, "info");
        WriteByte(ms, (byte)'d');
        WriteString(ms, "length");
        WriteInt(ms, fileLength);
        WriteString(ms, "name");
        WriteString(ms, fileName);
        WriteString(ms, "piece length");
        WriteInt(ms, pieceLength);
        WriteString(ms, "pieces");
        byte[] dummyHashes = new byte[pieceCount * 20];
        WriteRawString(ms, dummyHashes);
        WriteByte(ms, (byte)'e');

        WriteByte(ms, (byte)'e');
        return ms.ToArray();
    }

    private static bool TryReadElement(
        ReadOnlySpan<byte> data,
        ref int offset,
        out int pieceLength,
        out bool isPrivate,
        out int infoPieceLength,
        out bool infoIsPrivate)
    {
        pieceLength = 0;
        isPrivate = false;
        infoPieceLength = 0;
        infoIsPrivate = false;

        if (offset >= data.Length)
            return false;

        byte tag = data[offset];
        if (tag == (byte)'d')
        {
            offset++;
            while (offset < data.Length && data[offset] != (byte)'e')
            {
                if (!TryReadByteString(data, ref offset, out string key))
                    return false;

                if (key == "info" && offset < data.Length && data[offset] == (byte)'d')
                {
                    if (!TryReadElement(data, ref offset, out int nestedPl, out bool nestedPriv, out _, out _))
                        return false;
                    infoPieceLength = nestedPl;
                    infoIsPrivate = nestedPriv;
                }
                else if (key == "piece length" && TryReadInteger(data, ref offset, out long pl))
                {
                    if (pl > 0 && pl <= int.MaxValue)
                        pieceLength = (int)pl;
                }
                else if (key == "private" && TryReadInteger(data, ref offset, out long priv))
                {
                    isPrivate = priv == 1;
                }
                else
                {
                    if (!SkipElement(data, ref offset))
                        return false;
                }
            }

            if (offset >= data.Length || data[offset] != (byte)'e')
                return false;
            offset++;
            return true;
        }

        return SkipElement(data, ref offset);
    }

    private static bool SkipElement(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length)
            return false;

        byte tag = data[offset];
        if (tag == (byte)'i')
            return TryReadInteger(data, ref offset, out _);

        if (tag == (byte)'l')
        {
            offset++;
            while (offset < data.Length && data[offset] != (byte)'e')
            {
                if (!SkipElement(data, ref offset))
                    return false;
            }
            if (offset >= data.Length)
                return false;
            offset++;
            return true;
        }

        if (tag == (byte)'d')
        {
            offset++;
            while (offset < data.Length && data[offset] != (byte)'e')
            {
                if (!SkipElement(data, ref offset) || !SkipElement(data, ref offset))
                    return false;
            }
            if (offset >= data.Length)
                return false;
            offset++;
            return true;
        }

        return TrySkipByteString(data, ref offset);
    }

    private static bool TryReadInteger(ReadOnlySpan<byte> data, ref int offset, out long value)
    {
        value = 0;
        if (offset >= data.Length || data[offset] != (byte)'i')
            return false;

        int end = data[(offset + 1)..].IndexOf((byte)'e');
        if (end < 0)
            return false;

        string text = Encoding.ASCII.GetString(data.Slice(offset + 1, end));
        if (!long.TryParse(text, out value))
            return false;

        offset += end + 2;
        return true;
    }

    private static bool TryReadByteString(ReadOnlySpan<byte> data, ref int offset, out string text)
    {
        text = string.Empty;
        int colonRel = data[offset..].IndexOf((byte)':');
        if (colonRel <= 0)
            return false;

        string lenText = Encoding.ASCII.GetString(data.Slice(offset, colonRel));
        if (!int.TryParse(lenText, out int len) || len < 0)
            return false;

        int start = offset + colonRel + 1;
        if (start + len > data.Length)
            return false;

        text = Encoding.UTF8.GetString(data.Slice(start, len));
        offset = start + len;
        return true;
    }

    private static bool TrySkipByteString(ReadOnlySpan<byte> data, ref int offset)
    {
        int colonRel = data[offset..].IndexOf((byte)':');
        if (colonRel <= 0)
            return false;

        string lenText = Encoding.ASCII.GetString(data.Slice(offset, colonRel));
        if (!int.TryParse(lenText, out int len) || len < 0)
            return false;

        int next = offset + colonRel + 1 + len;
        if (next > data.Length)
            return false;

        offset = next;
        return true;
    }

    private static void WriteByte(Stream s, byte b) => s.WriteByte(b);

    private static void WriteInt(Stream s, long value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes($"i{value}e");
        s.Write(bytes);
    }

    private static void WriteString(Stream s, string value)
    {
        WriteRawString(s, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteRawString(Stream s, byte[] raw)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"{raw.Length}:");
        s.Write(prefix);
        s.Write(raw);
    }
}
