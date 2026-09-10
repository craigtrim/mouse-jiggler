using System.Windows.Forms;

namespace MouseJiggler.App
{
    /// <summary>Layout shapes the Settings window and its panels share.</summary>
    internal static class SettingsLayout
    {
        /// <summary>
        /// A single column that is as wide as the panel holding it.
        /// </summary>
        /// <remarks>
        /// The explicit column style is what makes wrapping labels work. Left to auto-size, the
        /// column asks each child how wide it would like to be, and a wrapping label answers with
        /// the width of its text on one line. The row is then given one line of height, the label
        /// is laid out at the narrower width the column actually settled on, it wraps onto a
        /// second line, and that line is drawn outside the row it was allotted: the group below
        /// covers it. Measuring and laying out at the same width is the whole fix.
        /// </remarks>
        public static TableLayoutPanel CreateColumn()
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
            };

            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            return panel;
        }
    }
}
