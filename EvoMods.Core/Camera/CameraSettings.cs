using System.Diagnostics;

using EvoMods.Core.FlatPad;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Tables;

namespace EvoMods.Core.Camera;

/// <param name="Values">Field number to value, for whichever fields the file carries.</param>
/// <param name="Exists">False when there is no user file yet.</param>
public sealed record CameraReading(IReadOnlyDictionary<int, float> Values, bool Exists)
{
    /// <summary>The value in the file, or the game's default when the file does not carry it.</summary>
    public float ValueOf(CameraField field) =>
        Values.TryGetValue(field.Number, out float v) ? v : field.Stock;

    public bool Carries(CameraField field) => Values.ContainsKey(field.Number);
}

/// <summary>
/// Reads and writes the camera settings the game actually honours.
/// </summary>
/// <remarks>
/// The file is protobuf with the interesting values as fixed32 floats at the top level, so
/// <see cref="PbTree"/> handles it and every field this code does not know about survives untouched.
/// <para>
/// ⚠️ That is a deliberate difference from the reference implementation, which rebuilds the file
/// from scratch out of the six fields it knows and a version marker — silently dropping the
/// submessage at field 1 and the bool fields along with it. Editing in place costs nothing and
/// cannot lose anything.
/// </para>
/// </remarks>
public static class CameraSettings
{
    /// <summary>Is the game running? Writes are refused while it is; reads are always allowed.</summary>
    /// <remarks>
    /// The game reads this file at startup and rewrites it on exit, so writing underneath a running
    /// game is at best pointless and at worst loses the edit without saying so.
    /// </remarks>
    public static Process? RunningGame() =>
        Process.GetProcessesByName(CameraSpec.GameProcess).FirstOrDefault();

    /// <summary>What the file currently says. Never throws for an absent file.</summary>
    public static CameraReading Read(string? path = null)
    {
        path ??= CameraSpec.UserFile;
        if (!File.Exists(path))
            return new CameraReading(new Dictionary<int, float>(), Exists: false);

        var values = new Dictionary<int, float>();
        foreach (PbNode node in PbTree.ParseTree(File.ReadAllBytes(path)))
        {
            if (node.Wire == WireType.I32 && node.Raw.Length == 4)
                values[(int)node.Number] = BitConverter.ToSingle(node.Raw, 0);
        }

        return new CameraReading(values, Exists: true);
    }

    /// <summary>
    /// Apply the given values, leaving every other field exactly as it was. Returns how many changed.
    /// </summary>
    /// <param name="guardGameRunning">
    /// Refuse to write while the game is up. A parameter rather than an unconditional check so the
    /// tests can exercise writing without depending on whether the machine running them happens to
    /// have the game open.
    /// </param>
    public static int Write(
        IReadOnlyDictionary<int, float> values, Action<string> log, string? path = null,
        bool guardGameRunning = true)
    {
        path ??= CameraSpec.UserFile;

        if (!File.Exists(path))
        {
            throw new InstallException(
                $"No camera settings file at {path}. Launch the game once so it writes one — this "
                + "edits the file the game already has rather than inventing one.");
        }

        if (guardGameRunning && RunningGame() is { } game)
        {
            throw new InstallException(
                $"Assetto Corsa EVO is running (PID {game.Id}). It reads this file at startup and "
                + "rewrites it on exit, so close it first.");
        }

        List<PbNode> tree = PbTree.ParseTree(File.ReadAllBytes(path));
        int changed = 0;

        foreach ((int number, float value) in values.OrderBy(v => v.Key))
        {
            PbNode? node = tree.FirstOrDefault(n => n.Number == number && n.Wire == WireType.I32);
            if (node is not null)
            {
                if (Math.Abs(BitConverter.ToSingle(node.Raw, 0) - value) <= 1e-7f)
                    continue;
                node.Raw = BitConverter.GetBytes(value);
            }
            else
            {
                TableEditor.InsertField(
                    tree, new PbNode(number, WireType.I32) { Raw = BitConverter.GetBytes(value) });
            }

            changed++;
        }

        if (changed == 0)
        {
            log("  nothing to change — the file already says that");
            return 0;
        }

        string backup = Backup(path);
        File.WriteAllBytes(path, PbTree.EncodeTree(tree));

        // Read back rather than trusting the write: this is a file the game also writes, and a
        // silently-wrong camera is exactly the sort of thing nobody notices until a replay is ruined.
        CameraReading after = Read(path);
        foreach ((int number, float value) in values)
        {
            if (!after.Values.TryGetValue(number, out float got) || Math.Abs(got - value) > 1e-6f)
            {
                throw new InstallException(
                    $"Verify failed on field {number}: wrote {value}, read back "
                    + (after.Values.TryGetValue(number, out float g) ? g.ToString() : "nothing"));
            }
        }

        log($"  {changed} setting(s) written to {Path.GetFileName(path)} and verified");
        log($"  backup: {backup}");
        return changed;
    }

    /// <summary>Put every setting back to the value the game ships.</summary>
    public static int Restore(Action<string> log, string? path = null, bool guardGameRunning = true) =>
        Write(CameraSpec.Fields.ToDictionary(f => f.Number, f => f.Stock), log, path, guardGameRunning);

    /// <summary>Copy the file aside, named for when it was last written.</summary>
    /// <remarks>
    /// Keyed on the file's own timestamp rather than now, so re-running without the game having
    /// touched the file in between does not pile up identical copies.
    /// </remarks>
    private static string Backup(string path)
    {
        // Beside the file being written, not beside the real user file: a backup that lands
        // somewhere other than next to its original is worse than none, and it once meant a test
        // writing into a live Saved Games folder.
        string dir = CameraSpec.BackupDirFor(path);
        Directory.CreateDirectory(dir);
        string name = $"camerasettings.{File.GetLastWriteTime(path):yyyyMMdd-HHmmss}.bak";
        string backup = Path.Combine(dir, name);
        if (!File.Exists(backup))
            File.Copy(path, backup);
        return backup;
    }
}
