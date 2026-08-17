using EvoMods.Core.Filters;
using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

using static EvoMods.Core.Tests.FilterFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// What the survey says, and what showing and hiding actually write.
/// </summary>
/// <remarks>
/// The states worth their own cases are the half-installed ones. A filter registered without its
/// file, or with its file and no registration, both look fine from every angle except the one that
/// matters — so if the survey does not name them nothing will.
/// </remarks>
public class FilterStateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("evomods-filters-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>The stock table served from memory, so none of this needs a 67 GB archive.</summary>
    private sealed class FakeStock(byte[]? table, bool available = true) : IStockRegistry
    {
        public bool Available => available;
        public string Describe => "content.kspkg.bak";
        public byte[]? Read(string reference) =>
            reference == FilterSpec.PpTable ? table : null;
    }

    private void WriteTable(byte[] bytes)
    {
        string path = RefPath.RealPath(_root, FilterSpec.PpTable);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void WriteFilterFile(FilterEntry entry)
    {
        string path = RefPath.RealPath(_root, entry.InstallRef);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Filter(StockCurve("exposure_compensation.curve")));
    }

    private byte[] Live() => File.ReadAllBytes(RefPath.RealPath(_root, FilterSpec.PpTable));

    private static readonly FilterEntry Hero = new("Video_Hero", "video_hero");

    private FilterSurvey Survey(IEnumerable<FilterEntry>? ours = null, IStockRegistry? stock = null) =>
        FilterStates.Detect(_root, ours, stock ?? new FakeStock(StockTable()));

    // ---- the stock nine

    [Fact]
    public void A_filter_the_game_ships_hidden_reads_as_stock_hidden()
    {
        WriteTable(StockTable());

        FilterSurvey survey = Survey();

        Assert.Equal(9, survey.Filters.Count(f => f.State == FilterState.StockHidden));
        Assert.Equal(FilterSpec.ShippedHidden.Order(), survey.ShippedHidden.Select(f => f.Name).Order());
    }

    [Fact]
    public void A_stock_filter_we_showed_reads_as_unhidden_so_it_can_be_put_back()
    {
        WriteTable(Table([.. StockNames.Select(n => Row(n, visible: true))]));

        FilterSurvey survey = Survey();

        Assert.Equal(9, survey.Filters.Count(f => f.State == FilterState.StockUnhidden));
        Assert.Equal(9, survey.ShippedHidden.Count());
    }

    [Fact]
    public void A_stock_filter_someone_else_hid_is_reported_and_not_claimed()
    {
        // Shipped visible, hidden here. Neither ours nor Kunos' doing, so it is described, not fixed.
        WriteTable(Table([.. StockNames.Select(n => Row(n, visible: n != "Natural"))]));

        FilterStatus natural = Survey().Filters.Single(f => f.Name == "Natural");

        Assert.Equal(FilterState.StockSuppressed, natural.State);
        Assert.False(natural.IsShippedHidden);
    }

    [Fact]
    public void A_filter_neither_stock_nor_ours_is_left_alone()
    {
        WriteTable(Table(Row("Default", true), Row("AC1_Movie", true)));

        FilterStatus other = Survey().Filters.Single(f => f.Name == "AC1_Movie");

        Assert.Equal(FilterState.Foreign, other.State);
    }

    // ---- ours

    [Fact]
    public void Our_filter_with_files_and_a_row_is_installed()
    {
        WriteTable(Table([.. StockNames.Select(n => Row(n, false)), Row("Video_Hero", true)]));
        WriteFilterFile(Hero);

        Assert.Equal(FilterState.Installed, Survey([Hero]).Filters.Single(f => f.Name == "Video_Hero").State);
    }

    [Fact]
    public void Our_filter_with_files_and_no_row_is_files_present_but_not_registered()
    {
        // What a game patch or a Steam file verification leaves behind.
        WriteTable(StockTable());
        WriteFilterFile(Hero);

        Assert.Equal(FilterState.FilesPresentButNotRegistered,
            Survey([Hero]).Filters.Single(f => f.Name == "Video_Hero").State);
    }

    [Fact]
    public void A_row_pointing_at_a_file_that_is_not_there_is_registered_but_missing()
    {
        // Listed in the video options, selectable, and silently fails to load.
        WriteTable(Table([.. StockNames.Select(n => Row(n, false)), Row("Video_Hero", true)]));

        Assert.Equal(FilterState.RegisteredButFileMissing,
            Survey([Hero]).Filters.Single(f => f.Name == "Video_Hero").State);
    }

    [Fact]
    public void Our_filter_with_neither_is_not_installed()
    {
        WriteTable(StockTable());

        Assert.Equal(FilterState.NotInstalled,
            Survey([Hero]).Filters.Single(f => f.Name == "Video_Hero").State);
    }

    // ---- degrading without an archive

    [Fact]
    public void With_no_archive_the_stock_half_comes_from_the_shipped_list_and_the_survey_says_so()
    {
        WriteTable(StockTable());

        FilterSurvey survey = Survey(stock: new UnavailableStockRegistry("no archive here"));

        Assert.False(survey.StockAvailable);
        Assert.Equal(9, survey.ShippedHidden.Count());
        Assert.All(survey.ShippedHidden, f => Assert.NotNull(f.Note));
    }

    [Fact]
    public void With_no_archive_a_stock_filter_is_still_not_mistaken_for_someone_elses()
    {
        // "Not in the hidden nine" is not the same as "not stock" — without the full shipped list
        // Default and Natural would be reported as another tool's rows.
        WriteTable(StockTable());

        FilterSurvey survey = Survey(stock: new UnavailableStockRegistry("no archive here"));

        Assert.DoesNotContain(survey.Filters, f => f.State == FilterState.Foreign);
        Assert.Equal(8, survey.Filters.Count(f => f.State == FilterState.StockShown));
    }

    // ---- a status check must always answer

    [Fact]
    public void An_unreadable_table_does_not_throw_out_of_a_status_check()
    {
        WriteTable([0xFF, 0xFF, 0xFF]);

        FilterSurvey survey = Survey();

        Assert.Empty(survey.Filters);
        Assert.False(survey.StockAvailable);
    }

    [Fact]
    public void A_missing_table_does_not_throw_out_of_a_status_check()
    {
        FilterSurvey survey = Survey();

        Assert.Empty(survey.Filters);
    }

    // ---- showing and hiding

    private FilterInstaller Installer(IStockRegistry? stock = null) =>
        new(_root, _ => { }, stock ?? new FakeStock(StockTable()));

    [Fact]
    public void Showing_the_shipped_hidden_filters_offers_all_nine()
    {
        WriteTable(StockTable());

        Assert.Equal(9, Installer().ShowShippedHidden());

        Assert.DoesNotContain(FilterTable.Read(Live()), r => !r.Visible);
    }

    [Fact]
    public void Showing_them_a_second_time_changes_nothing()
    {
        WriteTable(StockTable());
        Installer().ShowShippedHidden();
        byte[] after = Live();

        Assert.Equal(0, Installer().ShowShippedHidden());

        Assert.Equal(after, Live());
    }

    [Fact]
    public void Hiding_them_again_restores_the_file_byte_for_byte()
    {
        WriteTable(StockTable());
        byte[] original = Live();

        Installer().ShowShippedHidden();
        Assert.NotEqual(original, Live());
        Installer().RestoreShippedHidden();

        Assert.Equal(original, Live());
    }

    [Fact]
    public void Showing_leaves_every_other_row_byte_identical()
    {
        WriteTable(Table([.. StockNames.Select(n => Row(n, !StockHiddenNames.Contains(n))),
                          Row("SomeoneElsesFilter", true)]));

        Installer().ShowShippedHidden();

        List<FilterRow> rows = FilterTable.Read(Live());
        Assert.Equal(18, rows.Count);
        Assert.True(rows.Single(r => r.Name == "SomeoneElsesFilter").Visible);
    }

    [Fact]
    public void Nothing_is_written_when_there_is_nothing_to_change()
    {
        WriteTable(StockTable());
        DateTime before = File.GetLastWriteTimeUtc(RefPath.RealPath(_root, FilterSpec.PpTable));

        Assert.Equal(0, Installer().RestoreShippedHidden());

        Assert.Equal(before, File.GetLastWriteTimeUtc(RefPath.RealPath(_root, FilterSpec.PpTable)));
    }

    [Fact]
    public void The_table_still_parses_after_a_show_and_a_hide()
    {
        WriteTable(StockTable());
        Installer().ShowShippedHidden();
        Installer().RestoreShippedHidden();

        Assert.Equal(Live(), PbTree.EncodeTree(PbTree.ParseTree(Live())));
        Assert.Equal(17, FilterTable.Read(Live()).Count);
    }
}
