using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tropa.Core.Probing;

// tropa-probe [--targets targets.json] [--only youtube,discord] [--json]
Console.OutputEncoding = Encoding.UTF8;

string targetsPath = Path.Combine(AppContext.BaseDirectory, "targets.json");
string[]? only = null;
var json = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--targets": targetsPath = args[++i]; break;
        case "--only": only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--json": json = true; break;
        case "-h" or "--help":
            Console.WriteLine("tropa-probe [--targets targets.json] [--only youtube,discord] [--json]");
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

var results = await runner.RunAsync(catalog.Select(only), cts.Token);

if (json)
{
    Console.WriteLine(JsonSerializer.Serialize(results.Select(r => new
    {
        r.Target.Name, r.Target.Host,
        Verdict = r.Verdict.ToString(), Failure = r.Failure.ToString(), FailedAt = r.FailedAt?.ToString(),
        Address = r.Address?.ToString(), r.HttpStatus, r.BodyBytes, r.Kbps,
        ElapsedMs = (int)r.Elapsed.TotalMilliseconds, r.Detail,
    }), new JsonSerializerOptions
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

return results.All(r => r.IsOk) ? 0 : 1;
