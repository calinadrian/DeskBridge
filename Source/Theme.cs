using System.Runtime.InteropServices;

namespace DeskBridge;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(20, 21, 24);
    public static readonly Color Surface = Color.FromArgb(29, 30, 34);
    public static readonly Color Field = Color.FromArgb(37, 38, 43);
    public static readonly Color Border = Color.FromArgb(66, 68, 76);
    public static readonly Color Text = Color.FromArgb(237, 238, 242);
    public static readonly Color Muted = Color.FromArgb(172, 174, 185);
    public static readonly Color Disabled = Color.FromArgb(149, 151, 163);
    public static readonly Color Accent = Color.FromArgb(255, 134, 91);
    public static readonly Color Connected = Color.FromArgb(128, 216, 163);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = new Font("Segoe UI", 10);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            form.HandleCreated += (_, _) =>
            {
                int enabled = 1;
                DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
            };
        }
    }

    public static void Style(Control parent, params Button[] primary)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Button button)
                StyleButton(button, primary.Contains(button));
            else if (control is TextBox box)
            {
                box.BorderStyle = BorderStyle.FixedSingle;
                box.BackColor = Field;
                box.ForeColor = Text;
            }
            else if (control is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Field;
                combo.ForeColor = Text;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.DrawItem += (_, e) =>
                {
                    if (e.Index < 0) return;
                    using var background = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Border : Field);
                    e.Graphics.FillRectangle(background, e.Bounds);
                    TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font,
                        e.Bounds, Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    e.DrawFocusRectangle();
                };
            }
            else if (control is NumericUpDown)
            {
                control.BackColor = Field;
                control.ForeColor = Text;
            }
            else if (control is ButtonBase toggle)
            {
                toggle.UseVisualStyleBackColor = false;
                toggle.FlatStyle = FlatStyle.Flat;
                toggle.FlatAppearance.CheckedBackColor = Field;
                toggle.FlatAppearance.MouseOverBackColor = Field;
                toggle.ForeColor = Text;
            }

            Style(control, primary);
        }
    }

    static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.Padding = new Padding(10, 4, 10, 4);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(255, 161, 126) : Border;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(235, 111, 66) : Surface;

        void Refresh()
        {
            bool highlighted = primary && button.Enabled;
            button.BackColor = highlighted ? Accent : Field;
            button.ForeColor = highlighted ? Background : Text;
            button.FlatAppearance.BorderColor = highlighted ? Accent : Border;
        }

        button.EnabledChanged += (_, _) => Refresh();
        Refresh();
    }
}

internal sealed class DashboardButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled)
        {
            base.OnPaint(e);
            return;
        }

        e.Graphics.Clear(Theme.Field);
        using var border = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Theme.Disabled,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class DashboardCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) base.OnPaint(e);
        else DisabledToggle.Paint(this, e.Graphics, Checked, false);
    }
}

internal sealed class DashboardRadioButton : RadioButton
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) base.OnPaint(e);
        else DisabledToggle.Paint(this, e.Graphics, Checked, true);
    }
}

internal static class DisabledToggle
{
    public static void Paint(ButtonBase control, Graphics graphics, bool isChecked, bool round)
    {
        graphics.Clear(control.BackColor);
        int size = control.LogicalToDeviceUnits(10);
        var outline = new Rectangle(0, (control.Height - size) / 2, size, size);
        using var pen = new Pen(Theme.Disabled);
        if (round) graphics.DrawEllipse(pen, outline);
        else graphics.DrawRectangle(pen, outline);
        if (isChecked)
        {
            var mark = Rectangle.Inflate(outline, -2, -2);
            using var brush = new SolidBrush(Theme.Disabled);
            if (round) graphics.FillEllipse(brush, mark);
            else graphics.FillRectangle(brush, mark);
        }

        int inset = control.LogicalToDeviceUnits(17);
        var textBounds = new Rectangle(inset, 0, Math.Max(0, control.Width - inset), control.Height);
        TextRenderer.DrawText(graphics, control.Text, control.Font, textBounds, Theme.Disabled,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
