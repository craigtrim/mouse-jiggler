using System;
using System.IO;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Where the artifact manifest is written, asserted from the far side. See issue #15.
    /// </summary>
    /// <remarks>
    /// The release workflow used to write artifact-manifest.json after package.ps1 had already
    /// produced SHA256SUMS.txt. The manifest was uploaded to the release page with no published
    /// checksum, the README claimed checksums were published for every artifact, and
    /// verify-release.ps1 reported a failure on a release that had nothing wrong with it.
    ///
    /// CI already runs package.ps1 and verify-package.ps1 over a real artifacts directory on
    /// every pull request, so whether the checksums actually cover the manifest is proven there,
    /// against real files, rather than here. What CI cannot observe is the ordering rule that
    /// keeps it true: that packaging writes the manifest before the checksums, and that nothing
    /// downstream adds another file to artifacts/ afterwards. A workflow step reintroducing the
    /// old behaviour would upload a fourth file whose checksum was never computed, and CI would
    /// go on passing because CI never runs the release workflow.
    ///
    /// These are text searches over a script and a workflow. They cannot say the checksums are
    /// correct. They can say that moving the manifest back out of packaging breaks a test rather
    /// than a promise.
    /// </remarks>
    public sealed class ReleaseManifestContractTests
    {
        private const string ManifestFileName = "artifact-manifest.json";
        private const string ChecksumsFileName = "SHA256SUMS.txt";

        private static string RepositoryFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MouseJiggler.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            string path = directory!.FullName;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }

            return path;
        }

        private static string ReadRepositoryText(params string[] parts)
        {
            string path = RepositoryFile(parts);
            Assert.True(File.Exists(path), "Expected to find " + path + ".");
            return File.ReadAllText(path);
        }

        [Fact]
        public void PackagingWritesTheManifestBeforeTheChecksums()
        {
            string script = ReadRepositoryText("scripts", "package.ps1");

            int manifest = script.IndexOf(ManifestFileName, StringComparison.Ordinal);
            int checksums = script.LastIndexOf(ChecksumsFileName, StringComparison.Ordinal);

            Assert.True(manifest >= 0, "package.ps1 no longer writes " + ManifestFileName + ".");
            Assert.True(checksums >= 0, "package.ps1 no longer writes " + ChecksumsFileName + ".");

            // The checksums cover the manifest only because the manifest already exists when they
            // are written. Reverse these two and the manifest ships unhashed again.
            Assert.True(
                manifest < checksums,
                "package.ps1 must write " + ManifestFileName + " before " + ChecksumsFileName +
                ", otherwise the checksums cannot cover it.");
        }

        [Fact]
        public void TheReleaseWorkflowDoesNotWriteTheManifestItself()
        {
            string workflow = ReadRepositoryText(".github", "workflows", "release.yml");

            // The workflow may name the manifest in a comment or upload it. What it must not do is
            // create it, because anything it creates lands in artifacts/ after packaging has
            // already written the checksums.
            foreach (string write in new[] { "Set-Content artifacts/" + ManifestFileName, "Write the artifact manifest" })
            {
                Assert.False(
                    workflow.Contains(write),
                    "release.yml writes the artifact manifest after packaging (found \"" + write +
                    "\"). Packaging owns the manifest so that the checksums can cover it.");
            }
        }

        [Fact]
        public void BothPackagingScriptsShareOneDefinitionOfAReleaseFile()
        {
            // The bug this guards against was two filters describing the same set differently.
            foreach (string script in new[] { "package.ps1", "verify-release.ps1" })
            {
                string text = ReadRepositoryText("scripts", script);

                Assert.True(
                    text.Contains("release-files.ps1") && text.Contains("Get-ReleaseArtifact"),
                    script + " no longer uses the shared release-file rule, so it can disagree " +
                    "with the other script about which files are released.");
            }
        }
    }
}
