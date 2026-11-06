using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Workbench.Core;

/// <summary>Untrusted import. Names and actual bytes are checked before a private copy becomes visible.</summary>
internal static class PackageArchive
{
    internal const int MaximumFiles = 1000;
    internal const long MaximumFileBytes = 50L * 1024 * 1024;
    internal const long MaximumTotalBytes = 100L * 1024 * 1024;

    internal static async Task<ValidatedPackage> ImportAsync(string source, string stage, Version hostVersion, CancellationToken token)
    {
        source = Path.GetFullPath(source);
        FileSystemGuard.RejectLinks(source);
        Directory.CreateDirectory(stage);
        if (Directory.Exists(source))
        {
            CheckBounds(PackageValidator.Inventory(source), MaximumFiles, MaximumFileBytes, MaximumTotalBytes);
            var original = await PackageValidator.ValidateAsync(source, hostVersion, token);
            RequireValid(original);
            await CopyFilesAsync(original.Files, stage, MaximumFiles, MaximumFileBytes, MaximumTotalBytes, token);
            var copied = await PackageValidator.ValidateAsync(stage, hostVersion, token);
            RequireValid(copied);
            // Include the source again: a stable staged copy is insufficient if
            // the original package changed during its inventory/copy window.
            var after = await PackageValidator.ValidateAsync(source, hostVersion, token);
            if (!after.IsValid || copied.Fingerprint != original.Fingerprint || after.Fingerprint != original.Fingerprint)
                throw new IOException("Package content changed during import.");
            return copied;
        }
        FileSystemGuard.EnsureRegularFile(source);
        await using var zipStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var (locations, centralStart) = ReadLocations(zipStream);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != locations.Count) throw new IOException("ZIP entry counts differ.");
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Path, ZipLocation Location)>();
        var entryIndex = 0;
        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var location = locations[entryIndex++];
            if (new UTF8Encoding(false, true).GetString(location.Name) != entry.FullName ||
                location.CompressedLength != entry.CompressedLength)
                throw new IOException("ZIP metadata differs between readers.");
            var directory = entry.FullName.EndsWith('/');
            var relative = directory ? entry.FullName[..^1] : entry.FullName;
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!PackageValidator.IsSafeRelativePath(relative) || !explicitPaths.Add(relative) ||
                unixType is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                (directory && unixType == 0x8000) || (!directory && (unixType == 0x4000 ||
                    (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0)))
                throw new IOException("The archive contains an unsafe, duplicate or linked path.");
            var components = relative.Split('/');
            var path = "";
            for (var i = 0; i < components.Length; i++)
            {
                path = i == 0 ? components[i] : path + "/" + components[i];
                var isDirectory = i < components.Length - 1 || directory;
                if (paths.TryGetValue(path, out var wasDirectory))
                {
                    // Canonical spelling must agree too, including implicit parents.
                    if (wasDirectory != isDirectory || paths.Keys.First(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)) != path)
                        throw new IOException("The archive contains case-colliding or conflicting paths.");
                }
                else paths.Add(path, isDirectory);
            }
            if (directory)
            {
                if (entry.Length != 0 || entry.CompressedLength > 2) throw new IOException("Archive directory entries cannot carry file content.");
                continue;
            }
            if (entries.Count >= MaximumFiles || entry.Length > MaximumFileBytes || entry.Length < 0 ||
                (declaredTotal += entry.Length) > MaximumTotalBytes)
                throw new IOException("The package exceeds the 1000-file, 50 MiB file or 100 MiB total limit.");
            entries.Add((entry, relative, location));
        }
        long actualTotal = 0;
        foreach (var (entry, relative, location) in entries)
        {
            var target = FileSystemGuard.ContainedPath(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // ZipArchiveEntry.Open truncates to the declared uncompressed
            // length. Read the bounded compressed payload independently so a
            // forged short length cannot hide extra inflated bytes.
            var payload = OpenPayload(zipStream, location, centralStart);
            using var input = location.Method == 8 ? new DeflateStream(payload, CompressionMode.Decompress) : (Stream)payload;
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            actualTotal += await CopyBoundedAsync(input, output, MaximumFileBytes,
                MaximumTotalBytes - actualTotal, token, entry.Length, location.Crc);
        }
        var validated = await PackageValidator.ValidateAsync(stage, hostVersion, token);
        RequireValid(validated);
        return validated;
    }

    internal static async Task CopyFilesAsync(IReadOnlyList<PackageFile> files, string stage,
        int maximumFiles, long maximumFileBytes, long maximumTotalBytes, CancellationToken token)
    {
        CheckBounds(files, maximumFiles, maximumFileBytes, maximumTotalBytes);
        long actualTotal = 0;
        foreach (var file in files)
        {
            FileSystemGuard.EnsureRegularFile(file.FullPath);
            var destination = FileSystemGuard.ContainedPath(stage, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            actualTotal += await CopyBoundedAsync(input, output, maximumFileBytes, maximumTotalBytes - actualTotal, token);
        }
    }

    internal static void CheckBounds(IReadOnlyList<PackageFile> files, int maximumFiles, long maximumFileBytes, long maximumTotalBytes)
    {
        if (files.Count > maximumFiles) throw new IOException("File count limit exceeded.");
        long declaredTotal = 0;
        foreach (var file in files)
        {
            FileSystemGuard.EnsureRegularFile(file.FullPath);
            var length = new FileInfo(file.FullPath).Length;
            if (length > maximumFileBytes || (declaredTotal += length) > maximumTotalBytes)
                throw new IOException("File size limit exceeded.");
        }
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long fileLimit, long remaining, CancellationToken token,
        long? expectedLength = null, uint? expectedCrc = null)
    {
        var buffer = new byte[81920];
        long copied = 0;
        uint crc = 0xFFFFFFFF;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            copied += count;
            if (copied > fileLimit || copied > remaining) throw new IOException("Actual copied bytes exceed package limits.");
            if (expectedCrc.HasValue)
                for (var i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (expectedLength.HasValue && copied != expectedLength || expectedCrc.HasValue && ~crc != expectedCrc)
            throw new IOException("ZIP payload length or CRC does not match its metadata.");
        await output.FlushAsync(token);
        return copied;
    }

    private sealed record ZipLocation(long Offset, ushort Method, ushort Flags, long CompressedLength, uint Crc, byte[] Name);

    // Package limits fit classic single-volume ZIP. ZIP64, encryption and other
    // compression methods are deliberately rejected, rather than partially read.
    private static (List<ZipLocation>, long) ReadLocations(FileStream stream)
    {
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        for (var end = tail.Length - 22; end >= 0; end--)
        {
            var span = tail.AsSpan(end);
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x06054B50 ||
                end + 22 + BinaryPrimitives.ReadUInt16LittleEndian(span[20..]) != tail.Length) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            var centralStart = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(span[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(span[6..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(span[8..]) != count || count == ushort.MaxValue ||
                centralStart == uint.MaxValue || centralSize == uint.MaxValue ||
                (long)centralStart + centralSize != stream.Length - tail.Length + end)
                throw new IOException("Only classic single-volume ZIP archives are supported.");
            stream.Position = centralStart;
            var locations = new List<ZipLocation>(count);
            var header = new byte[46];
            for (var i = 0; i < count; i++)
            {
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50) throw new IOException("Invalid ZIP directory.");
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
                var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
                var compressed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42));
                if ((flags & ~0x080E) != 0 || method is not (0 or 8) || compressed == uint.MaxValue || offset == uint.MaxValue ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) == uint.MaxValue ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 0)
                    throw new IOException("Unsupported ZIP encryption, compression or ZIP64 entry.");
                var name = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))];
                stream.ReadExactly(name);
                if ((flags & 0x0800) == 0 && name.Any(value => value >= 128))
                    throw new IOException("ZIP filenames must be ASCII or explicitly UTF-8.");
                locations.Add(new(offset, method, flags, compressed, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)), name));
                stream.Position += BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
                if (stream.Position > (long)centralStart + centralSize) throw new IOException("Invalid ZIP directory bounds.");
            }
            if (stream.Position != (long)centralStart + centralSize) throw new IOException("Invalid ZIP directory size.");
            return (locations, centralStart);
        }
        throw new IOException("The ZIP end record is missing.");
    }

    private static PayloadStream OpenPayload(FileStream stream, ZipLocation location, long centralStart)
    {
        stream.Position = location.Offset;
        Span<byte> header = stackalloc byte[30];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034B50 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != location.Flags ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != location.Method)
            throw new IOException("Invalid ZIP local header.");
        var name = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header[26..])];
        stream.ReadExactly(name);
        if (!name.SequenceEqual(location.Name)) throw new IOException("ZIP local and directory filenames differ.");
        stream.Position += BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        if (stream.Position + location.CompressedLength > centralStart) throw new IOException("Invalid ZIP payload bounds.");
        return new(stream, location.CompressedLength);
    }

    private sealed class PayloadStream(Stream source, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
            remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();

    internal static void RequireValid(ValidatedPackage package)
    {
        if (!package.IsValid) throw new IOException("Package validation failed: " + string.Join(" ", package.Diagnostics.Select(d => d.Message)));
    }
}
