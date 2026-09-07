using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

/// <summary>The reconstructed table shown for review, plus copy / CSV export.</summary>
public sealed partial class TableExtractViewModel : ObservableObject
{
    private readonly TableGrid _grid;
    private readonly string _sourceName;
    private readonly int _pageNumber;

    public TableExtractViewModel(TableGrid grid, string sourceName, int pageNumber)
    {
        _grid = grid;
        _sourceName = Path.GetFileNameWithoutExtension(sourceName);
        _pageNumber = pageNumber;

        Table = new DataView(BuildDataTable(grid));
        Summary = $"{grid.RowCount} rows × {grid.ColumnCount} columns — page {pageNumber}";
    }

    public DataView Table { get; }

    public string Summary { get; }

    public event System.Action? Closed;

    [RelayCommand]
    private void CopyTsv()
    {
        Citations.CopyToClipboard(_grid.ToTsv());
    }

    [RelayCommand]
    private void SaveCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save table as CSV",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{_sourceName} p{_pageNumber} table.csv",
            AddExtension = true,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _grid.ToCsv(), new System.Text.UTF8Encoding(true));
        }
        catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    private static DataTable BuildDataTable(TableGrid grid)
    {
        var table = new DataTable();
        int columns = System.Math.Max(1, grid.ColumnCount);
        for (int c = 0; c < columns; c++)
        {
            table.Columns.Add(c.ToString(), typeof(string));
        }

        foreach (IReadOnlyList<string> row in grid.Rows)
        {
            table.Rows.Add(Enumerable.Range(0, columns).Select(c => c < row.Count ? row[c] : string.Empty).ToArray());
        }

        return table;
    }
}
