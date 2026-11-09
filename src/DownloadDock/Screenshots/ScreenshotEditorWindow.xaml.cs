using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DownloadDock.Core.Settings;
using DownloadDock.Dragging;
using DownloadDock.Services;

namespace DownloadDock.Screenshots;

/// <summary>
/// The screenshot preview/editor opened by clicking a floating thumbnail: markup (pen, highlighter),
/// crop, undo, copy, save, save as, open, delete and a drag handle that drops the edited image into any
/// app as a real PNG. "Done" saves the changes into the screenshot's file, as on macOS.
/// </summary>
internal partial class ScreenshotEditorWindow
{
    private readonly ScreenshotItem _item;
    private readonly ScreenshotStore _store;
    private readonly Stack<Action> _undo = new();
    private BitmapSource _image;
    private Color _color = Color.FromRgb(0xE8, 0x2B, 0x2B);
    private Point? _cropStart;
    private Point? _dragPress;
    private bool _dirty;

    public ScreenshotEditorWindow(ScreenshotItem item, ScreenshotStore store, ThemePreference theme)
    {
        _item = item;
        _store = store;
        _image = item.Image;
        ThemeMode = theme switch { ThemePreference.Light => ThemeMode.Light, ThemePreference.Dark => ThemeMode.Dark, _ => ThemeMode.System };
        InitializeComponent();
        ShowImage(_image);
        DragThumb.Source = _image;
        Ink.StrokeCollected += (_, e) =>
        {
            _dirty = true;
            var stroke = e.Stroke;
            _undo.Push(() => Ink.Strokes.Remove(stroke));
        };
        foreach (var c in new[] { "#E82B2B", "#F5A623", "#2BB34A", "#2F7BEA", "#111111", "#FFFFFF" })
        {
            var color = (Color)ColorConverter.ConvertFromString(c);
            var swatch = new Border
            {
                Width = 18, Height = 18, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                ToolTip = "Ink colour",
            };
            swatch.MouseLeftButtonUp += (_, _) => { _color = color; ApplyTool(); };
            Swatches.Children.Add(swatch);
        }
        UpdateTitle();
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
    }

    private string? CurrentFile => _item.FileTask.IsCompletedSuccessfully ? _item.FileTask.Result : null;

    private void UpdateTitle()
    {
        var name = CurrentFile is { } f ? System.IO.Path.GetFileName(f) : "Screenshot";
        Title = (_dirty ? "• " : "") + name;
        Status.Text = $"{_image.PixelWidth} × {_image.PixelHeight} px" + (CurrentFile is { } p ? "   ·   " + p : "") + (_item.IsTemporary ? "   ·   temporary (use Done or Save As to keep it)" : "");
    }

    private void ShowImage(BitmapSource image)
    {
        ShotImage.Source = image;
        Surface.Width = image.PixelWidth;
        Surface.Height = image.PixelHeight;
        ShotImage.Width = image.PixelWidth;
        ShotImage.Height = image.PixelHeight;
    }

    // ------------------------------------------------------------------ tools

    private void Pen_Click(object sender, RoutedEventArgs e) => SelectTool(PenButton);
    private void Highlighter_Click(object sender, RoutedEventArgs e) => SelectTool(HighlighterButton);
    private void Crop_Click(object sender, RoutedEventArgs e) => SelectTool(CropButton);

    private void SelectTool(ToggleButton button)
    {
        foreach (var b in new[] { PenButton, HighlighterButton, CropButton })
            if (b != button) b.IsChecked = false;
        ApplyTool();
    }

    private void ApplyTool()
    {
        var scale = Math.Max(1, Math.Max(_image.PixelWidth, _image.PixelHeight) / 900.0);
        CropLayer.Visibility = CropButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (PenButton.IsChecked == true)
        {
            Ink.EditingMode = InkCanvasEditingMode.Ink;
            Ink.DefaultDrawingAttributes = new DrawingAttributes { Color = _color, Width = 4 * scale, Height = 4 * scale, FitToCurve = true };
        }
        else if (HighlighterButton.IsChecked == true)
        {
            Ink.EditingMode = InkCanvasEditingMode.Ink;
            var c = _color == Color.FromRgb(0xE8, 0x2B, 0x2B) ? Color.FromRgb(0xFF, 0xE3, 0x3B) : _color;
            Ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = c, Width = 6 * scale, Height = 20 * scale, IsHighlighter = true, StylusTip = StylusTip.Rectangle, FitToCurve = false,
            };
        }
        else
        {
            Ink.EditingMode = InkCanvasEditingMode.None;
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _undo.Pop()();
        _dirty = _undo.Count > 0;
        UpdateTitle();
    }

    private void Crop_Down(object sender, MouseButtonEventArgs e)
    {
        _cropStart = e.GetPosition(Surface);
        CropLayer.CaptureMouse();
        CropRect.Visibility = Visibility.Visible;
        UpdateCropRect(_cropStart.Value);
    }

    private void Crop_Move(object sender, MouseEventArgs e)
    {
        if (_cropStart is not null) UpdateCropRect(e.GetPosition(Surface));
    }

    private void UpdateCropRect(Point p)
    {
        var r = new Rect(_cropStart!.Value, p);
        Canvas.SetLeft(CropRect, r.X);
        Canvas.SetTop(CropRect, r.Y);
        CropRect.Width = r.Width;
        CropRect.Height = r.Height;
    }

    private void Crop_Up(object sender, MouseButtonEventArgs e)
    {
        if (_cropStart is not { } start) return;
        CropLayer.ReleaseMouseCapture();
        _cropStart = null;
        CropRect.Visibility = Visibility.Collapsed;
        var r = Rect.Intersect(new Rect(start, e.GetPosition(Surface)), new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
        if (r.IsEmpty || r.Width < 4 || r.Height < 4) return;
        var rect = new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);

        var previousImage = _image;
        var previousStrokes = Ink.Strokes.Clone();
        var flattened = Flatten();
        _image = ScreenCapture.Crop(flattened, rect);
        Ink.Strokes.Clear();
        ShowImage(_image);
        _undo.Push(() =>
        {
            _image = previousImage;
            ShowImage(_image);
            Ink.Strokes = previousStrokes;
        });
        _dirty = true;
        CropButton.IsChecked = false;
        ApplyTool();
        UpdateTitle();
    }

    // ------------------------------------------------------------------ output

    /// <summary>The image with markup burned in, at full resolution.</summary>
    internal BitmapSource Flatten()
    {
        if (Ink.Strokes.Count == 0) return _image;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_image, new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
            Ink.Strokes.Draw(dc);
        }
        var rtb = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Writes the edits into the screenshot file (making a temporary one permanent first).</summary>
    private bool Save()
    {
        try
        {
            var target = _item.IsTemporary ? _store.MakePermanent(_item) : CurrentFile ?? _store.MakePermanent(_item);
            if (_dirty)
            {
                if (_store.IsInUse(target))
                {
                    // Never rewrite a file that is mid-drag; save the edits next to it instead.
                    target = Core.Screenshots.ScreenshotNaming.UniquePath(System.IO.Path.GetDirectoryName(target)!, DateTime.Now);
                }
                ScreenshotClipboard.Write(Flatten(), target);
            }
            _dirty = false;
            UpdateTitle();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The screenshot could not be saved:\n" + ex.Message, "DownloadDock", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if ((_dirty || _item.IsTemporary) && !Save()) return;
        _dirty = false;
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ScreenshotClipboard.Copy(Flatten(), _dirty ? null : CurrentFile);

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenshotClipboard.SaveAs(Flatten(), CurrentFile, this) is { } path)
            Status.Text = "Saved to " + path;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && !Save()) return;
        if (CurrentFile is { } f)
        {
            try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { Log.Error("Open failed", ex); }
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete this screenshot?", "DownloadDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (CurrentFile is { } f && !_store.IsInUse(f))
        {
            try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Delete failed", ex); }
        }
        _dirty = false;
        Close();
    }

    private void DragHandle_Down(object sender, MouseButtonEventArgs e) => _dragPress = e.GetPosition(this);

    private void DragHandle_Move(object sender, MouseEventArgs e)
    {
        if (_dragPress is not { } press || e.LeftButton != MouseButtonState.Pressed) { _dragPress = null; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - press.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragPress = null;

        // Edited? Drop the edited version (a new file); otherwise the screenshot file itself.
        string? path;
        if (_dirty)
        {
            path = _store.WritePng(Flatten(), ScreenshotStore.TemporaryFolder, DateTime.Now);
        }
        else
        {
            path = _item.WaitForFile(TimeSpan.FromSeconds(5));
        }
        if (path is null) return;
        using var lease = _store.Use(path);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var image = FileDragSource.CreateDragImage(Flatten(), 160, dpi, isIcon: false);
        FileDragSource.DoDragDrop(new System.Windows.Interop.WindowInteropHelper(this).Handle, new[] { path }, image, new Point(20 * dpi, 20 * dpi), out _);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { Copy_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.P) { PenButton.IsChecked = PenButton.IsChecked != true; SelectTool(PenButton); }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.H) { HighlighterButton.IsChecked = HighlighterButton.IsChecked != true; SelectTool(HighlighterButton); }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.C) { CropButton.IsChecked = CropButton.IsChecked != true; SelectTool(CropButton); }
        else if (e.Key == Key.Escape) Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty) return;
        var answer = MessageBox.Show(this, "Save your changes to this screenshot?", "DownloadDock", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) e.Cancel = true;
        else if (answer == MessageBoxResult.Yes && !Save()) e.Cancel = true;
    }
}
