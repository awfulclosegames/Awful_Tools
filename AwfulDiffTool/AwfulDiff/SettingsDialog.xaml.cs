using AwfulDiff.Models;
using AwfulDiff.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AwfulDiff;

/// <summary>
/// Settings dialog — theme selection and font scale control.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly string _originalTheme;
    private readonly double _originalFontScale;
    private readonly bool _originalSyntaxColoring;

    /// <summary>Working copy of the selected theme's custom colours (persisted on Apply).</summary>
    private ThemeColorOverrides _overrides = new();

    /// <summary>Role key → enable checkbox, for the Custom Colours rows.</summary>
    private Dictionary<string, CheckBox>? _colorChecks;

    public string SelectedTheme => GetSelectedTheme();
    public double SelectedFontScale => FontScaleSlider.Value;

    public SettingsDialog()
    {
        InitializeComponent();
        _originalTheme = ThemeManager.CurrentThemeName;
        _originalFontScale = ThemeManager.FontScale;
        _originalSyntaxColoring = ThemeManager.UseSyntaxColoring;

        // Must exist before PopulateControls — the theme radio's Checked event fires during
        // population and loads that theme's saved overrides.
        _colorChecks = new()
        {
            ["Added"] = ChkColorAdded,
            ["Removed"] = ChkColorRemoved,
            ["Modified"] = ChkColorModified,
            ["MergeCandidates"] = ChkColorMergeCandidates,
            ["Conflicts"] = ChkColorConflicts,
            ["Text"] = ChkColorText,
        };

        // Populate with current values
        PopulateControls();
    }

    private void PopulateControls()
    {
        var theme = ThemeManager.CurrentThemeName;
        RbAwful.IsChecked = theme == "Awful";
        RbSystemLight.IsChecked = theme == "SystemLight";
        RbSystemDark.IsChecked = theme == "SystemDark";

        FontScaleSlider.Value = ThemeManager.FontScale;
        UpdateFontScaleLabel();

        var engine = SettingsService.Load().DiffAlgorithm;
        RbPatienceEngine.IsChecked = engine == nameof(DiffAlgorithm.Patience);
        RbStandardEngine.IsChecked = !RbPatienceEngine.IsChecked;

        ChkSyntaxColoring.IsChecked = ThemeManager.UseSyntaxColoring;
    }

    private string GetSelectedTheme()
    {
        if (RbSystemLight.IsChecked == true) return "SystemLight";
        if (RbSystemDark.IsChecked == true) return "SystemDark";
        return "Awful";
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        var theme = GetSelectedTheme();
        var fontScale = FontScaleSlider.Value;
        var overrides = BuildOverrides();

        // Register before ApplyTheme so the rebuilt viewer theme includes the new colours.
        ThemeManager.SetCustomColors(theme, overrides);
        ThemeManager.ApplyTheme(theme, fontScale);
        ThemeManager.SetSyntaxColoring(ChkSyntaxColoring.IsChecked == true);

        var s = GetUpdatedSettings();
        if (overrides.HasAnyEnabled())
            s.CustomColors[theme] = overrides;
        else
            s.CustomColors.Remove(theme);
        SettingsService.Save(s);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        // Restore original theme if user cancelled with unsaved changes
        var theme = GetSelectedTheme();
        var fontScale = FontScaleSlider.Value;

        if (theme != _originalTheme || Math.Abs(fontScale - _originalFontScale) > 0.001)
        {
            ThemeManager.ApplyTheme(_originalTheme, _originalFontScale);
        }

        bool syntax = ChkSyntaxColoring.IsChecked == true;
        if (syntax != _originalSyntaxColoring)
            ThemeManager.SetSyntaxColoring(_originalSyntaxColoring);

        DialogResult = false;
        Close();
    }

    private void Help_Click(object? sender, RoutedEventArgs e)
    {
        // Single instance: activate an already-open manual window instead of stacking another.
        var existing = Application.Current.Windows.OfType<ManualWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        // Non-modal with no owner so it survives the settings dialog closing.
        new ManualWindow().Show();
    }

    // ── Custom title bar (WindowStyle=None) ───────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed && e.ClickCount == 1)
            DragMove();
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Cancel_Click(sender, e);

    private void PresetScale_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && double.TryParse(btn.Tag?.ToString(), out var val))
        {
            FontScaleSlider.Value = val;
        }
        UpdateFontScaleLabel();
    }

    private void UpdateFontScaleLabel()
    {
        if (FontScaleValue != null)
            FontScaleValue.Text = $"{(int)(FontScaleSlider.Value * 100)}%";
    }

    private void FontScaleSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFontScaleLabel();
    }

    // ── Custom Colours (per selected theme) ───────────────────────────────────

    /// <summary>The enable checkbox's role was toggled — refresh the swatches.</summary>
    private void ColorRow_StateChanged(object? sender, RoutedEventArgs e) => RefreshSwatches(GetSelectedTheme());

    /// <summary>A theme radio was selected — load that theme's saved overrides into the rows.</summary>
    private void ThemeRadio_Checked(object? sender, RoutedEventArgs e) => LoadOverridesFor(GetSelectedTheme());

    private static ColorOverride Role(ThemeColorOverrides o, string role) => role switch
    {
        "Added" => o.Added,
        "Removed" => o.Removed,
        "Modified" => o.Modified,
        "MergeCandidates" => o.MergeCandidates,
        "Conflicts" => o.Conflicts,
        _ => o.Text
    };

    /// <summary>The selected theme's built-in colour for a role — seeds the picker when no custom colour is active.</summary>
    private static Color BaseRoleColor(string theme, string role)
    {
        var t = DiffViewerTheme.Create(theme);
        return role switch
        {
            "Added" => t.FgAdded.Color,
            "Removed" => t.FgRemoved.Color,
            "Modified" => t.FgModified.Color,
            "MergeCandidates" => t.FgChangeA.Color,
            "Conflicts" => t.FgConflict.Color,
            _ => t.FgUnchanged.Color
        };
    }

    private void LoadOverridesFor(string theme)
    {
        var saved = SettingsService.Load().CustomColors;
        _overrides = saved.TryGetValue(theme, out var o) ? o : new ThemeColorOverrides();

        ChkColorAdded.IsChecked = _overrides.Added.Enabled;
        ChkColorRemoved.IsChecked = _overrides.Removed.Enabled;
        ChkColorModified.IsChecked = _overrides.Modified.Enabled;
        ChkColorMergeCandidates.IsChecked = _overrides.MergeCandidates.Enabled;
        ChkColorConflicts.IsChecked = _overrides.Conflicts.Enabled;
        ChkColorText.IsChecked = _overrides.Text.Enabled;

        RefreshSwatches(theme);
    }

    /// <summary>
    /// Swatch shows the chosen colour when enabled + valid, otherwise the theme's built-in
    /// colour dimmed — so an unchecked row previews what "default" actually looks like.
    /// </summary>
    private void RefreshSwatches(string theme)
    {
        var baseTheme = DiffViewerTheme.Create(theme);

        UpdateSwatch(ChkColorAdded, BtnSwatchAdded, _overrides.Added, baseTheme.FgAdded.Color);
        UpdateSwatch(ChkColorRemoved, BtnSwatchRemoved, _overrides.Removed, baseTheme.FgRemoved.Color);
        UpdateSwatch(ChkColorModified, BtnSwatchModified, _overrides.Modified, baseTheme.FgModified.Color);
        UpdateSwatch(ChkColorMergeCandidates, BtnSwatchMergeCandidates, _overrides.MergeCandidates, baseTheme.FgChangeA.Color);
        UpdateSwatch(ChkColorConflicts, BtnSwatchConflicts, _overrides.Conflicts, baseTheme.FgConflict.Color);
        UpdateSwatch(ChkColorText, BtnSwatchText, _overrides.Text, baseTheme.FgUnchanged.Color);
    }

    private static void UpdateSwatch(CheckBox chk, Button swatch, ColorOverride ov, Color baseColor)
    {
        bool on = chk.IsChecked == true && DiffViewerTheme.TryParseHex(ov.Hex, out var c);
        swatch.Background = new SolidColorBrush(on ? c : baseColor);
        swatch.Opacity = on ? 1.0 : 0.35;
        swatch.ToolTip = on ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "Theme default";
    }

    private void Swatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string role) return;

        var theme = GetSelectedTheme();
        var ov = Role(_overrides, role);

        // Start the picker from the current effective colour: the custom colour when
        // ticked and valid, otherwise the selected theme's built-in colour for this role.
        Color initial = (ov.Enabled && DiffViewerTheme.TryParseHex(ov.Hex, out var c))
            ? c
            : BaseRoleColor(theme, role);

        var picker = new ColorPickerDialog(initial) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            ov.Hex = picker.SelectedHex;
            ov.Enabled = true;
            _colorChecks![role].IsChecked = true;   // fires ColorRow_StateChanged → swatch refresh
        }
    }

    /// <summary>Snapshot the rows into a ThemeColorOverrides (checkboxes are the enabled flags).</summary>
    private ThemeColorOverrides BuildOverrides()
    {
        _overrides.Added.Enabled = ChkColorAdded.IsChecked == true;
        _overrides.Removed.Enabled = ChkColorRemoved.IsChecked == true;
        _overrides.Modified.Enabled = ChkColorModified.IsChecked == true;
        _overrides.MergeCandidates.Enabled = ChkColorMergeCandidates.IsChecked == true;
        _overrides.Conflicts.Enabled = ChkColorConflicts.IsChecked == true;
        _overrides.Text.Enabled = ChkColorText.IsChecked == true;
        return _overrides;
    }

    private AppSettings GetUpdatedSettings()
    {
        var s = SettingsService.Load();
        s.ThemeName = GetSelectedTheme();
        s.FontScale = FontScaleSlider.Value;
        s.DiffAlgorithm = RbPatienceEngine.IsChecked == true
            ? nameof(DiffAlgorithm.Patience)
            : nameof(DiffAlgorithm.Standard);
        s.UseSyntaxColoring = ChkSyntaxColoring.IsChecked == true;
        return s;
    }
}
