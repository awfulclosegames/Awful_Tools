using System.Runtime.InteropServices;
using System.Windows;

namespace AwfulDiff.Services;

/// <summary>
/// Clipboard write that never freezes the UI thread. If another process holds the clipboard
/// open (clipboard managers, screen-capture tools — GetOpenClipboardWindow reports this in
/// O(1) without touching the clipboard), Clipboard.SetText would block for seconds on its
/// internal OpenClipboard retries; in that case the write is retried off-thread and lands as
/// soon as the holder releases. Returns true if the copy was made or scheduled.
/// </summary>
public static class ClipboardHelper
{
    // Bumped on every copy so only the newest copy can land while background retries are pending.
    private static int _clipboardGen;

    public static bool TrySetText(string text)
    {
        int gen = Interlocked.Increment(ref _clipboardGen);

        if (GetOpenClipboardWindow() == IntPtr.Zero && TrySetNow(text))
            return true;   // fast path: nobody holds the clipboard, write succeeded

        // Clipboard is held (or the write failed anyway) — retry off the UI thread. A newer
        // copy bumps the generation and voids this scheduled write, so an old copy can never
        // clobber a newer one.
        _ = Task.Run(() =>
        {
            for (int attempt = 0; attempt < 30; attempt++)   // ~3 s total
            {
                Thread.Sleep(100);
                if (Volatile.Read(ref _clipboardGen) != gen) return;
                try
                {
                    if (GetOpenClipboardWindow() == IntPtr.Zero && TrySetNow(text))
                        return;
                }
                catch { /* any OLE hiccup — keep retrying until the cap */ }
            }
        });
        return true;   // will land within a few seconds
    }

    private static bool TrySetNow(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)   // COMException (CLIPBRD_E_CANT_OPEN) or any other OLE failure
        {
            return false;
        }
    }

    /// <summary>
    /// HWND of the window that currently has the clipboard open, or IntPtr.Zero. Undocumented
    /// but stable since Windows 95 (used by AutoHotkey and the like); the call never blocks.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();
}
