using EvoMods.Core.Filters;
using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Tests;

/// <summary>
/// The filters this build actually carries, checked against the real embedded bytes.
/// </summary>
/// <remarks>
/// These are the build-time equivalent of the validation a dropped zip would get at drop time — the
/// same rules, one trigger point earlier. A filter shipping with a space in its name, or referencing
/// a curve nothing supplies, would install perfectly and silently fail to render; catching that in
/// CI is much better than catching it by looking at the sky.
/// </remarks>
public class EmbeddedBundleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("evomods-embedded-").FullName;
    private readonly EmbeddedFilterBundle _bundle = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class NoArchive : IStockRegistry
    {
        public bool Available => false;
        public string Describe => "no archive";
        public byte[]? Read(string reference) => null;
    }

    private IGameAssets Game() => new GameAssets(_root, new NoArchive());

    /// <summary>Curves the game itself ships, which a ported filter is always allowed to lean on.</summary>
    private static bool IsStockCurve(string reference) =>
        RefPath.Canon(reference).StartsWith($"{FilterSpec.PpDir}/natural1/", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Every_filter_the_bundle_names_has_bytes_behind_it()
    {
        Assert.NotEmpty(_bundle.Filters);
        foreach (FilterEntry entry in _bundle.Filters)
            Assert.NotEmpty(_bundle.ReadFilter(entry, Game()));
    }

    [Fact]
    public void No_embedded_filter_name_contains_a_space()
    {
        // A space makes the game list it, let it be selected, and never load it. No error anywhere.
        Assert.All(_bundle.Filters, e => Assert.True(FilterSpec.IsLoadableName(e.Name), e.Name));
    }

    [Fact]
    public void Every_embedded_filter_decodes_as_protobuf_and_re_encodes_byte_identically()
    {
        foreach (FilterEntry entry in _bundle.Filters)
        {
            byte[] bytes = _bundle.ReadFilter(entry, Game());
            Assert.Equal(bytes, PbTree.EncodeTree(PbTree.ParseTree(bytes)));
        }
    }

    [Fact]
    public void Every_curve_the_embedded_filters_reference_is_either_stock_or_embedded()
    {
        foreach (FilterEntry entry in _bundle.Filters)
        {
            foreach (string curve in FilterPlanner.CurveRefs(_bundle.ReadFilter(entry, Game())))
            {
                Assert.True(IsStockCurve(curve) || _bundle.ReadAsset(curve, Game()) is not null,
                    $"{entry.Name} references {curve}, which is neither stock nor carried here");
            }
        }
    }

    [Fact]
    public void Each_embedded_filter_references_exactly_seven_curves()
    {
        // A canary on a bad rebuild rather than a rule of the format: every one of these was ported
        // from the same base, so a filter arriving with a different count means something upstream
        // changed and the assumptions here deserve another look.
        foreach (FilterEntry entry in _bundle.Filters)
            Assert.Equal(7, FilterPlanner.CurveRefs(_bundle.ReadFilter(entry, Game())).Count());
    }

    [Fact]
    public void The_shared_curve_is_carried_even_though_no_filter_here_owns_its_folder()
    {
        // Video_Hero and Video_Hero_Soft both point into pure_gamma_full's folder, and this bundle
        // does not offer Pure_Gamma_Full. The reference implementation attaches that curve to a
        // hand-written list on Video_Hero, so installing Soft alone ships a dangling reference.
        const string shared =
            "content/tracks/common_assets/post_process/pure_gamma_full/exposure_compensation.curve";

        Assert.NotNull(_bundle.ReadAsset(shared, Game()));

        FilterEntry soft = _bundle.Filters.Single(f => f.Name == "Video_Hero_Soft");
        Assert.Contains(RefPath.Canon(shared),
            FilterPlanner.CurveRefs(_bundle.ReadFilter(soft, Game())), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_reference_outside_the_post_process_folder_is_not_served()
    {
        Assert.Null(_bundle.ReadAsset("system/post_processing.table", Game()));
        Assert.Null(_bundle.ReadAsset("content/tracks/sebring/sebring.scene", Game()));
    }

    [Fact]
    public void An_asset_this_bundle_does_not_carry_reads_as_null_rather_than_throwing()
    {
        Assert.Null(_bundle.ReadAsset($"{FilterSpec.PpDir}/video_hero/nonexistent.curve", Game()));
    }
}
