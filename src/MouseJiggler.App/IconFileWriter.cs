using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace MouseJiggler.App
{
    /// <summary>
    /// Packs the drawn artwork into a multi-resolution .ico.
    /// </summary>
    /// <remarks>
    /// The shapes are drawn in code rather than shipped as image files, so the icon Explorer,
    /// the taskbar, Alt+Tab and Add or remove programs use has to be generated from them. Each
    /// size is rendered at its own resolution instead of scaling one bitmap down, because a
    /// 256 pixel drawing resampled to 16 turns the shapes into grey mush at exactly the size
    /// the user sees most often.
    ///
    /// Sizes up to 48 are stored as 32-bit DIBs, and 256 as PNG. That split is the convention
    /// every icon toolchain follows: a 256 pixel DIB would add roughly 256KB of mostly
    /// transparent pixels for no benefit.
    /// </remarks>
    public static class IconFileWriter
    {
        private const int PngOnlyAtOrAbove = 256;

        /// <summary>Writes the icon file. The caller owns the stream.</summary>
        public static void Write(TrayIconFactory.Shape shape, IEnumerable<int> sizes, Stream destination)
        {
            if (sizes == null)
            {
                throw new ArgumentNullException(nameof(sizes));
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            int[] ordered = sizes.Distinct().OrderBy(size => size).ToArray();

            if (ordered.Length == 0 || ordered.Length > ushort.MaxValue)
            {
                throw new ArgumentException("At least one size is required.", nameof(sizes));
            }

            byte[][] images = ordered.Select(size => RenderEntry(shape, size)).ToArray();

            using (var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)ordered.Length);

                // Every entry is a fixed sixteen bytes, so the first image starts after all of them.
                int offset = 6 + (16 * ordered.Length);

                for (int i = 0; i < ordered.Length; i++)
                {
                    int size = ordered[i];

                    // 256 is stored as zero: the field is a single byte and cannot hold it.
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);

                    offset += images[i].Length;
                }

                foreach (byte[] image in images)
                {
                    writer.Write(image);
                }
            }
        }

        /// <summary>Convenience for the generator script, which works in file paths.</summary>
        public static void WriteFile(TrayIconFactory.Shape shape, IEnumerable<int> sizes, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("A path is required.", nameof(path));
            }

            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Write(shape, sizes, file);
            }
        }

        private static byte[] RenderEntry(TrayIconFactory.Shape shape, int size)
        {
            using (Bitmap bitmap = Render(shape, size))
            {
                return size >= PngOnlyAtOrAbove ? EncodePng(bitmap) : EncodeDib(bitmap);
            }
        }

        private static Bitmap Render(TrayIconFactory.Shape shape, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    TrayIconFactory.Draw(graphics, shape, size);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static byte[] EncodePng(Bitmap bitmap)
        {
            using (var buffer = new MemoryStream())
            {
                bitmap.Save(buffer, ImageFormat.Png);
                return buffer.ToArray();
            }
        }

        /// <summary>
        /// Writes a BITMAPINFOHEADER, the colour rows bottom up, and the AND mask.
        /// </summary>
        /// <remarks>
        /// The mask is fully opaque rather than derived from the alpha channel. Windows uses the
        /// alpha of a 32-bit entry and ignores the mask, but a zeroed mask is what every real
        /// icon file carries, and some older shell paths fall back to it.
        /// </remarks>
        private static byte[] EncodeDib(Bitmap bitmap)
        {
            int size = bitmap.Width;
            int colourBytes = size * size * 4;

            // Mask rows are one bit per pixel, padded up to a four byte boundary.
            int maskStride = ((size + 31) / 32) * 4;
            int maskBytes = maskStride * size;

            var buffer = new MemoryStream(40 + colourBytes + maskBytes);

            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(40);

                // The height covers the colour rows and the mask rows together.
                writer.Write(size);
                writer.Write(size * 2);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);
                writer.Write(colourBytes + maskBytes);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);

                BitmapData locked = bitmap.LockBits(
                    new Rectangle(0, 0, size, size),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);

                try
                {
                    var row = new byte[size * 4];

                    // A DIB is stored bottom up.
                    for (int y = size - 1; y >= 0; y--)
                    {
                        IntPtr line = IntPtr.Add(locked.Scan0, y * locked.Stride);
                        System.Runtime.InteropServices.Marshal.Copy(line, row, 0, row.Length);
                        writer.Write(row);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(locked);
                }

                writer.Write(new byte[maskBytes]);
            }

            return buffer.ToArray();
        }
    }
}
