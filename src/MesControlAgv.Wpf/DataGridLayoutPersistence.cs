using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MesControlAgv.Wpf;

/// <summary>Persists DataGrid column order and widths as local UI preferences.</summary>
public static class DataGridLayoutPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(LayoutHandler), typeof(DataGridLayoutPersistence));

    public static readonly DependencyProperty LayoutKeyProperty = DependencyProperty.RegisterAttached(
        "LayoutKey", typeof(string), typeof(DataGridLayoutPersistence),
        new PropertyMetadata(string.Empty, OnConfigurationChanged));

    public static readonly DependencyProperty PersistLayoutProperty = DependencyProperty.RegisterAttached(
        "PersistLayout", typeof(bool), typeof(DataGridLayoutPersistence),
        new PropertyMetadata(false, OnConfigurationChanged));

    public static void SetLayoutKey(DependencyObject element, string value) => element.SetValue(LayoutKeyProperty, value);
    public static string GetLayoutKey(DependencyObject element) => (string)element.GetValue(LayoutKeyProperty);
    public static void SetPersistLayout(DependencyObject element, bool value) => element.SetValue(PersistLayoutProperty, value);
    public static bool GetPersistLayout(DependencyObject element) => (bool)element.GetValue(PersistLayoutProperty);

    public static void Reset(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        (grid.GetValue(HandlerProperty) as LayoutHandler)?.Reset();
    }

    private static void OnConfigurationChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not DataGrid grid) return;
        var enabled = GetPersistLayout(grid) && !string.IsNullOrWhiteSpace(GetLayoutKey(grid));
        var handler = grid.GetValue(HandlerProperty) as LayoutHandler;
        if (enabled && handler is null)
        {
            grid.SetValue(HandlerProperty, new LayoutHandler(grid, GetLayoutKey(grid)));
        }
        else if (!enabled && handler is not null)
        {
            handler.Dispose();
            grid.ClearValue(HandlerProperty);
        }
    }

    private static string LayoutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesControlAgv",
        "ui-grid-layout.json");

    private sealed class LayoutHandler : IDisposable
    {
        private readonly DataGrid _grid;
        private readonly string _key;
        private readonly Dictionary<string, ColumnSnapshot> _defaults = new(StringComparer.Ordinal);
        private readonly List<(DependencyPropertyDescriptor Descriptor, DataGridColumn Column)> _widthSubscriptions = [];
        private bool _loaded;
        private bool _suppress;

        public LayoutHandler(DataGrid grid, string key)
        {
            _grid = grid;
            _key = key;
            _grid.Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_loaded) return;
            _loaded = true;
            CaptureDefaults();
            SubscribeToColumnWidths();
            _grid.ColumnReordered += OnColumnReordered;
            ApplySaved();
        }

        private void SubscribeToColumnWidths()
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            if (descriptor is null) return;
            foreach (var column in _grid.Columns)
            {
                descriptor.AddValueChanged(column, OnColumnWidthChanged);
                _widthSubscriptions.Add((descriptor, column));
            }
        }

        private void CaptureDefaults()
        {
            for (var index = 0; index < _grid.Columns.Count; index++)
            {
                var column = _grid.Columns[index];
                _defaults[ColumnKey(column, index)] = new ColumnSnapshot(column.DisplayIndex, column.ActualWidth);
            }
        }

        private void ApplySaved()
        {
            if (!ReadLayouts().TryGetValue(_key, out var saved)) return;
            _suppress = true;
            try
            {
                foreach (var entry in saved)
                {
                    var column = FindColumn(entry.Key);
                    if (column is null) continue;
                    if (entry.Width > 24 && !double.IsNaN(entry.Width) && !double.IsInfinity(entry.Width))
                        column.Width = new DataGridLength(entry.Width);
                }

                foreach (var entry in saved.OrderBy(item => item.DisplayIndex))
                {
                    var column = FindColumn(entry.Key);
                    if (column is not null)
                        column.DisplayIndex = Math.Clamp(entry.DisplayIndex, 0, _grid.Columns.Count - 1);
                }
            }
            finally { _suppress = false; }
        }

        private void OnColumnWidthChanged(object? sender, EventArgs args) => SaveIfNeeded();
        private void OnColumnReordered(object? sender, DataGridColumnEventArgs args) => SaveIfNeeded();

        private void SaveIfNeeded()
        {
            if (_suppress || !_loaded) return;
            var layouts = ReadLayouts();
            layouts[_key] = _grid.Columns.Select((column, index) => new ColumnLayout(
                ColumnKey(column, index), column.DisplayIndex, column.ActualWidth)).ToList();
            WriteLayouts(layouts);
        }

        public void Reset()
        {
            if (!_loaded) return;
            _suppress = true;
            try
            {
                foreach (var pair in _defaults)
                {
                    var column = FindColumn(pair.Key);
                    if (column is null) continue;
                    column.DisplayIndex = pair.Value.DisplayIndex;
                    if (pair.Value.Width > 24 && !double.IsNaN(pair.Value.Width) && !double.IsInfinity(pair.Value.Width))
                        column.Width = new DataGridLength(pair.Value.Width);
                }
            }
            finally { _suppress = false; }

            var layouts = ReadLayouts();
            if (layouts.Remove(_key)) WriteLayouts(layouts);
        }

        private DataGridColumn? FindColumn(string key) => _grid.Columns
            .Select((column, index) => new { column, index })
            .FirstOrDefault(item => ColumnKey(item.column, item.index) == key)?.column;

        private static string ColumnKey(DataGridColumn column, int definitionIndex) =>
            $"{definitionIndex}:{column.Header?.ToString() ?? string.Empty}";

        private static Dictionary<string, List<ColumnLayout>> ReadLayouts()
        {
            try
            {
                if (!File.Exists(LayoutPath)) return new(StringComparer.Ordinal);
                return JsonSerializer.Deserialize<Dictionary<string, List<ColumnLayout>>>(File.ReadAllText(LayoutPath), JsonOptions)
                    ?? new(StringComparer.Ordinal);
            }
            catch (IOException) { return new(StringComparer.Ordinal); }
            catch (JsonException) { return new(StringComparer.Ordinal); }
        }

        private static void WriteLayouts(Dictionary<string, List<ColumnLayout>> layouts)
        {
            try
            {
                var directory = Path.GetDirectoryName(LayoutPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(LayoutPath, JsonSerializer.Serialize(layouts, JsonOptions));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            _grid.Loaded -= OnLoaded;
            _grid.ColumnReordered -= OnColumnReordered;
            foreach (var subscription in _widthSubscriptions)
                subscription.Descriptor.RemoveValueChanged(subscription.Column, OnColumnWidthChanged);
            _widthSubscriptions.Clear();
        }

        private sealed record ColumnSnapshot(int DisplayIndex, double Width);
        private sealed record ColumnLayout(string Key, int DisplayIndex, double Width);
    }
}
