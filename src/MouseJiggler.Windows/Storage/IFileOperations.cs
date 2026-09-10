using System;
using System.IO;

namespace MouseJiggler.Windows.Storage
{
    /// <summary>
    /// The file operations the settings store needs, behind an interface so that tests can
    /// inject failures at each step. Every method reports failure as a return value or a
    /// thrown <see cref="IOException"/>; none of them retries on its own.
    /// </summary>
    public interface IFileOperations
    {
        bool FileExists(string path);

        void CreateDirectory(string path);

        byte[] ReadAllBytes(string path);

        /// <summary>Writes a new file and flushes it to disk before returning.</summary>
        void WriteAllBytesDurable(string path, byte[] contents);

        /// <summary>
        /// Replaces <paramref name="destination"/> with <paramref name="source"/> atomically,
        /// keeping the previous contents in <paramref name="backup"/>.
        /// </summary>
        void Replace(string source, string destination, string backup);

        /// <summary>Used only to create the file for the first time, where there is nothing to replace.</summary>
        void Move(string source, string destination);

        void Delete(string path);

        void Copy(string source, string destination, bool overwrite);

        string[] GetFiles(string directory, string searchPattern);

        DateTime GetLastWriteTimeUtc(string path);

        long GetFileLength(string path);

        /// <summary>
        /// Opens the per-user lock file with no sharing. Returns null when another writer holds
        /// it, which the caller treats as contention rather than as an error.
        /// </summary>
        IDisposable? TryAcquireExclusive(string path);
    }
}
