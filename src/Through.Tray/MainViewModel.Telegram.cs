using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Through.Core.Control;

namespace Through.Tray;

/// <summary>Строка дата-центра на странице Telegram.</summary>
public sealed class DcRowVm(int dc, string place) : Observable
{
    private string _summary = Loc.T("не проверялся");
    private Health _health = Health.Pending;

    public int Dc => dc;
    public string Name => $"DC{dc}";
    public string Place => place;
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public Health Health { get => _health; set => Set(ref _health, value); }
}

/// <summary>Страница «Telegram»: локальный прокси службы (порт tg-ws-proxy), ссылка для клиента, проверка DC.</summary>
public sealed partial class MainViewModel
{
    private TelegramReport? _tg;
    private DispatcherTimer? _tgTimer;
    private bool _tgBusy;
    private string _tgHeadline = Loc.T("Прокси для Telegram");
    private string _tgSubline = "";
    private Health _tgHealth = Health.Pending;
    private string _tgStats = "";
    private string? _tgRival;

    public ObservableCollection<DcRowVm> DcRows { get; } =
    [
        new(1, Loc.T("Майами")), new(2, Loc.T("Амстердам")), new(3, Loc.T("Майами")), new(4, Loc.T("Амстердам")), new(5, Loc.T("Сингапур")),
    ];

    public string TgHeadline { get => _tgHeadline; private set => Set(ref _tgHeadline, value); }
    public string TgSubline { get => _tgSubline; private set => Set(ref _tgSubline, Loc.Msg(value)); }
    public Health TgHealth { get => _tgHealth; private set => Set(ref _tgHealth, value); }
    public string TgStats { get => _tgStats; private set { if (Set(ref _tgStats, value)) Raise(nameof(HasTgStats)); } }
    public bool HasTgStats => TgStats.Length > 0;
    public bool TgOn => _tg is { Running: true };
    public string TgPrimaryLabel => TgOn ? Loc.T("Подключить Telegram") : _tg is { Enabled: true } ? Loc.T("Запустить снова") : Loc.T("Включить");
    public string? TgLink => _tg?.Link;
    public string TgAddress => _tg is { } t ? $"127.0.0.1:{t.Port}" : "";

    /// <summary>Уже запущен tg-ws-proxy от Flowseal: он держит 1443, а Тропа делает то же самое.</summary>
    public string? TgRival { get => _tgRival; private set { if (Set(ref _tgRival, value)) Raise(nameof(HasTgRival)); } }
    public bool HasTgRival => TgRival is not null;

    public ICommand TgPrimaryCommand => _tgPrimary ??= new RelayCommand(async () =>
    {
        if (TgOn) OpenTelegramLink();
        else await EnableTelegramAsync();
    }, () => !_tgBusy && !IsRunning);
    private ICommand? _tgPrimary;

    public ICommand TgCheckCommand => _tgCheck ??= new RelayCommand(CheckTelegramAsync, () => !_tgBusy && TgOn && _client is not null);
    private ICommand? _tgCheck;

    public ICommand TgCopyCommand => _tgCopy ??= new RelayCommand(() =>
    {
        if (TgLink is { } link)
        {
            Clipboard.SetText(link);
            TgSubline = Loc.T("Ссылка скопирована. Её можно открыть на этом компьютере или вставить в Telegram: Настройки → Продвинутые → Тип соединения.");
        }
        return Task.CompletedTask;
    }, () => TgLink is not null);
    private ICommand? _tgCopy;

    public ICommand TgOffCommand => _tgOff ??= new RelayCommand(async () =>
    {
        if (_client is null) return;
        await TelegramCallAsync("telegram", new { on = false });
        Log(Loc.T("прокси Telegram выключен; если Telegram был подключён к нему, отключите прокси и в самом Telegram"));
    }, () => !_tgBusy && _tg is { Enabled: true } && _client is not null);
    private ICommand? _tgOff;

    public ICommand TgCloseRivalCommand => _tgCloseRival ??= new RelayCommand(() =>
    {
        foreach (var p in RivalProcesses())
            using (p)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log(Loc.F("не удалось закрыть {0}: {1}", p.ProcessName, ex.Message), error: true); }
            }
        DetectRival();
        return Task.CompletedTask;
    });
    private ICommand? _tgCloseRival;

    private async Task EnableTelegramAsync()
    {
        _tgBusy = true;
        RefreshHome();
        IsRunning = true;
        try
        {
            TgHeadline = Loc.T("Включаю прокси");
            TgSubline = Loc.T("Прокси работает внутри службы Тропы, поэтому Telegram будет ходить через него и при закрытом окне.");
            await EnsureServiceAsync();
            var s = await _client!.CallAsync<StatusReport>("telegram", new { on = true }, _cts.Token);
            ApplyTelegram(s.Telegram);
            if (s.Telegram is { Running: true })
            {
                Log(Loc.F("прокси Telegram работает на 127.0.0.1:{0}", s.Telegram.Port));
                OpenTelegramLink();
                _ = CheckTelegramAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            TgHealth = Health.Blocked;
            TgHeadline = Loc.T("Прокси не включился");
            TgSubline = ex.Message;
            Log(Loc.F("прокси Telegram: {0}", ex.Message), error: true);
        }
        finally
        {
            _tgBusy = false;
            RefreshHome();
            IsRunning = false;
        }
    }

    private async Task CheckTelegramAsync()
    {
        if (_client is null || _tgBusy) return;
        _tgBusy = true;
        RefreshHome();
        CommandManager.InvalidateRequerySuggested();
        foreach (var row in DcRows)
        {
            row.Health = Health.Pending;
            row.Summary = Loc.T("проверяю…");
        }
        try
        {
            var s = await _client.CallAsync<StatusReport>("telegram.check", ct: _cts.Token);
            ApplyTelegram(s.Telegram);
            var bad = s.Telegram?.Checks.Count(c => !c.Ok) ?? 0;
            Log(bad == 0 ? Loc.T("Telegram: все дата-центры отвечают через прокси") : Loc.F("Telegram: не отвечают дата-центры ({0} из 5)", bad), error: bad > 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            TgSubline = ex.Message;
        }
        finally
        {
            _tgBusy = false;
            RefreshHome();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task TelegramCallAsync(string method, object? data)
    {
        _tgBusy = true;
        RefreshHome();
        try
        {
            ApplyTelegram((await _client!.CallAsync<StatusReport>(method, data, _cts.Token)).Telegram);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            TgSubline = ex.Message;
        }
        finally
        {
            _tgBusy = false;
            RefreshHome();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>tg://proxy открывает Telegram Desktop с вопросом «Подключить прокси?».</summary>
    private void OpenTelegramLink()
    {
        if (TgLink is not { } link) return;
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
            TgSubline = Loc.T("Telegram спросит, подключить ли прокси, — нажмите «Подключить». Дальше он будет ходить через Тропу сам.");
        }
        catch (Win32Exception)
        {
            Clipboard.SetText(link);
            TgSubline = Loc.T("Telegram Desktop не нашёлся. Ссылка скопирована: вставьте её в Telegram (Настройки → Продвинутые → Тип соединения) или установите Telegram Desktop и нажмите «Подключить» ещё раз.");
        }
    }

    /// <summary>Отчёт службы о прокси → страница. null — служба старая или её нет.</summary>
    private void ApplyTelegram(TelegramReport? t)
    {
        _tg = t;
        RaisePhone();
        Raise(nameof(TgOn));
        Raise(nameof(TgPrimaryLabel));
        Raise(nameof(TgLink));
        Raise(nameof(TgAddress));
        CommandManager.InvalidateRequerySuggested();

        if (t is null || !t.Enabled)
        {
            TgHealth = Health.Pending;
            TgHeadline = Loc.T("Прокси для Telegram");
            TgSubline = _client is null
                ? Loc.T("Нажмите «Включить»: Тропа поставит службу (Windows один раз спросит разрешение) и поднимет в ней прокси.")
                : Loc.T("Нажмите «Включить»: Тропа поднимет прокси в своей службе и сама добавит его в Telegram.");
            TgStats = "";
            foreach (var row in DcRows) { row.Health = Health.Pending; row.Summary = Loc.T("не проверялся"); }
            return;
        }
        if (!t.Running)
        {
            TgHealth = Health.Blocked;
            TgHeadline = Loc.T("Прокси не запустился");
            TgSubline = t.Error ?? Loc.T("Служба не смогла открыть порт. Нажмите «Запустить снова».");
            TgStats = "";
            return;
        }

        foreach (var row in DcRows)
        {
            var c = t.Checks.FirstOrDefault(x => x.Dc == row.Dc);
            row.Health = c is null ? Health.Pending : c.Ok ? (c.ElapsedMs > 3000 ? Health.Slow : Health.Ok) : Health.Blocked;
            row.Summary = c is null ? Loc.T("не проверялся") : c.Ok ? Loc.F("{0} мс", c.ElapsedMs) : Loc.T("не отвечает");
        }

        var failed = t.Checks.Count(c => !c.Ok);
        (TgHealth, TgHeadline) = t.Checks.Length == 0 ? (Health.Ok, Loc.T("Прокси работает"))
            : failed == 0 ? (Health.Ok, Loc.T("Telegram работает через Тропу"))
            : failed == t.Checks.Length ? (Health.Blocked, Loc.T("Дата-центры Telegram не отвечают"))
            : (Health.Partial, Loc.T("Часть дата-центров не отвечает"));

        var st = t.Stats;
        TgSubline = failed > 0
            ? Loc.T("Прокси перебрал все пути: WebSocket, Cloudflare и прямой. Попробуйте проверить позже или подобрать обход на «Обзоре».")
            : st is not { Total: > 0 }
                ? Loc.T("Telegram к нему ещё не подключался. Нажмите «Подключить Telegram» — он спросит, добавить ли прокси.")
                : st.Active > 0
                    ? Loc.F("Telegram подключён: соединений сейчас {0}.", st.Active)
                    : Loc.F("Telegram подключался {0}. Сейчас открытых соединений нет.", Ago(st.LastConnectionAt));
        if (st is { BadSecret: > 0 } && st.Total == st.BadSecret)
            TgSubline = Loc.T("Telegram стучится со старой ссылкой (другой секрет). Нажмите «Подключить Telegram», чтобы обновить её.");

        TgStats = st is not { Total: > 0 } ? "" : string.Join(Environment.NewLine, new[]
        {
            Loc.F("Соединений всего: {0}, сейчас: {1}", st.Total, st.Active),
            Loc.F("Через WebSocket: {0} · Cloudflare: {1} · напрямую: {2}", st.ViaWebSocket, st.ViaCloudflare, st.ViaTcp),
            st.Failed > 0 ? Loc.F("Не дошли: {0}", st.Failed) : null,
            Loc.F("Трафик: ↑ {0} · ↓ {1}", Bytes(st.BytesUp), Bytes(st.BytesDown)),
            st.LastRoute is { } r ? Loc.F("Последнее: {0}", Loc.Msg(r)) : null,
        }.Where(x => x is not null));
    }

    private static string Ago(DateTimeOffset? at)
    {
        if (at is not { } a) return Loc.T("давно");
        var d = DateTimeOffset.Now - a;
        return d.TotalMinutes < 1 ? Loc.T("только что") : d.TotalHours < 1 ? Loc.F("{0} мин назад", (int)d.TotalMinutes) : Loc.F("в {0:HH:mm}", a.LocalDateTime);
    }

    private static string Bytes(long n) => n switch
    {
        < 1024 => Loc.F("{0} Б", n),
        < 1024 * 1024 => Loc.F("{0:0.#} КБ", n / 1024.0),
        < 1024L * 1024 * 1024 => Loc.F("{0:0.#} МБ", n / 1024.0 / 1024),
        _ => Loc.F("{0:0.##} ГБ", n / 1024.0 / 1024 / 1024),
    };

    private static Process[] RivalProcesses() =>
        Process.GetProcesses()
            .Where(p =>
            {
                var n = p.ProcessName.Replace("-", "").Replace("_", "");
                var rival = n.Contains("tgwsproxy", StringComparison.OrdinalIgnoreCase);
                if (!rival) p.Dispose();
                return rival;
            })
            .ToArray();

    private void DetectRival()
    {
        var found = RivalProcesses();
        TgRival = found.Length == 0 ? null
            : Loc.F("Запущен {0}. Тропа делает то же самое внутри службы, держать обе программы незачем: закройте его и уберите из автозапуска.", found[0].ProcessName);
        foreach (var p in found) p.Dispose();
    }

    /// <summary>Пока открыта страница Telegram, раз в 3 секунды подтягиваем счётчики.</summary>
    private void OnTelegramPage(bool visible)
    {
        if (!visible)
        {
            _tgTimer?.Stop();
            return;
        }
        DetectRival();
        ApplyTelegram(_status?.Telegram);
        _tgTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) =>
        {
            if (_client is null || _tgBusy || Page != Pages.Telegram || _tg is not { Enabled: true }) return;
            try { ApplyTelegram((await _client.CallAsync<StatusReport>("status", ct: _cts.Token)).Telegram); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }, Application.Current.Dispatcher);
        _tgTimer.Start();
    }
}
