using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Through.Setup;

public sealed record SetupOptions(bool DesktopShortcut, bool Autostart, bool LaunchAfter);

/// <summary>
/// Установка, обновление и удаление Сквозь. Файлы — в Program Files (туда без прав администратора не записать,
/// так что exe службы от SYSTEM не подменить), служба ставится её же командой install, остальное — ярлыки
/// и запись в «Приложения и возможности». Всё пишется в журнал установки.
/// </summary>
public sealed class SetupEngine(Action<int, string> progress)
{
    public const string ServiceName = "Through";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Through";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Through");

    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "Through-Setup.log");

    private static string StartMenuLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Сквозь.lnk");

    private static string DesktopLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Сквозь.lnk");

    public static string Version =>
        typeof(SetupEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    public static bool HasPayload => typeof(SetupEngine).Assembly.GetManifestResourceNames().Contains("payload.zip");

    /// <summary>Путь к самому установщику (Environment.ProcessPath в .NET Framework нет).</summary>
    private static string SelfPath => Process.GetCurrentProcess().MainModule!.FileName;

    public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, "Through.exe"));

    // ---------- установка и обновление

    public void Install(SetupOptions o)
    {
        Log($"установка Сквозь {Version} в {InstallDir}");
        if (!HasPayload) throw new InvalidOperationException("в установщике нет файлов программы (payload.zip): соберите его через build/publish.ps1");

        Step(0, "Останавливаю прежнюю версию");
        StopService();
        StopWindows();

        Step(1, "Копирую файлы");
        ExtractPayload();
        CopySelf();

        Step(2, "Ставлю службу");
        Run(Path.Combine(InstallDir, "Through.Service.exe"), "install");

        Step(3, "Создаю ярлыки");
        Shortcut(StartMenuLink);
        if (o.DesktopShortcut) Shortcut(DesktopLink);
        else TryDelete(DesktopLink);
        SetAutostart(o.Autostart);
        RegisterUninstall();

        Step(4, "Готово");
        if (o.LaunchAfter) LaunchThrough();
        Log("установка закончена");
    }

    // ---------- удаление

    public void Uninstall(bool removeData)
    {
        Log($"удаление Сквозь из {InstallDir}");
        Step(0, "Останавливаю службу и winws");
        StopService();
        StopWindows();
        if (ServiceExists()) RunQuiet("sc.exe", $"delete {ServiceName}");

        Step(1, "Убираю ярлыки и автозапуск");
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        SetAutostart(false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        Step(2, "Удаляю файлы");
        TryDeleteDir(InstallDir);

        if (removeData)
        {
            Step(3, "Удаляю настройки и журнал");
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Through"));
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Through"));
        }
        Step(4, "Готово");
        Log("удаление закончено");
    }

    /// <summary>Удалить файлы из Program Files нельзя, пока установщик запущен оттуда: перезапускаемся из TEMP.</summary>
    public static bool RelaunchFromTempForUninstall(string[] args)
    {
        var self = SelfPath;
        if (!self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) return false;
        var temp = Path.Combine(Path.GetTempPath(), $"Through-Uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(self, temp, overwrite: true);
        Process.Start(new ProcessStartInfo(temp, string.Join(" ", args)) { UseShellExecute = false })?.Dispose();
        return true;
    }

    // ---------- шаги

    private void Step(int index, string text)
    {
        Log(text);
        progress(index, text);
    }

    private static bool ServiceExists() =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Служба держит свои файлы и winws: останавливаем до копирования. Остановка выключает и winws.</summary>
    private static void StopService()
    {
        if (!ServiceExists()) return;
        using var sc = new ServiceController(ServiceName);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        Log("останавливаю службу");
        if (sc.Status is not ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    /// <summary>Окно Сквозь (часто живёт в трее) держит DLL. При обновлении его закрывает само окно, тут — страховка.</summary>
    private static void StopWindows()
    {
        foreach (var p in Process.GetProcessesByName("Through"))
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    if (!path.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                    Log($"закрываю окно Сквозь (pid {p.Id})");
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000)) p.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // уже закрылось или чужой процесс с тем же именем
                }
            }
        }
    }

    private static void ExtractPayload()
    {
        using var stream = typeof(SetupEngine).Assembly.GetManifestResourceStream("payload.zip")!;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var root = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"в пакете странный путь: {entry.FullName}");
            if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        Log($"распаковано файлов: {zip.Entries.Count}");
    }

    /// <summary>Копия установщика рядом с программой: ей «Приложения и возможности» удаляют Сквозь.</summary>
    private static void CopySelf()
    {
        var self = SelfPath;
        var target = Path.Combine(InstallDir, "Through.Setup.exe");
        if (!self.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(self, target, overwrite: true);
    }

    private static void Shortcut(string path)
    {
        var exe = Path.Combine(InstallDir, "Through.exe");
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic lnk = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path })!;
        lnk.TargetPath = exe;
        lnk.WorkingDirectory = InstallDir;
        lnk.IconLocation = exe + ",0";
        lnk.Description = "Сквозь: диагностика сети и настройка DPI-обработки";
        lnk.Save();
        Log($"ярлык: {path}");
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Through", $"\"{Path.Combine(InstallDir, "Through.exe")}\" --tray");
        else k.DeleteValue("Through", throwOnMissingValue: false);
    }

    private static void RegisterUninstall()
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        var setup = Path.Combine(InstallDir, "Through.Setup.exe");
        k.SetValue("DisplayName", "Сквозь");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "Through");
        k.SetValue("DisplayIcon", Path.Combine(InstallDir, "Through.exe"));
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{setup}\" /uninstall");
        k.SetValue("QuietUninstallString", $"\"{setup}\" /uninstall /silent");
        k.SetValue("URLInfoAbout", "https://github.com/xckrt/Through");
        k.SetValue("HelpLink", "https://github.com/xckrt/Through/issues");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var kb = Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024L;
        k.SetValue("EstimatedSize", (int)Math.Min(kb, int.MaxValue), RegistryValueKind.DWord);
    }

    /// <summary>Установщик работает с правами администратора, а окно должно запускаться обычным:
    /// запуск через проводник даёт права того, кто вошёл в Windows.</summary>
    public static void LaunchThrough() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(InstallDir, "Through.exe")}\"") { UseShellExecute = false })?.Dispose();

    private static void Run(string exe, string args)
    {
        Log($"> {Path.GetFileName(exe)} {args}");
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} {args}: код {p.ExitCode}. {err.Trim()}");
    }

    private static void RunQuiet(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception ex) { Log($"{exe}: {ex.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"не удалилось {path}: {ex.Message}"); }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"не удалилось {path}: {ex.Message}"); }
    }

    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
