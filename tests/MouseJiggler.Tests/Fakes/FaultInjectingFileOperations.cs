using System;
using System.IO;
using MouseJiggler.Windows.Storage;

namespace MouseJiggler.Tests.Fakes
{
    /// <summary>
    /// Wraps the real file system so a test can fail one specific step. The point is to prove
    /// that an interrupted save leaves either the old document or the new one, never a partial
    /// one, and never an app that starts when the user had stopped it.
    /// </summary>
    public sealed class FaultInjectingFileOperations : IFileOperations
    {
        private readonly IFileOperations _inner;

        public FaultInjectingFileOperations(IFileOperations inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public bool FailWrite { get; set; }

        public bool FailReplace { get; set; }

        public bool FailMove { get; set; }

        public bool DenyLock { get; set; }

        /// <summary>Writes the bytes and then fails, standing in for a crash after the flush.</summary>
        public bool FailAfterWrite { get; set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAllBytesDurable(string path, byte[] contents)
        {
            if (FailWrite)
            {
                throw new IOException("Injected write failure.");
            }

            _inner.WriteAllBytesDurable(path, contents);

            if (FailAfterWrite)
            {
                throw new IOException("Injected failure after flush.");
            }
        }

        public void Replace(string source, string destination, string backup)
        {
            if (FailReplace)
            {
                throw new IOException("Injected replace failure.");
            }

            _inner.Replace(source, destination, backup);
        }

        public void Move(string source, string destination)
        {
            if (FailMove)
            {
                throw new IOException("Injected move failure.");
            }

            _inner.Move(source, destination);
        }

        public void Delete(string path) => _inner.Delete(path);

        public void Copy(string source, string destination, bool overwrite) => _inner.Copy(source, destination, overwrite);

        public string[] GetFiles(string directory, string searchPattern) => _inner.GetFiles(directory, searchPattern);

        public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

        public long GetFileLength(string path) => _inner.GetFileLength(path);

        public IDisposable? TryAcquireExclusive(string path) => DenyLock ? null : _inner.TryAcquireExclusive(path);
    }

    /// <summary>A temporary directory that removes itself, so tests never touch real user settings.</summary>
    public sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mousejiggler-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
