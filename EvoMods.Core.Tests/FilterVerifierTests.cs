using EvoMods.Core.Filters;
using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Refs;

using static EvoMods.Core.Tests.FilterFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// What verification reports, and what it refuses to call a problem.
/// </summary>
/// <remarks>
/// The two failures worth catching are both invisible from inside the game: a row pointing at a file
/// that is not there, and a name with a space in it. Both leave the filter listed and selectable,
/// and both simply keep rendering whatever was selected before.
/// </remarks>
public class FilterVerifierTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("evomods-verify-").FullName;
    private readonly EmbeddedFilterBundle _bundle = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class NoArchive : IStockRegistry
    {
        public bool Available => false;
        public string Describe => "no archive to compare against";
        public byte[]? Read(string reference) => null;
    }

    private sealed class FakeStock(byte[] table) : IStockRegistry
    {
        public bool Available => true;
        public string Describe => "content.kspkg.bak";
        public byte[]? Read(string reference) => reference == FilterSpec.PpTable ? table : null;
    }

    private void Write(string reference, byte[] bytes)
    {
        string path = RefPath.RealPath(_root, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void GivenStockInstall(params byte[][] extraRows) =>
        Write(FilterSpec.PpTable, Table([
            .. StockNames.Select(n => Row(n, !StockHiddenNames.Contains(n))), .. extraRows]));

    private VerifyReport Verify(IStockRegistry? stock = null) =>
        new FilterVerifier(_root, _bundle, stock ?? new FakeStock(StockTable())).Run();

    [Fact]
    public void Verify_reports_counts_even_when_nothing_of_ours_is_installed()
    {
        GivenStockInstall();

        VerifyReport report = Verify();

        Assert.True(report.Passed);
        Assert.Contains(report.Lines, l => l.Contains("rows: 17"));
        Assert.Contains(report.Lines, l => l.Contains("0/5 installed"));
        Assert.Contains(report.Lines, l => l.Contains("shipped hidden: 9"));
    }

    [Fact]
    public void Verify_fails_when_a_registered_filter_has_no_file_on_disk()
    {
        GivenStockInstall(Row("Video_Hero", PathFor("Video_Hero"), true));

        VerifyReport report = Verify();

        Assert.False(report.Passed);
        Assert.Contains(report.Problems, p => p.Contains("Video_Hero") && p.Contains("silently"));
    }

    [Fact]
    public void Verify_fails_when_a_row_carries_a_space_the_game_does_not_define()
    {
        GivenStockInstall(Row("My Filter", true));
        Write(PathFor("My Filter").Replace('\\', '/'), Filter());

        VerifyReport report = Verify();

        Assert.False(report.Passed);
        Assert.Contains(report.Problems, p => p.Contains("localization key"));
    }

    [Fact]
    public void A_stock_name_with_a_space_is_not_a_problem()
    {
        // en.loc defines "TV 1" and "Natural 5". Only NEW names have to avoid spaces.
        GivenStockInstall();

        VerifyReport report = Verify();

        Assert.True(report.Passed);
        Assert.Contains(report.Lines, l => l.Contains("names: 0"));
    }

    [Fact]
    public void Verify_fails_when_a_filter_references_a_curve_that_is_not_there()
    {
        GivenStockInstall(Row("Video_Hero", PathFor("Video_Hero"), true));
        Write(PathFor("Video_Hero").Replace('\\', '/'), Filter(StockCurve("missing.curve")));

        VerifyReport report = Verify(new NoArchive());

        Assert.False(report.Passed);
        Assert.Contains(report.Problems, p => p.Contains("missing.curve"));
    }

    [Fact]
    public void A_curve_that_lives_only_in_the_archive_is_not_a_problem()
    {
        // A packed install. Checking the disk alone would report every dependency broken.
        GivenStockInstall(Row("Video_Hero", PathFor("Video_Hero"), true));
        Write(PathFor("Video_Hero").Replace('\\', '/'), Filter(StockCurve("white_balance.curve")));

        VerifyReport report = new FilterVerifier(_root, _bundle, new ArchiveHolding(
            RefPath.Canon(StockCurve("white_balance.curve")), StockTable())).Run();

        Assert.DoesNotContain(report.Problems, p => p.Contains("white_balance"));
    }

    private sealed class ArchiveHolding(string curve, byte[] table) : IStockRegistry
    {
        public bool Available => true;
        public string Describe => "content.kspkg.bak";
        public byte[]? Read(string reference) =>
            reference == FilterSpec.PpTable ? table
            : RefPath.Canon(reference).Equals(curve, StringComparison.OrdinalIgnoreCase) ? [1, 2, 3]
            : null;
    }

    [Fact]
    public void Verify_says_when_it_had_no_archive_rather_than_failing()
    {
        GivenStockInstall();

        VerifyReport report = Verify(new NoArchive());

        Assert.True(report.Passed);
        Assert.Contains(report.Lines, l => l.Contains("the shipped list"));
    }

    [Fact]
    public void Verify_reports_the_reference_implementations_leftover_backups()
    {
        GivenStockInstall();
        Write("system/post_processing.table.bak", [1]);
        Write("system/post_processing.table.previs", [1]);

        VerifyReport report = Verify();

        Assert.Contains(report.Lines, l => l.Contains("leftover backups: 2") && l.Contains("left alone"));
        Assert.True(report.Passed);
    }

    [Fact]
    public void An_unreadable_table_is_a_problem_rather_than_an_exception()
    {
        Write(FilterSpec.PpTable, [0xFF, 0xFF]);

        VerifyReport report = Verify();

        Assert.False(report.Passed);
        Assert.Contains(report.Problems, p => p.Contains("could not be read"));
    }

    [Fact]
    public void Files_present_with_no_row_are_reported_without_failing()
    {
        // Recoverable in one click, so it is news rather than damage.
        GivenStockInstall();
        foreach (FilterEntry entry in _bundle.Filters)
            Write(entry.InstallRef, Filter());

        VerifyReport report = Verify();

        Assert.True(report.Passed);
        Assert.Contains(report.Lines, l => l.Contains("have files but no row"));
    }
}
