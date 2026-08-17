using EvoMods.Core.Filters;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Tables;

using static EvoMods.Core.Tests.FilterFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// Reading and editing <c>post_processing.table</c>. The load-bearing assertions are byte equality
/// on the whole file: this is a file the game owns, and every row we are not deliberately changing
/// has to come back out exactly as it went in.
/// </summary>
public class FilterTableTests
{
    // ---- reading

    [Fact]
    public void Untouched_post_processing_table_round_trips_byte_identical()
    {
        byte[] original = StockTable();

        Assert.Equal(original, PbTree.EncodeTree(PbTree.ParseTree(original)));
    }

    [Fact]
    public void Every_row_the_game_ships_is_read_back()
    {
        List<FilterRow> rows = FilterTable.Read(StockTable());

        Assert.Equal(17, rows.Count);
        Assert.Equal(StockNames, rows.Select(r => r.Name));
    }

    [Fact]
    public void The_nine_the_game_ships_hidden_read_as_hidden()
    {
        List<FilterRow> rows = FilterTable.Read(StockTable());

        Assert.Equal(StockHiddenNames, rows.Where(r => !r.Visible).Select(r => r.Name));
        Assert.Equal(FilterSpec.ShippedHidden.Order(), rows.Where(r => !r.Visible).Select(r => r.Name).Order());
    }

    [Fact]
    public void A_row_whose_name_parses_as_protobuf_is_still_found()
    {
        // AC1_Movie leaves PbNode.Text null. A reader built on TextAt would drop this row silently.
        byte[] table = Table(Row("Default", true), Row("AC1_Movie", true));

        List<FilterRow> rows = FilterTable.Read(table);

        Assert.Equal(["Default", "AC1_Movie"], rows.Select(r => r.Name));
    }

    [Fact]
    public void A_row_with_no_payload_is_skipped_rather_than_throwing()
    {
        // Another tool's row, or a shape from a future patch. A status check still has to answer.
        byte[] table = PbFixture.MessageField(2, PbFixture.Cat(
            PbFixture.MessageField(3, PbFixture.StringField(9, "not a payload")),
            Row("Natural", true)));

        List<FilterRow> rows = FilterTable.Read(table);

        Assert.Equal(["Natural"], rows.Select(r => r.Name));
    }

    [Fact]
    public void The_content_path_is_read_off_the_row()
    {
        List<FilterRow> rows = FilterTable.Read(Table(Row("TV 1", false)));

        Assert.Equal(PathFor("TV 1"), rows[0].Path);
    }

    // ---- visibility

    [Fact]
    public void Showing_a_hidden_filter_inserts_field_3_between_the_path_and_the_flags()
    {
        List<PbNode> tree = PbTree.ParseTree(StockTable());
        (PbNode root, List<FilterRow> rows) = FilterTable.Read(tree);
        FilterRow tv1 = rows.Single(r => r.Name == "TV 1");

        Assert.True(FilterTable.SetVisible(root, tv1, visible: true));

        Assert.Equal([1, 2, 3, 4, 5], tv1.Payload.Message!.Select(n => n.Number));
    }

    [Fact]
    public void A_filter_we_showed_is_byte_identical_to_one_the_game_ships_visible()
    {
        List<PbNode> tree = PbTree.ParseTree(Table(Row("TV 1", visible: false)));
        (PbNode root, List<FilterRow> rows) = FilterTable.Read(tree);

        FilterTable.SetVisible(root, rows[0], visible: true);

        Assert.Equal(Table(Row("TV 1", visible: true)), PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Hiding_a_filter_we_showed_restores_the_file_byte_for_byte()
    {
        byte[] original = StockTable();
        List<PbNode> tree = PbTree.ParseTree(original);
        (PbNode root, List<FilterRow> rows) = FilterTable.Read(tree);

        foreach (FilterRow r in rows.Where(r => !r.Visible).ToList())
            FilterTable.SetVisible(root, r, visible: true);
        byte[] shown = PbTree.EncodeTree(tree);

        tree = PbTree.ParseTree(shown);
        (root, rows) = FilterTable.Read(tree);
        foreach (string name in FilterSpec.ShippedHidden)
            FilterTable.SetVisible(root, rows.Single(r => r.Name == name), visible: false);

        Assert.NotEqual(original, shown);
        Assert.Equal(original, PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Showing_a_filter_leaves_every_other_row_byte_identical()
    {
        List<PbNode> tree = PbTree.ParseTree(StockTable());
        (PbNode root, List<FilterRow> rows) = FilterTable.Read(tree);
        FilterTable.SetVisible(root, rows.Single(r => r.Name == "TV 4"), visible: true);

        byte[] expected = Table([.. StockNames.Select(n =>
            Row(n, visible: n == "TV 4" || !StockHiddenNames.Contains(n)))]);

        Assert.Equal(expected, PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Showing_a_filter_that_is_already_shown_changes_nothing()
    {
        byte[] original = StockTable();
        List<PbNode> tree = PbTree.ParseTree(original);
        (PbNode root, List<FilterRow> rows) = FilterTable.Read(tree);

        Assert.False(FilterTable.SetVisible(root, rows.Single(r => r.Name == "Natural"), visible: true));

        Assert.Equal(original, PbTree.EncodeTree(tree));
    }

    [Fact]
    public void The_edit_survives_a_reparse_of_what_was_written()
    {
        List<PbNode> tree = PbTree.ParseTree(StockTable());
        (PbNode root, List<FilterRow> rows) = FilterTable.Read(tree);
        FilterTable.SetVisible(root, rows.Single(r => r.Name == "Washed"), visible: true);

        List<FilterRow> reread = FilterTable.Read(PbTree.EncodeTree(tree));

        Assert.True(reread.Single(r => r.Name == "Washed").Visible);
        Assert.Equal(8, reread.Count(r => !r.Visible));
    }

    // ---- appending and removing

    [Fact]
    public void An_appended_row_carries_the_name_and_path_it_was_given()
    {
        List<PbNode> tree = PbTree.ParseTree(StockTable());
        (_, List<FilterRow> rows) = FilterTable.Read(tree);

        FilterTable.AppendRow(tree, FilterTable.Template(rows, new HashSet<string>()),
            "Video_Hero", FilterSpec.InstallRef("video_hero"));

        FilterRow added = FilterTable.Read(PbTree.EncodeTree(tree)).Single(r => r.Name == "Video_Hero");
        Assert.Equal(@"content\tracks\common_assets\post_process\video_hero\video_hero.postprocessing",
            added.Path);
        Assert.True(added.Visible);
    }

    [Fact]
    public void An_appended_row_keeps_the_templates_unidentified_flags()
    {
        // Fields 4 and 5 were never recovered from a descriptor. Cloning is the only reason it is
        // safe to register a row at all, so a change that dropped them has to fail here.
        List<PbNode> tree = PbTree.ParseTree(StockTable());
        (_, List<FilterRow> rows) = FilterTable.Read(tree);

        PbNode added = FilterTable.AppendRow(tree, FilterTable.Template(rows, new HashSet<string>()),
            "Video_Hero", FilterSpec.InstallRef("video_hero"));

        PbNode payload = added.First((int)FilterSpec.Payload)!;
        Assert.Equal([1, 2, 3, 4, 5], payload.Message!.Select(n => n.Number));
        Assert.Equal(1UL, payload.First(4)!.Varint);
        Assert.Equal(1UL, payload.First(5)!.Varint);
    }

    [Fact]
    public void An_appended_row_leaves_every_stock_row_byte_identical()
    {
        byte[] original = StockTable();
        List<PbNode> tree = PbTree.ParseTree(original);
        (_, List<FilterRow> rows) = FilterTable.Read(tree);

        FilterTable.AppendRow(tree, FilterTable.Template(rows, new HashSet<string>()),
            "Video_Hero", FilterSpec.InstallRef("video_hero"));
        byte[] written = PbTree.EncodeTree(tree);

        // The stock table is a prefix of the result apart from the [2] container's length header.
        Assert.Equal(original.Length + Row("Video_Hero", @"content\tracks\common_assets\post_process\video_hero\video_hero.postprocessing", true).Length,
            written.Length);
        Assert.Equal(17, FilterTable.Read(written).Count(r => StockNames.Contains(r.Name)));
    }

    [Fact]
    public void A_template_is_only_ever_taken_from_a_visible_row()
    {
        // A row cloned from a hidden one inherits the missing flag and can never be selected.
        List<FilterRow> rows = FilterTable.Read(StockTable());

        Assert.True(FilterTable.Template(rows, new HashSet<string>()).Visible);
    }

    [Fact]
    public void A_table_with_no_visible_row_to_clone_is_refused()
    {
        List<FilterRow> rows = FilterTable.Read(Table(Row("TV 1", false), Row("TV 4", false)));

        Assert.Throws<InvalidDataException>(() => FilterTable.Template(rows, new HashSet<string>()));
    }

    [Fact]
    public void Removing_rows_drops_only_the_ones_matched()
    {
        List<PbNode> tree = PbTree.ParseTree(Table(
            Row("Default", true), Row("Video_Hero", true), Row("SomeoneElsesFilter", true)));

        Assert.Equal(1, FilterTable.RemoveRows(tree, r => r.Name == "Video_Hero"));

        Assert.Equal(["Default", "SomeoneElsesFilter"],
            FilterTable.Read(PbTree.EncodeTree(tree)).Select(r => r.Name));
    }

    [Fact]
    public void Removing_nothing_leaves_the_file_byte_identical()
    {
        byte[] original = StockTable();
        List<PbNode> tree = PbTree.ParseTree(original);

        Assert.Equal(0, FilterTable.RemoveRows(tree, r => r.Name == "Video_Hero"));

        Assert.Equal(original, PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Appending_after_removing_our_own_row_leaves_one_of_it()
    {
        // The idempotency mechanism: load live, drop ours, re-add. Never a snapshot restore.
        List<PbNode> tree = PbTree.ParseTree(Table(
            Row("Default", true),
            Row("Video_Hero", @"content\old\path.postprocessing", true)));

        FilterTable.RemoveRows(tree, r => r.Name == "Video_Hero");
        (_, List<FilterRow> rows) = FilterTable.Read(tree);
        FilterTable.AppendRow(tree, FilterTable.Template(rows, new HashSet<string>()),
            "Video_Hero", FilterSpec.InstallRef("video_hero"));

        List<FilterRow> after = FilterTable.Read(PbTree.EncodeTree(tree));
        Assert.Single(after, r => r.Name == "Video_Hero");
        Assert.DoesNotContain("old", after.Single(r => r.Name == "Video_Hero").Path);
    }
}
