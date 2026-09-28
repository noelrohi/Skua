using System.Diagnostics;

namespace Skua.Avalonia.Services;

/// <summary>macOS notifications, through <c>osascript</c>: it needs no app bundle, signing or entitlement, as the notification center's API does.</summary>
public static class MacNotifications
{
    /// <summary>Posts a notification without waiting; a failure is only logged.</summary>
    public static void Post(string title, string body)
    {
        try
        {
            // The text goes in as arguments, never as script source.
            ProcessStartInfo start = new("/usr/bin/osascript")
            {
                ArgumentList =
                {
                    "-e", "on run argv",
                    "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                    "-e", "end run",
                    title, body,
                },
                UseShellExecute = false,
            };
            using Process? process = Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.WriteLine($"Couldn't post a notification: {e.Message}");
        }
    }
}
