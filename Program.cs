using System;
using System.Drawing;
using System.Windows.Forms;

namespace AsterCut;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

/// <summary>exe に埋め込まれたアプリアイコン (各ウィンドウの題名バー用)。</summary>
internal static class AppIcon
{
    private static Icon? _value;
    private static bool _loaded;

    public static Icon? Value
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try { _value = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { _value = null; }
            }
            return _value;
        }
    }
}
