using System.Security.Cryptography;
using Through.Core.Telegram;

/// <summary>--telegram: поднять прокси на свободном порту и спросить каждый DC через него. Ничего не ставит.</summary>
internal static class TelegramCommand
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var secret = RandomNumberGenerator.GetBytes(16);
        await using var proxy = new TelegramProxy(new TelegramProxyOptions { Secret = secret, Port = 0 });
        proxy.Start();
        Console.WriteLine($"прокси на 127.0.0.1:{proxy.Port}, прогрев пула…");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var failed = 0;
        foreach (var dc in new[] { 1, 2, 3, 4, 5 })
        {
            var r = await TelegramProbe.CheckAsync("127.0.0.1", proxy.Port, secret, dc, TimeSpan.FromSeconds(30), ct);
            var route = proxy.Stats().LastRoute; // без MarkSelfCheck: здесь как раз нужен путь проверки
            Console.WriteLine(r.Ok ? $"  DC{dc}  OK  {r.ElapsedMs} мс  ({route})" : $"  DC{dc}  нет  {r.Error}");
            if (!r.Ok) failed++;
        }
        if (proxy.Stats().LastError is { } err) Console.WriteLine($"последняя ошибка: {err}");
        return failed == 0 ? 0 : 1;
    }
}
