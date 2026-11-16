using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HardwareTriage.ViewModels;

namespace HardwareTriage;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string ?? "#22C55E";
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            if (parameter is string mode && mode.Equals("tint", StringComparison.OrdinalIgnoreCase))
            {
                color = Color.FromArgb(38, color.R, color.G, color.B);
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        var mode = parameter as string;

        if (string.Equals(mode, "InvertBool", StringComparison.OrdinalIgnoreCase))
        {
            return !flag;
        }

        if (string.Equals(mode, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
