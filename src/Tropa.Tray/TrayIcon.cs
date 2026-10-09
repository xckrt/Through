using System.Drawing;
using Forms = System.Windows.Forms;

namespace Tropa.Tray;

/// <summary>
/// Значок у часов: та же бело-цветно-белая метка, что в заголовке. Средняя полоса — состояние защиты:
/// сосна — включена, красный — включена, но не всё работает, янтарь — идёт подбор, серый — выключена.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _protect;
    private Icon? _current;

    public event Action? Open;
    public event Action? ToggleProtection;
    public event Action? Exit;

    public TrayIcon()
    {
        _protect = new Forms.ToolStripMenuItem("Включить защиту", null, (_, _) => ToggleProtection?.Invoke());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Открыть Тропу", null, (_, _) => Open?.Invoke()) { Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(_protect);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Выйти из окна", null, (_, _) => Exit?.Invoke()));

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Open?.Invoke(); };
        Update(Pill.NoService, "Тропа", protecting: false, canToggle: false);
    }

    public void Update(Pill pill, string text, bool protecting, bool canToggle)
    {
        var stripe = pill switch
        {
            Pill.On => Color.FromArgb(0x2E, 0x7D, 0x5B),
            Pill.Warn => Color.FromArgb(0xC8, 0x32, 0x2B),
            Pill.Busy => Color.FromArgb(0xB7, 0x79, 0x1F),
            _ => Color.FromArgb(0x9A, 0xA5, 0x9F),
        };
        var old = _current;
        _current = Draw(stripe);
        _icon.Icon = _current;
        old?.Dispose();

        // подсказка у значка ограничена 63 символами
        var tip = $"Тропа: {text}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        _protect.Text = protecting ? "Выключить защиту" : "Включить защиту";
        _protect.Enabled = canToggle;
    }

    public void Hint(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    /// <summary>Знак Тропы под размер значков в трее (16 при 100 %, больше на крупном масштабе).</summary>
    private static Icon Draw(Color stripe)
    {
        using var bmp = IconArt.Draw(Forms.SystemInformation.SmallIconSize.Width, stripe);
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
