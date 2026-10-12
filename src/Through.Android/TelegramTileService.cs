using Android.Content;
using Android.Graphics.Drawables;
using Android.Service.QuickSettings;

namespace Through.Droid;

/// <summary>
/// Плитка в шторке быстрых настроек: включить или выключить прокси Telegram в одно касание, не открывая
/// приложение. Пока шторка открыта, плитка сама обновляется вслед за службой.
/// </summary>
[Service(Name = "com.xckrt.through.TelegramTileService", Exported = true, Icon = "@drawable/ic_notify",
    Label = "@string/tile_label", Permission = "android.permission.BIND_QUICK_SETTINGS_TILE")]
[IntentFilter([ActionQsTile])]
public sealed class TelegramTileService : TileService
{
    private const string ActionQsTile = "android.service.quicksettings.action.QS_TILE";

    public override void OnStartListening()
    {
        base.OnStartListening();
        TelegramService.Changed += Refresh;
        Refresh();
    }

    public override void OnStopListening()
    {
        TelegramService.Changed -= Refresh;
        base.OnStopListening();
    }

    public override void OnClick()
    {
        if (TelegramService.Proxy is not null)
        {
            TelegramService.Stop(this);
        }
        else
        {
            // запуск службы переднего плана из шторки часть прошивок запрещает — тогда открываем приложение,
            // оно запустит прокси из видимого окна
            try
            {
                TelegramService.Start(this);
            }
            catch (Java.Lang.Exception)
            {
                OpenApp();
                return;
            }
        }
        Refresh();
    }

    private void OpenApp()
    {
        var intent = new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask);
#pragma warning disable CA1416 // StartActivityAndCollapse(Intent) — только до Android 14
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartActivityAndCollapse(PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable)!);
        else
            StartActivityAndCollapse(intent);
#pragma warning restore CA1416
    }

    private void Refresh()
    {
        if (QsTile is not { } tile) return;
        var on = TelegramService.Proxy is not null;
        tile.State = on ? TileState.Active : TileState.Inactive;
        tile.Label = GetString(Resource.String.tile_label);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            tile.Subtitle = GetString(on ? Resource.String.tg_on : Resource.String.tg_off);
        tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.ic_notify);
        tile.UpdateTile();
    }
}
