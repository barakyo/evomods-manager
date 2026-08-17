using EvoMods.Core.Protobuf;
using EvoMods.Core.Tables;

namespace EvoMods.Core.Filters;

/// <summary>One row of <c>post_processing.table</c>, with the nodes needed to edit it in place.</summary>
/// <param name="Entry">The <c>[2.3]</c> row node.</param>
/// <param name="Payload">The <c>[2.3.15]</c> payload node — where name, path and visibility live.</param>
public sealed record FilterRow(PbNode Entry, PbNode Payload, string Name, string Path, bool Visible);

/// <summary>
/// Reads and edits <c>system\post_processing.table</c>, the registry deciding which filters the
/// video options offer.
/// </summary>
/// <remarks>
/// Lives here rather than in <c>Tables/</c> for the same reason <c>RegistryIntegrity</c> lives in
/// <c>FlatPad/</c>: it knows this one file's layout. Only the schema-agnostic primitives it is built
/// from belong to <see cref="TableEditor"/>.
/// </remarks>
public static class FilterTable
{
    /// <summary>The <c>[2]</c> container and every row under it.</summary>
    /// <remarks>
    /// Rows whose payload cannot be read are skipped rather than throwing: the table is a file the
    /// game owns and other tools write to, and a status check has to answer even when one row is
    /// shaped unexpectedly.
    /// </remarks>
    public static (PbNode Root, List<FilterRow> Rows) Read(List<PbNode> tree)
    {
        (PbNode root, List<PbNode> entries) = TableEditor.TableEntries(tree);
        var rows = new List<FilterRow>();
        foreach (PbNode entry in entries)
        {
            if (entry.First((int)FilterSpec.Payload) is not { } payload || payload.Message is null)
                continue;

            // RawText, never TextAt: a name that happens to parse as protobuf leaves Text null, and
            // AC1_Movie and AC1_Sepia both do. See TableEditor.RawText.
            string? name = TableEditor.RawText(payload.First((int)FilterSpec.Name));
            if (name is null)
                continue;

            rows.Add(new FilterRow(
                entry,
                payload,
                name,
                TableEditor.RawText(payload.First((int)FilterSpec.Path)) ?? "",
                payload.First((int)FilterSpec.Visible) is not null));
        }

        return (root, rows);
    }

    /// <summary>Rows straight out of bytes — for the stock side of a comparison.</summary>
    public static List<FilterRow> Read(byte[] data) => Read(PbTree.ParseTree(data)).Rows;

    /// <summary>Add or drop the visible flag. False when the row already reads that way.</summary>
    public static bool SetVisible(PbNode root, FilterRow row, bool visible)
    {
        if (row.Visible == visible)
            return false;

        if (visible)
        {
            TableEditor.InsertField(
                row.Payload, new PbNode(FilterSpec.Visible, WireType.Varint) { Varint = 1 });
        }
        else
        {
            TableEditor.RemoveFields(row.Payload, FilterSpec.Visible);
        }

        // All three. Dirty is not propagated, so a clean [2] or [2.3] re-emits its original bytes
        // and the edit is gone with nothing to show for it.
        TableEditor.MarkDirty(root, row.Entry, row.Payload);
        return true;
    }

    /// <summary>Clone a row, swapping only the name and the content path, and force it visible.</summary>
    /// <remarks>
    /// Only the two strings change; every other byte of the template, at both nesting levels,
    /// survives verbatim. That is the whole safety argument for registering a filter without knowing
    /// what payload fields 4 and 5 mean.
    /// </remarks>
    public static PbNode AppendRow(List<PbNode> tree, FilterRow template, string name, string path)
    {
        (PbNode root, _) = TableEditor.TableEntries(tree);
        if (root.Message is null)
            throw new InvalidDataException("the [2] container holds no rows to append to");

        PbNode entry = TableEditor.CloneNode(template.Entry);
        PbNode payload = entry.First((int)FilterSpec.Payload)
            ?? throw new InvalidDataException($"the template row '{template.Name}' has no [15] payload");

        TableEditor.SetText(
            payload.First((int)FilterSpec.Name)
                ?? throw new InvalidDataException("the template row has no name"),
            name);
        TableEditor.SetText(
            payload.First((int)FilterSpec.Path)
                ?? throw new InvalidDataException("the template row has no content path"),
            FilterSpec.TablePath(path));

        if (payload.First((int)FilterSpec.Visible) is null)
        {
            TableEditor.InsertField(
                payload, new PbNode(FilterSpec.Visible, WireType.Varint) { Varint = 1 });
        }

        TableEditor.MarkDirty(root, entry, payload);
        root.Message.Add(entry);
        return entry;
    }

    /// <summary>Drop every row matching <paramref name="predicate"/>. Returns how many went.</summary>
    public static int RemoveRows(List<PbNode> tree, Func<FilterRow, bool> predicate)
    {
        (PbNode root, List<FilterRow> rows) = Read(tree);
        List<PbNode> doomed = rows.Where(predicate).Select(r => r.Entry).ToList();
        if (doomed.Count == 0)
            return 0;

        root.Message!.RemoveAll(doomed.Contains);
        root.Dirty = true;
        return doomed.Count;
    }

    /// <summary>A row worth cloning: visible, and not one of <paramref name="exclude"/>.</summary>
    /// <remarks>
    /// ⚠️ Visible on purpose. A row cloned from a HIDDEN one inherits the missing flag and the new
    /// filter never appears in the video options — which is a real way to ship something that
    /// installs perfectly and cannot be selected. <see cref="AppendRow"/> forces the flag afterwards
    /// regardless, so this is belt and braces.
    /// </remarks>
    public static FilterRow Template(IEnumerable<FilterRow> rows, ISet<string> exclude)
    {
        List<FilterRow> usable = rows
            .Where(r => r.Visible && !exclude.Contains(r.Name))
            .ToList();

        return usable.Count > 0
            ? usable[^1]
            : throw new InvalidDataException(
                "post_processing.table has no visible row to clone a new one from");
    }
}
