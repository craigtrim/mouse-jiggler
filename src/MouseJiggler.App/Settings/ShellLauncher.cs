using System;
using System.ComponentModel;
using System.IO;

namespace MouseJiggler.App
{
    /// <summary>
    /// Hands a folder, a URL or a settings page to the shell.
    /// </summary>
    /// <remarks>
    /// Failure is swallowed on purpose. There is no browser association, or no Explorer, or the
    /// target has gone: none of that is worth a dialog, because the user asked for a
    /// convenience and the app carries on working exactly as it did. Crashing over it would be
    /// absurd, and reporting it would be noise.
    /// </remarks>
    internal static class ShellLauncher
    {
        public static void Open(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(target);
            }
            catch (Win32Exception)
            {
            }
            catch (FileNotFoundException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
