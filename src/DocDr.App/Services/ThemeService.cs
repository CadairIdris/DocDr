using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace DocDr.App.Services;

/// <summary>
/// Applies the chosen <see cref="AppTheme"/>: sets WPF's Fluent <c>ThemeMode</c> for the built-in
/// controls and swaps DocDr's own <c>Palette.Light</c> / <c>Palette.Dark</c> resource dictionary.
/// For <see cref="AppTheme.System"/> it tracks the OS light/dark setting live.
/// </summary>
public sealed class ThemeService
{
    private const string PaletteMarker = "/Themes/Palette.";

    private readonly AppSettings _settings;

    public ThemeService(AppSettings settings)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Current == AppTheme.System)
            {
                Application.Current?.Dispatcher.Invoke(() => Apply(AppTheme.System));
            }
        };
    }

    public AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>True while the effective theme (after resolving <see cref="AppTheme.System"/>) is dark.</summary>
    public bool IsDark { get; private set; }

    public void Apply(AppTheme theme)
    {
        Current = theme;
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        if (Application.Current is { } app)
        {
#pragma warning disable WPF0001 // ThemeMode is evaluation-only in .NET 9
            app.ThemeMode = theme switch
            {
                AppTheme.Dark => ThemeMode.Dark,
                AppTheme.Light => ThemeMode.Light,
                _ => ThemeMode.System,
            };
#pragma warning restore WPF0001

            SwapPalette(app, IsDark ? "Dark" : "Light");
        }

        if (_settings.Theme != theme)
        {
            _settings.Theme = theme;
            _settings.Save();
        }
    }

    private static void SwapPalette(Application app, string variant)
    {
        var merged = app.Resources.MergedDictionaries;
        var target = new Uri($"/DocDr.App;component/Themes/Palette.{variant}.xaml", UriKind.Relative);

        for (int i = merged.Count - 1; i >= 0; i--)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source is not null && source.Contains(PaletteMarker) && !source.Contains("Shared"))
            {
                merged.RemoveAt(i);
            }
        }

        merged.Add(new ResourceDictionary { Source = target });
    }

    private static bool IsSystemDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
