using System.IO;
using System.Windows.Input;
using Tropa.Core.Control;
using Tropa.Core.Probing;

namespace Tropa.Tray;

/// <summary>
/// «Почему не открывается?»: человек вставляет адрес любого сайта, Тропа прогоняет его по этапам,
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
    public string SiteExplain { get => _siteExplain; private set => Set(ref _siteExplain, value); }
    public bool SiteCanBypass => _site?.Verdict is SiteVerdict.Fixable or SiteVerdict.Slow;
    public string SiteBypassLabel => _site is null ? "" : $"Обходить {_site.Domain}";
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
            SiteHeadline = "Это не похоже на адрес сайта";
            SiteExplain = "Вставьте адрес, например rutracker.org или ссылку https://x.com/…";
            RaiseSite();
            return;
        }

        SiteBusy = true;
        var row = new TargetVm("site", host, host);
        row.Reset();
        SiteRow = row;
        SiteHeadline = $"Проверяю {host}";
        SiteExplain = "Иду по этапам: адрес, подключение, защищённое соединение, ответ, загрузка.";
        try
        {
            // проверка идёт из окна, но через тот же winws, что и вся система: видно, помогает ли текущая защита
            _site = await SiteCheck.RunAsync(new ServiceProber(), host, _cts.Token);
            row.Apply(TargetReport.From("site", _site.Probe));
            (SiteHeadline, SiteExplain) = Explain(_site);
            Log($"проверка {host}: {SiteHeadline}");
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
            SiteVerdict.Works when bypassed => ($"{r.Host} открывается через обход", "Тропа уже обходит этот сайт."),
            SiteVerdict.Works => ($"{r.Host} открывается", "Провайдер его не трогает. Если в браузере он всё равно не грузится, дело в браузере или в самом сайте."),
            SiteVerdict.Fixable => ($"{r.Host} режет провайдер", r.Probe.SniFiltered == true
                ? "Сервер доступен, но провайдер узнаёт сайт по имени и глушит соединение. Это лечится настройкой DPI-обработки: Тропа подберёт стратегию для него одного."
                : $"Провайдер вмешивается в соединение ({Wording.Explain(TargetReport.From("site", r.Probe))}). Обычно это лечится настройкой DPI-обработки."),
            SiteVerdict.Slow => ($"{r.Host} работает медленно", "Похоже на замедление со стороны провайдера. Обход часто помогает и против него."),
            SiteVerdict.AddressBlocked => ($"{r.Host} заблокирован по адресу", "Провайдер глушит сам сервер, а не имя сайта, либо сайт лежит. Настройка DPI-обработки тут не поможет: нужен VPN или прокси."),
            SiteVerdict.DnsSpoofed => ($"DNS подменяет адрес {r.Host}", "Провайдер выдаёт неправильный адрес сайта. Это лечится не обходом, а защищённым DNS: Тропа включит его одной кнопкой."),
            SiteVerdict.NotFound => ($"{r.Host} не находится", "Такого имени нет: проверьте адрес. Если он точно верный, DNS провайдера может его прятать."),
            SiteVerdict.SiteRefuses => ($"{r.Host} отказывает сам", $"Провайдер пропускает, а сервер ответил «HTTP {r.Probe.HttpStatus}». Часто это блокировка по стране со стороны сайта: тут поможет только VPN."),
            _ => ($"Не удалось проверить {r.Host}", r.Probe.Detail ?? "Похоже, нет сети."),
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
            Log($"добавлен сайт {_site.Domain}");
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
        Raise(nameof(SiteBypassLabel));
        CommandManager.InvalidateRequerySuggested();
    }
}
