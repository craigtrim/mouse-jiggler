using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using MouseJiggler.Core.Activity;

namespace MouseJiggler.App
{
    /// <summary>
    /// Draws the tray icons.
    /// </summary>
    /// <remarks>
    /// Each state has its own shape as well as its own colour, so the icon is still readable
    /// with a monochrome or high-contrast theme and by anyone who cannot distinguish the
    /// colours: a square when stopped, a triangle when running, a clock face while waiting for
    /// a schedule, and an exclamation mark on error. The artwork is drawn here rather than
    /// shipped as image files, which keeps it owned outright with no licensing question and
    /// lets it render at whatever pixel size the current DPI asks for.
    /// </remarks>
    public static class TrayIconFactory
    {
        public enum Shape
        {
            Stopped,
            Running,
            Waiting,
            Error,
        }

        public static Shape ShapeFor(StatusCode status)
        {
            switch (status)
            {
                case StatusCode.RunningScheduled:
                case StatusCode.RunningContinuous:
                case StatusCode.RunningManual:
                case StatusCode.LockedKeepingAwake:
                    return Shape.Running;

                case StatusCode.WaitingForSchedule:
                case StatusCode.PausedOnBattery:
                case StatusCode.PowerStatusUnavailable:
                case StatusCode.SessionUnavailable:
                    return Shape.Waiting;

                case StatusCode.Error:
                    return Shape.Error;

                default:
                    return Shape.Stopped;
            }
        }

        /// <summary>
        /// Renders an icon at the requested pixel size. The caller owns the result and must
        /// dispose it; the tray needs a fresh one whenever the DPI changes.
        /// </summary>
        public static Icon Create(Shape shape, int pixelSize)
        {
            if (pixelSize < 8 || pixelSize > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelSize));
            }

            using (var bitmap = new Bitmap(pixelSize, pixelSize))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    Draw(graphics, shape, pixelSize);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        // Clone so the icon survives destroying the temporary handle.
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    NativeIcon.Destroy(handle);
                }
            }
        }

        /// <summary>
        /// Draws one shape at the given size. Internal so the .ico writer renders each entry at
        /// its own resolution rather than resampling a single large bitmap down.
        /// </summary>
        internal static void Draw(Graphics graphics, Shape shape, int size)
        {
            float inset = size * 0.18f;
            float extent = size - (inset * 2);
            Color colour = ColourFor(shape);

            using (var brush = new SolidBrush(colour))
            using (var pen = new Pen(colour, Math.Max(1f, size * 0.12f)))
            {
                switch (shape)
                {
                    case Shape.Stopped:
                        graphics.FillRectangle(brush, inset, inset, extent, extent);
                        break;

                    case Shape.Running:
                        graphics.FillPolygon(brush, new[]
                        {
                            new PointF(inset, inset),
                            new PointF(size - inset, size / 2f),
                            new PointF(inset, size - inset),
                        });
                        break;

                    case Shape.Waiting:
                        graphics.DrawEllipse(pen, inset, inset, extent, extent);
                        graphics.DrawLine(pen, size / 2f, size / 2f, size / 2f, inset + (extent * 0.2f));
                        graphics.DrawLine(pen, size / 2f, size / 2f, size - inset - (extent * 0.25f), size / 2f);
                        break;

                    case Shape.Error:
                        graphics.FillRectangle(brush, (size / 2f) - (size * 0.08f), inset, size * 0.16f, extent * 0.6f);
                        graphics.FillEllipse(brush, (size / 2f) - (size * 0.09f), size - inset - (size * 0.18f), size * 0.18f, size * 0.18f);
                        break;
                }
            }
        }

        private static Color ColourFor(Shape shape)
        {
            switch (shape)
            {
                case Shape.Running:
                    return Color.FromArgb(0x10, 0x7C, 0x10);

                case Shape.Waiting:
                    return Color.FromArgb(0x79, 0x74, 0x6D);

                case Shape.Error:
                    return Color.FromArgb(0xC4, 0x2B, 0x1C);

                default:
                    return Color.FromArgb(0x60, 0x5E, 0x5C);
            }
        }

        /// <summary>The sizes Windows may ask for across the supported DPI range.</summary>
        public static IReadOnlyList<int> StandardSizes { get; } = new[] { 16, 20, 24, 32, 48, 256 };
    }

    internal static class NativeIcon
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static void Destroy(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                DestroyIcon(handle);
            }
        }
    }
}
