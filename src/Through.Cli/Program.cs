using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Through.Core.Probing;
using Through.Core.Tuning;

// through-probe [--targets targets.json] [--only youtube,discord] [--json] [--plan | --tune] [--presets presets.json]
Console.OutputEncoding = Encoding.UTF8;

string targetsPath = Path.Combine(AppContext.BaseDirectory, "targets.json");
string presetsPath = Path.Combine(AppContext.BaseDirectory, "presets.json");
string[]? only = null;
var json = false;
var plan = false;
var tune = false;
var telegram = false;
var dns = false;
var speedTest = false;
var home = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--targets": targetsPath = args[++i]; break;
        case "--only": only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--json": json = true; break;
        case "--plan": plan = true; break;
        case "--tune": tune = true; break;
        case "--telegram": telegram = true; break;
        case "--dns": dns = true; break;
        case "--speed": speedTest = true; break;
        case "--home": home = true; break;
        case "--presets": presetsPath = args[++i]; break;
        case "-h" or "--help":
            Console.WriteLine("through-probe [--targets targets.json] [--only youtube,discord] [--json] [--plan | --tune] [--presets presets.json]");
            Console.WriteLine("  --plan  показать, какие стратегии подборщик стал бы пробовать (ничего не запускает)");
            Console.WriteLine("  --tune  скачать zapret по engine.json и подобрать стратегию (нужны права администратора)");
            Console.WriteLine("  --telegram  проверить, доходит ли Telegram до каждого DC через встроенный прокси");
            Console.WriteLine("  --dns       проверить защищённый DNS (DoH) на свободном порту, систему не трогает");
            return 0;
        default:
            Console.Error.WriteLine($"неизвестный аргумент: {args[i]}");
            return 2;
    }
}

var catalog = TargetCatalog.Load(targetsPath);
var runner = new ProbeRunner(new ServiceProber());
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (home)
    return await HomeCommand.RunAsync(cts.Token);

if (speedTest)
    return await SpeedCommand.RunAsync(cts.Token);

if (dns)
    return await DnsCommand.RunAsync(cts.Token);

if (telegram)
    return await TelegramCommand.RunAsync(cts.Token);

if (tune)
    return await TuneCommand.RunAsync(catalog, presetsPath, only, cts.Token);

var results = await runner.RunAsync(catalog.Select(only), cts.Token);
IReadOnlyList<ServicePlan>? plans = null;
if (plan)
{
    plans = ProbeRunner.IsNetworkDown(results)
        ? []
        : Tuner.Plan(catalog, only, results, PresetCatalog.Load(presetsPath));
}

if (json)
{
    var rows = results.Select(r => new
    {
        r.Target.Name, r.Target.Host,
        Verdict = r.Verdict.ToString(), Failure = r.Failure.ToString(), FailedAt = r.FailedAt?.ToString(),
        Address = r.Address?.ToString(), r.SniFiltered, r.HttpStatus, r.BodyBytes, r.Kbps,
        ElapsedMs = (int)r.Elapsed.TotalMilliseconds, r.Detail,
    });
    // без --plan формат прежний (массив), с --plan объект с результатами и планом
    object payload = plans is null ? rows : new
    {
        Results = rows,
        Plan = plans.Select(p => new
        {
            p.Service, Status = p.Status.ToString(),
            Diagnosis = p.Diagnosis.Where(r => !r.IsOk).Select(r => r.Failure.ToString()).Distinct(),
            Candidates = p.Candidates.Select(c => c.Id),
        }),
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));
}
else
{
    foreach (var r in results)
    {
        var (mark, color) = r.Verdict switch
        {
            Verdict.Ok => ("OK  ", ConsoleColor.Green),
            Verdict.Throttled => ("SLOW", ConsoleColor.Yellow),
            Verdict.Blocked => ("FAIL", ConsoleColor.Red),
            _ => ("??  ", ConsoleColor.DarkGray),
        };
        Console.ForegroundColor = color;
        Console.Write(mark);
        Console.ResetColor();

        var where = r.FailedAt is { } s ? $"[{s}] {r.Failure}" : r.HttpStatus is { } code ? $"HTTP {code}" : "";
        var speed = r.Kbps is { } k && r.Target.MinBytes > 0 ? $" {k:F0} кбит/с" : "";
        Console.WriteLine($"  {r.Target.Name,-18} {where,-26} {(int)r.Elapsed.TotalMilliseconds,5} мс{speed}");
        if (r.Detail is not null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"      {r.Detail}");
            Console.ResetColor();
        }
    }
}

if (plans is not null && !json)
{
    Console.WriteLine();
    if (ProbeRunner.IsNetworkDown(results))
        Console.WriteLine("План подбора: нет сети, подбирать нечего");
    else
        Console.WriteLine("План подбора:");
    foreach (var p in plans)
    {
        var failures = string.Join(", ", p.Diagnosis.Where(r => !r.IsOk).Select(r => r.Failure).Distinct());
        var what = p.Status switch
        {
            PlanStatus.Ok => "работает без обхода",
            PlanStatus.NotTreatable => $"{failures}: режут по адресу, настройка DPI-обработки не поможет",
            PlanStatus.NoCandidates => $"{failures}: нет подходящих стратегий",
            _ => $"{failures} → {string.Join(", ", p.Candidates.Select(c => c.Id))}",
        };
        Console.WriteLine($"  {p.Service,-12} {what}");
    }
}

return results.All(r => r.IsOk) ? 0 : 1;
