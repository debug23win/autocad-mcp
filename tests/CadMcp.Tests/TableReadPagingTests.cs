using System.Text.Json;
using CadMcp.Core;

namespace CadMcp.Tests;

public sealed class TableReadPagingTests
{
    [Theory]
    [InlineData(603, 121, int.MaxValue, int.MaxValue)]
    [InlineData(103, 51, 100, 50)]
    [InlineData(31, 41, 20, 20)]
    [InlineData(150, 3, int.MaxValue, int.MaxValue)]
    public void Complete_cursor_traversal_visits_every_cell_once_without_exceeding_any_page_budget(int rows, int columns, int rowCount, int columnCount)
    {
        JsonElement request = Wire.Element(new { first_row = 0, first_column = 0, row_count = rowCount, column_count = columnCount });
        var cells = new Dictionary<(int Row, int Column), string>(); int pages = 0;
        while (true)
        {
            var page = TableReadPage.FromRequest(request, rows, columns); pages++;
            Assert.InRange(page.RowCount, 1, 100); Assert.InRange(page.ColumnCount, 1, 50);
            Assert.InRange(page.RowCount * page.ColumnCount, 1, 500);
            for (int row = page.FirstRow; row < page.EndRow; row++)
                for (int column = page.FirstColumn; column < page.EndColumn; column++)
                    Assert.True(cells.TryAdd((row, column), $"value:{row}:{column}"), "The cursor repeated a cell");
            var metadata = Wire.Element(page.Metadata());
            Assert.Equal(page.RowCount * page.ColumnCount, metadata.GetProperty("returned_cells").GetInt32());
            Assert.Equal((long)rows * columns, metadata.GetProperty("total_cells").GetInt64());
            Assert.Equal(page.NextPage is not null, metadata.GetProperty("has_more").GetBoolean());
            if (page.NextPage is not { } next) break;
            Assert.True(next.FirstRow > page.FirstRow || next.FirstRow == page.FirstRow && next.FirstColumn > page.FirstColumn);
            request = Wire.Element(next);
        }
        Assert.True(pages > 1); Assert.Equal(rows * columns, cells.Count);
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                Assert.Equal($"value:{row}:{column}", cells[(row, column)]);
    }

    [Fact]
    public void Short_last_column_stripe_keeps_the_same_row_band_before_advancing()
    {
        var first = TableReadPage.Create(100, 120, rowCount: 100, columnCount: 50);
        Assert.Equal(10, first.RowCount); Assert.Equal(50, first.ColumnCount);
        var second = TableReadPage.FromRequest(Wire.Element(first.NextPage!), 100, 120);
        var lastStripe = TableReadPage.FromRequest(Wire.Element(second.NextPage!), 100, 120);
        Assert.Equal(0, lastStripe.FirstRow); Assert.Equal(100, lastStripe.FirstColumn);
        Assert.Equal(10, lastStripe.RowCount); Assert.Equal(20, lastStripe.ColumnCount);
        Assert.Equal(10, lastStripe.NextRow); Assert.Null(lastStripe.NextColumn);
        Assert.Equal(new TableReadPage.Cursor(10, 0, 10, 50), lastStripe.NextPage);
        var nextBand = TableReadPage.FromRequest(Wire.Element(lastStripe.NextPage!), 100, 120);
        Assert.Equal(10, nextBand.FirstRow); Assert.Equal(0, nextBand.FirstColumn); Assert.Equal(10, nextBand.RowCount);
    }

    [Fact]
    public void Large_positive_counts_are_visible_and_capped_with_overflow_safe_actual_dimensions()
    {
        var page = TableReadPage.Create(int.MaxValue, int.MaxValue, int.MaxValue - 1, int.MaxValue - 1, int.MaxValue, int.MaxValue);
        Assert.Equal(1, page.RowCount); Assert.Equal(1, page.ColumnCount);
        Assert.Equal(int.MaxValue, page.EndRow); Assert.Equal(int.MaxValue, page.EndColumn); Assert.Null(page.NextPage);
        var metadata = Wire.Element(page.Metadata());
        Assert.Equal(int.MaxValue, metadata.GetProperty("requested_row_count").GetInt32());
        Assert.Equal(int.MaxValue, metadata.GetProperty("requested_column_count").GetInt32());
        Assert.Equal(100, metadata.GetProperty("max_rows").GetInt32());
        Assert.Equal(50, metadata.GetProperty("max_columns").GetInt32());
        Assert.Equal(500, metadata.GetProperty("max_cells").GetInt32());
        Assert.Equal((long)int.MaxValue * int.MaxValue, metadata.GetProperty("total_cells").GetInt64());
        Assert.False(metadata.GetProperty("complete_table").GetBoolean());
    }

    [Theory]
    [InlineData(-1, 0, 20, 20)]
    [InlineData(0, -1, 20, 20)]
    [InlineData(101, 0, 20, 20)]
    [InlineData(0, 121, 20, 20)]
    [InlineData(0, 0, 0, 20)]
    [InlineData(0, 0, -1, 20)]
    [InlineData(0, 0, 20, 0)]
    [InlineData(0, 0, 20, -1)]
    public void Invalid_counts_and_offsets_do_not_turn_into_an_empty_or_truncated_success(int row, int column, int rowCount, int columnCount)
    {
        var request = Wire.Element(new { first_row = row, first_column = column, row_count = rowCount, column_count = columnCount });
        Assert.Equal("INVALID_TABLE_RANGE", Assert.Throws<CadFault>(() => TableReadPage.FromRequest(request, 100, 120)).Code);
    }

    [Theory]
    [InlineData("{\"row_count\":1.5}")]
    [InlineData("{\"column_count\":\"100\"}")]
    [InlineData("{\"first_row\":2147483648}")]
    public void Noninteger_or_unrepresentable_request_values_have_a_read_range_error(string json)
    {
        using var request = JsonDocument.Parse(json);
        Assert.Equal("INVALID_TABLE_RANGE", Assert.Throws<CadFault>(() => TableReadPage.FromRequest(request.RootElement, 100, 120)).Code);
    }

    [Fact]
    public void Default_counts_and_exact_table_boundaries_are_explicit()
    {
        var defaults = TableReadPage.FromRequest(Wire.Element(new { }), 70, 80);
        Assert.Equal(20, defaults.RowCount); Assert.Equal(20, defaults.ColumnCount);
        var complete = TableReadPage.Create(7, 3, rowCount: int.MaxValue, columnCount: int.MaxValue);
        Assert.True(complete.CompleteTable); Assert.Null(complete.NextPage);
        foreach (var end in new[] { TableReadPage.Create(70, 80, 70, 0), TableReadPage.Create(70, 80, 0, 80), TableReadPage.Create(0, 0) })
        {
            Assert.Equal(0, end.RowCount * end.ColumnCount); Assert.Null(end.NextPage); Assert.Null(end.NextRow); Assert.Null(end.NextColumn);
        }
    }

    [Fact]
    public void A_nonzero_initial_offset_is_not_labeled_as_a_complete_table()
    {
        var page = TableReadPage.Create(603, 121, 600, 120, 5000, 5000);
        Assert.Equal(3, page.RowCount); Assert.Equal(1, page.ColumnCount); Assert.Null(page.NextPage);
        Assert.False(page.CompleteTable);
        Assert.False(Wire.Element(page.Metadata()).GetProperty("complete_table").GetBoolean());
    }

    [Fact]
    public async Task Actual_MCP_schema_exposes_table_bounds_and_large_counts_reach_the_bounded_reader()
    {
        string pipe = "table-read-pages-" + Guid.NewGuid().ToString("N");
        using var broker = new PipeServer(pipe, (request, _) =>
        {
            Assert.Equal("cad_table_get", request.Operation);
            var page = TableReadPage.FromRequest(request.Data, 650, 120);
            return Task.FromResult(new Response(request.RequestId, "completed", new
            {
                pagination = page.Metadata(), next_page = page.NextPage, next_row = page.NextRow, next_column = page.NextColumn,
                cells = Enumerable.Range(page.FirstRow, page.RowCount).SelectMany(row => Enumerable.Range(page.FirstColumn, page.ColumnCount).Select(column => new { row, column, value = row * 120 + column })).ToArray()
            }, "s", "d", 21));
        });
        broker.Start(); var host = await McpHostProcess.StartAsync(pipe);
        JsonElement result;
        await using (host)
        {
            var tools = await host.RequestAsync("tools/list", new { });
            var tool = tools.GetProperty("tools").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "cad_table_get");
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            var properties = tool.GetProperty("inputSchema").GetProperty("properties");
            Assert.Contains(">= 0", properties.GetProperty("first_row").GetProperty("description").GetString());
            Assert.Contains("next_page", properties.GetProperty("first_column").GetProperty("description").GetString());
            Assert.Contains("100", properties.GetProperty("row_count").GetProperty("description").GetString());
            Assert.Contains("50", properties.GetProperty("column_count").GetProperty("description").GetString());
            result = await host.CallAsync("cad_table_get", new { session_id = "s", document_id = "d", expected_revision = 21, handle = "AB", first_row = 600, first_column = 100, row_count = int.MaxValue, column_count = int.MaxValue });
        }
        Assert.False(result.TryGetProperty("isError", out var failed) && failed.GetBoolean(), "MCP table result: " + result.GetRawText() + "\nHost stderr: " + await host.ErrorOutput);
        using var body = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        var data = body.RootElement.GetProperty("data"); var pagination = data.GetProperty("pagination");
        Assert.Equal(10, pagination.GetProperty("row_count").GetInt32()); Assert.Equal(20, pagination.GetProperty("column_count").GetInt32());
        Assert.Equal(int.MaxValue, pagination.GetProperty("requested_row_count").GetInt32()); Assert.Equal(200, data.GetProperty("cells").GetArrayLength());
        Assert.Equal(610, data.GetProperty("next_page").GetProperty("first_row").GetInt32());
        Assert.Equal(0, data.GetProperty("next_page").GetProperty("first_column").GetInt32());
    }
}
