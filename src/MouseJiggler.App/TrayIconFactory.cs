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
        /// <remarks>
        /// Every state draws the same mouse. What changes is what the mouse is doing, so the
        /// icon says which application it belongs to before it says what that application is
        /// currently up to. A plain grey square said neither.
        ///
        /// The state is carried by silhouette rather than by colour alone: the mouse leans and
        /// throws off motion arcs while running, and wears a distinct badge while waiting or
        /// after a failure. That is what keeps it readable in a high-contrast theme and for
        /// anyone who cannot separate the hues.
        ///
        /// Everything is expressed as a fraction of the icon size and drawn at the requested
        /// resolution, so 16 pixels is drawn as 16 pixels rather than resampled down from
        /// something larger, which is where small icons usually turn to mush.
        /// </remarks>
        internal static void Draw(Graphics graphics, Shape shape, int size)
        {
            Color colour = ColourFor(shape);

            using (var brush = new SolidBrush(colour))
            {
                if (shape == Shape.Running)
                {
                    // Dancing: leaning into the step, with the motion coming off both sides.
                    // Drawn at the same scale as every other state, from issue #21, so the
                    // mouse does not appear to shrink the moment the app starts running. The
                    // lean costs nothing in height, because rotating about the base swings the
                    // dome inward by as much as it swings the base out, and the strokes still
                    // clear the body by about two pixels at sixteen.
                    DrawMouse(graphics, size, brush, tiltDegrees: -13f, scale: 1f);
                    DrawMotionArcs(graphics, size, colour);
                    return;
                }

                DrawMouse(graphics, size, brush, tiltDegrees: 0f, scale: 1f);

                if (shape == Shape.Waiting)
                {
                    DrawClockBadge(graphics, size, colour);
                }
                else if (shape == Shape.Error)
                {
                    DrawAlertBadge(graphics, size, colour);
                }
            }
        }

        /// <summary>The mouse itself: a filled body with its buttons cut back out of it.</summary>
        /// <remarks>
        /// Filled rather than outlined. At sixteen pixels an outline is a one pixel ring that
        /// disappears against a busy taskbar, while a solid shape keeps its edge. The button
        /// split is erased from the body instead of being drawn over it, so it stays visible
        /// whatever the icon sits on.
        /// </remarks>
        private static void DrawMouse(Graphics graphics, int size, Brush brush, float tiltDegrees, float scale)
        {
            GraphicsState saved = graphics.Save();

            try
            {
                if (Math.Abs(scale - 1f) > 0.001f || Math.Abs(tiltDegrees) > 0.01f)
                {
                    // Pivot about the base, the way something standing on it would lean.
                    graphics.TranslateTransform(size * 0.5f, size * 0.72f);
                    graphics.RotateTransform(tiltDegrees);
                    graphics.ScaleTransform(scale, scale);
                    graphics.TranslateTransform(size * -0.5f, size * -0.72f);
                }

                using (GraphicsPath body = BodyPath(size))
                {
                    graphics.FillPath(brush, body);
                }

                // SourceCopy writes the transparent pixels straight through the body rather than
                // blending with it, which is the only way to take a bite out of what was just
                // filled. It costs the antialiasing on these two lines, and at these sizes a
                // crisp split reads better than a soft one anyway.
                CompositingMode previous = graphics.CompositingMode;
                graphics.CompositingMode = CompositingMode.SourceCopy;

                try
                {
                    using (var cut = new SolidBrush(Color.Transparent))
                    {
                        float gap = Math.Max(1f, size * 0.055f);
                        float split = size * 0.44f;

                        // Across, separating the buttons from the body.
                        graphics.FillRectangle(cut, size * 0.24f, split - (gap / 2f), size * 0.52f, gap);

                        // Down, separating left button from right.
                        graphics.FillRectangle(cut, (size * 0.5f) - (gap / 2f), size * 0.10f, gap, split - (size * 0.10f));
                    }
                }
                finally
                {
                    graphics.CompositingMode = previous;
                }
            }
            finally
            {
                graphics.Restore(saved);
            }
        }

        /// <summary>The mouse outline: a tall dome over a rounded base.</summary>
        private static GraphicsPath BodyPath(int size)
        {
            float left = size * 0.27f;
            float right = size * 0.73f;
            float top = size * 0.11f;
            float bottom = size * 0.91f;
            float width = right - left;

            float topRadius = width;
            float bottomRadius = width * 0.7f;

            var path = new GraphicsPath();

            // The dome is a full half circle, so the top is as round as a mouse actually is.
            path.AddArc(left, top, width, topRadius, 180f, 180f);
            path.AddArc(left, bottom - bottomRadius, width, bottomRadius, 0f, 180f);
            path.CloseFigure();

            return path;
        }

        /// <summary>Motion coming off both sides, which is what makes it read as moving.</summary>
        /// <remarks>
        /// Straight strokes, not arcs. An arc short enough to fit beside a sixteen pixel mouse
        /// is only three or four pixels of curve, which renders as a vertical dash and reads as
        /// a tally mark rather than as movement. A straight stroke at an angle survives the same
        /// space because its direction is carried by two endpoints instead of by curvature.
        ///
        /// One a side, and symmetric, because the mouse is shaking in place rather than
        /// travelling. Trailing marks on one side only would say it had gone somewhere.
        /// </remarks>
        private static void DrawMotionArcs(Graphics graphics, int size, Color colour)
        {
            using (var pen = new Pen(colour, Math.Max(1.5f, size * 0.085f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                // Beside the body rather than beside the dome, and far enough out that the
                // round caps still leave daylight at sixteen pixels. Touching the body at all
                // turns them into limbs and the mouse into an insect.
                graphics.DrawLine(pen, size * 0.04f, size * 0.58f, size * 0.15f, size * 0.49f);
                graphics.DrawLine(pen, size * 0.96f, size * 0.58f, size * 0.85f, size * 0.49f);
            }
        }

        /// <summary>A clock in the corner, for a run that is waiting on its schedule.</summary>
        private static void DrawClockBadge(Graphics graphics, int size, Color colour)
        {
            RectangleF badge = BadgeBounds(size);
            PunchBadgeMoat(graphics, size, badge);

            using (var brush = new SolidBrush(colour))
            {
                graphics.FillEllipse(brush, badge);
            }

            CompositingMode previous = graphics.CompositingMode;
            graphics.CompositingMode = CompositingMode.SourceCopy;

            try
            {
                using (var cut = new Pen(Color.Transparent, Math.Max(1f, size * 0.055f)))
                {
                    float cx = badge.X + (badge.Width / 2f);
                    float cy = badge.Y + (badge.Height / 2f);

                    cut.StartCap = LineCap.Flat;
                    cut.EndCap = LineCap.Flat;

                    graphics.DrawLine(cut, cx, cy, cx, cy - (badge.Height * 0.30f));
                    graphics.DrawLine(cut, cx, cy, cx + (badge.Width * 0.26f), cy);
                }
            }
            finally
            {
                graphics.CompositingMode = previous;
            }
        }

        /// <summary>An exclamation in the corner, for a capability that failed.</summary>
        private static void DrawAlertBadge(Graphics graphics, int size, Color colour)
        {
            RectangleF badge = BadgeBounds(size);
            PunchBadgeMoat(graphics, size, badge);

            using (var brush = new SolidBrush(colour))
            {
                graphics.FillEllipse(brush, badge);
            }

            CompositingMode previous = graphics.CompositingMode;
            graphics.CompositingMode = CompositingMode.SourceCopy;

            try
            {
                using (var cut = new SolidBrush(Color.Transparent))
                {
                    float stem = Math.Max(1f, badge.Width * 0.18f);
                    float x = badge.X + (badge.Width / 2f) - (stem / 2f);

                    graphics.FillRectangle(cut, x, badge.Y + (badge.Height * 0.20f), stem, badge.Height * 0.32f);
                    graphics.FillRectangle(cut, x, badge.Y + (badge.Height * 0.62f), stem, stem);
                }
            }
            finally
            {
                graphics.CompositingMode = previous;
            }
        }

        /// <summary>Where a state badge sits: the bottom right corner, hard against the edge.</summary>
        private static RectangleF BadgeBounds(int size)
        {
            float diameter = size * 0.46f;
            return new RectangleF(size - diameter, size - diameter, diameter, diameter);
        }

        /// <summary>
        /// Clears a ring around the badge before it is drawn.
        /// </summary>
        /// <remarks>
        /// Without this the badge and the mouse are the same colour touching each other, so they
        /// fuse into one shape and the badge stops being a badge. The gap is what separates
        /// them, and it has to be cut out of the body rather than drawn over it, because there
        /// is no background colour to draw with: the icon is transparent behind.
        /// </remarks>
        private static void PunchBadgeMoat(Graphics graphics, int size, RectangleF badge)
        {
            float moat = Math.Max(1f, size * 0.07f);

            CompositingMode previous = graphics.CompositingMode;
            graphics.CompositingMode = CompositingMode.SourceCopy;

            try
            {
                using (var cut = new SolidBrush(Color.Transparent))
                {
                    graphics.FillEllipse(
                        cut,
                        badge.X - moat,
                        badge.Y - moat,
                        badge.Width + (moat * 2f),
                        badge.Height + (moat * 2f));
                }
            }
            finally
            {
                graphics.CompositingMode = previous;
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
