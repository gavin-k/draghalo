using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DropHalo.Core.Jobs;

/// <summary>A fingerprint of an output and its whole tree; unreadable items and links are never owned.</summary>
internal sealed record OutputSnapshot(string Fingerprint, string ContentFingerprint = "", string MetadataFingerprint = "")
{
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileIdInfo = 18;

    internal readonly record struct ItemIdentity(ulong Volume, ulong Low, ulong High, string? CanonicalPath);

    internal sealed record TreeNode(bool IsFolder, long LastWriteTicks, long Length, byte[]? FileSha256, (string Name, TreeNode Node)[]? Children);

    internal sealed record StagedOutput(OutputSnapshot Snapshot, TreeNode Root)
    {
        /// <summary>Rebinds file-system identity after an atomic rename without re-reading file contents.</summary>
        public OutputSnapshot? RebindAfterMove(string path)
        {
            try
            {
                using var fullHash = SHA256.Create();
                using var fullSink = new CryptoStream(Stream.Null, fullHash, CryptoStreamMode.Write);
                using var full = new BinaryWriter(fullSink);

                using var metaHash = SHA256.Create();
                using var metaSink = new CryptoStream(Stream.Null, metaHash, CryptoStreamMode.Write);
                using var meta = new BinaryWriter(metaSink);

                if (!RebindNode(path, Root, full, meta))
                {
                    return null;
                }

                full.Flush();
                fullSink.FlushFinalBlock();
                meta.Flush();
                metaSink.FlushFinalBlock();
                return new(Convert.ToHexString(fullHash.Hash!), Snapshot.ContentFingerprint, Convert.ToHexString(metaHash.Hash!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static OutputSnapshot? Capture(string path) => Stage(path)?.Snapshot;

    public static StagedOutput? Stage(string path)
    {
        try
        {
            using var fullHash = SHA256.Create();
            using var fullSink = new CryptoStream(Stream.Null, fullHash, CryptoStreamMode.Write);
            using var full = new BinaryWriter(fullSink);

            using var contentHash = SHA256.Create();
            using var contentSink = new CryptoStream(Stream.Null, contentHash, CryptoStreamMode.Write);
            using var content = new BinaryWriter(contentSink);

            using var metaHash = SHA256.Create();
            using var metaSink = new CryptoStream(Stream.Null, metaHash, CryptoStreamMode.Write);
            using var meta = new BinaryWriter(metaSink);

            var root = WriteSinglePass(path, full, content, meta);
            full.Flush();
            fullSink.FlushFinalBlock();
            content.Flush();
            contentSink.FlushFinalBlock();
            meta.Flush();
            metaSink.FlushFinalBlock();

            var snapshot = new OutputSnapshot(
                Convert.ToHexString(fullHash.Hash!),
                Convert.ToHexString(contentHash.Hash!),
                Convert.ToHexString(metaHash.Hash!));
            return new(snapshot, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Matches(string path) => Capture(path) is { } current && current.Fingerprint == Fingerprint;

    /// <summary>Fast stat-only check before moving a rollback candidate; avoids reading content when metadata already changed.</summary>
    public bool MetadataMatches(string path)
    {
        if (MetadataFingerprint.Length == 0)
        {
            return true;
        }

        try
        {
            using var metaHash = SHA256.Create();
            using var metaSink = new CryptoStream(Stream.Null, metaHash, CryptoStreamMode.Write);
            using var meta = new BinaryWriter(metaSink);
            WriteMetadataOnly(path, meta);
            meta.Flush();
            metaSink.FlushFinalBlock();
            return Convert.ToHexString(metaHash.Hash!) == MetadataFingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Matches after moving <paramref name="path"/> within the volume, where FAT/exFAT may assign a new directory-entry ID.</summary>
    public bool MatchesMoved(string path) => Capture(path) is { } current
        && (current.Fingerprint == Fingerprint || (ContentFingerprint.Length > 0 && current.ContentFingerprint == ContentFingerprint));

    /// <summary>True when two existing paths resolve to the same physical file or directory (e.g. via junction or symlink).</summary>
    public static bool SameItem(string a, string b)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryGetIdentity(a, out var ia) && TryGetIdentity(b, out var ib) && ia == ib;
    }

    internal static bool TryGetIdentity(string path, out ItemIdentity identity)
    {
        identity = default;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFileW(path, 0, (uint)(FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
                if (!handle.IsInvalid && TryReadWindowsIdentity(handle, out var info))
                {
                    identity = new(info.Volume, info.Low, info.High, null);
                    return true;
                }

                identity = new(0, 0, 0, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant());
                return true;
            }

            var canonical = ResolveCanonicalPath(path, depth: 0);
            identity = new(0, 0, 0, OperatingSystem.IsLinux() ? canonical : canonical.ToUpperInvariant());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveCanonicalPath(string path, int depth)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (depth >= 16)
        {
            return full;
        }

        var root = Path.GetPathRoot(full) ?? Path.DirectorySeparatorChar.ToString();
        var current = root;
        var relative = full[root.Length..];
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved)
            {
                current = ResolveCanonicalPath(resolved.FullName, depth + 1);
            }
            else
            {
                current = next;
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static void WriteIdentity(string path, BinaryWriter full, BinaryWriter meta)
    {
        if (OperatingSystem.IsWindows())
        {
            // NTFS can tunnel creation times across replacement; the volume and file ID identify the item.
            // Volumes without 128-bit FileIdInfo (FAT32, Parallels shared folders, SMB) fall back to 64-bit file index or creation time.
            using var handle = CreateFileW(path, 0, (uint)(FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero,
                OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (!handle.IsInvalid && TryReadWindowsIdentity(handle, out var identity))
            {
                full.Write(identity.Volume);
                full.Write(identity.Low);
                full.Write(identity.High);
                meta.Write(identity.Volume);
                meta.Write(identity.Low);
                meta.Write(identity.High);
                return;
            }
        }

        var created = File.GetCreationTimeUtc(path).Ticks;
        full.Write(created);
        meta.Write(created);
    }

    private static TreeNode WriteSinglePass(string path, BinaryWriter full, BinaryWriter content, BinaryWriter meta)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Cannot own a symbolic link or junction.");
        }

        var folder = (attributes & FileAttributes.Directory) != 0;
        full.Write(folder);
        content.Write(folder);
        meta.Write(folder);

        WriteIdentity(path, full, meta);

        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;
        full.Write(lastWrite);
        content.Write(lastWrite);
        meta.Write(lastWrite);

        if (folder)
        {
            var entries = Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray();
            full.Write(entries.Length);
            content.Write(entries.Length);
            meta.Write(entries.Length);
            var children = new (string Name, TreeNode Node)[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var name = Path.GetFileName(entries[i]);
                full.Write(name);
                content.Write(name);
                meta.Write(name);
                children[i] = (name, WriteSinglePass(entries[i], full, content, meta));
            }

            return new(true, lastWrite, 0, null, children);
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        var digest = SHA256.HashData(file);
        full.Write(length);
        full.Write(digest);
        content.Write(length);
        content.Write(digest);
        meta.Write(length);
        return new(false, lastWrite, length, digest, null);
    }

    private static bool RebindNode(string path, TreeNode node, BinaryWriter full, BinaryWriter meta)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var folder = (attributes & FileAttributes.Directory) != 0;
        if (folder != node.IsFolder)
        {
            return false;
        }

        full.Write(folder);
        meta.Write(folder);
        WriteIdentity(path, full, meta);

        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;
        if (lastWrite != node.LastWriteTicks)
        {
            return false;
        }

        full.Write(lastWrite);
        meta.Write(lastWrite);

        if (folder)
        {
            var entries = Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray();
            var expected = node.Children!;
            if (entries.Length != expected.Length)
            {
                return false;
            }

            full.Write(entries.Length);
            meta.Write(entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                var name = Path.GetFileName(entries[i]);
                if (!string.Equals(name, expected[i].Name, StringComparison.Ordinal))
                {
                    return false;
                }

                full.Write(name);
                meta.Write(name);
                if (!RebindNode(entries[i], expected[i].Node, full, meta))
                {
                    return false;
                }
            }

            return true;
        }

        var length = new FileInfo(path).Length;
        if (length != node.Length)
        {
            return false;
        }

        full.Write(length);
        full.Write(node.FileSha256!);
        meta.Write(length);
        return true;
    }

    private static void WriteMetadataOnly(string path, BinaryWriter meta)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Cannot own a symbolic link or junction.");
        }

        var folder = (attributes & FileAttributes.Directory) != 0;
        meta.Write(folder);
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(path, 0, (uint)(FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero,
                OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (!handle.IsInvalid && TryReadWindowsIdentity(handle, out var identity))
            {
                meta.Write(identity.Volume);
                meta.Write(identity.Low);
                meta.Write(identity.High);
            }
            else
            {
                meta.Write(File.GetCreationTimeUtc(path).Ticks);
            }
        }
        else
        {
            meta.Write(File.GetCreationTimeUtc(path).Ticks);
        }

        meta.Write(File.GetLastWriteTimeUtc(path).Ticks);
        if (folder)
        {
            var children = Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray();
            meta.Write(children.Length);
            foreach (var child in children)
            {
                meta.Write(Path.GetFileName(child));
                WriteMetadataOnly(child, meta);
            }
        }
        else
        {
            meta.Write(new FileInfo(path).Length);
        }
    }

    private static bool TryReadWindowsIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        if (GetFileInformationByHandleEx(handle, FileIdInfo, out identity, (uint)Marshal.SizeOf<FileIdentity>()))
        {
            return true;
        }

        if (GetFileInformationByHandle(handle, out var basic) && (basic.FileIndexLow != 0 || basic.FileIndexHigh != 0))
        {
            identity = new()
            {
                Volume = basic.VolumeSerialNumber,
                Low = ((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow,
                High = 0,
            };
            return true;
        }

        identity = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity identity, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
