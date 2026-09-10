using System;
using System.IO;

namespace MouseJiggler.Windows.Storage
{
    /// <summary>The real file system.</summary>
    public sealed class PhysicalFileOperations : IFileOperations
    {
        public bool FileExists(string path) => File.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteAllBytesDurable(string path, byte[] contents)
        {
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);

                // Flush(true) pushes the bytes past the operating system cache to the device,
                // so a power loss after this call cannot leave a half-written document.
                stream.Flush(true);
            }
        }

        public void Replace(string source, string destination, string backup)
        {
            // File.Replace is the atomic swap. Copy-then-delete is not equivalent: it has a
            // window in which neither file is complete.
            File.Replace(source, destination, backup, ignoreMetadataErrors: true);
        }

        public void Move(string source, string destination) => File.Move(source, destination);

        public void Delete(string path) => File.Delete(path);

        public void Copy(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);

        public string[] GetFiles(string directory, string searchPattern) => Directory.GetFiles(directory, searchPattern);

        public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

        public long GetFileLength(string path) => new FileInfo(path).Length;

        public IDisposable? TryAcquireExclusive(string path)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another writer holds it. The caller decides whether to wait or give up.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
