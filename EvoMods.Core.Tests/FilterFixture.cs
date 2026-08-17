using static EvoMods.Core.Tests.PbFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// Builds <c>post_processing.table</c> payloads and synthetic <c>.postprocessing</c> assets, so none
/// of this needs a game install or a checked-in binary.
/// </summary>
/// <remarks>
/// ⚠️ Models only the fields the code reads. A real row's payload carries fields 4 and 5 whose
/// meaning was never recovered; they are here because the row-cloning path has to preserve them
/// byte-for-byte, and a fixture without them would let a bug through that drops them.
/// <para>
/// The folder slugs are regular (<c>TV 1</c> → <c>tv1</c>) and the game's are not — it ships
/// <c>natural_5</c> beside <c>natural6</c>, and <c>tv3</c> serves both <c>TV 3_1</c> and
/// <c>TV 3_low</c>. Nothing under test maps a display name to a folder, so the fixture does not
/// pretend to model that.
/// </para>
/// </remarks>
public static class FilterFixture
{
    /// <summary>Every filter the game ships, and whether it offers it. Order as the real table.</summary>
    private static readonly (string Name, bool Visible)[] Stock =
    [
        ("Default", true), ("Natural", true), ("TV 1", false),
        ("Cinematic 1", true), ("Cinematic 2", true), ("Cinematic 3", true),
        ("TV 3_1", false), ("Natural 2", true), ("Natural 3", true),
        ("Natural 6", false), ("Natural 8", false), ("Natural 5", false),
        ("TV 3_low", false), ("TV 4", false), ("TV 5", false),
        ("Washed", false), ("Cinematic 4", true),
    ];

    public static string Slug(string name) => name.Replace(" ", "").ToLowerInvariant();

    public static string PathFor(string name) =>
        $@"content\tracks\common_assets\post_process\{Slug(name)}\{Slug(name)}.postprocessing";

    /// <summary>A <c>[3]</c> row wrapping a <c>[15]</c> payload of name, path, (visible,) 4, 5.</summary>
    public static byte[] Row(string name, string path, bool visible) =>
        MessageField(3, MessageField(15, Cat(
            StringField(1, name),
            StringField(2, path),
            visible ? VarintField(3, 1) : [],
            VarintField(4, 1),
            VarintField(5, 1))));

    public static byte[] Row(string name, bool visible) => Row(name, PathFor(name), visible);

    /// <summary>A whole table: the <c>[2]</c> container, a header, then rows.</summary>
    public static byte[] Table(params byte[][] rows) =>
        MessageField(2, Cat(MessageField(1, StringField(1, "header")), Cat(rows)));

    /// <summary>The table as the game ships it — 17 rows, nine of them without the visible flag.</summary>
    public static byte[] StockTable() =>
        Table([.. Stock.Select(f => Row(f.Name, f.Visible))]);

    public static IEnumerable<string> StockNames => Stock.Select(f => f.Name);

    public static IEnumerable<string> StockHiddenNames =>
        Stock.Where(f => !f.Visible).Select(f => f.Name);

    /// <summary>
    /// A stand-in <c>.postprocessing</c>: nested submessages holding curve references, which is all
    /// the shape a dependency scan depends on.
    /// </summary>
    /// <remarks>
    /// The real asset nests its curve paths under <c>dynamicSettings</c> at varying depths, always as
    /// field 1 of a wrapper message. The scan finds string leaves wherever they are, so the exact
    /// nesting here is arbitrary — but it is nested, because a flat one would not exercise the
    /// recursion.
    /// </remarks>
    public static byte[] Filter(params string[] curveRefs) =>
        MessageField(23, Cat(
            [.. curveRefs.Select((r, i) => MessageField(i + 1, MessageField(1, StringField(1, r))))]));

    /// <summary>A curve reference into the folder Kunos ships, which any real filter leans on.</summary>
    public static string StockCurve(string file) =>
        $@"content\tracks\common_assets\post_process\natural1\{file}";
}
