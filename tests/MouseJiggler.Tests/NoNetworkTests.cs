using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The claim that the app makes no network requests, checked rather than asserted.
    /// </summary>
    /// <remarks>
    /// The README and the privacy section both promise no telemetry, no upload and no network
    /// traffic. That promise is one careless line away from being false, and the line would not
    /// look alarming: an update check, a crash report, a font fetched from somewhere. Nothing in
    /// the test suite would fail.
    ///
    /// A compiled assembly names every type it touches in its metadata, so the names are what is
    /// searched for. This cannot see a reflective call built from concatenated strings, and it
    /// does not try to: it catches the ordinary way networking gets added, which is somebody
    /// writing HttpClient.
    /// </remarks>
    public sealed class NoNetworkTests
    {
        /// <summary>Namespaces and types that would mean the app had learned to talk to a network.</summary>
        private static readonly string[] Forbidden =
        {
            "System.Net",
            "HttpClient",
            "HttpWebRequest",
            "WebRequest",
            "WebClient",
            "TcpClient",
            "UdpClient",
            "Socket",
            "Dns",
            "SmtpClient",
            "WebSocket",
        };

        /// <summary>
        /// Names that are allowed to contain a forbidden substring for unrelated reasons.
        /// </summary>
        /// <remarks>
        /// NamedPipeServerStream and its friends live in System.IO.Pipes, not System.Net, so
        /// they do not collide. This list exists so that a future collision is recorded here
        /// deliberately rather than by loosening the search.
        /// </remarks>
        private static readonly string[] Allowed = Array.Empty<string>();

        private static IEnumerable<string> ShippedAssemblies()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;

            // Exactly what a release contains. The test assembly and its packages are not part
            // of the product and are deliberately not examined.
            foreach (string name in new[] { "MouseJiggler.exe", "MouseJiggler.Core.dll", "MouseJiggler.Windows.dll" })
            {
                string path = Path.Combine(directory, name);

                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }

        [Fact]
        public void TheShippedAssembliesAreAllPresentToBeChecked()
        {
            // A search that finds no files would pass every assertion below without checking
            // anything, which is the failure mode this test class exists to avoid.
            Assert.Equal(3, ShippedAssemblies().Count());
        }

        [Fact]
        public void NothingShippedNamesANetworkingType()
        {
            var offences = new List<string>();

            foreach (string path in ShippedAssemblies())
            {
                string metadata = ReadAsciiStrings(File.ReadAllBytes(path));

                foreach (string forbidden in Forbidden)
                {
                    if (metadata.IndexOf(forbidden, StringComparison.Ordinal) >= 0 &&
                        !Allowed.Contains(forbidden, StringComparer.Ordinal))
                    {
                        offences.Add(Path.GetFileName(path) + " names " + forbidden);
                    }
                }
            }

            Assert.True(offences.Count == 0, string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void TheSearchWouldActuallyFindSomethingIfItWereThere()
        {
            // The check above passes trivially if the search is broken, so this proves the
            // search finds a name that is genuinely present in the same assemblies.
            string metadata = string.Join(
                Environment.NewLine,
                ShippedAssemblies().Select(path => ReadAsciiStrings(File.ReadAllBytes(path))));

            Assert.Contains("MouseJiggler", metadata, StringComparison.Ordinal);

            // A P/Invoke the app certainly makes, so finding it proves the search reaches the
            // part of the file where imported names live.
            Assert.Contains("SetThreadExecutionState", metadata, StringComparison.Ordinal);
        }

        [Fact]
        public void NoShippedAssemblyReferencesANetworkingAssembly()
        {
            foreach (string path in ShippedAssemblies())
            {
                AssemblyName[] references = Assembly.ReflectionOnlyLoadFrom(path).GetReferencedAssemblies();

                foreach (AssemblyName reference in references)
                {
                    // System itself carries System.Net, so a reference to it proves nothing and
                    // is not treated as an offence. These are the assemblies that exist only to
                    // do networking.
                    Assert.DoesNotContain("System.Net", reference.Name, StringComparison.Ordinal);
                    Assert.NotEqual("System.ServiceModel", reference.Name);
                    Assert.NotEqual("System.Web", reference.Name);
                }
            }
        }

        /// <summary>
        /// Pulls printable ASCII runs out of the file, which is where a .NET assembly keeps the
        /// namespace and type names it refers to.
        /// </summary>
        private static string ReadAsciiStrings(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length / 4);

            foreach (byte value in bytes)
            {
                if (value >= 0x20 && value < 0x7F)
                {
                    builder.Append((char)value);
                }
                else
                {
                    // A separator, so a name cannot be formed by accident across a boundary.
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }
    }
}
