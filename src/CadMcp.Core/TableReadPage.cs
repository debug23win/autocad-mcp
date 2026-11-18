using System.Text.Json;

namespace CadMcp.Core;

/// <summary>A bounded rectangle and continuation for reading an existing table, independent of creation limits.</summary>
public sealed class TableReadPage
{
    public const int MaximumRows = 100, MaximumColumns = 50, MaximumCells = 500;
    public sealed record Cursor(int FirstRow, int FirstColumn, int RowCount, int ColumnCount);
    public int FirstRow { get; }
    public int FirstColumn { get; }
    public int RowCount { get; }
    public int ColumnCount { get; }
    public int RequestedRowCount { get; }
    public int RequestedColumnCount { get; }
    public int TotalRows { get; }
    public int TotalColumns { get; }
    public int EndRow => FirstRow + RowCount;
    public int EndColumn => FirstColumn + ColumnCount;
    public int? NextRow { get; }
    public int? NextColumn { get; }
    public Cursor? NextPage { get; }
    public bool CompleteTable => FirstRow == 0 && FirstColumn == 0 && RowCount == TotalRows && ColumnCount == TotalColumns;

    private TableReadPage(int rows, int columns, int firstRow, int firstColumn, int rowCount, int columnCount)
    {
        TotalRows = rows; TotalColumns = columns; FirstRow = firstRow; FirstColumn = firstColumn;
        RequestedRowCount = rowCount; RequestedColumnCount = columnCount;
        if (firstRow == rows || firstColumn == columns) return;
        int columnLimit = Math.Min(columnCount, MaximumColumns);
        // Use the full table width, not the remaining width. A short final column stripe
        // must not expand the row band and skip cells when traversal returns to column zero.
        int bandRows = Math.Min(Math.Min(rowCount, MaximumRows), MaximumCells / Math.Min(columnLimit, columns));
        RowCount = Math.Min(bandRows, rows - firstRow);
        ColumnCount = Math.Min(columnLimit, columns - firstColumn);
        NextRow = EndRow < rows ? EndRow : null;
        NextColumn = EndColumn < columns ? EndColumn : null;
        NextPage = NextColumn is { } nextColumn ? new(firstRow, nextColumn, RowCount, columnLimit)
            : NextRow is { } nextRow ? new(nextRow, 0, RowCount, columnLimit) : null;
    }

    public static TableReadPage Create(int rows, int columns, int firstRow = 0, int firstColumn = 0, int rowCount = 20, int columnCount = 20)
    {
        if (rows < 0 || columns < 0) throw new ArgumentOutOfRangeException(nameof(rows), "Table dimensions cannot be negative");
        if (firstRow < 0 || firstRow > rows) throw new CadFault("INVALID_TABLE_RANGE", $"first_row must be 0..{rows}; use next_page from the previous table page");
        if (firstColumn < 0 || firstColumn > columns) throw new CadFault("INVALID_TABLE_RANGE", $"first_column must be 0..{columns}; use next_page from the previous table page");
        if (rowCount < 1) throw new CadFault("INVALID_TABLE_RANGE", "row_count must be >= 1; positive counts are capped at 100 rows and the 500-cell page budget");
        if (columnCount < 1) throw new CadFault("INVALID_TABLE_RANGE", "column_count must be >= 1; positive counts are capped at 50 columns and the 500-cell page budget");
        return new(rows, columns, firstRow, firstColumn, rowCount, columnCount);
    }

    public static TableReadPage FromRequest(JsonElement request, int rows, int columns) => Create(rows, columns,
        Integer(request, "first_row", 0), Integer(request, "first_column", 0), Integer(request, "row_count", 20), Integer(request, "column_count", 20));

    private static int Integer(JsonElement request, string name, int fallback)
    {
        if (!request.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int integer))
            throw new CadFault("INVALID_TABLE_RANGE", name + " must be a 32-bit integer");
        return integer;
    }

    public object Metadata() => new
    {
        first_row = FirstRow, first_column = FirstColumn, row_count = RowCount, column_count = ColumnCount,
        requested_row_count = RequestedRowCount, requested_column_count = RequestedColumnCount,
        max_rows = MaximumRows, max_columns = MaximumColumns, max_cells = MaximumCells,
        returned_cells = RowCount * ColumnCount, total_cells = (long)TotalRows * TotalColumns,
        complete_table = CompleteTable, has_more = NextPage is not null,
        traversal = "Follow the complete next_page tuple; column stripes precede the next row band. A nonzero initial offset leaves earlier cells unread."
    };
}
