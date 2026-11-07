using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DropHalo.App.Styles;

internal static class AppStyles
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];
    private static readonly UISettings System = new();
    private static readonly AccessibilitySettings Accessibility = new();
    private static readonly DispatcherQueue Dispatcher = DispatcherQueue.GetForCurrentThread();
    private static readonly ResourceDictionary Tokens = Application.Current.Resources.MergedDictionaries.Single(
        dictionary => dictionary.Source?.AbsolutePath.EndsWith("/Styles/Tokens.xaml", StringComparison.Ordinal) == true);
    private static FrameworkElement? _root;

    // These shared brushes and system subscriptions live with the app; all brush updates stay on the UI thread.
    static AppStyles()
    {
        System.ColorValuesChanged += (_, _) => Dispatcher.TryEnqueue(Refresh);
    }

    public static event Action? PaletteChanged;

    public static void Initialize(FrameworkElement root)
    {
        if (_root is not null)
        {
            _root.ActualThemeChanged -= OnThemeChanged;
        }

        _root = root;
        _root.ActualThemeChanged += OnThemeChanged;
        Refresh();
    }

    public static Brush Brush(string key)
    {
        if (!Brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Resolve(key));
            Brushes.Add(key, brush);
        }

        return brush;
    }

    private static void OnThemeChanged(FrameworkElement sender, object args) => Refresh();

    private static void Refresh()
    {
        foreach (var (key, brush) in Brushes)
        {
            brush.Color = Resolve(key);
        }

        PaletteChanged?.Invoke();
    }

    private static Color Resolve(string key)
    {
        if (Accessibility.HighContrast)
        {
            return System.UIElementColor(key switch
            {
                "DwBgBrush" or "DwSurfaceBrush" or "DwSurface2Brush" => UIElementType.Window,
                "DwText3Brush" => UIElementType.GrayText,
                "DwAccentBrush" => UIElementType.Hotlight,
                "DwAccentSoftBrush" => UIElementType.Highlight,
                "DwOnAccentBrush" => UIElementType.HighlightText,
                _ => UIElementType.WindowText,
            });
        }

        var light = _root is not null ? _root.ActualTheme == ElementTheme.Light : Application.Current.RequestedTheme == ApplicationTheme.Light;
        var palette = (ResourceDictionary)Tokens.ThemeDictionaries[light ? "Light" : "Default"];
        return ((SolidColorBrush)palette[key]).Color;
    }

    public static Border Card(UIElement child) => new()
    {
        Padding = new Thickness(20),
        CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(1),
        Background = Brush("DwSurfaceBrush"),
        BorderBrush = Brush("DwBorderBrush"),
        Child = child,
    };
}
