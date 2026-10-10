using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Tropa.Core.Control;
using Tropa.Core.Telegram;

namespace Tropa.Tray;

/// <summary>«Для всего дома»: телефоны и ТВ в этой Wi-Fi ходят на сайты из обхода через компьютер.</summary>
public sealed partial class MainViewModel
{
    private bool _homeBusy;
    private string? _homeQrFor;
    private ImageSource? _homeQr;

    public bool HomeOn => _status?.Home is { Enabled: true };
    public string? HomePac => _status?.Home?.PacUrl;
    public bool HasHomePac => HomePac is not null;

    public string HomeText => _status?.Home switch
    {
        null or { Enabled: false } => Loc.T("Телефоны, планшеты и телевизор в этой Wi-Fi смогут открывать YouTube, Discord и другие сайты из обхода через этот компьютер. Остальное они откроют напрямую, как обычно."),
        { Running: false, Error: { } e } => Loc.F("Не запустился: {0}", Loc.Msg(e)),
        { PacUrl: null } => Loc.T("Компьютер не в домашней сети: телефонам не к чему подключиться."),
        { Stats: { Devices: > 0 } st } => Loc.F("Работает: устройств {0}, соединений сейчас {1}, трафик {2}.", st.Devices, st.Active, Bytes(st.BytesUp + st.BytesDown)),
        _ => Loc.T("Работает. Осталось настроить телефон: Wi-Fi → эта сеть → Прокси → Авто, адрес ниже."),
    };

    public ImageSource? HomeQr
    {
        get
        {
            if (HomePac is not { } pac) return null;
            if (_homeQrFor == pac) return _homeQr;
            _homeQrFor = pac;
            return _homeQr = Render(QrCode.Encode(pac));
        }
    }

    public ICommand HomeToggleCommand => _homeToggle ??= new RelayCommand(async () =>
    {
        _homeBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await EnsureServiceAsync();
            ApplyStatus(await _client!.CallAsync<StatusReport>("homeproxy", new { on = !HomeOn }, _cts.Token));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log(ex.Message, error: true);
        }
        finally
        {
            _homeBusy = false;
            RaiseHome();
        }
    }, () => !_homeBusy && !IsRunning);
    private ICommand? _homeToggle;

    private void RaiseHome()
    {
        Raise(nameof(HomeOn));
        Raise(nameof(HomePac));
        Raise(nameof(HasHomePac));
        Raise(nameof(HomeText));
        Raise(nameof(HomeQr));
        CommandManager.InvalidateRequerySuggested();
    }
}
