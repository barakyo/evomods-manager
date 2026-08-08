using ACEvo.Package;

namespace FlatPad.Core.Game;

/// <summary>What is inside a <c>.kspkg</c>, read without extracting anything.</summary>
/// <param name="PackagePath">The package this describes.</param>
/// <param name="Files">Real files, in physical order. Directory pseudo-entries are dropped.</param>
/// <param name="DirectoryCount">Directory entries, kept only so the summary can account for them.</param>
/// <param name="TotalBytes">What unpacking writes.</param>
/// <param name="CommonRoot">
/// The longest folder prefix every file shares, or empty when they share none. A car mod is
/// entirely under one folder, so this is what lets the confirmation say which car it is.
/// </param>
public sealed record PackageInfo(
    string PackagePath,
    IReadOnlyList<ArchiveEntry> Files,
    int DirectoryCount,
    long TotalBytes,
    string CommonRoot);

/// <summary>The outcome of unpacking a package.</summary>
/// <param name="Written">Files actually written.</param>
/// <param name="BytesWritten">Their total size.</param>
/// <param name="Refused">
/// Entries deliberately not written — a name that would escape the output folder, or one the
/// archive lists but cannot produce.
/// </param>
public sealed record UnpackedPackage(int Written, long BytesWritten, IReadOnlyList<string> Refused)
{
    public bool Ok => Refused.Count == 0;
}

/// <summary>
/// Unpacks a standalone <c>.kspkg</c> — a car mod, or any other package — into a folder.
/// </summary>
/// <remarks>
/// Distinct from <see cref="GameArchive.Unpack"/>, which is a change to how the GAME reads its
/// content: it works on one specific archive found in the game root, and finishes by renaming that
/// archive aside so loose folders take over. This does none of that. It points at a package
/// anywhere on disk, writes its contents somewhere else, and leaves everything as it found it —
/// which is what someone inspecting or modifying a car mod actually wants.
///
/// Mod packages differ from the game archive in two ways worth knowing: nothing in them is
/// encrypted, and their file table is a fixed 64 MB regardless of payload (a 599 MB mod carried
/// 532 MB of files), which is why they are always large enough to dodge the reader limitation
/// described on <see cref="SmallestReadable"/>.
/// </remarks>
public static class PackageUnpacker
{
    /// <summary>
    /// Below this a file cannot be a package at all — there is nowhere for the table to live.
    /// </summary>
    /// <remarks>
    /// ⚠️ A package between 32 and 64 MB is readable in principle but not through this library.
    /// <c>DetectFileTableSize</c> tries the 64 MB table first and sets
    /// <c>fs.Position = fs.Length - tableSize</c> with no bounds check, so a negative position
    /// throws before the 32 MB candidate is ever reached. <see cref="Inspect"/> turns that into a
    /// sentence; fixing it properly means a patch upstream, since the submodule stays unforked.
    /// </remarks>
    public const long SmallestReadable = 0x2_000_000;    // 32 MB

    private const long LargestTable = 0x4_000_000;       // 64 MB

    /// <summary>Read a package's file table. Writes nothing.</summary>
    public static PackageInfo Inspect(string packagePath)
    {
        if (!File.Exists(packagePath))
            throw new GameArchiveException($"'{packagePath}' does not exist");

        string name = Path.GetFileName(packagePath);
        long length = new FileInfo(packagePath).Length;
        if (length < SmallestReadable)
        {
            throw new GameArchiveException(
                $"{name} is only {GameArchive.Bytes(length)}. A .kspkg keeps its file table in the "
                + "last 32 or 64 MB, so a file this small cannot be one.");
        }

        List<ArchiveEntry> entries;
        try
        {
            entries = GameArchive.ListEntries(packagePath);
        }
        catch (ArgumentOutOfRangeException) when (length < LargestTable)
        {
            ReleaseLeakedHandle();
            throw new GameArchiveException(
                $"{name} is {GameArchive.Bytes(length)}, and the pack reader tries a 64 MB file table "
                + "first without checking it fits. It cannot fall back to the 32 MB layout for a file "
                + "this small. Packages built by current tools are always larger than this.");
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            ReleaseLeakedHandle();
            throw new GameArchiveException($"{name} is not a readable .kspkg ({e.Message})");
        }

        List<ArchiveEntry> files = entries.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
            throw new GameArchiveException($"{name} lists no files to unpack");

        return new PackageInfo(packagePath, files, entries.Count - files.Count,
            files.Sum(e => e.Size), CommonRoot(files));
    }

    /// <summary>Extract everything in a package into <paramref name="outputDir"/>.</summary>
    /// <remarks>
    /// ⚠️ Cancellation is checked BETWEEN files, never during one, so every file on disk when this
    /// stops is complete. A half-written file that looks finished is worse than a missing one.
    /// </remarks>
    public static UnpackedPackage Unpack(PackageInfo info, string outputDir,
        IProgress<UnpackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Outside the try on purpose: an access error here has to reach the caller as
        // UnauthorizedAccessException so the GUI can offer to restart elevated.
        Directory.CreateDirectory(outputDir);

        long free = FreeSpace(outputDir);
        if (free > 0 && free < info.TotalBytes)
        {
            throw new GameArchiveException(
                $"not enough free space: unpacking {Path.GetFileName(info.PackagePath)} writes about "
                + $"{GameArchive.Bytes(info.TotalBytes)} and {GameArchive.Bytes(free)} is free.");
        }

        var refused = new List<string>();
        long doneBytes = 0, writtenBytes = 0;
        int done = 0, written = 0;
        var report = new ThrottledProgress<UnpackProgress>(progress, GameArchive.ReportInterval);

        using (PackFile pack = PackFile.Open(info.PackagePath))
        {
            // Files arrive in physical order from ListEntries, which keeps reads sequential.
            foreach (ArchiveEntry entry in info.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Checked before extracting: ExtractFile builds the output path itself, so refusing
                // to call it is the only way to keep a hostile entry out of the rest of the disk.
                if (GameArchive.SafeOutputPath(outputDir, entry.Path) is null
                    || !pack.ExtractFile(entry.Path, outputDir))
                {
                    refused.Add(entry.Path);
                }
                else
                {
                    written++;
                    writtenBytes += entry.Size;
                }

                done++;
                doneBytes += entry.Size;
                var snapshot = new UnpackProgress(done, info.Files.Count, doneBytes, info.TotalBytes,
                    entry.Path);
                if (done == info.Files.Count)
                    report.ReportFinal(snapshot);
                else
                    report.Report(snapshot);
            }
        }

        return new UnpackedPackage(written, writtenBytes, refused);
    }

    /// <summary>Inspect and unpack in one call, for callers that do not need the summary first.</summary>
    public static UnpackedPackage Unpack(string packagePath, string outputDir,
        IProgress<UnpackProgress>? progress = null, CancellationToken cancellationToken = default)
        => Unpack(Inspect(packagePath), outputDir, progress, cancellationToken);

    /// <summary>Unlock a package whose failed <c>PackFile.Open</c> left a handle behind.</summary>
    /// <remarks>
    /// ⚠️ <c>PackFile.Open</c> calls <c>File.OpenRead</c> BEFORE it validates anything, and the
    /// stream stays a local until the <c>PackFile</c> that would own it is constructed at the end.
    /// So every failure path leaks an open handle, and nothing public holds a reference to close
    /// it. Without this, picking the wrong file leaves it locked against being moved or deleted
    /// until a collection happens to run — which, in a GUI, reads as the tool having eaten the file.
    ///
    /// Finalisation is the only lever the public API leaves us. It runs on the error path only, so
    /// the cost lands where a user has already hit a dead end. The real fix belongs upstream,
    /// alongside the bounds check described on <see cref="SmallestReadable"/>.
    /// </remarks>
    private static void ReleaseLeakedHandle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Free space on the output drive, or 0 when the drive will not say.</summary>
    private static long FreeSpace(string outputDir)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputDir))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return 0;   // exotic or unmapped drive: let the write decide rather than refusing blind
        }
    }

    /// <summary>The deepest folder every file sits under, or empty when they share none.</summary>
    /// <remarks>
    /// Compares whole path segments, not characters: <c>car_a</c> and <c>car_a_gt</c> share a
    /// prefix but are different folders, and a summary that claimed otherwise would be wrong.
    /// </remarks>
    public static string CommonRoot(IReadOnlyList<ArchiveEntry> files)
    {
        if (files.Count == 0)
            return "";

        string[] shared = Split(files[0].Path);
        foreach (ArchiveEntry file in files)
        {
            string[] parts = Split(file.Path);
            int keep = Math.Min(shared.Length, parts.Length);
            for (int i = 0; i < keep; i++)
            {
                if (!string.Equals(shared[i], parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    keep = i;
                    break;
                }
            }

            if (keep < shared.Length)
                shared = shared[..keep];
            if (shared.Length == 0)
                return "";
        }

        return string.Join('\\', shared);

        static string[] Split(string path)
        {
            string folder = Path.GetDirectoryName(path) ?? "";
            return folder.Length == 0 ? [] : folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
