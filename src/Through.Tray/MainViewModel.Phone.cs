using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Through.Core.Control;
using Through.Core.Telegram;

namespace Through.Tray;

/// <summary>Телефон через прокси Telegram: прокси открыт для домашней сети, ссылка — QR-кодом для камеры.</summary>
public sealed partial class MainViewModel
{
    private string? _qrFor;
    private ImageSource? _qr;

    public bool TgLanOn => _tg is { Lan: true };
    public string? TgLanLink => _tg?.LanLink;
    public bool HasTgQr => TgLanLink is not null;

    /// <summary>QR со ссылкой https://t.me/proxy?…: тёмный на белом в любой теме, иначе камеры не читают.</summary>
    public ImageSource? TgQr
    {
        get
        {
            if (TgLanLink is not { } link) return null;
            if (_qrFor == link) return _qr;
            _qrFor = link;
            return _qr = Render(QrCode.Encode(link));
        }
    }

    public ICommand TgLanCommand => _tgLan ??= new RelayCommand(async () =>
    {
        if (_client is null) return;
        await TelegramCallAsync("telegram.lan", new { on = !TgLanOn });
        Log(TgLanOn ? Loc.T("прокси Telegram открыт для телефонов в домашней сети") : Loc.T("прокси Telegram снова только для этого компьютера"));
    }, () => !_tgBusy && TgOn && _client is not null);
    private ICommand? _tgLan;

    private void RaisePhone()
    {
        Raise(nameof(TgLanOn));
        Raise(nameof(TgLanLink));
        Raise(nameof(HasTgQr));
        Raise(nameof(TgQr));
    }

    private static ImageSource Render(bool[,] m, int scale = 8, int quiet = 4)
    {
        var n = m.GetLength(0);
        var size = (n + quiet * 2) * scale;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)255);
        for (var r = 0; r < n; r++)
            for (var c = 0; c < n; c++)
                if (m[r, c])
                    for (var y = 0; y < scale; y++)
                        Array.Fill(pixels, (byte)0, ((r + quiet) * scale + y) * size + (c + quiet) * scale, scale);
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bmp.Freeze();
        return bmp;
    }
}
