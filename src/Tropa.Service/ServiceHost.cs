using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Tropa.Core.Control;
using Tropa.Core.Engine;
using Tropa.Core.Probing;
using Tropa.Core.Tuning;
using Tropa.Engine;

namespace Tropa.Service;

/// <summary>Собирает службу: каталоги рядом с exe, данные в %ProgramData%\Tropa, канал с ACL, сторож.</summary>
internal sealed class ServiceHost(string? dataDir = null)
{
    public static string DefaultDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tropa");

    /// <summary>Где профиль, настройки и движок: у службы %ProgramData%\Tropa, у переносной версии — папка data рядом.</summary>
    public string DataDir { get; } = dataDir ?? DefaultDataDir;

    private FileLog? _logField;
    private FileLog _log => _logField ??= new(Path.Combine(DataDir, "service.log"));

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        var baseDir = AppContext.BaseDirectory;
        var controller = new TropaController(new ControllerOptions
        {
            Targets = TargetCatalog.Load(Path.Combine(baseDir, "targets.json")),
            Presets = PresetCatalog.Load(Path.Combine(baseDir, "presets.json")),
            Engine = PrepareEngineAsync,
            EngineUpdate = FindUpdateAsync,
            EngineAccepted = AcceptEngine,
            DataDir = DataDir,
            DnsSwitch = new WindowsDnsSwitch(DataDir, _log),
            Firewall = Firewall,
            Network = WindowsNetworkIdentity.CurrentAsync,
            ObserveApp = EtwAppObserver.ObserveAsync,
            MeasureSpeed = ct => YouTubeSpeed.MeasureAsync(ct),
            Community = StrategyBase.FromConfig(Path.Combine(baseDir, "community.json")),
        });

        // смена сети (другой Wi-Fi, телефон вместо роутера): через 5 секунд тишины смотрим, что за сеть
        CancellationTokenSource? settle = null;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            settle?.Cancel();
            var mine = settle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), mine.Token);
                    await controller.NetworkChangedAsync(mine.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.Write($"смена сети: {ex.Message}"); }
            });
        };

        string? lastMessage = null;
        controller.Notify += (evt, data) =>
        {
            if (evt == "status" && data is StatusReport { Message: { } m } && m != lastMessage)
                _log.Write(lastMessage = m);
            else if (evt == "attempt" && data is AttemptReport a)
                _log.Write($"подбор {a.Service}: {a.PresetId} {(a.Error is null ? a.Score : "ошибка: " + a.Error)}");
        };

        _log.Write("служба запущена");
        try
        {
            await using (controller)
            {
                await controller.StartAsync(ct);
                var first = true;
                var server = new ControlServer(controller, ControlProtocol.PipeName, () =>
                {
                    var pipe = CreatePipe(first);
                    first = false;
                    return pipe;
                });
                await Task.WhenAll(server.RunAsync(ct), controller.RunWatchdogAsync(ct));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write($"служба упала: {ex}");
            throw;
        }
        _log.Write("служба остановлена, winws выключен");
    }

    /// <summary>
    /// Читать и писать в канал может любой вошедший пользователь (окно работает без прав администратора),
    /// создавать экземпляры канала только служба. Первый экземпляр создаётся с FirstPipeInstance:
    /// если кто-то занял имя раньше службы, она не станет работать на чужом канале.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(ControlProtocol.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    private string CurrentManifestPath => Path.Combine(DataDir, "engine-current.json");
    private readonly Dictionary<string, EngineManifest> _pending = [];

    /// <summary>Закреплённый в engine.json релиз; дополнительные файлы и зеркала берутся всегда отсюда.</summary>
    private static EngineManifest Pinned() => EngineManifest.Load(Path.Combine(AppContext.BaseDirectory, "engine.json"));

    /// <summary>Стартуем с последней принятой версии (обновление), а если её нет — с закреплённой.</summary>
    private async Task<EngineContext> PrepareEngineAsync(CancellationToken ct)
    {
        var pinned = Pinned();
        var manifest = pinned;
        if (File.Exists(CurrentManifestPath))
        {
            try
            {
                var current = EngineManifest.Load(CurrentManifestPath);
                current.Validate();
                // extras и зеркала — из новой сборки Тропы, версия и хеши — из принятого обновления
                if (ReleaseResolver.Compare(current.Version, pinned.Version) > 0)
                    manifest = pinned with { Version = current.Version, Sha256 = current.Sha256, WinwsSha256 = current.WinwsSha256 };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                _log.Write($"engine-current.json не прочитался, беру закреплённую версию: {ex.Message}");
            }
        }
        return await ContextFor(manifest, ct);
    }

    private async Task<EngineContext> ContextFor(EngineManifest manifest, CancellationToken ct)
    {
        using var http = Http();
        var install = await new EngineFetcher(http, Path.Combine(DataDir, "engine"))
            .EnsureAsync(manifest, new LogProgress(_log), ct);
        var host = new WinwsEngineHost(new WinwsOptions { ExecutablePath = install.WinwsPath, EngineDir = install.Root });
        _pending[install.Version] = manifest;
        return new EngineContext(new NoForeignWinws(host), install.Version);
    }

    /// <summary>Есть ли на GitHub релиз новее текущего: скачать, проверить и вернуть движок (ещё не принятый).</summary>
    private async Task<EngineContext?> FindUpdateAsync(string currentVersion, CancellationToken ct)
    {
        using var http = Http();
        var resolver = new ReleaseResolver(http);
        var latest = await resolver.LatestTagAsync(ct);
        if (latest is null || ReleaseResolver.Compare(latest, currentVersion) is not > 0) return null;

        _log.Write($"нашёлся zapret {latest}, текущий {currentVersion}");
        var manifest = await resolver.ManifestForAsync(latest, Pinned(), ct);
        return await ContextFor(manifest, ct);
    }

    private void AcceptEngine(EngineContext ctx)
    {
        if (!_pending.TryGetValue(ctx.Version, out var manifest)) return;
        var tmp = CurrentManifestPath + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(new
        {
            version = manifest.Version, sha256 = manifest.Sha256, winwsSha256 = manifest.WinwsSha256,
            urls = manifest.Urls, maxBytes = manifest.MaxBytes,
        }));
        File.Move(tmp, CurrentManifestPath, overwrite: true);
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Tropa/0.1");
        return http;
    }

    /// <summary>Правило «Tropa Telegram proxy»: входящий TCP на порт прокси, только с адресов своей подсети.
    /// Профиль любой: домашний Wi-Fi Windows часто сама помечает «общедоступной», и правило для частных его не открывало.</summary>
    private static async Task Firewall(string rule, int port, bool open, CancellationToken ct)
    {
        await Netsh($"advfirewall firewall delete rule name=\"{rule}\"", ct, ignoreErrors: true);
        if (open)
            await Netsh($"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet profile=any", ct);
    }

    private static async Task Netsh(string args, CancellationToken ct, bool ignoreErrors = false)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 && !ignoreErrors) throw new InvalidOperationException($"netsh {args}: {output.Trim()}");
    }

    /// <summary>Чужой winws (zapret, запущенный вручную) делит с нами WinDivert и путает диагноз: не стартуем рядом.</summary>
    private sealed class NoForeignWinws(IEngineHost inner) : IEngineHost
    {
        public Task<IEngineSession> StartAsync(IReadOnlyList<EngineLaunch> launches, CancellationToken ct = default)
        {
            // свой предыдущий winws к этому моменту уже остановлен, так что любой найденный чужой
            var foreign = Process.GetProcessesByName("winws");
            foreach (var p in foreign) p.Dispose();
            if (foreign.Length > 0)
                throw new InvalidOperationException("уже запущен другой winws (похоже, zapret). Остановите его, и Тропа включит защиту.");
            return inner.StartAsync(launches, ct);
        }
    }

    private sealed class LogProgress(FileLog log) : IProgress<string>
    {
        public void Report(string value) => log.Write(value);
    }
}

/// <summary>Журнал службы: одна строка на событие, не больше ~1 МБ (старое уходит в .1).</summary>
internal sealed class FileLog(string path)
{
    private const long Limit = 1024 * 1024;
    private readonly object _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > Limit)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // журнал не должен ронять службу
            }
        }
    }
}
