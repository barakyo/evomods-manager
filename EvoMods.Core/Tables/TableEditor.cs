using System.Text;

using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Tables;

/// <summary>
/// Reads and edits the <c>system\*.table</c> registries — the catalogs the game menus enumerate.
/// </summary>
/// <remarks>
/// A table is one <c>[2]</c> container holding repeated <c>[2.3]</c> entries. Registering a track
/// means deep-cloning an existing entry, rewriting its strings, and overriding its numeric ids —
/// never hand-building one, because an unfilled field is a crash.
/// </remarks>
public static class TableEditor
{
    /// <summary>Children of a node list with the given field number.</summary>
    public static List<PbNode> Find(IEnumerable<PbNode> nodes, long number) =>
        nodes.Where(n => n.Number == number).ToList();

    /// <summary>Descend a node by field numbers. Null if any hop is missing.</summary>
    public static PbNode? Child(PbNode? node, params long[] path)
    {
        PbNode? cur = node;
        foreach (long number in path)
        {
            if (cur?.Message is null)
                return null;
            PbNode? hit = cur.Message.FirstOrDefault(n => n.Number == number);
            if (hit is null)
                return null;
            cur = hit;
        }

        return cur;
    }

    public static string? TextAt(PbNode? node, params long[] path) => Child(node, path)?.Text;

    /// <summary>A string leaf's text, decoded from its bytes rather than from the parse's guess.</summary>
    /// <remarks>
    /// ⚠️ <see cref="PbTree.ParseTree"/> tries <c>IsMessage</c> BEFORE <c>LooksText</c>, so a payload
    /// that happens to be valid protobuf becomes a message and <see cref="PbNode.Text"/> stays null.
    /// Real names do this: <c>AC1_Movie</c> is nine bytes starting 0x41, which reads as one I64 field
    /// with exactly eight bytes behind it. <see cref="TextAt"/> returns null for such a row, so
    /// anything that IDENTIFIES a row by name has to come through here instead or it will silently
    /// skip exactly the rows it was looking for.
    /// </remarks>
    public static string? RawText(PbNode? node) =>
        node is null || node.Wire != WireType.Len ? null : Encoding.UTF8.GetString(node.Raw);

    /// <summary>Text of a nested string leaf, by the same rules as <see cref="RawText"/>.</summary>
    public static string? RawTextAt(PbNode? node, params long[] path) => RawText(Child(node, path));

    /// <summary>Replace a string leaf, dropping the message parse that would otherwise shadow it.</summary>
    /// <remarks>
    /// ⚠️ <see cref="PbTree.EncodeTree"/> checks <c>Dirty &amp;&amp; Message is not null</c> FIRST, so
    /// assigning <see cref="PbNode.Text"/> on a node that also parsed as a message writes the OLD
    /// bytes back. Clearing <see cref="PbNode.Message"/> is what makes the assignment take.
    /// Marks only this node — the caller still owns the ancestor chain. See <see cref="MarkDirty"/>.
    /// </remarks>
    public static void SetText(PbNode node, string value)
    {
        node.Text = value;
        node.Message = null;
        node.Raw = Encoding.UTF8.GetBytes(value);
        node.Dirty = true;
    }

    /// <summary>Deep-copy a subtree — round-tripped through the encoder, so lossless by construction.</summary>
    public static PbNode CloneNode(PbNode node) =>
        PbTree.ParseTree(PbTree.EncodeTree([node]))[0];

    /// <summary>Set a nested varint, marking the ancestor chain dirty so it re-encodes.</summary>
    public static void SetVarint(PbNode entry, long[] path, ulong value)
    {
        var chain = new List<PbNode> { entry };
        PbNode cur = entry;
        foreach (long number in path)
        {
            PbNode? hit = cur.Message?.FirstOrDefault(n => n.Number == number);
            if (hit is null)
            {
                throw new KeyNotFoundException(
                    $"no field {number} under [{string.Join(", ", chain.Select(n => n.Number))}]");
            }

            cur = hit;
            chain.Add(cur);
        }

        cur.Varint = value;
        foreach (PbNode n in chain[..^1])
            n.Dirty = true;
    }

    /// <summary>Mark length-delimited nodes dirty, so their re-serialised bodies actually get written.</summary>
    /// <remarks>
    /// ⚠️ <see cref="PbNode.Dirty"/> is NOT propagated, and <see cref="PbTree.EncodeTree"/> consults it
    /// only on wire-2 nodes. An edit whose ancestors are still clean is re-encoded from their
    /// <see cref="PbNode.Raw"/> and vanishes with no error at all. <see cref="SetVarint"/> walks the
    /// chain it descended; anything that navigates to a node by hand has to say so here. Nulls and
    /// non-wire-2 nodes are ignored, so a chain can be passed verbatim.
    /// </remarks>
    public static void MarkDirty(params PbNode?[] chain)
    {
        foreach (PbNode? n in chain)
        {
            if (n is not null && n.Wire == WireType.Len)
                n.Dirty = true;
        }
    }

    /// <summary>Insert a field into a message node, keeping its children in numeric order.</summary>
    /// <remarks>
    /// ⚠️ <see cref="PbTree.ParseTree"/> preserves WIRE order, and protobuf does not require that to
    /// be numeric. So this ASSERTS ascending order rather than assuming it: a message shaped some
    /// other way is one this code was not written against, and refusing beats writing a field into a
    /// position the game has never been handed. Marks <paramref name="message"/> dirty; the caller
    /// still owns everything above it.
    /// </remarks>
    public static void InsertField(PbNode message, PbNode field)
    {
        InsertField(
            message.Message
                ?? throw new InvalidDataException($"[{message.Number}] holds no message to insert into"),
            field);
        message.Dirty = true;
    }

    /// <summary>Insert into a bare field list — a parsed file's top level, which has no owning node.</summary>
    public static void InsertField(List<PbNode> fields, PbNode field)
    {
        for (int i = 1; i < fields.Count; i++)
        {
            if (fields[i].Number < fields[i - 1].Number)
            {
                throw new InvalidDataException(
                    $"fields are out of numeric order ({fields[i - 1].Number} then {fields[i].Number})" +
                    " — refusing to guess where a new one belongs");
            }
        }

        int at = fields.FindIndex(n => n.Number > field.Number);
        fields.Insert(at < 0 ? fields.Count : at, field);
    }

    /// <summary>Drop every field with this number from a message node. Returns how many went.</summary>
    /// <remarks>Marks <paramref name="message"/> dirty only when something was actually removed.</remarks>
    public static int RemoveFields(PbNode message, long number)
    {
        List<PbNode>? children = message.Message;
        if (children is null)
            return 0;

        int removed = children.RemoveAll(n => n.Number == number);
        if (removed > 0)
            message.Dirty = true;
        return removed;
    }

    /// <summary>The <c>[2]</c> container of a *.table file and its repeated <c>[2.3]</c> entries.</summary>
    public static (PbNode Root, List<PbNode> Entries) TableEntries(List<PbNode> tree)
    {
        List<PbNode> root = Find(tree, 2);
        if (root.Count == 0)
            throw new InvalidDataException("not a *.table file: no [2] container");
        return (root[0], Find(root[0].Message ?? [], 3));
    }

    /// <summary>Append a clone of <paramref name="template"/> (a <c>[2.3]</c> entry) to a table.</summary>
    /// <param name="replacements">Rewrites applied to the clone's string leaves.</param>
    /// <param name="varints">Field paths RELATIVE TO THE ENTRY NODE, mapped to new values.</param>
    public static PbNode AppendTableEntry(List<PbNode> tree, PbNode template,
        List<(string Old, string New)> replacements,
        IEnumerable<(long[] Path, ulong Value)>? varints = null)
    {
        (PbNode root, _) = TableEntries(tree);
        if (root.Message is null)
            throw new InvalidDataException("the [2] container holds no entries to append to");

        PbNode entry = CloneNode(template);
        PbTree.TransformText([entry], s => CloneTree.Replace(s, replacements));
        foreach ((long[] path, ulong value) in varints ?? [])
            SetVarint(entry, path, value);

        root.Message.Add(entry);
        root.Dirty = true;
        entry.Dirty = true;
        return entry;
    }

    /// <summary>Append an entry node verbatim, keeping every field it already carries.</summary>
    /// <remarks>
    /// Used to put back a stock entry a broken tool removed. Unlike
    /// <see cref="AppendTableEntry"/> nothing is rewritten — the whole point is that the restored
    /// entry keeps the game's own id and dense menu index, which is what makes it the entry the
    /// update shipped rather than a lookalike.
    /// </remarks>
    public static PbNode AppendEntry(List<PbNode> tree, PbNode entry)
    {
        (PbNode root, _) = TableEntries(tree);
        if (root.Message is null)
            throw new InvalidDataException("the [2] container holds no entries to append to");

        PbNode clone = CloneNode(entry);
        root.Message.Add(clone);
        root.Dirty = true;
        return clone;
    }

    /// <summary>Drop every <c>[2.3]</c> entry matching <paramref name="predicate"/>. Returns the count.</summary>
    public static int RemoveTableEntries(List<PbNode> tree, Func<PbNode, bool> predicate)
    {
        (PbNode root, _) = TableEntries(tree);
        List<PbNode> children = root.Message ?? [];
        List<PbNode> keep = children.Where(n => !(n.Number == 3 && predicate(n))).ToList();
        int removed = children.Count - keep.Count;
        if (removed > 0)
        {
            children.Clear();
            children.AddRange(keep);
            root.Dirty = true;
        }

        return removed;
    }
}
