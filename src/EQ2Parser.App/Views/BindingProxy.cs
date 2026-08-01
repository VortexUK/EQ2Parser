using System.Windows;

namespace EQ2Parser.App.Views;

/// <summary>Freezable bridge that carries the view model into places without
/// a visual-tree DataContext (ColumnDefinitions inside a DataTemplate).</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
