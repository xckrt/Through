using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using Tropa.Core;
using Tropa.Core.Engine;

namespace Tropa.Tray;

/// <summary>
/// Обновления самой Тропы и отчёты об ошибках. Обе вещи показываются полосой под заголовком окна:
/// «вышла новая версия» или «в прошлый раз Тропа упала».
/// </summary>
public sealed partial class MainViewModel
{
    private AppRelease? _release;
    private string? _bannerText;
    private string _bannerAction = "";
    private bool _bannerIsCrash;
    private bool _updateBusy;

    public string? BannerText { get => _bannerText; private set { if (Set(ref _bannerText, value)) Raise(nameof(HasBanner)); } }
    public bool HasBanner => BannerText is not null;
    public string BannerAction { get => _bannerAction; private set => Set(ref _bannerAction, value); }
    public bool BannerIsUpdate => _release is not null && !_bannerIsCrash;
    public string VersionText => $"Тропа {TropaBuild.VersionText}";

    public bool CheckAppUpdates
    {
        get => _ui.CheckAppUpdates;
        set
        {
            if (_ui.CheckAppUpdates == value) return;
            _ui.CheckAppUpdates = value;
            _ui.Save();
            Raise(nameof(CheckAppUpdates));
        }
    }

    public ICommand BannerPrimaryCommand => _bannerPrimary ??= new RelayCommand(
        () => _bannerIsCrash ? SendCrashAsync() : InstallUpdateAsync(), () => !_updateBusy);
    private ICommand? _bannerPrimary;

    public ICommand BannerLaterCommand => _bannerLater ??= new RelayCommand(() =>
    {
        if (_bannerIsCrash) CrashReports.MarkSeen();
        else if (_release is not null) { _ui.SkippedVersion = _release.Version.ToString(); _ui.Save(); }
        BannerText = null;
        Raise(nameof(BannerIsUpdate));
        return Task.CompletedTask;
    });
    private ICommand? _bannerLater;

    public ICommand ReleaseNotesCommand => _notes ??= new RelayCommand(() =>
    {
        if (_release is not null) Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    });
    private ICommand? _notes;

    public ICommand CheckAppUpdateCommand => _checkApp ??= new RelayCommand(() => CheckAppUpdateAsync(manual: true), () => !_updateBusy);
    private ICommand? _checkApp;

    public ICommand ReportProblemCommand => _report ??= new RelayCommand(() =>
    {
        var cut = CrashReports.OpenIssue("Проблема: ", CrashReports.Build("(опишите, что пошло не так)", LogLinesText(), StatusLine()));
        Log(cut ? "открыт черновик отчёта на GitHub; полный текст в буфере обмена" : "открыт черновик отчёта на GitHub");
        return Task.CompletedTask;
    });
    private ICommand? _report;

    /// <summary>При запуске: сначала незакрытые падения (важнее), потом обновления.</summary>
    private async Task StartBannersAsync()
    {
        if (CrashReports.Pending().Count > 0)
        {
            _bannerIsCrash = true;
            BannerText = "В прошлый раз Тропа упала. Отправите отчёт? Это поможет её починить.";
            BannerAction = "Отправить отчёт";
            Raise(nameof(BannerIsUpdate));
        }
        await Task.Delay(TimeSpan.FromSeconds(5));
        await CheckAppUpdateAsync(manual: false);

        // проверка раз в 12 часов, пока окно живёт (часто — в трее неделями)
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(12) };
        timer.Tick += async (_, _) => await CheckAppUpdateAsync(manual: false);
        timer.Start();
    }

    private async Task CheckAppUpdateAsync(bool manual)
    {
        if (!manual && !CheckAppUpdates) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Tropa/{TropaBuild.VersionText}");
            var release = await AppUpdates.CheckAsync(http, TropaBuild.Version, _cts.Token);
            if (release is null)
            {
                if (manual) AppUpdateText = $"У вас последняя версия ({TropaBuild.VersionText}).";
                return;
            }
            AppUpdateText = $"Вышла Тропа {release.Version}.";
            if (!manual && _ui.SkippedVersion == release.Version.ToString()) return;
            if (_bannerIsCrash && HasBanner) return; // отчёт о падении важнее, обновление покажем в настройках
            _release = release;
            _bannerIsCrash = false;
            BannerText = $"Вышла Тропа {release.Version}. Обновление займёт минуту, подобранные стратегии сохранятся.";
            BannerAction = "Обновить";
            Raise(nameof(BannerIsUpdate));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            if (manual) AppUpdateText = $"Не удалось проверить обновления: {ex.Message}";
        }
    }

    private string _appUpdateText = "";
    public string AppUpdateText { get => _appUpdateText; private set => Set(ref _appUpdateText, value); }

    /// <summary>Скачать установщик, сверить SHA-256 с релизом и запустить обновление. Окно закрывается: установщик
    /// заменит файлы, поднимет службу и откроет Тропу снова.</summary>
    private async Task InstallUpdateAsync()
    {
        if (_release is not { } r) return;
        _updateBusy = true;
        CommandManager.InvalidateRequerySuggested();
        var path = Path.Combine(Path.GetTempPath(), r.SetupName);
        try
        {
            BannerText = $"Скачиваю Тропу {r.Version}…";
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Tropa/{TropaBuild.VersionText}");
                using var response = await http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync(_cts.Token);
                await using var dst = File.Create(path);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, _cts.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                    done += n;
                    if (total > 0) BannerText = $"Скачиваю Тропу {r.Version}: {done * 100 / total} %";
                }
            }

            BannerText = "Сверяю контрольную сумму…";
            await using (var f = File.OpenRead(path))
            {
                var got = Convert.ToHexString(await SHA256.HashDataAsync(f, _cts.Token));
                if (!got.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("скачанный установщик не совпал с релизом по SHA-256, ставить его нельзя");
            }

            BannerText = "Запускаю обновление: Windows спросит разрешение…";
            Process.Start(new ProcessStartInfo(path, "/update") { UseShellExecute = true })?.Dispose();
            Log($"запущено обновление до {r.Version}");
            Application.Current.Shutdown(); // установщику нужны файлы окна
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            BannerText = "Обновление отменено. Можно запустить снова кнопкой «Обновить».";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            BannerText = $"Не удалось обновиться: {ex.Message}";
            Log($"обновление: {ex.Message}", error: true);
        }
        finally
        {
            _updateBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private Task SendCrashAsync()
    {
        var report = CrashReports.Build("Тропа упала. (Если помните, что делали перед этим, допишите сюда.)", LogLinesText(), StatusLine());
        var cut = CrashReports.OpenIssue($"Падение Тропы {TropaBuild.VersionText}", report);
        CrashReports.MarkSeen();
        BannerText = cut
            ? "Черновик отчёта открыт в браузере. Отчёт длинный: полный текст в буфере обмена, вставьте его в issue."
            : "Черновик отчёта открыт в браузере: проверьте текст и нажмите Submit. Спасибо!";
        _bannerIsCrash = false;
        Raise(nameof(BannerIsUpdate));
        return Task.CompletedTask;
    }

    private IEnumerable<string> LogLinesText() => LogLines.Select(l => $"{l.Time} {l.Text}");

    private string? StatusLine() => _status is { } s
        ? $"защита {(s.Protecting ? "включена" : "выключена")}, zapret {s.EngineVersion ?? "не скачан"}, " +
          $"профиль: {string.Join(", ", s.Profile.Select(kv => $"{kv.Key}={kv.Value}"))}"
        : "не подключена";
}
