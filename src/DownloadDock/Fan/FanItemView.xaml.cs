using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DownloadDock.ViewModels;

namespace DownloadDock.Fan;

/// <summary>
/// Visual for one fan item: name pill on the left, preview card on the right. Its render
/// transform pivots on the centre of the thumbnail box so the fan geometry can rotate and
/// scale it exactly where the layout calculator placed it.
/// </summary>
internal partial class FanItemView
{
    public ScaleTransform OpenScale { get; } = new(1, 1);
    public ScaleTransform HoverScale { get; } = new(1, 1);
    public RotateTransform Rotation { get; } = new(0);
    public TranslateTransform Offset { get; } = new(0, 0);

    /// <summary>Distance from the item's left edge to the thumbnail centre.</summary>
    public double PivotX { get; private set; }

    public double ItemWidth { get; private set; }
    public double ItemHeight { get; private set; }

    public int SlotIndex { get; set; }
    public double TargetAngle { get; set; }

    public FanItemViewModel ViewModel => (FanItemViewModel)DataContext;

    public FanItemView(FanItemViewModel vm, double labelWidth, double gap)
    {
        InitializeComponent();
        var box = vm.BoxSize;
        LabelColumn.Width = new GridLength(labelWidth);
        GapColumn.Width = new GridLength(gap);
        ThumbColumn.Width = new GridLength(box);
        ThumbBox.Width = box;
        ThumbBox.Height = box;
        LabelPill.MaxWidth = labelWidth;

        ItemWidth = labelWidth + gap + box;
        ItemHeight = box;
        Width = ItemWidth;
        Height = ItemHeight;
        PivotX = labelWidth + gap + box / 2;
        RenderTransformOrigin = new Point(PivotX / ItemWidth, 0.5);

        var group = new TransformGroup();
        group.Children.Add(OpenScale);
        group.Children.Add(HoverScale);
        group.Children.Add(Rotation);
        group.Children.Add(Offset);
        RenderTransform = group;

        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        if (vm.HasThumbnail) PreviewSurface.Opacity = 1;
    }

    public void Detach() => ViewModel.PropertyChanged -= OnViewModelChanged;

    /// <summary>True if the element under the mouse belongs to the preview card (vs. the label).</summary>
    public bool IsOverThumbnail(DependencyObject? element)
    {
        while (element is not null && element != this)
        {
            if (element == ThumbBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>Position of a point (in this element's coordinates) relative to the visible card, in DIPs.</summary>
    public Point PointInCard(Point itemPoint)
    {
        var vm = ViewModel;
        var cardLeft = PivotX - vm.ThumbWidth / 2;
        var cardTop = ItemHeight / 2 - vm.ThumbHeight / 2;
        return new Point(Math.Clamp(itemPoint.X - cardLeft, 0, vm.ThumbWidth), Math.Clamp(itemPoint.Y - cardTop, 0, vm.ThumbHeight));
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FanItemViewModel.Thumbnail)) return;
        if (ViewModel.HasThumbnail)
        {
            var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
            PreviewSurface.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            PreviewSurface.BeginAnimation(OpacityProperty, null);
            PreviewSurface.Opacity = 0;
        }
    }
}
