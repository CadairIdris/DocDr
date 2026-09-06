using System;
using System.IO;

namespace DocDr.App.ViewModels;

/// <summary>One entry in the home screen's recent-documents list.</summary>
public sealed class RecentFileViewModel(string path)
{
    public string Path { get; } = path;

    public string FileName { get; } = System.IO.Path.GetFileName(path);

    /// <summary>Containing folder, with the user profile shortened to <c>~</c>.</summary>
    public string Folder
    {
        get
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (string.IsNullOrEmpty(dir))
            {
                return string.Empty;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return dir.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                ? "~" + dir[home.Length..]
                : dir;
        }
    }

    public bool Exists => File.Exists(Path);
}
