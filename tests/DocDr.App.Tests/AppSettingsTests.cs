using System.Text.Json;
using DocDr.App.Services;

namespace DocDr.App.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void PushRecentFile_puts_the_newest_path_first()
    {
        var s = new AppSettings();
        s.PushRecentFile(@"C:\a.pdf");
        s.PushRecentFile(@"C:\b.pdf");

        Assert.Equal([@"C:\b.pdf", @"C:\a.pdf"], s.RecentFiles);
    }

    [Fact]
    public void PushRecentFile_dedupes_case_insensitively_and_moves_to_front()
    {
        var s = new AppSettings();
        s.PushRecentFile(@"C:\a.pdf");
        s.PushRecentFile(@"C:\b.pdf");
        s.PushRecentFile(@"c:\A.PDF");

        Assert.Equal([@"c:\A.PDF", @"C:\b.pdf"], s.RecentFiles);
    }

    [Fact]
    public void PushRecentFile_caps_the_list()
    {
        var s = new AppSettings();
        for (int i = 0; i < AppSettings.MaxRecentFiles + 5; i++)
        {
            s.PushRecentFile($@"C:\f{i}.pdf");
        }

        Assert.Equal(AppSettings.MaxRecentFiles, s.RecentFiles.Count);
        Assert.Equal($@"C:\f{AppSettings.MaxRecentFiles + 4}.pdf", s.RecentFiles[0]);
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var s = new AppSettings
        {
            Theme = AppTheme.Dark,
            LastPageSize = "A3",
            LastPageLandscape = true,
            LastCustomColor = 0xFF102030,
        };
        s.PushRecentFile(@"C:\x.pdf");

        var opts = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, opts), opts)!;

        Assert.Equal(AppTheme.Dark, back.Theme);
        Assert.Equal("A3", back.LastPageSize);
        Assert.True(back.LastPageLandscape);
        Assert.Equal(0xFF102030u, back.LastCustomColor);
        Assert.Equal([@"C:\x.pdf"], back.RecentFiles);
    }
}
