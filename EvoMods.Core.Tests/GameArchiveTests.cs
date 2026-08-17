using EvoMods.Core.Game;

namespace EvoMods.Core.Tests;

/// <summary>
/// Detecting how the game reads its content, and the guards around switching that.
/// </summary>
/// <remarks>
/// The extraction path itself is validated against the real 68.5 GB archive with
/// <c>check-unpack</c>, which samples loose files and compares them byte-for-byte; it cannot be
/// covered here without shipping a game archive. These tests cover the decisions made AROUND it,
/// which is where the install-bricking mistakes live.
/// </remarks>
public class GameArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flatpad-game-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relative, long bytes = 0, DateTime? written = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (FileStream fs = File.Create(path))
            fs.SetLength(bytes);
        if (written is not null)
            File.SetLastWriteTimeUtc(path, written.Value);
        return path;
    }

    private void LooseContent() => Directory.CreateDirectory(Path.Combine(_root, "content", "tracks"));

    [Fact]
    public void Loose_content_without_tracks_still_counts_as_unpacked()
    {
        // Archive state describes the ARCHIVE. Whether the install is ready for a track mod is a
        // separate question, asked separately — otherwise a freshly unpacked car-only package
        // reports "unknown" straight after a successful unpack.
        Directory.CreateDirectory(Path.Combine(_root, "content", "cars"));
        Touch("content.kspkg.bak", 10);

        Assert.Equal(ArchiveMode.Unpacked, GameArchive.Detect(_root).Mode);
    }

    [Fact]
    public void An_unpacked_install_whose_archive_was_deleted_is_still_unpacked()
    {
        LooseContent();

        GameArchiveState state = GameArchive.Detect(_root);

        Assert.Equal(ArchiveMode.Unpacked, state.Mode);
        Assert.Empty(state.DisabledPackages);   // …but there is nothing to revert to
    }

    [Fact]
    public void A_live_archive_means_packed_and_tracks_cannot_be_modded()
    {
        Touch("content.kspkg", 73_579_626_496);

        GameArchiveState state = GameArchive.Detect(_root);

        Assert.Equal(ArchiveMode.Packed, state.Mode);
        Assert.NotNull(state.LivePackage);
        // Derived from the archive, never hardcoded, so it survives the archive changing size.
        Assert.Equal(73_579_626_496, state.RequiredBytes);
        // Win32 matches "content.kspkg.*" against "content.kspkg" too. Listing the live archive as
        // renamed-aside would spuriously trip the ambiguity guard below.
        Assert.Empty(state.DisabledPackages);
    }

    [Fact]
    public void A_packed_install_that_also_has_a_backup_is_not_ambiguous()
    {
        Touch("content.kspkg", 100);
        Touch("content.kspkg.bak", 90);

        GameArchiveState state = GameArchive.Detect(_root);

        Assert.Equal(ArchiveMode.Packed, state.Mode);
        Assert.Equal(["content.kspkg.bak"], state.DisabledPackages.Select(Path.GetFileName));
    }

    [Fact]
    public void An_archive_renamed_aside_plus_loose_content_means_unpacked()
    {
        Touch("content.kspkg.bak", 1024);
        LooseContent();

        Assert.Equal(ArchiveMode.Unpacked, GameArchive.Detect(_root).Mode);
    }

    [Fact]
    public void A_folder_that_is_neither_is_reported_as_unknown_rather_than_guessed_at()
    {
        Assert.Equal(ArchiveMode.Unknown, GameArchive.Detect(_root).Mode);
    }

    [Fact]
    public void Disabled_archives_are_listed_newest_first()
    {
        Touch("content.kspkg.bak", 10, new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        Touch("content.kspkg.jul22update-disabled", 20, new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc));
        LooseContent();

        GameArchiveState state = GameArchive.Detect(_root);

        Assert.Equal(["content.kspkg.jul22update-disabled", "content.kspkg.bak"],
            state.DisabledPackages.Select(Path.GetFileName));
    }

    [Fact]
    public void Revert_refuses_to_guess_between_archives_from_different_game_versions()
    {
        // A game update downloads a FRESH archive, so an install accumulates them. Restoring the
        // wrong one puts stale content under a newer build — and nothing would say so until things
        // broke in-game.
        Touch("content.kspkg.bak", 10, new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        Touch("content.kspkg.jul22update-disabled", 20, new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc));
        LooseContent();

        var ex = Assert.Throws<GameArchiveException>(() => GameArchive.RevertToPacked(_root));

        Assert.Contains("ambiguous", ex.Message);
        Assert.Contains("content.kspkg.bak", ex.Message);
        Assert.Contains("content.kspkg.jul22update-disabled", ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "content.kspkg")));   // nothing was moved
    }

    [Fact]
    public void Revert_restores_the_only_archive_there_is()
    {
        Touch("content.kspkg.bak", 42);
        LooseContent();

        GameArchive.RevertToPacked(_root);

        Assert.True(File.Exists(Path.Combine(_root, "content.kspkg")));
        Assert.False(File.Exists(Path.Combine(_root, "content.kspkg.bak")));
        Assert.Equal(ArchiveMode.Packed, GameArchive.Detect(_root).Mode);
    }

    [Fact]
    public void Revert_restores_the_archive_the_caller_picked()
    {
        // Refusing to GUESS is right; a caller that asked the user is not guessing.
        Touch("content.kspkg.bak", 10, new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        string july = Touch("content.kspkg.jul22update-disabled", 20,
            new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc));
        LooseContent();

        GameArchive.RevertToPacked(_root, july);

        Assert.Equal(ArchiveMode.Packed, GameArchive.Detect(_root).Mode);
        Assert.Equal(20, new FileInfo(Path.Combine(_root, "content.kspkg")).Length);
        Assert.True(File.Exists(Path.Combine(_root, "content.kspkg.bak")));   // the other is untouched
    }

    [Fact]
    public void Revert_rejects_a_file_that_is_not_one_of_this_installs_archives()
    {
        Touch("content.kspkg.bak", 10);
        string stranger = Touch("some_other_file.kspkg", 10);
        LooseContent();

        var ex = Assert.Throws<GameArchiveException>(() => GameArchive.RevertToPacked(_root, stranger));

        Assert.Contains("not a renamed-aside archive", ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "content.kspkg")));
    }

    [Fact]
    public void Revert_is_a_no_op_error_when_the_archive_is_already_live()
    {
        Touch("content.kspkg", 42);

        Assert.Throws<GameArchiveException>(() => GameArchive.RevertToPacked(_root));
    }

    [Fact]
    public void Revert_says_so_when_there_is_nothing_to_restore()
    {
        LooseContent();

        var ex = Assert.Throws<GameArchiveException>(() => GameArchive.RevertToPacked(_root));

        Assert.Contains("no renamed-aside archive", ex.Message);
    }

    [Fact]
    public void Unpacking_an_already_unpacked_install_fails_before_touching_anything()
    {
        Touch("content.kspkg.bak", 42);
        LooseContent();

        var ex = Assert.Throws<GameArchiveException>(() => GameArchive.Unpack(_root));

        Assert.Contains("already unpacked", ex.Message);
    }

    [Theory]
    [InlineData(73_579_626_496, 590_000_000_000, true)]     // the real install: comfortable
    [InlineData(73_579_626_496, 70_000_000_000, false)]     // more free than the unpack writes, still short
    [InlineData(73_579_626_496, 73_579_626_496, true)]      // exactly enough
    public void The_space_needed_is_derived_from_the_archive_not_hardcoded(
        long archiveBytes, long freeBytes, bool fits)
    {
        // Unpacking roughly DOUBLES the install: the archive is kept and its contents written out
        // alongside. Computing the requirement from the archive means it survives the archive
        // changing size with a game update, which a hardcoded "needs 140 GB" would not.
        var state = new GameArchiveState(ArchiveMode.Packed, "content.kspkg", [], false,
            archiveBytes, freeBytes);

        Assert.Equal(archiveBytes, state.RequiredBytes);
        Assert.Equal(fits, state.HasEnoughSpace);
    }

    [Theory]
    [InlineData(512, "512 bytes")]
    [InlineData(73_579_626_496, "68.5 GB")]
    [InlineData(1_900_544, "1.8 MB")]
    public void Sizes_are_reported_the_way_a_person_would_read_them(long value, string expected)
    {
        Assert.Equal(expected, GameArchive.Bytes(value));
    }

    [Fact]
    public void A_folder_without_the_game_exe_is_not_the_game()
    {
        Assert.False(GameLocator.LooksLikeGame(_root));
        Touch("AssettoCorsaEVO.exe");
        Assert.True(GameLocator.LooksLikeGame(_root));
    }

    // ------------------------------------------------------------------ the listing parse
    //
    // Reading the library's own listing back is the only way to enumerate the file table through
    // its public API, so these lines are copied verbatim from a real 119,443-entry listing. The
    // three flag renderings below are the only three that archive produces.

    [Fact]
    public void A_directory_pseudo_entry_is_recognised_by_its_flag_not_by_its_zero_size()
    {
        // Extracting one reports success and writes nothing, so counting it as a file inflates
        // every total and, in check-unpack, crashes the byte compare that reads it back.
        List<ArchiveEntry> entries = GameArchive.ParseListing(
            ["cfg - offset:00000000, size:00000000 hash: 9685107974627194447 (IsDirectory)"]);

        Assert.True(Assert.Single(entries).IsDirectory);
    }

    [Fact]
    public void A_real_zero_byte_file_is_still_a_file_because_the_archive_holds_dozens_of_them()
    {
        // 36 of them in the June listing. The old "Size > 0" test dropped every one from the
        // sample AND from the denominator, so check-unpack quietly under-reported its own scope.
        List<ArchiveEntry> entries = GameArchive.ParseListing(
            [@"content\cars\ks_mini_jcs_1990\data\tuning\minimkvi_bigbrake.tuningpart"
             + " - offset:589492194, size:00000000 hash: 11728099583420404163 (Encrypted)"]);

        ArchiveEntry entry = Assert.Single(entries);
        Assert.False(entry.IsDirectory);
        Assert.Equal(0, entry.Size);
    }

    [Theory]
    [InlineData("(Encrypted)")]
    [InlineData("(0)")]
    public void Whether_a_file_is_encrypted_is_no_concern_of_ours_the_library_decrypts_it(string flags)
    {
        List<ArchiveEntry> entries = GameArchive.ParseListing(
            [@"cfg\crazy1.lut - offset:111DA9CB0E, size:00000027 hash: 12402782211981809042 " + flags]);

        ArchiveEntry entry = Assert.Single(entries);
        Assert.False(entry.IsDirectory);
        Assert.Equal(0x27, entry.Size);
        Assert.Equal(0x111DA9CB0E, entry.Offset);
    }

    [Fact]
    public void A_line_without_the_flags_column_degrades_to_a_file_rather_than_failing_to_parse()
    {
        // The submodule writes this line and the pack format HAS changed across game versions. If
        // the tail ever moves, every entry should still parse — which is what it did before flags
        // were read at all — instead of a format tweak making a 70 GB archive unreadable.
        List<ArchiveEntry> entries = GameArchive.ParseListing(
            [@"content\a.texture - offset:0000000A, size:0000000B"]);

        ArchiveEntry entry = Assert.Single(entries);
        Assert.False(entry.IsDirectory);
        Assert.Equal(0xB, entry.Size);
    }

    [Fact]
    public void A_path_containing_the_separator_the_parser_looks_for_is_not_cut_short()
    {
        // Real paths do contain " - ": content\tracks\barcelona\materials\17 - default.material.
        List<ArchiveEntry> entries = GameArchive.ParseListing(
            [@"content\tracks\barcelona\materials\17 - default.material"
             + " - offset:789F4F5B9, size:0000039B hash: 1326968994957191550 (Encrypted)"]);

        Assert.Equal(@"content\tracks\barcelona\materials\17 - default.material",
            Assert.Single(entries).Path);
    }

    [Fact]
    public void Entries_come_back_in_archive_order_so_reads_stay_sequential()
    {
        List<ArchiveEntry> entries = GameArchive.ParseListing([
            @"content\c - offset:00000030, size:00000001 hash: 3 (0)",
            @"content\a - offset:00000010, size:00000001 hash: 1 (0)",
            @"content\b - offset:00000020, size:00000001 hash: 2 (0)",
        ]);

        Assert.Equal([0x10, 0x20, 0x30], entries.Select(e => e.Offset));
    }

    // ------------------------------------------------------------------ where an entry may land
    //
    // Entry names come out of the package unvalidated, and until now every package came from Steam.
    // A mod package is downloaded from the internet, so these are the tests that stop a hostile one
    // writing outside the folder the user chose.

    [Fact]
    public void An_entry_with_a_drive_letter_is_refused_because_Path_Combine_discards_the_destination()
    {
        // The sharp edge, and the one that needs no traversal sequence: Path.Combine throws the
        // destination away entirely when the second argument is rooted.
        Assert.Null(GameArchive.SafeOutputPath(_root, @"C:\Windows\System32\evil.dll"));
    }

    [Theory]
    [InlineData(@"..\..\evil.dll")]
    [InlineData(@"content\..\..\evil.dll")]
    [InlineData(@"\evil.dll")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_entry_that_climbs_out_of_the_destination_is_refused_rather_than_written(string entry)
    {
        Assert.Null(GameArchive.SafeOutputPath(_root, entry));
    }

    [Fact]
    public void An_ordinary_entry_resolves_to_a_path_underneath_the_destination()
    {
        string? resolved = GameArchive.SafeOutputPath(_root, @"content\tracks\sebring\sebring.scene");

        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(_root, "content", "tracks", "sebring", "sebring.scene"), resolved);
    }

    [Fact]
    public void An_entry_that_doubles_back_but_stays_inside_is_allowed()
    {
        string? resolved = GameArchive.SafeOutputPath(_root, @"content\cars\..\tracks\a.scene");

        Assert.Equal(Path.Combine(_root, "content", "tracks", "a.scene"), resolved);
    }

    [Fact]
    public void A_sibling_folder_whose_name_merely_starts_with_the_destinations_is_not_inside_it()
    {
        // Without a trailing separator on the comparison, "C:\out" happily contains "C:\output\x".
        string sibling = _root + "-elsewhere";

        Assert.Null(GameArchive.SafeOutputPath(_root, Path.Combine(sibling, "a.scene")));
    }

    [Fact]
    public void The_destination_folder_itself_is_not_somewhere_an_entry_can_be_written()
    {
        Assert.Null(GameArchive.SafeOutputPath(_root, "."));
    }

    [Fact]
    public void A_line_the_parser_cannot_read_is_skipped_rather_than_taking_the_archive_with_it()
    {
        List<ArchiveEntry> entries = GameArchive.ParseListing([
            "",
            "some banner the library might print one day",
            @"content\a - offset:00000010, size:00000001 hash: 1 (0)",
        ]);

        Assert.Equal(@"content\a", Assert.Single(entries).Path);
    }
}
