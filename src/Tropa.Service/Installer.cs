using System.Diagnostics;
using System.ServiceProcess;

namespace Tropa.Service;

/// <summary>
/// Установка службы. Окно запускает это с запросом UAC, человек ничего не делает руками.
/// Служба работает от SYSTEM, поэтому её файлы копируются в Program Files: туда без прав администратора
/// не записать, и подменить exe службы обычный пользователь не сможет. Повторный запуск обновляет установку.
/// </summary>
internal static class Installer
{
    public const string Name = "Tropa";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tropa");

    /// <summary>Журнал установки: окно показывает из него причину, если установка не удалась.</summary>
    public static FileLog Log { get; } = new(Path.Combine(ServiceHost.DefaultDataDir, "install.log"));

    public static int Install()
    {
        Directory.CreateDirectory(ServiceHost.DefaultDataDir);
        var exists = Exists();
        if (exists) Stop();

        var source = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(InstallDir).TrimEnd(Path.DirectorySeparatorChar);
        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
            CopyTree(source, target);

        var exe = Path.Combine(target, "Tropa.Service.exe");
        // путь в кавычках: без них Windows поищет C:\Program.exe (классическая дыра «unquoted service path»)
        var binPath = $"\"{exe}\"";
        var code = exists
            ? Sc("config", Name, "binPath=", binPath, "start=", "auto")
            : Sc("create", Name, "binPath=", binPath, "start=", "auto", "DisplayName=", "Тропа");
        if (code != 0) throw new InvalidOperationException($"sc {(exists ? "config" : "create")} вернул {code}");

        Sc("description", Name, "Диагностика сети и настройка DPI-обработки");
        // упала — перезапустить через 5 с, счётчик сбоев обнуляется раз в сутки
        Sc("failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
        Sc("failureflag", Name, "1"); // перезапуск и при остановке с кодом ошибки, а не только при падении процесса

        using var sc = new ServiceController(Name);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        Log.Write($"служба установлена в {target} и запущена");
        return 0;
    }

    public static int Uninstall()
    {
        if (!Exists()) return 0;
        Stop();
        // служба при остановке сама возвращает DNS; если она до этого упала, вернём по сохранённому файлу
        try { new WindowsDnsSwitch(ServiceHost.DefaultDataDir, Log).RestoreAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { Log.Write($"DNS не вернулся: {ex.Message}"); }
        var code = Sc("delete", Name);
        if (code != 0) throw new InvalidOperationException($"sc delete вернул {code}");
        Log.Write("служба удалена");
        return 0;
    }

    private static bool Exists() =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(Name, StringComparison.OrdinalIgnoreCase));

    private static void Stop()
    {
        using var sc = new ServiceController(Name);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        if (sc.Status is not ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    /// <summary>Копия с заменой. Файл, занятый окном Тропы, запущенным из Program Files, пропускается, если он тот же.</summary>
    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            try
            {
                File.Copy(file, dest, overwrite: true);
            }
            catch (IOException) when (Same(file, dest))
            {
            }
        }

        static bool Same(string a, string b) =>
            File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length
                           && File.GetLastWriteTimeUtc(a) == File.GetLastWriteTimeUtc(b);
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) Log.Write($"sc {string.Join(' ', args)}: {output.Trim()}");
        return p.ExitCode;
    }
}
