using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DocDr.App.Services;

/// <summary>
/// App version/build metadata and the crash-log sink. The <em>only</em> always-on logging: one
/// line per launch, plus any unhandled exception, appended to a dated file under
/// <c>%APPDATA%\DocDr\logs</c>. Everything here swallows its own IO errors — logging must never
/// be the thing that brings the app down.
/// </summary>
public static class DiagnosticsLog
{
    private static readonly object Gate = new();

    static DiagnosticsLog()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        Version = asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
        InformationalVersion =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version;

        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocDr", "logs");
        LogFile = Path.Combine(LogDirectory, $"docdr-{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>Marketing-style version, e.g. <c>0.9.0</c>.</summary>
    public static string Version { get; }

    /// <summary>Version with the build's git SHA, e.g. <c>0.9.0+abc1234</c>. Use this in bug reports.</summary>
    public static string InformationalVersion { get; }

    public static string LogDirectory { get; }

    public static string LogFile { get; }

    /// <summary>Create the log folder and write the session-start banner. Call once at startup.</summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Write($"---- session start | DocDr {InformationalVersion} | {RuntimeInformation.OSDescription} " +
                  $"| .NET {Environment.Version} ----");
        }
        catch
        {
            // no log this session — not fatal
        }
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
            catch
            {
                // best effort
            }
        }
    }

    public static void Exception(string context, Exception ex) => Write($"{context}{Environment.NewLine}{ex}");
}
