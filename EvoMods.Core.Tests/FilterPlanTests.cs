using EvoMods.Core.Filters;
using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Refs;

using static EvoMods.Core.Tests.FilterFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// Working out what an install has to write, from what the filters actually reference.
/// </summary>
/// <remarks>
/// The case that matters most is a curve one filter needs and another filter's folder owns. The
/// reference implementation hangs that curve off a hand-written list attached to the wrong filter,
/// so installing the other one alone silently ships a dangling reference. Deriving the plan makes
/// that unrepresentable, and
/// <see cref="A_curve_only_the_second_filter_references_is_still_planned_when_the_first_is_not_installed"/>
/// is the test that says so.
/// </remarks>
public class FilterPlanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("evomods-plan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Shared =
        "content/tracks/common_assets/post_process/pure_gamma_full/exposure_compensation.curve";

    /// <summary>A bundle built from a dictionary, so a test can describe exactly what it carries.</summary>
    private sealed class FakeBundle(
        IReadOnlyList<FilterEntry> filters, Dictionary<string, byte[]> assets) : IFilterBundle
    {
        public string Describe => "a test bundle";
        public IReadOnlyList<FilterEntry> Filters => filters;

        public byte[] ReadFilter(FilterEntry filter, IGameAssets game) =>
            assets[RefPath.Canon(filter.InstallRef)];

        public byte[]? ReadAsset(string reference, IGameAssets game) =>
            assets.TryGetValue(RefPath.Canon(reference), out byte[]? b) ? b : null;
    }

    private sealed class NoArchive : IStockRegistry
    {
        public bool Available => false;
        public string Describe => "no archive";
        public byte[]? Read(string reference) => null;
    }

    /// <summary>An archive holding whatever a test says the game ships.</summary>
    private sealed class FakeArchive(params string[] refs) : IStockRegistry
    {
        private readonly HashSet<string> _refs = new(refs.Select(RefPath.Canon), StringComparer.OrdinalIgnoreCase);
        public bool Available => true;
        public string Describe => "content.kspkg.bak";
        public byte[]? Read(string reference) =>
            _refs.Contains(RefPath.Canon(reference)) ? [1, 2, 3] : null;
    }

    private void PutOnDisk(string reference)
    {
        string path = RefPath.RealPath(_root, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    private IGameAssets Game(IStockRegistry? stock = null) => new GameAssets(_root, stock ?? new NoArchive());

    private static readonly FilterEntry Hero = new("Video_Hero", "video_hero");
    private static readonly FilterEntry Soft = new("Video_Hero_Soft", "video_hero_soft");

    // ---- resolving against the game

    [Fact]
    public void A_curve_the_game_already_has_on_disk_is_not_copied()
    {
        string stockCurve = RefPath.Canon(StockCurve("exposure_compensation.curve"));
        PutOnDisk(stockCurve);
        var bundle = new FakeBundle([Hero], new()
        {
            [RefPath.Canon(Hero.InstallRef)] = Filter(StockCurve("exposure_compensation.curve")),
        });

        FilterPlan plan = FilterPlanner.Build(bundle, bundle.Filters, Game());

        Assert.Contains(stockCurve, plan.Resolved);
        Assert.Equal([RefPath.Canon(Hero.InstallRef)], plan.Writes.Keys);
    }

    [Fact]
    public void A_curve_that_lives_only_in_the_archive_counts_as_resolved()
    {
        // A packed install: the stock curves are inside content.kspkg, not loose. Treating that as
        // missing would refuse the install for no reason.
        string stockCurve = StockCurve("white_balance.curve");
        var bundle = new FakeBundle([Hero], new()
        {
            [RefPath.Canon(Hero.InstallRef)] = Filter(stockCurve),
        });

        FilterPlan plan = FilterPlanner.Build(bundle, bundle.Filters, Game(new FakeArchive(stockCurve)));

        Assert.Contains(RefPath.Canon(stockCurve), plan.Resolved);
        Assert.Single(plan.Writes);
    }

    [Fact]
    public void A_curve_the_filter_needs_and_the_game_lacks_is_taken_from_the_bundle()
    {
        var bundle = new FakeBundle([Hero], new()
        {
            [RefPath.Canon(Hero.InstallRef)] = Filter(FilterSpec.TablePath(Shared)),
            [Shared] = [9, 9, 9],
        });

        FilterPlan plan = FilterPlanner.Build(bundle, bundle.Filters, Game());

        Assert.Equal([9, 9, 9], plan.Writes[Shared]);
    }

    [Fact]
    public void A_curve_only_the_second_filter_references_is_still_planned_when_the_first_is_not_installed()
    {
        // Soft references a curve that lives in pure_gamma_full's folder, and Pure_Gamma_Full is not
        // being installed. A hand-maintained extras list attached to Hero misses this entirely.
        var bundle = new FakeBundle([Hero, Soft], new()
        {
            [RefPath.Canon(Hero.InstallRef)] = Filter(StockCurve("a.curve")),
            [RefPath.Canon(Soft.InstallRef)] = Filter(FilterSpec.TablePath(Shared)),
            [Shared] = [9, 9, 9],
        });
        PutOnDisk(RefPath.Canon(StockCurve("a.curve")));

        FilterPlan plan = FilterPlanner.Build(bundle, [Soft], Game());

        Assert.Contains(Shared, plan.Writes.Keys);
    }

    [Fact]
    public void Two_filters_sharing_a_curve_plan_one_copy_of_it()
    {
        var bundle = new FakeBundle([Hero, Soft], new()
        {
            [RefPath.Canon(Hero.InstallRef)] = Filter(FilterSpec.TablePath(Shared)),
            [RefPath.Canon(Soft.InstallRef)] = Filter(FilterSpec.TablePath(Shared)),
            [Shared] = [9, 9, 9],
        });

        FilterPlan plan = FilterPlanner.Build(bundle, bundle.Filters, Game());

        Assert.Equal(3, plan.Writes.Count);          // two filters plus the one shared curve
        Assert.Single(plan.Curves);
    }

    // ---- refusing

    [Fact]
    public void A_curve_nothing_can_supply_stops_the_plan_before_anything_is_written()
    {
        var bundle = new FakeBundle([Hero], new()
        {
            [RefPath.Canon(Hero.InstallRef)] = Filter(FilterSpec.TablePath(Shared)),
        });

        InstallException e = Assert.Throws<InstallException>(
            () => FilterPlanner.Build(bundle, bundle.Filters, Game()));

        Assert.Contains("exposure_compensation.curve", e.Message);
        Assert.Contains("Video_Hero", e.Message);
    }

    [Fact]
    public void A_filter_name_containing_a_space_is_refused_because_the_game_would_never_load_it()
    {
        var spaced = new FilterEntry("Video Hero", "video_hero");
        var bundle = new FakeBundle([spaced], new()
        {
            [RefPath.Canon(spaced.InstallRef)] = Filter(),
        });

        InstallException e = Assert.Throws<InstallException>(
            () => FilterPlanner.Build(bundle, bundle.Filters, Game()));

        Assert.Contains("localization key", e.Message);
    }

    // ---- what counts as a reference

    [Fact]
    public void A_reference_that_is_not_a_curve_is_ignored()
    {
        // Legacy fields in shipped assets still name a lens-dirt texture. The runtime discards them,
        // and planting one would be inventing a dependency the game does not have.
        var bundle = new FakeBundle([Hero], new()
        {
            [RefPath.Canon(Hero.InstallRef)] = Filter(@"content\postprocessing\lens_dirt.texture"),
        });

        FilterPlan plan = FilterPlanner.Build(bundle, bundle.Filters, Game());

        Assert.Single(plan.Writes);
        Assert.Empty(plan.Resolved);
    }

    [Fact]
    public void Both_curve_and_curve4_references_are_found()
    {
        byte[] filter = Filter(StockCurve("white_balance.curve"), StockCurve("master_gamma_4.curve4"));

        Assert.Equal(2, FilterPlanner.CurveRefs(filter).Count());
    }

    [Fact]
    public void A_reference_named_twice_is_planned_once()
    {
        byte[] filter = Filter(StockCurve("a.curve"), StockCurve("a.curve"));

        Assert.Single(FilterPlanner.CurveRefs(filter));
    }

    [Fact]
    public void Bytes_that_are_not_protobuf_yield_no_references_rather_than_throwing()
    {
        Assert.Empty(FilterPlanner.CurveRefs([0xFF, 0xFF, 0xFF]));
    }
}
