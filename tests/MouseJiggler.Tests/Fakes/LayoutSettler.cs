using System.Text;
using System.Windows.Forms;

namespace MouseJiggler.Tests.Fakes
{
    /// <summary>
    /// Waits for a window to stop rearranging itself.
    /// </summary>
    /// <remarks>
    /// Showing a form starts a cascade: OnLoad measures and resizes it, the resize triggers
    /// another pass, the clamp sets Bounds again, and an AutoSize panel docked to the top is
    /// resized by its parent after its own children have moved. Measuring at an arbitrary point
    /// in that sequence catches a panel that is briefly shorter than its contents and reports it
    /// as clipping, which is a bug in the observation rather than in the window.
    ///
    /// Waiting on the form's own size is not enough, because the outer size settles before the
    /// panels inside it do. The whole tree has to stop moving, so the whole tree is what is
    /// compared. A real user sees the settled window; so does a test that waits for it.
    /// </remarks>
    public static class LayoutSettler
    {
        private const int MaxAttempts = 20;

        public static void Settle(Control root)
        {
            string previous = string.Empty;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                Application.DoEvents();
                root.PerformLayout();
                Application.DoEvents();

                string current = Describe(root);

                if (current == previous)
                {
                    return;
                }

                previous = current;
            }
        }

        /// <summary>Every control's bounds, so any movement anywhere shows as a difference.</summary>
        private static string Describe(Control control)
        {
            var builder = new StringBuilder(4096);
            Append(control, builder);
            return builder.ToString();
        }

        private static void Append(Control control, StringBuilder builder)
        {
            builder.Append(control.GetType().Name);
            builder.Append(control.Bounds);
            builder.Append(control.Visible ? '+' : '-');
            builder.Append(';');

            foreach (Control child in control.Controls)
            {
                Append(child, builder);
            }
        }
    }
}
