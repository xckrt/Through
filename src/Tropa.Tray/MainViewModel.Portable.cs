using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Tropa.Tray;

/// <summary>
/// Переносная версия (zip с файлом-меткой «portable» рядом с Tropa.exe): без службы и установки.
/// Всё, что меняет сеть, делает Tropa.Service.exe portable — тот же хост службы, запущенный с правами
/// администратора и данными в папке data рядом. Он живёт, пока открыто окно, и при закрытии выключает winws
/// и возвращает DNS.
/// </summary>
public sealed partial class MainViewModel
{
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

    /// <summary>Автозапуск и удаление службы в переносной версии не нужны.</summary>
    public bool NotPortable => !IsPortable;

    private async Task StartPortableHostAsync()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Tropa.Service.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException("Рядом с программой нет Tropa.Service.exe. Распакуйте архив целиком.");
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"portable {Environment.ProcessId} \"{data}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Без разрешения администратора обход не включить: winws работает с сетевым драйвером.");
        }

        for (var i = 0; i < 30; i++)
        {
            if (await ConnectAsync(TimeSpan.FromMilliseconds(500)) is not null) return;
            await Task.Delay(500, _cts.Token);
        }
        throw new InvalidOperationException(@"Фоновый процесс Тропы не ответил. Подробности в data\service.log рядом с программой.");
    }
}
