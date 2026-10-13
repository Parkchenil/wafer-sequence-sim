namespace App.WinForms;

internal static class DashboardTheme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#100E16");
    public static readonly Color Panel = ColorTranslator.FromHtml("#211D2A");
    public static readonly Color Accent = ColorTranslator.FromHtml("#FF812C");
    public static readonly Color Text = ColorTranslator.FromHtml("#F5F2F7");
    public static readonly Color Muted = ColorTranslator.FromHtml("#A49CAD");
    public static readonly Color Border = ColorTranslator.FromHtml("#514956");

    public static void Apply(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control.ForeColor != Muted) control.ForeColor = Text;
            if (control.BackColor != Background) control.BackColor = control is TextBox or ComboBox or NumericUpDown or ListView or BufferedPanel ? Background : Panel;
            if (control is TabControl tabs)
            {
                tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                tabs.DrawItem += (_, e) =>
                {
                    using var fill = new SolidBrush(tabs.SelectedIndex == e.Index ? Border : Panel);
                    e.Graphics.FillRectangle(fill, e.Bounds);
                    TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                };
            }
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = Border;
                if (button.Text == "시작") { button.BackColor = Accent; button.ForeColor = Background; }
            }
            if (control is ListView list) { list.GridLines = false; list.ShowItemToolTips = true; }
            Apply(control);
        }
    }
}
