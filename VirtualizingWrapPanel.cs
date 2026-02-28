using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BackgroundSlideShow;

/// <summary>
/// A virtualizing panel that lays out children in a wrapping grid of uniform-sized cells.
/// Only creates visuals for items that are on-screen, keeping memory usage low for large collections.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(64.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(64.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private int _columns = 1;
    private int _rows;
    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private ScrollViewer? _scrollOwner;

    private int GetItemCount()
    {
        var generator = ItemContainerGenerator as ItemContainerGenerator;
        return generator?.Items.Count ?? 0;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsInfinity(availableSize.Width) || availableSize.Width <= 0)
            availableSize.Width = 500;

        _columns = Math.Max(1, (int)(availableSize.Width / ItemWidth));
        var itemCount = GetItemCount();
        _rows = (int)Math.Ceiling((double)itemCount / _columns);

        var viewportHeight = availableSize.Height;
        _viewport = new Size(availableSize.Width, viewportHeight);
        _extent = new Size(availableSize.Width, _rows * ItemHeight);

        if (_offset.Y > _extent.Height - _viewport.Height && _extent.Height > _viewport.Height)
            _offset.Y = _extent.Height - _viewport.Height;
        if (_offset.Y < 0) _offset.Y = 0;

        _scrollOwner?.InvalidateScrollInfo();

        if (itemCount == 0)
        {
            if (InternalChildren.Count > 0)
                RemoveInternalChildRange(0, InternalChildren.Count);
            return availableSize;
        }

        var firstVisibleRow = (int)(_offset.Y / ItemHeight);
        var lastVisibleRow = (int)((_offset.Y + viewportHeight) / ItemHeight);
        var firstIndex = firstVisibleRow * _columns;
        var lastIndex = Math.Min(itemCount - 1, (lastVisibleRow + 1) * _columns - 1);

        // Generate and measure visible items
        var generator2 = ItemContainerGenerator;
        var startPos = generator2.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        using (generator2.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (int i = firstIndex; i <= lastIndex; i++)
            {
                var child = generator2.GenerateNext(out bool isNew) as UIElement;
                if (child == null) continue;

                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);
                    generator2.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
                childIndex++;
            }
        }

        // Remove items that are no longer visible
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = generator2.GeneratorPositionFromIndex(
                generator2.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
            var itemIndex = generator2.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));

            if (itemIndex < firstIndex || itemIndex > lastIndex)
            {
                generator2.Remove(new GeneratorPosition(i, 0), 1);
                RemoveInternalChildRange(i, 1);
            }
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator == null) return finalSize;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) continue;

            var row = itemIndex / _columns;
            var col = itemIndex % _columns;
            var x = col * ItemWidth;
            var y = row * ItemHeight - _offset.Y;

            child.Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // Offset may be beyond new extent
                if (_offset.Y > 0)
                    _offset.Y = Math.Max(0, Math.Min(_offset.Y, _extent.Height - _viewport.Height));
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    // IScrollInfo implementation
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentHeight => _extent.Height;
    public double ExtentWidth => _extent.Width;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public double ViewportHeight => _viewport.Height;
    public double ViewportWidth => _viewport.Width;

    public ScrollViewer? ScrollOwner
    {
        get => _scrollOwner;
        set => _scrollOwner = value;
    }

    public void LineDown() => SetVerticalOffset(_offset.Y + ItemHeight);
    public void LineUp() => SetVerticalOffset(_offset.Y - ItemHeight);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + ItemHeight * 3);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - ItemHeight * 3);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (offset == _offset.Y) return;
        _offset.Y = offset;
        _scrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
