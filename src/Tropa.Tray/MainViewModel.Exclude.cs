using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Tropa.Core.Control;

namespace Tropa.Tray;

/// <summary>«Не трогать»: сайты, которые обход ломает (банк, госуслуги), — вне любого профиля winws.</summary>
public sealed partial class MainViewModel
{
    private string _excludeInput = "";

    public ObservableCollection<string> ExcludedSites { get; } = [];
    public bool HasExcluded => ExcludedSites.Count > 0;
    public string ExcludeInput { get => _excludeInput; set => Set(ref _excludeInput, value); }

    public ICommand AddExcludeCommand => _addExclude ??= new RelayCommand(async () =>
    {
        if (ExcludeInput.Trim().Length == 0) return;
        if (await ExcludeCallAsync("exclude.add", ExcludeInput)) ExcludeInput = "";
    }, () => !IsRunning);
    private ICommand? _addExclude;

    public ICommand RemoveExcludeCommand => _removeExclude ??= new ParamCommand(async p =>
    {
        if (p is string site) await ExcludeCallAsync("exclude.remove", site);
    }, () => !IsRunning && _client is not null);
    private ICommand? _removeExclude;

    /// <summary>Из «Почему не открывается?»: сайт ломается из-за обхода — убрать его из обхода.</summary>
    public ICommand SiteExcludeCommand => _siteExclude ??= new RelayCommand(async () =>
    {
        if (_site is { } s) await ExcludeCallAsync("exclude.add", s.Domain);
    }, () => !IsRunning && _site is not null);
    private ICommand? _siteExclude;

    public string SiteExcludeLabel => _site is null ? "" : Loc.F("Не трогать {0}", _site.Domain);

    private async Task<bool> ExcludeCallAsync(string method, string site)
    {
        try
        {
            await EnsureServiceAsync();
            ApplyStatus(await _client!.CallAsync<StatusReport>(method, new { site }, _cts.Token));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log(ex.Message, error: true);
            return false;
        }
    }

    private void SyncExcluded(StatusReport s)
    {
        if (ExcludedSites.SequenceEqual(s.Excluded)) return;
        ExcludedSites.Clear();
        foreach (var x in s.Excluded) ExcludedSites.Add(x);
        Raise(nameof(HasExcluded));
    }
}
