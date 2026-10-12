using System.IO;
using System.Windows.Input;
using Through.Core.Control;
using Through.Core.Probing;

namespace Through.Tray;

/// <summary>
/// «Почему не открывается?»: человек вставляет адрес любого сайта, Сквозь прогоняет его по этапам,
/// говорит, кто виноват (провайдер, адрес, DNS, сам сайт), и, если это лечится, обходит его одной кнопкой.
/// </summary>
public sealed partial class MainViewModel
{
    private string _siteInput = "";
    private TargetVm? _siteRow;
    private string? _siteHeadline;
    private string _siteExplain = "";
    private SiteCheckResult? _site;
    private bool _siteBusy;

    public string SiteInput { get => _siteInput; set => Set(ref _siteInput, value); }
    public TargetVm? SiteRow { get => _siteRow; private set { if (Set(ref _siteRow, value)) Raise(nameof(HasSiteResult)); } }
    public bool HasSiteResult => SiteRow is not null;
    public string? SiteHeadline { get => _siteHeadline; private set => Set(ref _siteHeadline, value); }
    public string SiteExplain { get => _siteExplain; private set => Set(ref _siteExplain, Loc.Msg(value)); }
    public bool SiteCanBypass => _site?.Verdict is SiteVerdict.Fixable or SiteVerdict.Slow;
    public string SiteBypassLabel => _site is null ? "" : Loc.F("Обходить {0}", _site.Domain);
    public bool SiteBusy { get => _siteBusy; private set { Set(ref _siteBusy, value); CommandManager.InvalidateRequerySuggested(); } }

    public ICommand CheckSiteCommand => _checkSite ??= new RelayCommand(CheckSiteAsync, () => !SiteBusy);
    private ICommand? _checkSite;

    public ICommand BypassSiteCommand => _bypassSite ??= new RelayCommand(BypassSiteAsync, () => !IsRunning && !SiteBusy && SiteCanBypass);
    private ICommand? _bypassSite;

    public ICommand CloseSiteCommand => _closeSite ??= new RelayCommand(() =>
    {
        SiteRow = null;
        _site = null;
        return Task.CompletedTask;
    });
    private ICommand? _closeSite;

    private async Task CheckSiteAsync()
    {
        var host = SiteCheck.NormalizeHost(SiteInput);
        if (host is null)
        {
            _site = null;
            SiteRow = new TargetVm("site", SiteInput.Trim(), SiteInput.Trim());
            SiteHeadline = Loc.T("Это не похоже на адрес сайта");
            SiteExplain = Loc.T("Вставьте адрес, например rutracker.org или ссылку https://x.com/…");
            RaiseSite();
            return;
        }

        SiteBusy = true;
        var row = new TargetVm("site", host, host);
        row.Reset();
        SiteRow = row;
        SiteHeadline = Loc.F("Проверяю {0}", host);
        SiteExplain = Loc.T("Иду по этапам: адрес, подключение, защищённое соединение, ответ, загрузка.");
        try
        {
            // проверка идёт из окна, но через тот же winws, что и вся система: видно, помогает ли текущая защита
            _site = await SiteCheck.RunAsync(new ServiceProber(), host, _cts.Token);
            row.Apply(TargetReport.From("site", _site.Probe));
            (SiteHeadline, SiteExplain) = Explain(_site);
            Log(Loc.F("проверка {0}: {1}", host, SiteHeadline));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SiteBusy = false;
            RaiseSite();
        }
    }

    private (string, string) Explain(SiteCheckResult r)
    {
        var bypassed = _userApps.Any(a => a.Domains.Contains(r.Domain));
        return r.Verdict switch
        {
            SiteVerdict.Works when bypassed => (Loc.F("{0} открывается через обход", r.Host), Loc.T("Сквозь уже обходит этот сайт.")),
            SiteVerdict.Works => (Loc.F("{0} открывается", r.Host), Loc.T("Провайдер его не трогает. Если в браузере он всё равно не грузится, дело в браузере или в самом сайте.")),
            SiteVerdict.Fixable => (Loc.F("{0} режет провайдер", r.Host), r.Probe.SniFiltered == true
                ? Loc.T("Сервер доступен, но провайдер узнаёт сайт по имени и глушит соединение. Это лечится настройкой DPI-обработки: Сквозь подберёт стратегию для него одного.")
                : Loc.F("Провайдер вмешивается в соединение ({0}). Обычно это лечится настройкой DPI-обработки.", Wording.Explain(TargetReport.From("site", r.Probe)))),
            SiteVerdict.Slow => (Loc.F("{0} работает медленно", r.Host), Loc.T("Похоже на замедление со стороны провайдера. Обход часто помогает и против него.")),
            SiteVerdict.AddressBlocked => (Loc.F("{0} заблокирован по адресу", r.Host), Loc.T("Провайдер глушит сам сервер, а не имя сайта, либо сайт лежит. Настройка DPI-обработки тут не поможет: нужен VPN или прокси.")),
            SiteVerdict.DnsSpoofed => (Loc.F("DNS подменяет адрес {0}", r.Host), Loc.T("Провайдер выдаёт неправильный адрес сайта. Это лечится не обходом, а защищённым DNS: Сквозь включит его одной кнопкой.")),
            SiteVerdict.NotFound => (Loc.F("{0} не находится", r.Host), Loc.T("Такого имени нет: проверьте адрес. Если он точно верный, DNS провайдера может его прятать.")),
            SiteVerdict.SiteRefuses => (Loc.F("{0} отказывает сам", r.Host), Loc.F("Провайдер пропускает, а сервер ответил «HTTP {0}». Часто это блокировка по стране со стороны сайта: тут поможет только VPN.", r.Probe.HttpStatus)),
            _ => (Loc.F("Не удалось проверить {0}", r.Host), r.Probe.Detail ?? Loc.T("Похоже, нет сети.")),
        };
    }

    /// <summary>Обойти только что проверенный сайт: своё приложение с его доменом и подбор только для него.</summary>
    private async Task BypassSiteAsync()
    {
        if (_site is null) return;
        var app = new AppDefinition
        {
            Key = Slug(_site.Domain),
            Title = _site.Domain,
            Domains = [_site.Domain],
            CheckHosts = [_site.Host],
        };
        try
        {
            IsRunning = true;
            await EnsureServiceAsync();
            await _client!.CallAsync<AppDefinition[]>("apps.add", UserApps.Normalize(app), _cts.Token);
            Log(Loc.F("добавлен сайт {0}", _site.Domain));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            SiteExplain = ex.Message;
            return;
        }
        finally
        {
            IsRunning = false;
        }
        await RefreshAppsAsync();
        SiteRow = null;
        await TuneAsync([app.Key]);
    }

    private void RaiseSite()
    {
        Raise(nameof(SiteCanBypass));
        Raise(nameof(SiteCanFixDns));
        Raise(nameof(SiteExcludeLabel));
        Raise(nameof(SiteBypassLabel));
        CommandManager.InvalidateRequerySuggested();
    }
}
