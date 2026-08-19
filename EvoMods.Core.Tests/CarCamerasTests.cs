using EvoMods.Core.Camera;
using EvoMods.Core.FlatPad;
using EvoMods.Core.Protobuf;

using static EvoMods.Core.Tests.PbFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// Editing the chase camera's geometry.
/// </summary>
/// <remarks>
/// This file holds every car's every camera, so the assertions that matter most are the ones about
/// what did NOT change: the four views you actually drive from, the onboard section, and the
/// trailers all share it, and a preset has no business touching any of them.
/// </remarks>
public class CarCamerasTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("evomods-chasecam-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string _file => Path.Combine(_dir, "CarCameras.carcamerausersettings");

    // ---- a file shaped like the game's

    /// <summary>Fixed32 floats at fields 1..n with exact zeros omitted, exactly as the game writes.</summary>
    private static byte[] Vec(params float[] components)
    {
        var parts = new List<byte[]>();
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i] != 0f)
                parts.Add(Fixed32Field(i + 1, components[i]));
        }

        return Cat(parts.ToArray());
    }

    /// <summary>Six camera slots: four drive-from views, then near and far chase.</summary>
    private static byte[] Settings(ChaseCamView near, ChaseCamView far)
    {
        (float[] Pos, float[] Ang)[] slots =
        [
            ([0.3f, 0.75f, -0.1f], [0f, 180f]),      // Cockpit
            ([0f, 0.85f, 0.2f], [-2f, 180f]),        // Dash
            ([0f, 1.1f, 1.4f], [-3f, 180f]),         // Bonnet
            ([0f, 0.6f, 2.1f], [-1f, 180f]),         // Bumper
            ([0f, near.Height, -near.Distance], [near.Pitch, 180f]),
            ([0f, far.Height, -far.Distance], [far.Pitch, 180f]),
        ];

        return Cat(
            Cat(slots.Select(s => MessageField(1, Vec(s.Pos))).ToArray()),
            Cat(slots.Select(s => MessageField(2, Vec(s.Ang))).ToArray()));
    }

    private static byte[] Entry(string key, ChaseCamView near, ChaseCamView far) =>
        MessageField(2, Cat(StringField(1, key), MessageField(2, Settings(near, far))));

    private static byte[] Fovs(params float[] values) =>
        MessageField(1, Cat(values.Select(BitConverter.GetBytes).ToArray()));

    /// <summary>
    /// A file at the shipped convention: two cars, the four driven views set to something distinct,
    /// an onboard section and the two trailers.
    /// </summary>
    private static byte[] StockFile(params string[] cars)
    {
        ChaseCamPreset stock = ChaseCamSpec.Stock;
        if (cars.Length == 0)
            cars = [ChaseCamSpec.ReferenceCar, "ks_toyota_gr86"];

        return Cat(
            MessageField(1, Cat(
                Fovs(50f, 52f, 54f, 56f, stock.Near.Fov, stock.Far.Fov),
                Cat(cars.Select(c => Entry(c, stock.Near, stock.Far)).ToArray()))),
            MessageField(2, Cat(StringField(1, "onboard"), Fixed32Field(3, 68f))),
            VarintField(3, 4),
            VarintField(4, 1));
    }

    private void Given(byte[] bytes) => File.WriteAllBytes(_file, bytes);

    private int Write(ChaseCamPreset preset) =>
        CarCameras.Write(preset.Near, preset.Far, _ => { }, _file, guardGameRunning: false);

    private static ChaseCamPreset Named(string name) =>
        ChaseCamSpec.Presets.Single(p => p.Name == name);

    // ---- reading

    [Fact]
    public void An_untouched_file_round_trips_byte_identical()
    {
        byte[] original = StockFile();

        Assert.Equal(original, PbTree.EncodeTree(PbTree.ParseTree(original)));
    }

    [Fact]
    public void A_missing_file_reads_as_absent_rather_than_throwing()
    {
        ChaseCamReading reading = CarCameras.Read(_file);

        Assert.False(reading.Exists);
        Assert.Empty(reading.Cars);
    }

    [Fact]
    public void Geometry_reads_back_with_the_files_own_conventions_undone()
    {
        // z is negative behind the car; distance is the number a person tunes.
        Given(StockFile());

        ChaseCamReading reading = CarCameras.Read(_file);

        Assert.True(reading.Exists);
        Assert.Equal(1.80f, reading.Near.Height, 3);
        Assert.Equal(5.19f, reading.Near.Distance, 3);
        Assert.Equal(-5.0f, reading.Near.Pitch, 3);
        Assert.Equal(80f, reading.Near.Fov, 3);
        Assert.Equal(2.50f, reading.Far.Height, 3);
        Assert.Equal(6.19f, reading.Far.Distance, 3);
    }

    [Fact]
    public void The_car_every_preset_was_tuned_against_is_the_one_reported()
    {
        Given(StockFile("ks_toyota_gr86", ChaseCamSpec.ReferenceCar));

        ChaseCamReading reading = CarCameras.Read(_file);

        Assert.Equal(ChaseCamSpec.ReferenceCar, reading.Representative);
        Assert.Equal(2, reading.Cars.Count);
    }

    [Fact]
    public void A_file_already_at_a_preset_reports_that_preset()
    {
        Given(StockFile());

        Assert.Equal("Stock", CarCameras.Read(_file).Preset?.Name);
    }

    [Fact]
    public void A_file_at_no_preset_reports_none()
    {
        Given(StockFile());
        CarCameras.Write(new ChaseCamView(1.5f, 5f, -4f, 80f), Named("Stock").Far, _ => { }, _file, false);

        Assert.Null(CarCameras.Read(_file).Preset);
    }

    // ---- writing

    [Fact]
    public void Applying_a_preset_moves_near_and_far_on_every_car()
    {
        Given(StockFile("a_car", "b_car", "c_car"));
        ChaseCamPreset wide = Named("Wide");

        Write(wide);

        List<PbNode> top = PbTree.ParseTree(File.ReadAllBytes(_file));
        foreach (PbNode entry in top.Single(n => n.Number == 1).Find(2))
        {
            PbNode settings = entry.First(2)!;
            AssertSlot(settings, ChaseCamSpec.NearChase, wide.Near);
            AssertSlot(settings, ChaseCamSpec.FarChase, wide.Far);
        }
    }

    private static void AssertSlot(PbNode settings, int index, ChaseCamView want)
    {
        PbNode position = settings.Find(1)[index];
        PbNode angles = settings.Find(2)[index];
        List<PbNode> xyz = PbTree.ParseTree(position.Raw);
        List<PbNode> pitchYaw = PbTree.ParseTree(angles.Raw);

        Assert.Equal(want.Height, BitConverter.ToSingle(xyz.Single(n => n.Number == 2).Raw), 4);
        Assert.Equal(-want.Distance, BitConverter.ToSingle(xyz.Single(n => n.Number == 3).Raw), 4);
        Assert.Equal(want.Pitch, BitConverter.ToSingle(pitchYaw.Single(n => n.Number == 1).Raw), 4);
        Assert.Equal(180f, BitConverter.ToSingle(pitchYaw.Single(n => n.Number == 2).Raw), 4);
    }

    [Fact]
    public void The_onboard_section_and_the_trailers_come_back_byte_identical()
    {
        // Nothing here is about them, and the game reads them from the same file.
        Given(StockFile());
        byte[] before = File.ReadAllBytes(_file);

        Write(Named("Hero"));

        List<PbNode> was = PbTree.ParseTree(before);
        List<PbNode> now = PbTree.ParseTree(File.ReadAllBytes(_file));
        Assert.Equal(was.Single(n => n.Number == 2).Raw, now.Single(n => n.Number == 2).Raw);
        Assert.Equal(was.Single(n => n.Number == 3).Varint, now.Single(n => n.Number == 3).Varint);
        Assert.Equal(was.Single(n => n.Number == 4).Varint, now.Single(n => n.Number == 4).Varint);
    }

    [Fact]
    public void The_four_views_you_drive_from_are_left_alone()
    {
        Given(StockFile());
        byte[] before = File.ReadAllBytes(_file);

        Write(Named("Aggressive"));

        PbNode was = PbTree.ParseTree(before).Single(n => n.Number == 1).Find(2)[0].First(2)!;
        PbNode now = PbTree.ParseTree(File.ReadAllBytes(_file)).Single(n => n.Number == 1).Find(2)[0].First(2)!;
        for (int i = 0; i < ChaseCamSpec.FirstWritableFov; i++)
        {
            Assert.Equal(was.Find(1)[i].Raw, now.Find(1)[i].Raw);
            Assert.Equal(was.Find(2)[i].Raw, now.Find(2)[i].Raw);
        }
    }

    [Fact]
    public void Field_of_view_lands_on_the_two_chase_cameras_and_nowhere_else()
    {
        Given(StockFile());
        ChaseCamPreset wide = Named("Wide");

        Write(wide);

        byte[] packed = PbTree.ParseTree(File.ReadAllBytes(_file)).Single(n => n.Number == 1).First(1)!.Raw;
        float[] fov = Enumerable.Range(0, packed.Length / 4)
            .Select(i => BitConverter.ToSingle(packed, i * 4)).ToArray();
        Assert.Equal([50f, 52f, 54f, 56f, wide.Near.Fov, wide.Far.Fov], fov);
    }

    [Fact]
    public void A_position_with_no_side_offset_keeps_its_zero_omitted()
    {
        // The game omits exact zeros, so a chase position is 10 bytes rather than 15. Writing the
        // zero would read back the same and stop the file being byte-comparable to one it wrote.
        Given(StockFile());

        Write(Named("Cinematic"));

        PbNode settings = PbTree.ParseTree(File.ReadAllBytes(_file))
            .Single(n => n.Number == 1).Find(2)[0].First(2)!;
        Assert.Equal(10, settings.Find(1)[ChaseCamSpec.NearChase].Raw.Length);
    }

    [Fact]
    public void A_side_offset_left_behind_by_the_in_game_gizmo_is_cleared()
    {
        // The R34 shipped with x = 0.25 — 25 cm toward the passenger side on a right-hand-drive car.
        Given(StockFile());

        Write(Named("Cinematic"));

        PbNode position = PbTree.ParseTree(File.ReadAllBytes(_file))
            .Single(n => n.Number == 1).Find(2)[0].First(2)!.Find(1)[ChaseCamSpec.NearChase];
        Assert.DoesNotContain(PbTree.ParseTree(position.Raw), n => n.Number == 1);
    }

    [Fact]
    public void Writing_the_framing_already_there_changes_nothing()
    {
        Given(StockFile());
        byte[] before = File.ReadAllBytes(_file);

        Assert.Equal(0, Write(Named("Stock")));

        Assert.Equal(before, File.ReadAllBytes(_file));
    }

    [Fact]
    public void Writing_to_a_file_that_is_not_there_says_to_launch_the_game_first()
    {
        InstallException e = Assert.Throws<InstallException>(() => Write(Named("Wide")));

        Assert.Contains("Launch the game once", e.Message);
    }

    [Fact]
    public void A_car_without_the_six_expected_cameras_is_refused_by_name()
    {
        ChaseCamPreset stock = ChaseCamSpec.Stock;
        byte[] truncated = Cat(
            MessageField(1, Cat(
                Fovs(50f, 52f, 54f, 56f, 80f, 65f),
                MessageField(2, Cat(
                    StringField(1, "half_a_car"),
                    MessageField(2, Cat(
                        MessageField(1, Vec(0f, stock.Near.Height, -stock.Near.Distance)),
                        MessageField(2, Vec(stock.Near.Pitch, 180f)))))))),
            VarintField(3, 4));
        Given(truncated);

        InstallException e = Assert.Throws<InstallException>(() => Write(Named("Wide")));

        Assert.Contains("half_a_car", e.Message);
        Assert.Contains("Nothing was written", e.Message);
    }

    [Fact]
    public void A_write_leaves_a_backup_behind()
    {
        Given(StockFile());
        string backups = Path.Combine(_dir, "carcameras_backups");

        Write(Named("Hero"));

        Assert.True(Directory.Exists(backups));
        Assert.Single(Directory.GetFiles(backups));
    }

    [Fact]
    public void Restoring_puts_every_car_back_to_the_shipped_convention()
    {
        Given(StockFile());
        Write(Named("Hero"));

        CarCameras.Restore(_ => { }, _file, guardGameRunning: false);

        ChaseCamReading after = CarCameras.Read(_file);
        Assert.Equal("Stock", after.Preset?.Name);
    }

    // ---- the presets themselves

    [Fact]
    public void Every_preset_is_reachable_by_hand()
    {
        // Custom is a true superset: nothing a preset sets is outside what a slider can reach.
        Assert.All(ChaseCamSpec.Presets, p =>
        {
            foreach (ChaseCamView view in new[] { p.Near, p.Far })
            {
                foreach (ChaseCamKnob knob in ChaseCamSpec.Knobs)
                    Assert.InRange(view[knob.Axis], knob.Min, knob.Max);
            }
        });
    }

    [Fact]
    public void Every_preset_recognises_itself_and_nothing_else()
    {
        Assert.All(ChaseCamSpec.Presets, p => Assert.Same(p, ChaseCamSpec.Match(p.Near, p.Far)));
        Assert.Null(ChaseCamSpec.Match(new ChaseCamView(1.5f, 5f, -4f, 80f), ChaseCamSpec.Stock.Far));
    }

    [Fact]
    public void The_far_camera_always_sits_higher_and_further_back_than_the_near_one()
    {
        Assert.All(ChaseCamSpec.Presets, p =>
        {
            Assert.True(p.Far.Height > p.Near.Height, p.Name);
            Assert.True(p.Far.Distance > p.Near.Distance, p.Name);
            Assert.True(p.Far.Fov < p.Near.Fov, p.Name);
        });
    }

    /// <remarks>
    /// These are the figures the reference published for each preset, and they are the acceptance
    /// test for the ported geometry: get the maths wrong and the presets are still applied, the
    /// numbers on screen are simply lies.
    /// </remarks>
    [Theory]
    [InlineData("Stock", -5.1, 17.4)]
    [InlineData("Cinematic", 0.1, 19.6)]
    [InlineData("Aggressive", 2.5, 20.5)]
    [InlineData("Wide", 1.9, 18.0)]
    [InlineData("Hero", 4.5, 18.1)]
    public void Each_preset_reproduces_its_published_frame_layout(string name, double sky, double width)
    {
        ChaseCamView near = Named(name).Near;

        Assert.InRange(ChaseCamSpec.RoofVsHorizon(near), sky - 0.06, sky + 0.06);
        Assert.InRange(ChaseCamSpec.CarWidthPercent(near), width - 0.06, width + 0.06);
    }

    [Fact]
    public void Lowering_the_camera_does_not_move_the_horizon()
    {
        // The finding the whole preset table was built on. Only pitch and lens move the horizon; what
        // lowering buys is the roofline landing higher against it.
        var high = new ChaseCamView(1.80f, 4.40f, -4f, 85f);
        ChaseCamView low = high with { Height = 0.95f };

        Assert.Equal(
            ChaseCamSpec.FrameFraction(0, high.Pitch, high.Fov),
            ChaseCamSpec.FrameFraction(0, low.Pitch, low.Fov), 9);
        Assert.True(ChaseCamSpec.RoofVsHorizon(low) > ChaseCamSpec.RoofVsHorizon(high));
    }

    [Fact]
    public void Stock_reads_observational_and_Hero_cuts_above_the_skyline()
    {
        Assert.Contains("below the skyline", ChaseCamSpec.Feel(ChaseCamSpec.Stock.Near));
        Assert.Contains("above the skyline", ChaseCamSpec.Feel(Named("Hero").Near));
    }

    [Fact]
    public void Every_framing_describes_itself_at_every_point_on_its_slider()
    {
        // A description blank almost everywhere the slider can stop is worse than no description.
        foreach (ChaseCamKnob knob in ChaseCamSpec.Knobs)
        {
            for (float v = knob.Min; v <= knob.Max; v += (knob.Max - knob.Min) / 40f)
            {
                ChaseCamView view = ChaseCamSpec.Stock.Near.With(knob.Axis, v);
                Assert.False(string.IsNullOrWhiteSpace(ChaseCamSpec.Feel(view)), $"{knob.Label} at {v}");
            }
        }
    }

    [Fact]
    public void A_lens_above_the_roof_and_an_aim_at_the_tarmac_both_get_called_out()
    {
        Assert.Contains("see the roof panel", ChaseCamSpec.Feel(new ChaseCamView(2.5f, 6f, -5f, 80f)));
        Assert.Contains("centre of frame is tarmac", ChaseCamSpec.Feel(new ChaseCamView(1.2f, 5f, -12f, 80f)));
    }
}
