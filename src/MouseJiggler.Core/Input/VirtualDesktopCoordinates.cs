using System;

namespace MouseJiggler.Core.Input
{
    /// <summary>A virtual desktop rectangle in physical pixels. The origin can be negative.</summary>
    public sealed class VirtualBounds
    {
        public VirtualBounds(int originX, int originY, int width, int height)
        {
            OriginX = originX;
            OriginY = originY;
            Width = width;
            Height = height;
        }

        public int OriginX { get; }

        public int OriginY { get; }

        public int Width { get; }

        public int Height { get; }

        public bool IsUsable => Width > 0 && Height > 0;
    }

    /// <summary>
    /// Converts physical pixels to the absolute range SendInput expects.
    /// </summary>
    /// <remarks>
    /// Absolute mouse input is expressed in a normalized 0 to 65535 space across the whole
    /// virtual desktop, and Windows maps a normalized value back with a floor. Scaling by the
    /// endpoints alone lands on the boundary between two quantization cells, where rounding can
    /// send the pointer back one pixel away from where it started. Targeting the centre of the
    /// destination pixel's cell instead survives the round trip exactly, which is what lets the
    /// app promise no cumulative drift.
    /// </remarks>
    public static class VirtualDesktopCoordinates
    {
        public const int NormalizedMax = 65535;

        private const long NormalizedRange = 65536;

        /// <summary>
        /// Maps a physical coordinate to its normalized cell centre. Returns false when the
        /// desktop extent is unusable or the value cannot be represented, in which case the
        /// caller skips rather than moving the pointer somewhere approximate.
        /// </summary>
        public static bool TryNormalize(int coordinate, int origin, int extent, out int normalized)
        {
            normalized = 0;

            if (extent <= 0)
            {
                return false;
            }

            long offset = (long)coordinate - origin;
            if (offset < 0 || offset >= extent)
            {
                return false;
            }

            // The centre of this pixel's cell, in 64-bit arithmetic so a large virtual desktop
            // cannot overflow the intermediate product.
            long value = (((2 * offset) + 1) * NormalizedRange) / (2L * extent);

            if (value < 0)
            {
                value = 0;
            }
            else if (value > NormalizedMax)
            {
                value = NormalizedMax;
            }

            // Confirm Windows will map it back to the pixel we meant. An extent beyond the
            // normalized range cannot always represent every pixel, and guessing is worse than
            // reporting unsupported geometry.
            if (Denormalize((int)value, extent) != offset)
            {
                return false;
            }

            normalized = (int)value;
            return true;
        }

        /// <summary>The inverse mapping Windows applies, used to verify the round trip.</summary>
        public static long Denormalize(int normalized, int extent)
        {
            if (extent <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(extent));
            }

            return ((long)normalized * extent) / NormalizedRange;
        }

        /// <summary>Maps a point, or reports that this geometry cannot be expressed.</summary>
        public static bool TryNormalizePoint(int x, int y, VirtualBounds bounds, out int normalizedX, out int normalizedY)
        {
            if (bounds == null)
            {
                throw new ArgumentNullException(nameof(bounds));
            }

            normalizedX = 0;
            normalizedY = 0;

            return bounds.IsUsable
                   && TryNormalize(x, bounds.OriginX, bounds.Width, out normalizedX)
                   && TryNormalize(y, bounds.OriginY, bounds.Height, out normalizedY);
        }
    }
}
