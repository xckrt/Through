using System.Net.Sockets;
using System.Security.Cryptography;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Through.Core.Telegram;

namespace Through.Droid;

/// <summary>
/// Прокси Telegram на самом телефоне: тот же TelegramProxy, что в службе Windows. Telegram подключается к
/// 127.0.0.1, трафик уходит через WebSocket веб-версии или Cloudflare. Работает и на мобильном интернете.
/// Служба переднего плана с уведомлением: иначе Android усыпит её через пару минут.
/// </summary>
// объявлена в AndroidManifest.xml: Android 14 требует у specialUse свойство с описанием
[Android.Runtime.Register("com/xckrt/through/TelegramService")]
public sealed class TelegramService : Service
{
    public const string ChannelId = "telegram";
    private const int NotificationId = 1;
    public const int DefaultPort = 1443;

    /// <summary>Работающий прокси (одна копия на приложение).</summary>
    public static TelegramProxy? Proxy { get; private set; }
    public static string? Error { get; private set; }
    public static event Action? Changed;

    public static byte[] Secret(Context ctx)
    {
        var prefs = ctx.GetSharedPreferences("through", FileCreationMode.Private)!;
        var hex = prefs.GetString("tg_secret", null);
        if (hex is { Length: 32 }) return Convert.FromHexString(hex);
        var secret = RandomNumberGenerator.GetBytes(16);
        prefs.Edit()!.PutString("tg_secret", Convert.ToHexString(secret).ToLowerInvariant())!.Apply();
        return secret;
    }

    public static string? Link(Context ctx) => Proxy is { } p ? TelegramProxy.LinkFor("127.0.0.1", p.Port, Secret(ctx)) : null;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartForegroundWithNotification();
        if (Proxy is null)
        {
            var secret = Secret(this);
            foreach (var port in Enumerable.Range(DefaultPort, 20))
            {
                var proxy = new TelegramProxy(new TelegramProxyOptions { Secret = secret, Port = port, PoolSize = 2 });
                try
                {
                    proxy.Start();
                    Proxy = proxy;
                    Error = null;
                    break;
                }
                catch (SocketException ex)
                {
                    _ = proxy.DisposeAsync().AsTask();
                    Error = ex.Message;
                }
            }
        }
        Changed?.Invoke();
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (Proxy is { } p)
        {
            Proxy = null;
            _ = p.DisposeAsync().AsTask();
        }
        Changed?.Invoke();
        base.OnDestroy();
    }

    private void StartForegroundWithNotification()
    {
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            nm.CreateNotificationChannel(new NotificationChannel(ChannelId, GetString(Resource.String.channel_telegram), NotificationImportance.Low));

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
#pragma warning disable CA1422 // Builder без канала — только для Android 7
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(this, ChannelId) : new Notification.Builder(this);
#pragma warning restore CA1422
        var n = builder
            .SetSmallIcon(Resource.Drawable.ic_notify)
            .SetContentTitle(GetString(Resource.String.notify_title))
            .SetContentText(GetString(Resource.String.notify_text))
            .SetContentIntent(open)
            .SetOngoing(true)
            .Build();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, n, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, n);
    }

    public static void Start(Context ctx)
    {
        var i = new Intent(ctx, typeof(TelegramService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) ctx.StartForegroundService(i);
        else ctx.StartService(i);
    }

    public static void Stop(Context ctx) => ctx.StopService(new Intent(ctx, typeof(TelegramService)));
}
