using System;
using System.Globalization;
using System.IO;

namespace MouseJiggler.Windows.Settings
{
    /// <summary>
    /// Where the settings live. Installed and portable editions deliberately share one location
    /// under %LOCALAPPDATA%, so moving between them keeps your settings and your explicit Stop.
    /// Nothing is ever written beside the executable, and nothing roams.
    /// </summary>
    public sealed class SettingsPaths
    {
        public const string ProductDirectoryName = "MouseJiggler";
        public const string SettingsFileName = "settings.json";
        public const string BackupFileName = "settings.json.bak";
        public const string LockFileName = "settings.lock";
        public const string LogsDirectoryName = "Logs";

        /// <summary>The prefix the store uses for its own temporary files, and the only one it will clean up.</summary>
        public const string TempFilePrefix = "settings.tmp-";

        public const string InvalidBackupPrefix = "settings.invalid-";

        /// <summary>At most this many quarantined copies are kept.</summary>
        public const int MaxInvalidBackups = 3;

        public SettingsPaths(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
            }

            RootDirectory = rootDirectory;
            SettingsFile = Path.Combine(rootDirectory, SettingsFileName);
            BackupFile = Path.Combine(rootDirectory, BackupFileName);
            LockFile = Path.Combine(rootDirectory, LockFileName);
            LogsDirectory = Path.Combine(rootDirectory, LogsDirectoryName);
        }

        public string RootDirectory { get; }

        public string SettingsFile { get; }

        public string BackupFile { get; }

        public string LockFile { get; }

        public string LogsDirectory { get; }

        /// <summary>%LOCALAPPDATA%\MouseJiggler for the current user.</summary>
        public static SettingsPaths ForCurrentUser()
        {
            string localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            return new SettingsPaths(Path.Combine(localAppData, ProductDirectoryName));
        }

        /// <summary>A unique sibling temp name, so two writers never collide on one scratch file.</summary>
        public string CreateTempFilePath()
        {
            string name = TempFilePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            return Path.Combine(RootDirectory, name);
        }

        public string CreateInvalidBackupPath(DateTime utcNow)
        {
            string stamp = utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return Path.Combine(RootDirectory, InvalidBackupPrefix + stamp + ".json");
        }
    }
}
