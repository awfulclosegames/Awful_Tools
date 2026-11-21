using System.Windows;
using System.Windows.Media;
using AwfulDiff.Controls;
using AwfulDiff.Models;

namespace AwfulDiff.Services;

/// <summary>
/// Manages application-wide theme switching.
/// Coordinates resource dictionary changes and FastDiffViewer brush updates.
/// </summary>
public static class ThemeManager
{
    /// <summary>The currently active theme name.</summary>
    public static string CurrentThemeName { get; private set; } = "Awful";

    /// <summary>
    /// Applied font scale multiplier (default 1.0).
    /// </summary>
    public static double FontScale { get; private set; } = 1.0;

    /// <summary>
    /// Whether Visual Studio-style syntax coloring is active in the diff viewers.
    /// </summary>
    public static bool UseSyntaxColoring { get; private set; } = false;

    /// <summary>
    /// Raised when the theme changes, allowing subscribers to update visuals.
    /// </summary>
    public static event Action<string>? ThemeChanged;

    /// <summary>
    /// Raised when font scale changes.
    /// </summary>
    public static event Action<double>? FontScaleChanged;

    /// <summary>
    /// Raised when syntax coloring is toggled on or off.
    /// </summary>
    public static event Action<bool>? SyntaxColoringChanged;

    /// <summary>
    /// Enable or disable syntax coloring on all live viewer instances.
    /// Kept separate from ApplyTheme so the theme early-return guard stays simple.
    /// </summary>
    public static void SetSyntaxColoring(bool enabled)
    {
        if (UseSyntaxColoring == enabled)
            return;

        UseSyntaxColoring = enabled;
        FastDiffViewer.SetSyntaxColoring(enabled);
        SyntaxColoringChanged?.Invoke(enabled);
    }

    /// <summary>
    /// Whether the current theme uses custom window chrome (Awful) or system chrome.
    /// </summary>
    public static bool UsesCustomChrome => CurrentThemeName == "Awful";

    // Per-theme custom colour overrides, keyed by theme name. Absent entry = built-in colours.
    private static readonly Dictionary<string, ThemeColorOverrides> _customColors =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The registered custom colours for a theme, or null when it uses its built-in colours.
    /// </summary>
    public static ThemeColorOverrides? GetCustomColors(string themeName)
        => _customColors.TryGetValue(themeName, out var o) ? o : null;

    /// <summary>
    /// Register (or clear, when `overrides` is null / all disabled) a theme's custom colours.
    /// If that theme is active the viewer theme is rebuilt immediately — ApplyTheme's
    /// early-return guard would otherwise swallow a same-name re-apply.
    /// </summary>
    public static void SetCustomColors(string themeName, ThemeColorOverrides? overrides)
    {
        if (overrides == null || !overrides.HasAnyEnabled())
            _customColors.Remove(themeName);
        else
            _customColors[themeName] = overrides;

        if (string.Equals(themeName, CurrentThemeName, StringComparison.OrdinalIgnoreCase))
            FastDiffViewer.ApplyTheme(CurrentThemeName, FontScale);
    }

    /// <summary>
    /// Apply a theme by name. Updates application resources and notifies subscribers.
    /// </summary>
    public static void ApplyTheme(string themeName, double fontScale = 1.0)
    {
        if (string.IsNullOrEmpty(themeName))
            themeName = "Awful";

        // Clamp font scale
        fontScale = System.Math.Clamp(fontScale, 0.5, 2.0);

        bool themeChanged = themeName != CurrentThemeName;
        bool scaleChanged = System.Math.Abs(fontScale - FontScale) > 0.001;

        if (!themeChanged && !scaleChanged)
            return;

        CurrentThemeName = themeName;
        FontScale = fontScale;

        // Update application resource dictionary for UI elements
        ApplyUiResources(themeName);

        // Update FastDiffViewer brushes
        FastDiffViewer.ApplyTheme(themeName, fontScale);

        if (themeChanged)
            ThemeChanged?.Invoke(themeName);

        if (scaleChanged)
            FontScaleChanged?.Invoke(fontScale);
    }

    /// <summary>
    /// Apply UI resource dictionary for the given theme to Application.Resources.
    /// </summary>
    private static void ApplyUiResources(string themeName)
    {
        var uri = themeName switch
        {
            "SystemLight" => new System.Uri("pack://application:,,,/AwfulDiff;component/Resources/Themes.SystemLight.xaml", System.UriKind.Absolute),
            "SystemDark" => new System.Uri("pack://application:,,,/AwfulDiff;component/Resources/Themes.SystemDark.xaml", System.UriKind.Absolute),
            _ => new System.Uri("pack://application:,,,/AwfulDiff;component/Resources/Themes.xaml", System.UriKind.Absolute),
        };

        var resourceDict = new ResourceDictionary { Source = uri };
        // Theme-agnostic control styles (e.g. the themed ScrollBar) merge after the theme dict
        // so they survive the dictionary replacement on every theme switch.
        var controlsDict = new ResourceDictionary
        {
            Source = new System.Uri("pack://application:,,,/AwfulDiff;component/Resources/Controls.Themed.xaml", System.UriKind.Absolute),
        };

        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(resourceDict);
        Application.Current.Resources.MergedDictionaries.Add(controlsDict);
    }
}
