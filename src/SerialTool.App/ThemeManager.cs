// ThemeMode (WPF Fluent theme switching) is still marked experimental.
#pragma warning disable WPF0001
using System.Windows;
using Microsoft.Win32;
using SerialTool.App.Controls;

namespace SerialTool.App;

/// <summary>
/// Applies the light or dark theme: WPF's Fluent <c>ThemeMode</c> for the standard controls (menus, combo boxes,
/// scrollbars, dialogs, title bar), the app's own color dictionary (Themes/*.xaml), and the text/hex view palette.
/// "System" follows the Windows app theme and switches live when it changes.
/// </summary>
internal static class ThemeManager
{
    private static AppTheme _theme = AppTheme.System;
    private static Dictionary<string, Dictionary<string, string>>? _overrides;
    private static bool _listening;

    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme, Dictionary<string, Dictionary<string, string>>? colorOverrides)
    {
        _theme = theme;
        _overrides = colorOverrides;
        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        Update();
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_theme != AppTheme.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(Update);
    }

    private static void Update()
    {
        var app = Application.Current;
        if (app is null) return;
        bool dark = _theme == AppTheme.Dark || (_theme == AppTheme.System && SystemUsesDarkTheme());
        bool first = app.ThemeMode == ThemeMode.None;
        if (!first && dark == IsDark) return;
        IsDark = dark;

        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var merged = app.Resources.MergedDictionaries;
        var mine = merged.FirstOrDefault(d => d.Contains("AppBackground")); // ours, not one of Fluent's
        if (mine is not null) merged.Remove(mine);
        merged.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml") });

        string key = dark ? "Dark" : "Light";
        var colors = _overrides?.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        ViewPalette.Current = (dark ? ViewPalette.Dark : ViewPalette.Light).With(colors);
    }

    /// <summary>Windows "Choose your app mode" setting (Settings › Personalization › Colors).</summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
