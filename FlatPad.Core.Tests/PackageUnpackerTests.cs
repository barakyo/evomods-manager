using FlatPad.Core.Game;

namespace FlatPad.Core.Tests;

/// <summary>
/// Reading and unpacking a standalone <c>.kspkg</c> — a car mod rather than the game's archive.
/// </summary>
/// <remarks>
/// Extraction itself cannot be covered here without shipping a package, so it is proven against the
/// real 599 MB R34 mod with <c>unpack-package</c>, whose output is diffed against Nenkai's own CLI.
/// What these cover is everything decided BEFORE a byte is written: whether the file can be a
/// package at all, what the summary claims is inside it, and where an entry is allowed to land.
/// </remarks>
public class PackageUnpackerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flatpad-pkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Sized(string name, long bytes)
    {
        string path = Path.Combine(_root, name);
        using FileStream fs = File.Create(path);
        fs.SetLength(bytes);
        return path;
    }

    private static ArchiveEntry File_(string path, long size = 1) => new(path, 0, size, false);

    // ------------------------------------------------------------------ can this even be a package

    [Fact]
    public void A_package_that_is_not_there_says_so_rather_than_throwing_an_io_error()
    {
        var ex = Assert.Throws<GameArchiveException>(
            () => PackageUnpacker.Inspect(Path.Combine(_root, "absent.kspkg")));

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void A_file_too_small_to_hold_any_file_table_is_rejected_with_a_reason_not_an_index_error()
    {
        // A .kspkg keeps its table in the last 32 or 64 MB. Seeking to a negative offset is what
        // the reader would do instead, and "ArgumentOutOfRangeException" tells a user nothing.
        string tiny = Sized("truncated.kspkg", 10 * 1024 * 1024);

        var ex = Assert.Throws<GameArchiveException>(() => PackageUnpacker.Inspect(tiny));

        Assert.Contains("10.0 MB", ex.Message);
        Assert.Contains("file table", ex.Message);
    }

    [Fact]
    public void A_package_in_the_gap_between_the_two_table_sizes_reports_the_readers_limitation()
    {
        // 32 MB <= size < 64 MB. The reader tries 64 MB first without checking it fits, so it
        // throws before it can fall back — the file is not necessarily bad, the reader cannot open
        // it. Saying which of those it is, is the whole point.
        string awkward = Sized("awkward.kspkg", 40L * 1024 * 1024);

        var ex = Assert.Throws<GameArchiveException>(() => PackageUnpacker.Inspect(awkward));

        Assert.Contains("40.0 MB", ex.Message);
        Assert.Contains("64 MB file table", ex.Message);
    }

    [Fact]
    public void A_large_file_that_is_not_a_package_at_all_is_reported_as_unreadable()
    {
        string notAPackage = Sized("random.kspkg", 70L * 1024 * 1024);

        var ex = Assert.Throws<GameArchiveException>(() => PackageUnpacker.Inspect(notAPackage));

        Assert.Contains("not a readable .kspkg", ex.Message);
    }

    [Fact]
    public void A_package_that_could_not_be_read_is_not_left_locked_against_being_moved()
    {
        // PackFile.Open opens the file before it validates it and only hands ownership to the
        // PackFile at the very end, so every failure path leaks the handle. A user who picks the
        // wrong file and then cannot delete it has been handed a second problem by the error.
        string notAPackage = Sized("locked.kspkg", 70L * 1024 * 1024);

        Assert.Throws<GameArchiveException>(() => PackageUnpacker.Inspect(notAPackage));

        File.Move(notAPackage, notAPackage + ".moved");   // throws IOException if still open
    }

    // ------------------------------------------------------------------ what the summary claims

    [Fact]
    public void The_common_root_is_reported_so_a_car_mod_says_which_car_it_is()
    {
        string root = PackageUnpacker.CommonRoot([
            File_(@"content\cars\nissan_skyline_r34_gtr\texture\m_ext_skin_2.texture"),
            File_(@"content\cars\nissan_skyline_r34_gtr\tyres\eco\tcurve_eco.curve"),
            File_(@"content\cars\nissan_skyline_r34_gtr\animations\hood.animation"),
        ]);

        Assert.Equal(@"content\cars\nissan_skyline_r34_gtr", root);
    }

    [Fact]
    public void A_package_whose_files_share_no_root_reports_none_rather_than_a_wrong_one()
    {
        string root = PackageUnpacker.CommonRoot([
            File_(@"content\cars\a\x.texture"),
            File_(@"editor\brushes\y.table"),
        ]);

        Assert.Equal("", root);
    }

    [Fact]
    public void A_file_sitting_at_the_top_of_the_package_collapses_the_common_root()
    {
        string root = PackageUnpacker.CommonRoot([
            File_(@"content\cars\a\x.texture"),
            File_("readme.txt"),
        ]);

        Assert.Equal("", root);
    }

    [Fact]
    public void The_common_root_stops_where_the_folders_diverge_rather_than_at_a_shared_prefix()
    {
        // "car_a" and "car_a_gt" share characters but are different folders. Comparing whole
        // segments is the difference between a true statement and a plausible one.
        string root = PackageUnpacker.CommonRoot([
            File_(@"content\cars\car_a\x.texture"),
            File_(@"content\cars\car_a_gt\y.texture"),
        ]);

        Assert.Equal(@"content\cars", root);
    }

    [Fact]
    public void One_file_makes_its_own_folder_the_common_root()
    {
        Assert.Equal(@"content\cars\a", PackageUnpacker.CommonRoot([File_(@"content\cars\a\x.texture")]));
    }

    [Fact]
    public void An_empty_package_has_no_common_root_rather_than_crashing_on_the_first_entry()
    {
        Assert.Equal("", PackageUnpacker.CommonRoot([]));
    }

    // ------------------------------------------------------------------ guards before any write

    [Fact]
    public void Unpacking_is_refused_before_writing_when_the_output_drive_cannot_hold_it()
    {
        // Claim more bytes than any drive has, so the guard fires wherever the tests run.
        var info = new PackageInfo(Path.Combine(_root, "big.kspkg"),
            [File_(@"content\a.texture", long.MaxValue / 2)], 0, long.MaxValue / 2, @"content");
        string output = Path.Combine(_root, "out");

        var ex = Assert.Throws<GameArchiveException>(() => PackageUnpacker.Unpack(info, output));

        Assert.Contains("not enough free space", ex.Message);
        Assert.Empty(Directory.GetFiles(output));   // the folder is made, nothing is put in it
    }

    [Fact]
    public void The_output_folder_is_created_rather_than_demanded_to_exist_already()
    {
        var info = new PackageInfo(Path.Combine(_root, "p.kspkg"), [], 0, 0, "");
        string output = Path.Combine(_root, "deep", "nested", "out");

        // No package to open, so this throws on Open — but only AFTER the folder is made, which is
        // what lets an access error surface as UnauthorizedAccessException for the elevation offer.
        Assert.ThrowsAny<Exception>(() => PackageUnpacker.Unpack(info, output));

        Assert.True(Directory.Exists(output));
    }
}
