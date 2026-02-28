using System.Windows;

namespace BackgroundSlideShow;

/// <summary>
/// A Freezable proxy that exposes a Data dependency property.
/// Used to smuggle a DataContext binding into contexts that are disconnected
/// from the visual tree (e.g., ContextMenu, ItemsPanelTemplate).
/// </summary>
public class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
