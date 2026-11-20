using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>ラベル・テキストボックス・ボタンの生成と、画面全体の配色をそろえるための部品。</summary>
internal static class Theme
{
    // タイトルバーは OS が描くため、DWM の属性でダークモードの色に切り替える。
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>ウィンドウのタイトルバーをダークモードの色にする。対応しない環境では何もしない。</summary>
    internal static void ApplyDarkTitleBar(Form form)
    {
        try
        {
            var value = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004 以降)、19 はそれ以前のビルド用。
            if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
        }
        catch { }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>コントロールのスクロールバーをダークモードの色にする。対応しない環境では何もしない。</summary>
    internal static void ApplyDarkScrollBars(Control control)
    {
        void Apply()
        {
            try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
            catch { }
        }
        if (control.IsHandleCreated) Apply();
        control.HandleCreated += (_, _) => Apply();
    }

    public static readonly Color PageBack = Color.FromArgb(29, 28, 36);
    public static readonly Color PanelBack = Color.FromArgb(37, 35, 45);
    public static readonly Color BoxBack = Color.FromArgb(34, 33, 42);
    public static readonly Color LabelFore = Color.FromArgb(230, 228, 238);
    public static readonly Color BoxFore = Color.FromArgb(239, 238, 244);
    public static readonly Color MutedFore = Color.FromArgb(178, 174, 190);
    public static readonly Color MenuFore = Color.FromArgb(235, 233, 242);
    public static readonly Color Accent = Color.FromArgb(125, 105, 168);

    public static Label MakeLabel(string text, int x, int y, int w, int h = 24)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            ForeColor = LabelFore,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
        };
    }

    public static TextBox MakeTextBox(int x, int y, int w, int h = 26)
    {
        // ここでは余白を付けない。
        // 先頭の 2px 余白は、パス欄など必要な欄だけに MainForm 側で付ける。
        return new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = BoxBack,
            ForeColor = BoxFore,
            BorderStyle = BorderStyle.FixedSingle,
        };
    }

    public static Button MakeButton(string text, int x, int y, int w, int h = 30)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(58, 54, 72),
            ForeColor = Color.FromArgb(246, 245, 250),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(88, 80, 110);
        return button;
    }

    public static Panel MakeGroupPanel(int x, int y, int w, int h, string title)
    {
        var panel = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = PageBack,
        };
        var titleLabel = MakeLabel(title, 12, 2, 480, 23);
        titleLabel.Font = new Font(titleLabel.Font, FontStyle.Bold);
        titleLabel.BackColor = PageBack;
        panel.Controls.Add(titleLabel);
        return panel;
    }
}

/// <summary>メニューの配色テーブル。</summary>
internal sealed class AsterCutMenuColorTable : ProfessionalColorTable
{
    private static readonly Color Bar = Color.FromArgb(46, 43, 56);
    // 選択中は半透明の白を重ねた色にする (バーにもドロップダウンにもなじむよう、色そのものに透明度を持たせる)。
    private static readonly Color Selected = Color.FromArgb(50, 255, 255, 255);
    private static readonly Color Pressed = Color.FromArgb(74, 255, 255, 255);
    private static readonly Color Border = Color.FromArgb(52, 44, 74);
    private static readonly Color DropDown = Color.FromArgb(37, 35, 45);

    public override Color MenuStripGradientBegin => Bar;
    public override Color MenuStripGradientEnd => Bar;
    public override Color MenuItemSelectedGradientBegin => Selected;
    public override Color MenuItemSelectedGradientEnd => Selected;
    public override Color MenuItemSelected => Selected;
    public override Color MenuItemBorder => Border;
    public override Color MenuItemPressedGradientBegin => Pressed;
    public override Color MenuItemPressedGradientMiddle => Pressed;
    public override Color MenuItemPressedGradientEnd => Pressed;
    public override Color MenuBorder => Border;
    public override Color ToolStripDropDownBackground => DropDown;
    public override Color ImageMarginGradientBegin => DropDown;
    public override Color ImageMarginGradientMiddle => DropDown;
    public override Color ImageMarginGradientEnd => DropDown;
    public override Color ImageMarginRevealedGradientBegin => DropDown;
    public override Color ImageMarginRevealedGradientMiddle => DropDown;
    public override Color ImageMarginRevealedGradientEnd => DropDown;
    public override Color SeparatorDark => Border;
    public override Color SeparatorLight => Color.FromArgb(80, 74, 98);
}

/// <summary>メニューのレンダラー。チェックは標準の太い字形ではなく、細い ✓ を描く。</summary>
internal sealed class AsterCutMenuRenderer : ToolStripProfessionalRenderer
{
    public AsterCutMenuRenderer() : base(new AsterCutMenuColorTable()) { }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = e.ImageRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var g = e.Graphics;
        var previousMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f;
        using (var pen = new Pen(e.Item.ForeColor, 2.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 4.5f, cy + 0.5f),
                new PointF(cx - 1.3f, cy + 3.7f),
                new PointF(cx + 4.8f, cy - 3.9f),
            });
        }
        g.SmoothingMode = previousMode;
    }
}
