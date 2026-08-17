using EvoMods.Core.Refs;

namespace EvoMods.Core.Filters;

/// <summary>
/// Where post-processing filters live, and the two rules that decide whether one will actually load.
/// </summary>
/// <remarks>
/// Shaped after <see cref="FlatPad.FlatPadSpec"/>: constants and small pure helpers, no state. The
/// numbers are empirical. <c>PostProcessingPreset</c> is declared in a <c>Customization.proto</c>
/// that was never recovered from the executable, so unlike the rest of the table schema these field
/// numbers come from reading the shipped file rather than from a descriptor — which is why a new row
/// is always cloned from an existing one instead of built.
/// </remarks>
public static class FilterSpec
{
    public const string PpTable = "system/post_processing.table";
    public const string PpDir = "content/tracks/common_assets/post_process";

    /// <summary>The row payload, <c>[2.3.15]</c>.</summary>
    public const long Payload = 15;

    /// <summary>Payload field 1: the filter's name — a localization key. See <see cref="IsLoadableName"/>.</summary>
    public const long Name = 1;

    /// <summary>Payload field 2: the content path, backslash-separated and game-root-relative.</summary>
    public const long Path = 2;

    /// <summary>
    /// Payload field 3: offer this filter in the video options. PRESENCE is the flag — the game
    /// ships it as varint 1 where it ships it at all, and simply omits it otherwise.
    /// </summary>
    public const long Visible = 3;

    /// <summary>
    /// The nine the game registers without <see cref="Visible"/>, so they load and can never be
    /// chosen.
    /// </summary>
    /// <remarks>
    /// A FALLBACK only. The stock table inside <c>content.kspkg</c> is the source of truth, and this
    /// list is what a survey uses when there is no archive to read — see
    /// <see cref="FilterStates.Detect"/>, which says so on the report rather than pretending.
    /// </remarks>
    public static readonly string[] ShippedHidden =
    [
        "TV 1", "TV 3_1", "TV 3_low", "TV 4", "TV 5",
        "Natural 5", "Natural 6", "Natural 8", "Washed",
    ];

    /// <summary>Every filter the game registers, hidden or not, in the order the table lists them.</summary>
    /// <remarks>
    /// The same fallback as <see cref="ShippedHidden"/> and needed for the same survey: without it,
    /// an install whose archive cannot be read would report <c>Default</c> and <c>Natural</c> as
    /// somebody else's filters, because "not in the hidden list" is not the same as "not stock".
    /// </remarks>
    public static readonly string[] Shipped =
    [
        "Default", "Natural", "TV 1", "Cinematic 1", "Cinematic 2", "Cinematic 3",
        "TV 3_1", "Natural 2", "Natural 3", "Natural 6", "Natural 8", "Natural 5",
        "TV 3_low", "TV 4", "TV 5", "Washed", "Cinematic 4",
    ];

    /// <summary>A filter lives in a folder named after itself, holding a file named after it too.</summary>
    public static string InstallRef(string folder) => $"{PpDir}/{folder}/{folder}.postprocessing";

    /// <summary>A reference as the table spells it — backslashes, like every row the game ships.</summary>
    public static string TablePath(string reference) => RefPath.Canon(reference).Replace('/', '\\');

    /// <summary>Does this reference point at a curve, rather than at anything else a filter names?</summary>
    public static bool IsCurveRef(string reference) =>
        reference.EndsWith(".curve", StringComparison.OrdinalIgnoreCase) ||
        reference.EndsWith(".curve4", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is this a name the game can actually load?</summary>
    /// <remarks>
    /// ⚠️ A filter name is a localization KEY, not a display string: the UI resolves it through
    /// <c>uiresources/localization/en.loc</c> and falls back to the raw key when there is no entry.
    /// A NEW filter whose name contains a space registers, appears in the list and is selectable —
    /// and never loads, with no error anywhere; the previously selected filter just keeps rendering.
    /// Measured with two rows byte-identical apart from the name, both pointing at copies of one
    /// payload: <c>zztestnospace</c> moved the sky +111.78, <c>ZZ test space</c> moved it −0.03.
    /// The stock names contain spaces only because en.loc defines them.
    /// </remarks>
    public static bool IsLoadableName(string name) => !name.Contains(' ');
}
