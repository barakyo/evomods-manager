using EvoMods.Core.Filters;
using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

using static EvoMods.Core.Tests.FilterFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// Installing and removing, against the filters this build really carries.
/// </summary>
/// <remarks>
/// The assertions that matter are about what is NOT touched. This writes into a file the game owns
/// and other tools also write to, so every test here that checks another mod's row survived, or that
/// a stock row came back byte-identical, is guarding the failure this project has already had once:
/// a whole-file restore that quietly deleted somebody else's registration.
/// </remarks>
public class FilterInstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("evomods-install-").FullName;
    private readonly EmbeddedFilterBundle _bundle = new();
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class NoArchive : IStockRegistry
    {
        public bool Available => false;
        public string Describe => "no archive";
        public byte[]? Read(string reference) => null;
    }

    private sealed class FakeStock(byte[] table) : IStockRegistry
    {
        public bool Available => true;
        public string Describe => "content.kspkg.bak";
        public byte[]? Read(string reference) => reference == FilterSpec.PpTable ? table : null;
    }

    private FilterInstaller Installer(IStockRegistry? stock = null) =>
        new(_root, _log.Add, stock ?? new FakeStock(StockTable()));

    private void Write(string reference, byte[] bytes)
    {
        string path = RefPath.RealPath(_root, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private byte[] Live() => File.ReadAllBytes(RefPath.RealPath(_root, FilterSpec.PpTable));

    private bool Exists(string reference) => File.Exists(RefPath.RealPath(_root, reference));

    /// <summary>An unpacked install: the stock table, plus every stock curve the bundle leans on.</summary>
    private void GivenAnInstall(params byte[][] extraRows)
    {
        Write(FilterSpec.PpTable, Table([
            .. StockNames.Select(n => Row(n, !StockHiddenNames.Contains(n))), .. extraRows]));

        var game = new GameAssets(_root, new NoArchive());
        foreach (FilterEntry entry in _bundle.Filters)
        {
            foreach (string curve in FilterPlanner.CurveRefs(_bundle.ReadFilter(entry, game)))
            {
                if (curve.Contains("/natural1/", StringComparison.OrdinalIgnoreCase))
                    Write(curve, [1, 2, 3]);
            }
        }
    }

    private const string SharedCurve =
        "content/tracks/common_assets/post_process/pure_gamma_full/exposure_compensation.curve";

    // ---- installing

    [Fact]
    public void Installing_writes_each_filter_under_a_folder_named_after_itself()
    {
        GivenAnInstall();

        Assert.Equal(5, Installer().Install(_bundle));

        foreach (FilterEntry entry in _bundle.Filters)
            Assert.True(Exists(entry.InstallRef), entry.InstallRef);
    }

    [Fact]
    public void Installing_registers_every_filter_visible()
    {
        GivenAnInstall();
        Installer().Install(_bundle);

        List<FilterRow> rows = FilterTable.Read(Live());
        foreach (FilterEntry entry in _bundle.Filters)
            Assert.True(rows.Single(r => r.Name == entry.Name).Visible, entry.Name);
    }

    [Fact]
    public void Installing_plants_the_curve_the_filters_share()
    {
        GivenAnInstall();
        Installer().Install(_bundle);

        Assert.True(Exists(SharedCurve));
    }

    [Fact]
    public void Installing_one_filter_still_plants_the_curve_it_shares()
    {
        // Video_Hero_Soft alone. The reference implementation attaches that curve to Video_Hero's
        // hand-written extras list, so this is the exact case it gets wrong.
        GivenAnInstall();
        FilterEntry soft = _bundle.Filters.Single(f => f.Name == "Video_Hero_Soft");

        Installer().Install(_bundle, [soft]);

        Assert.True(Exists(SharedCurve));
        Assert.False(Exists(_bundle.Filters.Single(f => f.Name == "Video_Hero").InstallRef));
    }

    [Fact]
    public void Installing_does_not_plant_a_second_copy_of_a_curve_the_game_has()
    {
        GivenAnInstall();
        string stockCurve = $"{FilterSpec.PpDir}/natural1/white_balance.curve";
        Write(stockCurve, [7, 7, 7]);

        Installer().Install(_bundle);

        Assert.Equal<byte[]>([7, 7, 7], File.ReadAllBytes(RefPath.RealPath(_root, stockCurve)));
    }

    [Fact]
    public void Installing_twice_leaves_one_row_per_filter()
    {
        GivenAnInstall();
        Installer().Install(_bundle);
        Installer().Install(_bundle);

        List<FilterRow> rows = FilterTable.Read(Live());
        foreach (FilterEntry entry in _bundle.Filters)
            Assert.Single(rows, r => r.Name == entry.Name);
    }

    [Fact]
    public void Installing_leaves_every_stock_row_readable_and_unchanged()
    {
        GivenAnInstall();
        List<FilterRow> before = FilterTable.Read(Live());
        Installer().Install(_bundle);
        List<FilterRow> after = FilterTable.Read(Live());

        foreach (FilterRow row in before)
        {
            FilterRow now = after.Single(r => r.Name == row.Name);
            Assert.Equal(row.Path, now.Path);
            Assert.Equal(row.Visible, now.Visible);
        }
    }

    [Fact]
    public void Installing_leaves_another_mods_row_alone()
    {
        GivenAnInstall(Row("AC1_Movie", true));

        Installer().Install(_bundle);

        Assert.Single(FilterTable.Read(Live()), r => r.Name == "AC1_Movie");
    }

    [Fact]
    public void A_bundle_claiming_a_name_the_game_already_ships_is_refused()
    {
        GivenAnInstall();

        InstallException e = Assert.Throws<InstallException>(
            () => Installer().Install(_bundle, [new FilterEntry("Natural", "video_hero")]));

        Assert.Contains("already ships", e.Message);
    }

    [Fact]
    public void A_missing_dependency_leaves_the_table_and_the_disk_untouched()
    {
        // No stock curves anywhere, and no archive to find them in.
        Write(FilterSpec.PpTable, StockTable());
        byte[] before = Live();

        Assert.Throws<InstallException>(() => Installer(new NoArchive()).Install(_bundle));

        Assert.Equal(before, Live());
        Assert.False(Exists(_bundle.Filters[0].InstallRef));
    }

    // ---- removing

    [Fact]
    public void Uninstalling_restores_the_table_to_the_bytes_it_had_before_installing()
    {
        GivenAnInstall();
        byte[] before = Live();

        Installer().Install(_bundle);
        Assert.NotEqual(before, Live());
        Installer().Uninstall(_bundle);

        Assert.Equal(before, Live());
    }

    [Fact]
    public void Uninstalling_deletes_the_folders_it_created()
    {
        GivenAnInstall();
        Installer().Install(_bundle);

        Installer().Uninstall(_bundle);

        foreach (FilterEntry entry in _bundle.Filters)
            Assert.False(Exists(entry.InstallRef), entry.InstallRef);
    }

    [Fact]
    public void Uninstalling_leaves_the_shared_curve_alone()
    {
        // It lives in a folder belonging to a filter this bundle does not offer, so there is no way
        // to prove we planted it. An orphaned 203 bytes is the cheaper mistake.
        GivenAnInstall();
        Installer().Install(_bundle);

        Installer().Uninstall(_bundle);

        Assert.True(Exists(SharedCurve));
    }

    [Fact]
    public void Uninstalling_never_removes_a_row_the_game_ships()
    {
        GivenAnInstall();
        Installer().Install(_bundle);

        Installer().Uninstall(_bundle, [new FilterEntry("Natural", "natural1")]);

        Assert.Single(FilterTable.Read(Live()), r => r.Name == "Natural");
    }

    [Fact]
    public void Uninstalling_never_deletes_a_folder_the_game_ships_a_filter_from()
    {
        GivenAnInstall();
        string stockAsset = $"{FilterSpec.PpDir}/{Slug("Natural")}/{Slug("Natural")}.postprocessing";
        Write(stockAsset, [1, 2, 3]);

        Installer().Uninstall(_bundle, [new FilterEntry("Natural", Slug("Natural"))]);

        Assert.True(Exists(stockAsset));
    }

    [Fact]
    public void Uninstalling_leaves_another_mods_row_alone()
    {
        GivenAnInstall(Row("AC1_Movie", true));
        Installer().Install(_bundle);

        Installer().Uninstall(_bundle);

        Assert.Single(FilterTable.Read(Live()), r => r.Name == "AC1_Movie");
    }

    [Fact]
    public void Uninstalling_what_was_never_installed_changes_nothing()
    {
        GivenAnInstall();
        byte[] before = Live();

        Assert.Equal(0, Installer().Uninstall(_bundle));

        Assert.Equal(before, Live());
    }

    // ---- what the survey says afterwards

    [Fact]
    public void The_survey_calls_them_installed_once_they_are()
    {
        GivenAnInstall();
        Installer().Install(_bundle);

        FilterSurvey survey = Installer().Survey(_bundle.Filters);

        Assert.Equal(5, survey.Filters.Count(f => f.State == FilterState.Installed));
    }

    [Fact]
    public void The_survey_spots_a_patch_that_reverted_the_table()
    {
        GivenAnInstall();
        Installer().Install(_bundle);

        // What a game update or a Steam file verification does: the table goes back, files stay.
        Write(FilterSpec.PpTable, StockTable());

        FilterSurvey survey = Installer().Survey(_bundle.Filters);

        Assert.Equal(5, survey.Filters.Count(f => f.State == FilterState.FilesPresentButNotRegistered));
    }

    [Fact]
    public void Reinstalling_after_a_patch_puts_the_rows_back()
    {
        GivenAnInstall();
        Installer().Install(_bundle);
        Write(FilterSpec.PpTable, StockTable());

        Installer().Install(_bundle);

        Assert.Equal(5, Installer().Survey(_bundle.Filters).Filters.Count(f => f.State == FilterState.Installed));
    }
}
