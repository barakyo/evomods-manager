using EvoMods.Core.Camera;
using EvoMods.Core.FlatPad;
using EvoMods.Core.Protobuf;

using static EvoMods.Core.Tests.PbFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// Editing the camera settings file the game actually reads.
/// </summary>
/// <remarks>
/// The assertion that matters most is that fields this code knows nothing about come back
/// untouched. The reference implementation rebuilds the file out of the six settings it understands
/// and drops the rest, and nothing in the game complains — it just quietly loses whatever was there.
/// </remarks>
public class CameraSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("evomods-cam-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string _file => Path.Combine(_dir, "camerasettings.camerasettings");

    /// <summary>
    /// A file shaped like the game's: a submessage, some bools, the floats, and a version marker.
    /// </summary>
    private static byte[] StockFile() => Cat(
        MessageField(1, StringField(1, "camera")),
        VarintField(3, 0),
        VarintField(6, 0),
        Fixed32Field(10, 10f),
        Fixed32Field(11, 4.5f),
        Fixed32Field(12, 0.4f),
        Fixed32Field(13, 1f),
        Fixed32Field(18, 0.1f),
        Fixed32Field(19, 1.2f),
        VarintField(20, 1));

    private void Given(byte[] bytes) => File.WriteAllBytes(_file, bytes);

    private int Write(params (int Number, float Value)[] values) =>
        CameraSettings.Write(values.ToDictionary(v => v.Number, v => v.Value), _ => { }, _file,
            guardGameRunning: false);

    // ---- reading

    [Fact]
    public void An_untouched_file_round_trips_byte_identical()
    {
        byte[] original = StockFile();

        Assert.Equal(original, PbTree.EncodeTree(PbTree.ParseTree(original)));
    }

    [Fact]
    public void Every_float_setting_is_read_back()
    {
        Given(StockFile());

        CameraReading reading = CameraSettings.Read(_file);

        Assert.True(reading.Exists);
        Assert.Equal(4.5f, reading.Values[11]);
        Assert.Equal(0.4f, reading.Values[12]);
        Assert.Equal(6, reading.Values.Count);
    }

    [Fact]
    public void A_missing_file_reads_as_absent_rather_than_throwing()
    {
        CameraReading reading = CameraSettings.Read(_file);

        Assert.False(reading.Exists);
        Assert.Empty(reading.Values);
    }

    [Fact]
    public void A_setting_the_file_does_not_carry_reads_as_the_games_default()
    {
        Given(Cat(Fixed32Field(11, 2f)));
        CameraField horizon = CameraSpec.Fields.Single(f => f.Number == 12);

        CameraReading reading = CameraSettings.Read(_file);

        Assert.False(reading.Carries(horizon));
        Assert.Equal(0.4f, reading.ValueOf(horizon));
    }

    // ---- writing

    [Fact]
    public void Writing_changes_the_value_it_was_given()
    {
        Given(StockFile());

        Assert.Equal(1, Write((11, 0.05f)));

        Assert.Equal(0.05f, CameraSettings.Read(_file).Values[11]);
    }

    [Fact]
    public void Fields_this_code_knows_nothing_about_survive_a_write()
    {
        // The whole reason for editing in place. A rebuild-from-scratch drops all three of these.
        Given(StockFile());

        Write((11, 0.05f));

        List<PbNode> after = PbTree.ParseTree(File.ReadAllBytes(_file));
        Assert.Contains(after, n => n.Number == 1 && n.Wire == WireType.Len);
        Assert.Contains(after, n => n.Number == 3 && n.Wire == WireType.Varint);
        Assert.Contains(after, n => n.Number == 20 && n.Wire == WireType.Varint);
    }

    [Fact]
    public void Writing_one_setting_leaves_every_other_byte_alone()
    {
        Given(StockFile());

        Write((11, 0.05f));

        byte[] expected = PbTree.EncodeTree(PbTree.ParseTree(StockFile()));
        byte[] actual = File.ReadAllBytes(_file);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(4.5f, BitConverter.ToSingle(expected, IndexOfFloat(expected, 4.5f)));
        Assert.Equal(0.05f, CameraSettings.Read(_file).Values[11]);
        Assert.Equal(10f, CameraSettings.Read(_file).Values[10]);
    }

    private static int IndexOfFloat(byte[] data, float value)
    {
        byte[] needle = BitConverter.GetBytes(value);
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data.AsSpan(i, 4).SequenceEqual(needle))
                return i;
        }

        return -1;
    }

    [Fact]
    public void Writing_the_value_already_there_changes_nothing()
    {
        Given(StockFile());
        byte[] before = File.ReadAllBytes(_file);

        Assert.Equal(0, Write((11, 4.5f)));

        Assert.Equal(before, File.ReadAllBytes(_file));
    }

    [Fact]
    public void A_setting_the_file_lacks_is_inserted_in_numeric_order()
    {
        Given(Cat(Fixed32Field(10, 10f), Fixed32Field(13, 1f), VarintField(20, 1)));

        Write((12, 0.9f));

        List<PbNode> after = PbTree.ParseTree(File.ReadAllBytes(_file));
        Assert.Equal([10, 12, 13, 20], after.Select(n => n.Number));
    }

    [Fact]
    public void Writing_to_a_file_that_is_not_there_says_to_launch_the_game_first()
    {
        InstallException e = Assert.Throws<InstallException>(() => Write((11, 1f)));

        Assert.Contains("Launch the game once", e.Message);
    }

    [Fact]
    public void A_write_leaves_a_backup_behind()
    {
        // The game rewrites this file whenever its own camera UI is opened, which is a loss the user
        // did not choose. Nothing ever restores these automatically.
        Given(StockFile());
        string backups = Path.Combine(_dir, "camsettings_backups");

        Write((11, 0.05f));

        Assert.True(Directory.Exists(backups));
        Assert.Single(Directory.GetFiles(backups));
    }

    // ---- restoring

    [Fact]
    public void Restoring_puts_every_setting_back_to_stock()
    {
        Given(StockFile());
        Write((11, 0.05f), (12, 1f), (10, 25f));

        CameraSettings.Restore(_ => { }, _file, guardGameRunning: false);

        CameraReading after = CameraSettings.Read(_file);
        foreach (CameraField f in CameraSpec.Fields)
            Assert.Equal(f.Stock, after.Values[f.Number]);
    }

    [Fact]
    public void Restoring_a_file_already_at_stock_writes_nothing()
    {
        Given(StockFile());
        byte[] before = File.ReadAllBytes(_file);

        Assert.Equal(0, CameraSettings.Restore(_ => { }, _file, guardGameRunning: false));

        Assert.Equal(before, File.ReadAllBytes(_file));
    }

    // ---- the spec itself

    [Fact]
    public void The_two_settings_that_were_measured_to_work_are_offered_first()
    {
        // Ordering is the recommendation: chase lag and horizon lock are the ones proven dramatic,
        // and everything after them changes the view you actually drive from.
        Assert.Equal(CameraEffect.Works, CameraSpec.Fields[0].Effect);
        Assert.Equal(CameraEffect.Works, CameraSpec.Fields[1].Effect);
        Assert.All(CameraSpec.Fields[2..], f => Assert.Equal(CameraEffect.Global, f.Effect));
    }

    [Fact]
    public void Every_offered_setting_has_a_stock_value_inside_its_own_range()
    {
        Assert.All(CameraSpec.Fields, f =>
        {
            Assert.InRange(f.Stock, f.Min, f.Max);
            Assert.True(f.Min < f.Max, f.Name);
        });
    }

    // ---- describing a value

    [Theory]
    [InlineData(4.5f, 0.22f)]   // stock
    [InlineData(3.0f, 0.33f)]   // subtle lag
    [InlineData(1.5f, 0.67f)]   // lags visibly, recovers within a corner
    [InlineData(0.5f, 2.0f)]    // still very loose
    [InlineData(0.05f, 20f)]    // never catches up
    public void Chase_lag_settle_time_matches_what_was_measured_in_game(float value, float settle)
    {
        // Computed as 1/value rather than interpolated between these points, because the setting is
        // a first-order lag and that IS its time constant. These five are the measurements it has to
        // keep agreeing with.
        CameraField chaseLag = CameraSpec.Fields.Single(f => f.Number == 11);

        string? readout = CameraSpec.Readout(chaseLag, value);

        Assert.NotNull(readout);
        Assert.Equal(settle, float.Parse(readout.Trim('~', 's')), 1);
    }

    [Fact]
    public void A_setting_with_no_derived_unit_shows_no_readout()
    {
        CameraField horizon = CameraSpec.Fields.Single(f => f.Number == 12);

        Assert.Null(CameraSpec.Readout(horizon, 0.4f));
    }

    [Fact]
    public void Every_setting_describes_itself_at_every_point_on_its_slider()
    {
        // A description that only appeared on the five measured values would be blank almost
        // everywhere the slider can actually stop.
        foreach (CameraField field in CameraSpec.Fields)
        {
            for (float v = field.Min; v <= field.Max; v += (field.Max - field.Min) / 40f)
                Assert.False(string.IsNullOrWhiteSpace(CameraSpec.Feel(field, v)), $"{field.Name} at {v}");
        }
    }

    [Fact]
    public void The_stock_chase_lag_describes_itself_as_stock()
    {
        CameraField chaseLag = CameraSpec.Fields.Single(f => f.Number == 11);

        Assert.Contains("how the game ships", CameraSpec.Feel(chaseLag, chaseLag.Stock));
    }

    [Fact]
    public void No_setting_measured_as_inert_is_offered()
    {
        // A control that does nothing is worse than no control, and these were tested to absurd
        // values on 0.8.1 with no effect at all.
        Assert.All(CameraSpec.Fields, f => Assert.DoesNotContain(f.Name, CameraSpec.KnownInert));
    }
}
