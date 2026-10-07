namespace Contexo.Core.Abstractions;

/// <summary>One cell of a table to render. Row/Column are 0-based grid positions of the cell's top-left corner.</summary>
public sealed record TableCell(int Row, int Column, string Text, int RowSpan = 1, int ColSpan = 1);

/// <summary>Structure-preserving table model shared by Word, PowerPoint and Excel parsers.</summary>
/// <param name="Cells">Only top-left cells of merged areas are listed; covered positions are omitted.</param>
/// <param name="HeaderRowCount">Leading rows rendered with &lt;th&gt; inside &lt;thead&gt;.</param>
public sealed record TableModel(int RowCount, int ColumnCount, IReadOnlyList<TableCell> Cells, int HeaderRowCount = 0, string? Caption = null);
