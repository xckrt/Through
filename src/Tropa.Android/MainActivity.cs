using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using Tropa.Core.Probing;
using Tropa.Core.Telegram;
using Uri = Android.Net.Uri;

namespace Tropa.Droid;

/// <summary>
/// Главный экран: большая кнопка прокси Telegram (как на компьютере — тропа по кругу, пока работает),
/// проверка «что режут здесь» тем же пробником по этапам и подсказки, как подключиться через компьютер
/// и включить частный DNS (Android не даёт программам менять их самим).
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private ImageView _ring = null!;
    private View _core = null!;
    private TextView _tgStatus = null!, _tgDetail = null!, _checkResult = null!, _dcResult = null!;
    private Button _tgConnect = null!, _tgCheck = null!, _check = null!;
    private ObjectAnimator? _spin;
    private bool _checking;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);
        _ring = FindViewById<ImageView>(Resource.Id.orb_ring)!;
        _core = FindViewById(Resource.Id.orb_core)!;
        _tgStatus = FindViewById<TextView>(Resource.Id.tg_status)!;
        _tgDetail = FindViewById<TextView>(Resource.Id.tg_detail)!;
        _tgConnect = FindViewById<Button>(Resource.Id.tg_connect)!;
        _tgCheck = FindViewById<Button>(Resource.Id.tg_check)!;
        _dcResult = FindViewById<TextView>(Resource.Id.tg_dc)!;
        _check = FindViewById<Button>(Resource.Id.check)!;
        _checkResult = FindViewById<TextView>(Resource.Id.check_result)!;
        FindViewById<TextView>(Resource.Id.version)!.Text = $"{GetString(Resource.String.app_name)} {Tropa.Core.TropaBuild.VersionText} · github.com/xckrt/Tropa";

        _core.Click += (_, _) => ToggleTelegram();
        _tgConnect.Click += (_, _) => OpenTelegram();
        _tgCheck.Click += async (_, _) => await CheckDatacentersAsync();
        _check.Click += async (_, _) => await CheckServicesAsync();
        FindViewById<Button>(Resource.Id.wifi_settings)!.Click += (_, _) => StartActivity(new Intent(Android.Provider.Settings.ActionWifiSettings));
        FindViewById<Button>(Resource.Id.dns_settings)!.Click += (_, _) => OpenPrivateDns();

        TelegramService.Changed += OnServiceChanged;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        Render();
    }

    protected override void OnDestroy()
    {
        TelegramService.Changed -= OnServiceChanged;
        base.OnDestroy();
    }

    private void OnServiceChanged() => RunOnUiThread(Render);

    private void ToggleTelegram()
    {
        Press(_core);
        if (TelegramService.Proxy is null) TelegramService.Start(this);
        else TelegramService.Stop(this);
    }

    private void OpenTelegram()
    {
        if (TelegramService.Link(this) is not { } link) return;
        try { StartActivity(new Intent(Intent.ActionView, Uri.Parse(link))); }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, Resource.String.no_telegram, ToastLength.Long)!.Show();
        }
    }

    private void Render()
    {
        var on = TelegramService.Proxy is not null;
        _tgStatus.SetText(on ? Resource.String.tg_on : Resource.String.tg_off);
        _tgStatus.SetTextColor(new Android.Graphics.Color(GetColor(on ? Resource.Color.pine : Resource.Color.ink_soft)));
        _tgDetail.Text = on
            ? GetString(Resource.String.tg_on_detail, Java.Lang.Integer.ValueOf(TelegramService.Proxy!.Port))
            : TelegramService.Error is { } e ? GetString(Resource.String.tg_error, new Java.Lang.String(e)) : GetString(Resource.String.tg_off_detail);
        _tgConnect.Visibility = _tgCheck.Visibility = on ? ViewStates.Visible : ViewStates.Gone;
        if (!on) _dcResult.Visibility = ViewStates.Gone;
        _core.SetBackgroundResource(on ? Resource.Drawable.orb_core_on : Resource.Drawable.orb_core_off);
        _ring.Alpha = on ? 1f : 0.35f;
        if (on && _spin is null)
        {
            // тропа идёт по кругу, пока прокси работает
            _spin = ObjectAnimator.OfFloat(_ring, "rotation", 0f, 360f)!;
            _spin.SetDuration(26000);
            _spin.RepeatCount = ValueAnimator.Infinite;
            _spin.SetInterpolator(new LinearInterpolator());
            _spin.Start();
        }
        else if (!on && _spin is not null)
        {
            _spin.Cancel();
            _spin = null;
        }
    }

    private static void Press(View v)
    {
        v.Animate()!.ScaleX(0.92f).ScaleY(0.92f).SetDuration(90).WithEndAction(new Java.Lang.Runnable(() =>
            v.Animate()!.ScaleX(1f).ScaleY(1f).SetDuration(220).SetInterpolator(new OvershootInterpolator(2f)).Start())).Start();
    }

    /// <summary>Тот же самотест, что на компьютере: req_pq_multi в каждый DC через свой прокси.</summary>
    private async Task CheckDatacentersAsync()
    {
        if (TelegramService.Proxy is not { } proxy || _checking) return;
        _checking = true;
        _dcResult.Visibility = ViewStates.Visible;
        _dcResult.SetText(Resource.String.checking);
        try
        {
            var secret = TelegramService.Secret(this);
            var results = await Task.WhenAll(new[] { 1, 2, 3, 4, 5 }.Select(dc =>
                TelegramProbe.CheckAsync("127.0.0.1", proxy.Port, secret, dc, TimeSpan.FromSeconds(25), self: proxy.MarkSelfCheck)));
            _dcResult.Text = string.Join("\n", results.Select(r => r.Ok ? $"DC{r.Dc}  ✓  {r.ElapsedMs} ms" : $"DC{r.Dc}  ✗  {r.Error}"));
        }
        finally { _checking = false; }
    }

    /// <summary>Что режут в этой сети: пробник по этапам, как на компьютере, по targets.json.</summary>
    private async Task CheckServicesAsync()
    {
        if (_checking) return;
        _checking = true;
        _check.Enabled = false;
        _checkResult.Visibility = ViewStates.Visible;
        _checkResult.SetText(Resource.String.checking);
        try
        {
            using var stream = Assets!.Open("targets.json");
            var path = System.IO.Path.Combine(CacheDir!.AbsolutePath, "targets.json");
            await using (var f = File.Create(path)) await stream.CopyToAsync(f);
            var catalog = TargetCatalog.Load(path);
            var results = await new ProbeRunner(new ServiceProber()).RunAsync(catalog.Select(["youtube", "discord"]));
            var lines = new List<string>();
            foreach (var g in results.Where(r => !r.Target.IsControl)
                         .GroupBy(r => catalog.Services.FirstOrDefault(kv => kv.Value.Contains(r.Target)).Key ?? r.Target.Name))
            {
                var required = g.Where(r => !r.Target.Optional).ToList();
                var ok = required.All(r => r.IsOk);
                var bad = required.FirstOrDefault(r => !r.IsOk);
                lines.Add(ok ? $"{ServiceName(g.Key)}  ✓" : $"{ServiceName(g.Key)}  ✗  {Explain(bad!)}");
            }
            if (ProbeRunner.IsNetworkDown(results)) lines = [GetString(Resource.String.no_network)];
            _checkResult.Text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            _checkResult.Text = ex.Message;
        }
        finally
        {
            _checking = false;
            _check.Enabled = true;
        }
    }

    private static string ServiceName(string key) => key switch { "youtube" => "YouTube", "discord" => "Discord", _ => key };

    private string Explain(ProbeResult r) => r.Failure switch
    {
        Failure.TlsReset or Failure.TlsTimeout => GetString(Resource.String.f_sni),
        Failure.TransferStalled => GetString(Resource.String.f_freeze),
        Failure.DnsBogus => GetString(Resource.String.f_dns),
        Failure.TcpTimeout or Failure.TcpReset or Failure.TcpRefused or Failure.TcpUnreachable => GetString(Resource.String.f_address),
        Failure.UdpBlocked => GetString(Resource.String.f_voice),
        _ => GetString(Resource.String.f_other),
    };

    /// <summary>«Частный DNS» — в настройках сети; прямого экрана у Android нет, открываем ближайший.</summary>
    private void OpenPrivateDns()
    {
        foreach (var action in new[] { "android.settings.PRIVATE_DNS_SETTINGS", Android.Provider.Settings.ActionWirelessSettings })
        {
            try
            {
                StartActivity(new Intent(action));
                return;
            }
            catch (ActivityNotFoundException) { }
        }
    }
}
