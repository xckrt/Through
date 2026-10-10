using System.IO;
using System.Windows.Input;
using Tropa.Core.Control;

namespace Tropa.Tray;

/// <summary>Защищённый DNS на обзоре и в «Почему не открывается?», когда провайдер подменяет адрес.</summary>
public sealed partial class MainViewModel
{
    private bool _dnsBusy;

    public bool DnsOn => _status?.Dns is { Enabled: true };

    public string DnsText => _status?.Dns switch
    {
        null or { Enabled: false } => Loc.T("Провайдер видит, какие сайты вы открываете, и может подменить их адреса. С защищённым DNS запросы идут через HTTPS к Cloudflare или Google."),
        { Running: true, Error: null, Stats: { } st } => Loc.F("Включён: адреса сайтов приходят через {0} по HTTPS. Запросов {1}, из кэша {2}.", st.Upstream, st.Queries, st.CacheHits),
        { Error: { } e } => Loc.F("Пока не работает: {0}. Интернет идёт через прежний DNS, Тропа попробует снова сама.", Loc.Msg(e)),
        _ => Loc.T("Включаю…"),
    };

    public ICommand DnsToggleCommand => _dnsToggle ??= new RelayCommand(() => SetDnsAsync(!DnsOn), () => !_dnsBusy && !IsRunning);
    private ICommand? _dnsToggle;

    /// <summary>Проверка сайта нашла подмену DNS, а защищённый ещё не включён.</summary>
    public bool SiteCanFixDns => _site?.Verdict == Tropa.Core.Control.SiteVerdict.DnsSpoofed && !DnsOn;

    public ICommand FixSiteDnsCommand => _fixSiteDns ??= new RelayCommand(async () =>
    {
        await SetDnsAsync(true);
        if (DnsOn) await CheckSiteAsync();
    }, () => !_dnsBusy && !IsRunning && SiteCanFixDns);
    private ICommand? _fixSiteDns;

    private async Task SetDnsAsync(bool on)
    {
        _dnsBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await EnsureServiceAsync();
            ApplyStatus(await _client!.CallAsync<StatusReport>("dns", new { on }, _cts.Token));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log(Loc.F("защищённый DNS: {0}", ex.Message), error: true);
        }
        finally
        {
            _dnsBusy = false;
            RaiseDns();
        }
    }

    private void RaiseDns()
    {
        Raise(nameof(DnsOn));
        Raise(nameof(DnsText));
        Raise(nameof(SiteCanFixDns));
        CommandManager.InvalidateRequerySuggested();
    }
}
