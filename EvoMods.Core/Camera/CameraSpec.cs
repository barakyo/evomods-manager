namespace EvoMods.Core.Camera;

/// <summary>How well a setting is understood, from measurement rather than from the schema.</summary>
public enum CameraEffect
{
    /// <summary>Measured, dramatic, and chase-camera only — safe to take to an extreme.</summary>
    Works,

    /// <summary>Measured and real, but it changes the view you actually drive from.</summary>
    Global,

    /// <summary>Tested to absurd values on build 0.8.1 and did nothing at all.</summary>
    Inert,
}

/// <param name="Number">Protobuf field number. The file is fixed32 floats at the top level.</param>
/// <param name="Name">The game's own name for it.</param>
/// <param name="Label">What to call it on screen.</param>
/// <param name="Stock">The game's default, for the restore path.</param>
public sealed record CameraField(
    int Number,
    string Name,
    string Label,
    float Stock,
    float Min,
    float Max,
    CameraEffect Effect,
    string Note);

/// <summary>
/// Where the camera settings live, and which of them actually do anything.
/// </summary>
/// <remarks>
/// ⚠️ There are TWO <c>camerasettings.camerasettings</c> files and only one of them is read. The one
/// under the game's <c>system\</c> folder has no effect at all while a user file exists — measured,
/// by cranking a value to 200 in the game file and seeing nothing change, then making the same edit
/// in the user file and seeing it honoured.
/// <para>
/// ⚠️ <c>%USERPROFILE%\Documents\ACE\</c> also exists and looks plausible. It is stale, and reading
/// it will mislead you. The live directory is <c>Saved Games\ACE</c>.
/// </para>
/// <para>
/// Everything here was established empirically against build 0.8.1+release.25. Re-verify after a
/// game update.
/// </para>
/// </remarks>
public static class CameraSpec
{
    /// <summary>The file the game actually reads.</summary>
    public static string UserFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Saved Games", "ACE", "camerasettings.camerasettings");

    /// <summary>Timestamped copies, kept because the game itself can overwrite this file.</summary>
    /// <remarks>
    /// Not the snapshot-and-restore pattern this project rejects elsewhere: nothing ever restores
    /// these automatically, and the file is the user's own rather than a registry other tools also
    /// write to. It exists because opening the in-game camera settings UI makes the game rewrite the
    /// file and discard whatever was set here — a loss the user cannot prevent and did not choose.
    /// </remarks>
    public static string BackupDirFor(string settingsFile) =>
        Path.Combine(Path.GetDirectoryName(settingsFile)!, "camsettings_backups");

    /// <summary>The process that must not be running before this file is written.</summary>
    public const string GameProcess = "AssettoCorsaEVO";

    public static readonly CameraField[] Fields =
    [
        new(11, "chasecam_rotation_lag_speed", "Chase lag", 4.5f, 0.05f, 10f, CameraEffect.Works,
            "First-order lag toward the camera's target orientation — lower is laggier."),

        new(12, "horizon_lock", "Horizon lock", 0.4f, 0f, 1f, CameraEffect.Works,
            "Dramatic on pitched or banked terrain, and invisible on flat ground — which is why "
            + "testing it on a flat pad reads as broken."),

        new(10, "look_around_speed", "Look-around speed", 10f, 1f, 30f, CameraEffect.Global,
            "Affects the onboard look, not just the chase camera."),

        new(13, "general_movement", "General movement", 1f, 0f, 3f, CameraEffect.Global,
            "Affects the view you actually drive from."),

        new(19, "onboard_movement", "Onboard movement", 1.2f, 0f, 3f, CameraEffect.Global,
            "Onboard camera only."),

        new(18, "bonnet_factor", "Bonnet factor", 0.1f, 0f, 1f, CameraEffect.Global,
            "Bonnet camera only."),
    ];

    /// <summary>
    /// Settings measured as doing nothing, kept here so they are not re-tested by hand.
    /// </summary>
    /// <remarks>
    /// <c>enableShake</c> (f3) and <c>g_forces_shake</c> were tested to 200 in both files,
    /// <c>gForceLag</c> (f4) at 50, and <c>worldAligned</c> (f6) true. Plausibly just unimplemented
    /// in early access. They are not offered because a control that does nothing is worse than no
    /// control.
    /// </remarks>
    public static readonly string[] KnownInert =
        ["enableShake", "g_forces_shake", "gForceLag", "worldAligned"];

    /// <summary>
    /// The value in units somebody can picture, or null when the number already says enough.
    /// </summary>
    /// <remarks>
    /// Chase lag is a first-order lag toward the camera's target orientation, so its time constant
    /// is simply <c>1 / value</c> — which is why this is computed rather than interpolated between
    /// measured points. It agrees with every settle time measured in game: 4.5 → 0.22 s, 3.0 →
    /// 0.33 s, 1.5 → 0.67 s, 0.5 → 2 s, 0.05 → 20 s.
    /// <para>
    /// This is the whole reason the readout exists. "1.5" means nothing on its own; "~0.7 s" is a
    /// duration a person can picture against a corner.
    /// </para>
    /// </remarks>
    public static string? Readout(CameraField field, float value) =>
        field.Number == 11 && value > 0 ? $"~{1f / value:0.##}s" : null;

    /// <summary>What a value actually feels like, from what was measured at that setting.</summary>
    /// <remarks>
    /// Bands rather than exact matches, because the slider moves continuously and a description that
    /// only appeared on five exact values would be blank almost everywhere. Fields with nothing
    /// measured per-value fall back to their standing note.
    /// </remarks>
    public static string Feel(CameraField field, float value) => field.Number switch
    {
        11 => "Lower is laggier. " + value switch
        {
            < 0.15f => "Never catches up — the camera barely follows the car through corners.",
            < 0.8f => "Still very loose.",
            < 2.2f => "Lags visibly, but recovers within a corner.",
            < 3.8f => "Subtle lag.",
            < 5.5f => "About how the game ships.",
            _ => "Tighter than stock — the camera stays locked to the car.",
        },

        12 => value switch
        {
            < 0.15f => "Low and level behind the car — immersive and car-focused.",
            < 0.45f => "Slightly raised, close to how the game ships.",
            < 0.75f => "Raised, looking down a little.",
            _ => "High and looking down — observational.",
        } + " Invisible on flat ground.",

        _ => field.Note,
    };
}
