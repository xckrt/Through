using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Tropa.Core.Engine;
using Tropa.Core.Probing;
using Tropa.Core.Tuning;
using Tropa.Engine;

namespace Tropa.SelfTest;

/// <summary>Fetcher против фейкового HTTP и WinwsEngineHost против фейкового winws (эта же программа).</summary>
internal static class EngineTests
{
    public const string FakeWinwsFlag = "--fake-winws";

    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Fetcher: скачал, хеш совпал, распаковал; второй раз без сети", () => InSandbox(async dir =>
        {
            var zip = Zip(("zapret-v1/binaries/windows-x86_64/winws.exe", "MZ"), ("zapret-v1/files/fake/tls.bin", "x"));
            var http = new FakeHttp { ["https://a.test/zapret-v1.zip"] = zip };
            var fetcher = new EngineFetcher(new HttpClient(http), dir);

            var install = await fetcher.EnsureAsync(Manifest(zip, "https://a.test/zapret-{version}.zip"));
            Assert(File.Exists(install.WinwsPath) && install.WinwsPath.Contains("x86_64"), install.WinwsPath);
            Assert(Directory.Exists(Path.Combine(install.Root, "files")), $"корень релиза без files/: {install.Root}");

            await fetcher.EnsureAsync(Manifest(zip, "https://a.test/zapret-{version}.zip"));
            Assert(http.Requests == 1, $"повторная установка не должна качать: запросов {http.Requests}");
        })),

        ("Fetcher: подменённый архив на первом зеркале, честный на втором", () => InSandbox(async dir =>
        {
            var good = Zip(("zapret-v1/binaries/windows-x86_64/winws.exe", "MZ"));
            var evil = Zip(("zapret-v1/binaries/windows-x86_64/winws.exe", "EVIL"));
            var http = new FakeHttp { ["https://a.test/z.zip"] = evil, ["https://b.test/z.zip"] = good };
            var install = await new EngineFetcher(new HttpClient(http), dir)
                .EnsureAsync(Manifest(good, "https://a.test/z.zip", "https://b.test/z.zip"));
            Assert(File.ReadAllText(install.WinwsPath) == "MZ", "установлен подменённый winws");
            Assert(http.Requests == 2, $"запросов {http.Requests}");
        })),

        ("Fetcher: все зеркала плохие → ошибка со всеми адресами, ничего не установлено", () => InSandbox(async dir =>
        {
            var good = Zip(("w/winws.exe", "MZ"));
            var http = new FakeHttp { ["https://a.test/z.zip"] = Zip(("w/winws.exe", "EVIL")) }; // b.test → 404
            var err = await Throws(() => new EngineFetcher(new HttpClient(http), dir)
                .EnsureAsync(Manifest(good, "https://a.test/z.zip", "https://b.test/z.zip")));
            Assert(err.Contains("a.test") && err.Contains("SHA-256 не совпал") && err.Contains("b.test"), err);
            Assert(!Directory.Exists(Path.Combine(dir, "v1")), "после провала не должно остаться установки");
        })),

        ("Fetcher: без SHA-256 в engine.json не качает вообще", () => InSandbox(async dir =>
        {
            var http = new FakeHttp();
            var err = await Throws(() => new EngineFetcher(new HttpClient(http), dir)
                .EnsureAsync(new EngineManifest { Version = "v1", Urls = ["https://a.test/z.zip"] }));
            Assert(err.Contains("SHA-256") && http.Requests == 0, $"{err} (запросов {http.Requests})");
        })),

        ("Fetcher: ../ в архиве → отказ, за пределы папки ничего не пишется", () => InSandbox(async dir =>
        {
            var zip = Zip(("w/winws.exe", "MZ"), ("../../evil.txt", "pwned"));
            var http = new FakeHttp { ["https://a.test/z.zip"] = zip };
            await Throws(() => new EngineFetcher(new HttpClient(http), Path.Combine(dir, "a", "b"))
                .EnsureAsync(Manifest(zip, "https://a.test/z.zip")));
            var leaked = Directory.EnumerateFiles(dir, "evil.txt", SearchOption.AllDirectories).ToList();
            Assert(leaked.Count == 0, $"файл вылез наружу: {string.Join(", ", leaked)}");
        })),

        ("Fetcher: архив больше потолка → отказ", () => InSandbox(async dir =>
        {
            var zip = Zip(("w/winws.exe", new string('x', 4096)));
            var http = new FakeHttp { ["https://a.test/z.zip"] = zip };
            var err = await Throws(() => new EngineFetcher(new HttpClient(http), dir)
                .EnsureAsync(Manifest(zip, "https://a.test/z.zip") with { MaxBytes = 100 }));
            Assert(err.Contains("больше"), err);
        })),

        ("Fetcher: в архиве нет winws.exe → отказ", () => InSandbox(async dir =>
        {
            var zip = Zip(("readme.txt", "hi"));
            var http = new FakeHttp { ["https://a.test/z.zip"] = zip };
            var err = await Throws(() => new EngineFetcher(new HttpClient(http), dir)
                .EnsureAsync(Manifest(zip, "https://a.test/z.zip")));
            Assert(err.Contains("winws.exe"), err);
        })),

        ("engine.json из репозитория: пустой хеш, качать не даст", () =>
        {
            var m = EngineManifest.Load(Path.Combine(AppContext.BaseDirectory, "engine.json"));
            Assert(m.Urls.All(u => u.StartsWith("https://")), "только https");
            try { m.Validate(); }
            catch (InvalidDataException) { return Task.CompletedTask; }
            Assert(m.Sha256.Length == 64, "если хеш заполнен, он должен быть полным SHA-256");
            return Task.CompletedTask;
        }),

        ("WinwsEngineHost: ждёт готовности, отдаёт аргументы и hostlist, убивает при остановке", () => InSandbox(async dir =>
        {
            var host = Host("ready", engineDir: dir);
            var session = (WinwsSession)await host.StartAsync(Launch());
            await Task.Delay(200); // эхо аргументов приходит сразу за маркером
            var log = session.Log;
            Assert(log.Contains("capture is started"), log);
            Assert(log.Contains("--wf-tcp=443") && log.Contains($"--fake-tls={dir}"), $"аргументы не раскрыты: {log}");
            Assert(log.Contains("hosts: www.youtube.com,i.ytimg.com"), $"hostlist не дошёл: {log}");
            Assert(!session.HasExited, "winws должен работать");

            await session.DisposeAsync();
            Assert(session.HasExited, "после остановки процесс должен быть мёртв");
            Assert(!File.Exists(session.HostlistPath), "временный hostlist не удалён");
        })),

        ("WinwsEngineHost: winws упал на старте → ошибка с его выводом", async () =>
        {
            var err = await Throws(() => Host("crash").StartAsync(Launch()));
            Assert(err.Contains("кодом 3") && err.Contains("unknown option"), err);
        }),

        ("WinwsEngineHost: молчит → таймаут готовности", async () =>
        {
            var err = await Throws(() => Host("silent", ready: TimeSpan.FromMilliseconds(800)).StartAsync(Launch()));
            Assert(err.Contains("не сообщил о готовности"), err);
        }),

        ("Tuner + WinwsEngineHost: упавший winws не останавливает подбор", async () =>
        {
            // диагноз TlsReset лечится пресетом x, но winws падает: попытка с ошибкой, итог «не починили»
            var catalog = new PresetCatalog { Presets = [new StrategyPreset { Id = "x", Treats = [Failure.TlsReset], Args = ["--hostlist={hostlist}"] }] };
            await using var srv = FakeServer.SniFilter("example.test", reset: true);
            var targets = new TargetCatalog { Services = new() { ["svc"] = [new ProbeTarget { Name = "svc", Host = "example.test", Port = srv.Port }] } };
            var opts = new ProbeOptions
            {
                Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
                AllowPrivateAddresses = true, ValidateCertificate = false,
            };
            var s = (await new Tuner(Host("crash"), opts).TuneAsync(targets, catalog)).Services.Single();
            Assert(s.Outcome == TuneOutcome.NoFix && s.Attempts.Single().Error?.Contains("unknown option") == true,
                $"outcome={s.Outcome} error={s.Attempts.SingleOrDefault()?.Error}");
        }),
    ];

    /// <summary>Режим «фейковый winws»: SelfTest запускает сама себя с этим флагом.</summary>
    public static async Task<int> FakeWinwsAsync(string mode, string[] args)
    {
        switch (mode)
        {
            case "crash":
                Console.Error.WriteLine("winws: unknown option --bogus");
                return 3;
            case "silent":
                await Task.Delay(Timeout.Infinite);
                return 0;
            default:
                Console.WriteLine("windivert initialized. capture is started.");
                Console.WriteLine("args: " + string.Join(" ", args));
                var hostlist = args.FirstOrDefault(a => a.StartsWith("--hostlist="))?["--hostlist=".Length..];
                if (hostlist is not null) Console.WriteLine("hosts: " + string.Join(",", File.ReadAllLines(hostlist)));
                Console.Out.Flush();
                await Task.Delay(Timeout.Infinite);
                return 0;
        }
    }

    private static WinwsEngineHost Host(string mode, TimeSpan? ready = null, string? engineDir = null)
    {
        // при `dotnet Tropa.SelfTest.dll` ProcessPath указывает на dotnet: тогда передаём сборку первым аргументом
        var self = Environment.ProcessPath!;
        string[] prefix = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetExecutingAssembly().Location, FakeWinwsFlag, mode]
            : [FakeWinwsFlag, mode];
        return new WinwsEngineHost(new WinwsOptions
        {
            ExecutablePath = self,
            EngineDir = engineDir ?? Path.GetTempPath(),
            PrefixArgs = prefix,
            ReadyTimeout = ready ?? TimeSpan.FromSeconds(10),
        });
    }

    private static EngineLaunch Launch() => new(
        new StrategyPreset
        {
            Id = "t", Treats = [Failure.TlsReset],
            Args = ["--wf-tcp={ports}", "--hostlist={hostlist}", "--fake-tls={engineDir}/files/fake/tls.bin"],
        },
        ["www.youtube.com", "i.ytimg.com"], [443]);

    private static EngineManifest Manifest(byte[] zip, params string[] urls) => new()
    {
        Version = "v1",
        Sha256 = Convert.ToHexString(SHA256.HashData(zip)),
        Urls = urls,
    };

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static async Task InSandbox(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tropa-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try { await body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static async Task<string> Throws(Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { return ex.Message; }
        throw new Exception("ожидали ошибку, а всё прошло");
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    /// <summary>HTTP без сети: адрес → тело, всё остальное 404.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = [];
        private int _requests;

        public int Requests => _requests;
        public byte[] this[string url] { set => _files[url] = value; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(_files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
