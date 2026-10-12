using System.Net;
using Through.Core.Engine;

namespace Through.SelfTest;

/// <summary>Проверка обновлений Сквозь против «GitHub» без сети: редирект на тег и страница ассетов с хешем.</summary>
internal static class AppUpdateTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Обновления Сквозь: новая версия находится вместе с хешем установщика", async () =>
        {
            using var http = new HttpClient(new FakeGitHub("v1.2.0", Assets("v1.2.0", "Through-Setup-1.2.0.exe", Hash)));
            var r = await AppUpdates.CheckAsync(http, new Version(1, 0, 0));
            Assert(r is not null && r.Version == new Version(1, 2, 0) && r.Sha256 == Hash, $"release={r}");
            Assert(r!.SetupUrl == "https://github.com/xckrt/Through/releases/download/v1.2.0/Through-Setup-1.2.0.exe", r.SetupUrl);
        }),

        ("Обновления Сквозь: та же или старшая своя версия — обновления нет", async () =>
        {
            using var http = new HttpClient(new FakeGitHub("v1.0.0", Assets("v1.0.0", "Through-Setup-1.0.0.exe", Hash)));
            Assert(await AppUpdates.CheckAsync(http, new Version(1, 0, 0)) is null, "своя версия не обновление");
            Assert(await AppUpdates.CheckAsync(http, new Version(1, 1, 0)) is null, "старый релиз не обновление");
        }),

        ("Обновления Сквозь: у установщика в релизе нет хеша → такое не ставим", async () =>
        {
            using var http = new HttpClient(new FakeGitHub("v2.0.0", "<li><a href=\"/xckrt/Through/releases/download/v2.0.0/Through-Setup-2.0.0.exe\">x</a></li>"));
            try
            {
                await AppUpdates.CheckAsync(http, new Version(1, 0, 0));
            }
            catch (InvalidDataException) { return; }
            throw new Exception("релиз без хеша принят");
        }),
    ];

    private static string Assets(string tag, string name, string sha) =>
        $"<ul><li><a href=\"/xckrt/Through/releases/download/{tag}/{name}\">{name}</a><span>sha256:{sha}</span></li></ul>";

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    /// <summary>releases/latest «перенаправляет» на тег, expanded_assets отдаёт страницу ассетов.</summary>
    private sealed class FakeGitHub(string latestTag, string assetsHtml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/releases/latest"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // так выглядит ответ после переадресации: конечный адрес — страница тега
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/xckrt/Through/releases/tag/{latestTag}"),
                    Content = new StringContent(""),
                });
            if (url.Contains("/releases/expanded_assets/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(assetsHtml) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
