using System.Text;

using EvoMods.Core.Protobuf;
using EvoMods.Core.Tables;

using static EvoMods.Core.Tests.PbFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// The message-node primitives, and the two decoder behaviours that make naive versions of them
/// wrong.
/// </summary>
/// <remarks>
/// Both traps are silent. A name that happens to parse as protobuf reads back as null through
/// <see cref="TableEditor.TextAt"/>, so a row lookup skips exactly the row it wanted; and an edit
/// whose ancestors are clean is re-encoded from their original bytes, so it disappears with no error.
/// </remarks>
public class TableFieldTests
{
    // ---- a name that is also valid protobuf

    [Fact]
    public void A_name_that_parses_as_protobuf_is_read_as_a_name_anyway()
    {
        // "AC1_Movie" is nine bytes starting 0x41 — tag for field 8, wire 1 (I64) — with exactly the
        // eight bytes an I64 wants behind it. The decoder therefore prefers the message reading.
        byte[] name = Encoding.UTF8.GetBytes("AC1_Movie");
        Assert.True(PbTree.IsMessage(name), "the premise of this test is that this name looks like a message");

        List<PbNode> tree = PbTree.ParseTree(MessageField(1, name));
        PbNode leaf = tree[0];

        Assert.Null(leaf.Text);                             // the trap
        Assert.Equal("AC1_Movie", TableEditor.RawText(leaf));
    }

    [Fact]
    public void A_plain_name_reads_the_same_through_either_route()
    {
        List<PbNode> tree = PbTree.ParseTree(StringField(1, "Video_Hero"));

        Assert.Equal("Video_Hero", tree[0].Text);
        Assert.Equal("Video_Hero", TableEditor.RawText(tree[0]));
    }

    [Fact]
    public void Setting_a_string_on_a_node_that_also_parsed_as_a_message_writes_the_new_bytes()
    {
        // Assigning Text alone would be silently discarded: EncodeTree checks Message first.
        List<PbNode> tree = PbTree.ParseTree(MessageField(1, Encoding.UTF8.GetBytes("AC1_Movie")));
        TableEditor.SetText(tree[0], "AC1_Renamed");

        byte[] written = PbTree.EncodeTree(tree);

        Assert.Equal(StringField(1, "AC1_Renamed"), written);
    }

    [Fact]
    public void A_varint_node_has_no_text_to_read()
    {
        List<PbNode> tree = PbTree.ParseTree(VarintField(3, 1));

        Assert.Null(TableEditor.RawText(tree[0]));
    }

    // ---- inserting and removing fields

    /// <summary>A row payload shaped like the game's: name, path, then the two unidentified flags.</summary>
    private static byte[] Payload(bool visible) => MessageField(15, Cat(
        StringField(1, "TV 1"),
        StringField(2, @"content\tracks\common_assets\post_process\tv1\tv1.postprocessing"),
        visible ? VarintField(3, 1) : [],
        VarintField(4, 1),
        VarintField(5, 1)));

    [Fact]
    public void An_inserted_field_lands_between_its_numeric_neighbours()
    {
        List<PbNode> tree = PbTree.ParseTree(Payload(visible: false));
        PbNode payload = tree[0];

        TableEditor.InsertField(payload, new PbNode(3, WireType.Varint) { Varint = 1 });

        Assert.Equal([1, 2, 3, 4, 5], payload.Message!.Select(n => n.Number));
    }

    [Fact]
    public void A_row_we_made_visible_is_byte_identical_to_one_the_game_ships_visible()
    {
        // The whole reason for inserting in numeric position rather than appending.
        List<PbNode> tree = PbTree.ParseTree(Payload(visible: false));
        TableEditor.InsertField(tree[0], new PbNode(3, WireType.Varint) { Varint = 1 });

        Assert.Equal(Payload(visible: true), PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Hiding_a_row_we_showed_restores_the_bytes_it_had()
    {
        List<PbNode> tree = PbTree.ParseTree(Payload(visible: true));

        Assert.Equal(1, TableEditor.RemoveFields(tree[0], 3));

        Assert.Equal(Payload(visible: false), PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Inserting_into_a_message_whose_fields_are_not_in_numeric_order_is_refused()
    {
        // Protobuf permits this; the game has never been observed emitting it. Refusing beats
        // guessing which side of field 4 a new field 3 belongs on.
        List<PbNode> tree = PbTree.ParseTree(MessageField(15, Cat(
            StringField(2, "path"),
            StringField(1, "name"))));

        InvalidDataException e = Assert.Throws<InvalidDataException>(() =>
            TableEditor.InsertField(tree[0], new PbNode(3, WireType.Varint) { Varint = 1 }));

        Assert.Contains("numeric order", e.Message);
    }

    [Fact]
    public void Removing_a_field_that_is_not_there_reports_nothing_removed()
    {
        List<PbNode> tree = PbTree.ParseTree(Payload(visible: false));

        Assert.Equal(0, TableEditor.RemoveFields(tree[0], 3));
        Assert.False(tree[0].Dirty);
    }

    [Fact]
    public void Inserting_into_a_node_that_holds_no_message_is_refused()
    {
        List<PbNode> tree = PbTree.ParseTree(StringField(1, "not a message"));

        Assert.Throws<InvalidDataException>(() =>
            TableEditor.InsertField(tree[0], new PbNode(3, WireType.Varint) { Varint = 1 }));
    }

    // ---- the dirty chain

    [Fact]
    public void An_edit_without_a_dirty_ancestor_is_lost()
    {
        // Asserting the DOCUMENTED behaviour, not a bug: it is what makes an untouched file
        // round-trip byte-for-byte. Anyone "fixing" EncodeTree should have to delete this test.
        byte[] original = MessageField(2, Payload(visible: false));
        List<PbNode> tree = PbTree.ParseTree(original);
        PbNode payload = tree[0].Message![0];

        TableEditor.InsertField(payload, new PbNode(3, WireType.Varint) { Varint = 1 });
        // ... and deliberately do not mark [2].

        Assert.Equal(original, PbTree.EncodeTree(tree));
    }

    [Fact]
    public void The_same_edit_survives_once_the_chain_is_marked()
    {
        byte[] original = MessageField(2, Payload(visible: false));
        List<PbNode> tree = PbTree.ParseTree(original);
        PbNode container = tree[0];
        PbNode payload = container.Message![0];

        TableEditor.InsertField(payload, new PbNode(3, WireType.Varint) { Varint = 1 });
        TableEditor.MarkDirty(container, payload);

        Assert.Equal(MessageField(2, Payload(visible: true)), PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Marking_ignores_nulls_and_nodes_that_carry_no_payload()
    {
        // So a caller can pass a navigation chain verbatim without filtering it first.
        List<PbNode> tree = PbTree.ParseTree(VarintField(3, 1));

        TableEditor.MarkDirty(null, tree[0]);

        Assert.False(tree[0].Dirty);
    }
}
