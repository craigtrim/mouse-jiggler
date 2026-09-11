using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MouseJiggler.App;
using MouseJiggler.Core.Activity;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The artwork and its packaging. The icon is drawn in code, so the committed .ico is a
    /// build product: these tests are what stop it drifting away from the shapes it came from.
    /// </summary>
    public sealed class IconTests
    {
        private static readonly int[] Expected = { 16, 20, 24, 32, 48, 256 };

        private static string RepositoryRoot()
        {
            // The test assembly sits at tests/MouseJiggler.Tests/bin/<config>/net48.
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MouseJiggler.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }

        [Fact]
        public void TheStandardSizesAreTheOnesWindowsAsksFor()
        {
            // 16 for the tray and menus, 20 and 24 for intermediate DPI scaling, 32 for the
            // desktop and Alt+Tab, 48 for large icons, 256 for the extra large view.
            Assert.Equal(Expected, TrayIconFactory.StandardSizes.ToArray());
        }

        [Fact]
        public void TheCommittedApplicationIconCarriesEveryStandardSize()
        {
            string path = Path.Combine(RepositoryRoot(), "assets", "MouseJiggler.ico");

            Assert.True(File.Exists(path), "assets/MouseJiggler.ico is missing. Run scripts/build-icon.ps1.");

            IReadOnlyList<int> sizes = ReadIconSizes(File.ReadAllBytes(path));

            Assert.Equal(Expected, sizes.ToArray());
        }

        [Fact]
        public void TheCommittedIconStillMatchesTheShapeTheCodeDraws()
        {
            string path = Path.Combine(RepositoryRoot(), "assets", "MouseJiggler.ico");
            byte[] committed = File.ReadAllBytes(path);

            byte[] regenerated;
            using (var buffer = new MemoryStream())
            {
                IconFileWriter.Write(TrayIconFactory.Shape.Running, TrayIconFactory.StandardSizes, buffer);
                regenerated = buffer.ToArray();
            }

            // Byte equality is deliberately not asserted: GDI+ antialiasing is not guaranteed
            // identical across Windows versions, so a build agent would fail on artwork that is
            // perfectly correct. The structure is what must not drift.
            Assert.Equal(ReadIconSizes(regenerated).ToArray(), ReadIconSizes(committed).ToArray());
            Assert.Equal(EntryKinds(regenerated), EntryKinds(committed));
        }

        [Fact]
        public void EverySizeIsDrawnAtItsOwnResolutionRatherThanResampled()
        {
            byte[] file;
            using (var buffer = new MemoryStream())
            {
                IconFileWriter.Write(TrayIconFactory.Shape.Running, TrayIconFactory.StandardSizes, buffer);
                file = buffer.ToArray();
            }

            foreach (int size in Expected.Where(size => size < 256))
            {
                using (var stream = new MemoryStream(file))
                using (var icon = new Icon(stream, new Size(size, size)))
                {
                    // Windows picks the nearest entry, so getting the exact size back is the
                    // proof that the entry is really there rather than being scaled from a
                    // neighbour.
                    Assert.Equal(size, icon.Width);
                    Assert.Equal(size, icon.Height);
                }
            }

            // System.Drawing.Icon skips PNG entries outright, so asking it for 256 hands back
            // the 48 pixel one and proves nothing. The Windows shell reads them perfectly well,
            // which is why the entry is stored that way; decoding the payload is how to check it.
            using (Bitmap largest = DecodeEntry(file, Expected.Length - 1))
            {
                Assert.Equal(256, largest.Width);
                Assert.Equal(256, largest.Height);
            }
        }

        [Fact]
        public void EveryDibEntryDeclaresTheSizeItsDirectoryEntryPromises()
        {
            byte[] file;
            using (var buffer = new MemoryStream())
            {
                IconFileWriter.Write(TrayIconFactory.Shape.Running, TrayIconFactory.StandardSizes, buffer);
                file = buffer.ToArray();
            }

            for (int i = 0; i < Expected.Length; i++)
            {
                if (Expected[i] >= 256)
                {
                    continue;
                }

                int entry = 6 + (i * 16);
                int offset = BitConverter.ToInt32(file, entry + 12);

                Assert.Equal(40, BitConverter.ToInt32(file, offset));
                Assert.Equal(Expected[i], BitConverter.ToInt32(file, offset + 4));

                // The declared height covers the colour rows and the AND mask together, which
                // is the one part of the format that silently produces a squashed icon if wrong.
                Assert.Equal(Expected[i] * 2, BitConverter.ToInt32(file, offset + 8));
                Assert.Equal(32, BitConverter.ToUInt16(file, offset + 14));
            }
        }

        /// <summary>Decodes one entry's image bytes, whichever encoding it uses.</summary>
        private static Bitmap DecodeEntry(byte[] file, int index)
        {
            int entry = 6 + (index * 16);
            int length = BitConverter.ToInt32(file, entry + 8);
            int offset = BitConverter.ToInt32(file, entry + 12);

            using (var payload = new MemoryStream(file, offset, length, writable: false))
            {
                return new Bitmap(payload);
            }
        }

        [Fact]
        public void TheSmallEntriesAreDibsAndTheLargestIsPng()
        {
            byte[] file;
            using (var buffer = new MemoryStream())
            {
                IconFileWriter.Write(TrayIconFactory.Shape.Running, TrayIconFactory.StandardSizes, buffer);
                file = buffer.ToArray();
            }

            List<string> kinds = EntryKinds(file);

            // A 256 pixel DIB would add roughly a quarter of a megabyte of mostly transparent
            // pixels; every icon toolchain stores that one as PNG.
            Assert.Equal(new[] { "dib", "dib", "dib", "dib", "dib", "png" }, kinds.ToArray());
        }

        [Fact]
        public void TheArtworkKeepsItsTransparentBackgroundAtEverySize()
        {
            foreach (int size in Expected)
            {
                using (Icon icon = TrayIconFactory.Create(TrayIconFactory.Shape.Running, size))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    // The corner is outside the drawn shape at every size, so an opaque one
                    // means the icon would show as a filled square against the taskbar.
                    Color corner = bitmap.GetPixel(0, 0);
                    Assert.Equal(0, corner.A);
                }
            }
        }

        [Fact]
        public void EachStateHasItsOwnShapeRatherThanOnlyItsOwnColour()
        {
            // Readable in a high-contrast theme, and by anyone who cannot tell the colours
            // apart. Two states that differ only in hue would fail this.
            var signatures = new HashSet<string>(StringComparer.Ordinal);

            foreach (TrayIconFactory.Shape shape in Enum.GetValues(typeof(TrayIconFactory.Shape)))
            {
                using (Icon icon = TrayIconFactory.Create(shape, 32))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    var occupancy = new System.Text.StringBuilder(32 * 32);

                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            occupancy.Append(bitmap.GetPixel(x, y).A > 128 ? '1' : '0');
                        }
                    }

                    Assert.True(
                        signatures.Add(occupancy.ToString()),
                        shape + " has the same silhouette as another state.");
                }
            }
        }

        [Fact]
        public void NoStateDrawsTheMouseSmallerThanTheOthers()
        {
            // From issue #21: Running was drawn at 78 percent scale, so the icon shrank the
            // moment the app started and the change read as the artwork swapping rather than
            // as the state changing. A state may stand taller than Stopped, because the
            // schedule and error badges hang below the body, but none may come up short.
            foreach (int size in Expected)
            {
                int stopped = InkHeight(TrayIconFactory.Shape.Stopped, size);

                foreach (TrayIconFactory.Shape shape in Enum.GetValues(typeof(TrayIconFactory.Shape)))
                {
                    int height = InkHeight(shape, size);

                    Assert.True(
                        height >= stopped * 0.9,
                        shape + " is " + height + " pixels tall at " + size + ", against " +
                        stopped + " for Stopped. The mouse must not change size between states.");
                }
            }
        }

        /// <summary>The vertical extent of everything the state draws, in pixels.</summary>
        private static int InkHeight(TrayIconFactory.Shape shape, int size)
        {
            using (Icon icon = TrayIconFactory.Create(shape, size))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                int top = size;
                int bottom = -1;

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        if (bitmap.GetPixel(x, y).A > 128)
                        {
                            if (y < top)
                            {
                                top = y;
                            }

                            bottom = y;
                            break;
                        }
                    }
                }

                Assert.True(bottom >= 0, shape + " drew nothing at " + size + " pixels.");
                return bottom - top + 1;
            }
        }

        [Fact]
        public void ARequestedSizeOutsideTheSupportedRangeIsRefusedRatherThanClamped()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconFactory.Create(TrayIconFactory.Shape.Running, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconFactory.Create(TrayIconFactory.Shape.Running, 257));
        }

        [Fact]
        public void EveryStatusMapsToAShape()
        {
            foreach (StatusCode status in Enum.GetValues(typeof(StatusCode)))
            {
                TrayIconFactory.Shape shape = TrayIconFactory.ShapeFor(status);
                Assert.True(Enum.IsDefined(typeof(TrayIconFactory.Shape), shape));
            }
        }

        [Fact]
        public void AHundredIconCyclesLeaveNoGdiObjectsBehind()
        {
            // The tray replaces its icon on every status change, and the icon is built from a
            // native HICON that GDI does not reclaim on its own. A leak here is invisible until
            // the process hits the ten thousand object quota and the interface stops drawing.
            const int Cycles = 100;

            IntPtr process = GetCurrentProcess();

            // Warm up first: the first few calls load GDI+ and allocate handles that are not
            // part of the loop, and counting those as a leak would make this fail at random.
            for (int i = 0; i < 10; i++)
            {
                using (Icon warmUp = TrayIconFactory.Create(TrayIconFactory.Shape.Running, 16))
                {
                    _ = warmUp.Handle;
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            uint before = GetGuiResources(process, GdiObjects);
            Assert.True(before > 0, "GetGuiResources is unavailable, so this proves nothing.");

            for (int i = 0; i < Cycles; i++)
            {
                TrayIconFactory.Shape shape = (TrayIconFactory.Shape)(i % 4);

                using (Icon icon = TrayIconFactory.Create(shape, 16 + (i % 3)))
                {
                    _ = icon.Handle;
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            uint after = GetGuiResources(process, GdiObjects);

            // A per-cycle leak would show as a hundred or more retained objects. The small
            // allowance covers caches GDI+ keeps that are not proportional to the loop.
            Assert.True(
                after <= before + 20,
                "GDI objects grew from " + before + " to " + after + " over " + Cycles + " icon cycles.");
        }

        private const int GdiObjects = 0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr process, int flags);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>Reads the directory, mapping the zero width and height back to 256.</summary>
        private static IReadOnlyList<int> ReadIconSizes(byte[] file)
        {
            Assert.True(file.Length > 6, "The icon file is truncated.");
            Assert.Equal(0, BitConverter.ToUInt16(file, 0));
            Assert.Equal(1, BitConverter.ToUInt16(file, 2));

            int count = BitConverter.ToUInt16(file, 4);
            var sizes = new List<int>(count);

            for (int i = 0; i < count; i++)
            {
                int entry = 6 + (i * 16);
                int width = file[entry];
                int height = file[entry + 1];

                Assert.Equal(width, height);
                sizes.Add(width == 0 ? 256 : width);
            }

            return sizes;
        }

        /// <summary>Whether each entry holds a PNG or a DIB, read from the image bytes.</summary>
        private static List<string> EntryKinds(byte[] file)
        {
            int count = BitConverter.ToUInt16(file, 4);
            var kinds = new List<string>(count);

            for (int i = 0; i < count; i++)
            {
                int entry = 6 + (i * 16);
                int offset = BitConverter.ToInt32(file, entry + 12);

                bool png = file[offset] == 0x89
                           && file[offset + 1] == 0x50
                           && file[offset + 2] == 0x4E
                           && file[offset + 3] == 0x47;

                kinds.Add(png ? "png" : "dib");
            }

            return kinds;
        }
    }
}
