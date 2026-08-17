using System.Collections.ObjectModel;

using EvoMods.Core.Filters;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EvoMods.App.Pages;

/// <summary>One filter as the list shows it.</summary>
public sealed class FilterRowView
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Chip { get; init; }
    public required Brush ChipForeground { get; init; }
    public required Brush ChipBackground { get; init; }
}

/// <summary>
/// Everything registered in <c>post_processing.table</c>, and the two things we can do about it.
/// </summary>
/// <remarks>
/// Code-behind with an ObservableCollection, matching the rest of the app. There is no selection:
/// both buttons act on a whole set, which is what keeps a screen about a 35-row registry down to a
/// list and two controls.
/// </remarks>
public sealed partial class FiltersPage : Page
{
    private readonly EmbeddedFilterBundle _bundle = new();
    private readonly ObservableCollection<FilterRowView> _ours = [];
    private readonly ObservableCollection<FilterRowView> _stock = [];
    private readonly ObservableCollection<FilterRowView> _other = [];

    private FilterSurvey? _survey;
    private string? _gameRoot;

    public FiltersPage()
    {
        InitializeComponent();
        OursList.ItemsSource = _ours;
        StockList.ItemsSource = _stock;
        OtherList.ItemsSource = _other;
        Loaded += (_, _) => Refresh();
    }

    // ---- reading

    private void Refresh()
    {
        _gameRoot = AppInfo.GameRoot;
        if (_gameRoot is null)
        {
            Blocked("No Assetto Corsa EVO install found. Nothing here can run without one.");
            return;
        }

        _survey = new FilterInstaller(_gameRoot, _ => { }).Survey(_bundle.Filters);
        if (_survey.Filters.Count == 0)
        {
            Blocked($"post_processing.table could not be read — {_survey.StockSource}");
            return;
        }

        SourceText.Text = _survey.StockAvailable
            ? $"Compared against {_survey.StockSource}"
            : $"Working from {_survey.StockSource} — no archive to compare against, so a filter the "
              + "game ships visible cannot be told from one shown here.";

        Fill(_ours, _survey.Filters.Where(IsOurs));
        Fill(_stock, _survey.Filters.Where(IsStock));
        Fill(_other, _survey.Filters.Where(f => f.State == FilterState.Foreign));
        OtherSection.Visibility = _other.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Notice.IsOpen = false;
        UpdateButtons();
    }

    private bool IsOurs(FilterStatus f) => _bundle.Filters.Any(e => e.Name == f.Name);

    private static bool IsStock(FilterStatus f) =>
        f.State is FilterState.StockShown or FilterState.StockHidden
                or FilterState.StockUnhidden or FilterState.StockSuppressed;

    private void Fill(ObservableCollection<FilterRowView> target, IEnumerable<FilterStatus> source)
    {
        target.Clear();
        foreach (FilterStatus f in source)
            target.Add(View(f));
    }

    private void Blocked(string why)
    {
        _survey = null;
        _ours.Clear();
        _stock.Clear();
        _other.Clear();
        OtherSection.Visibility = Visibility.Collapsed;
        SourceText.Text = "";
        Notice.Title = "Nothing to show";
        Notice.Message = why;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
        UnlockButton.Content = "Unlock hidden filters";
        InstallButton.Content = "Install EvoMods filters";
        UnlockButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
    }

    // ---- what the buttons say

    private void UpdateButtons()
    {
        if (_survey is null)
            return;

        bool anyHidden = _survey.ShippedHidden.Any(f => f.State == FilterState.StockHidden);
        UnlockButton.Content = anyHidden ? "Unlock hidden filters" : "Re-hide them";
        UnlockButton.IsEnabled = _survey.ShippedHidden.Any();

        List<FilterStatus> ours = [.. _survey.Filters.Where(IsOurs)];
        bool anyBroken = ours.Any(f => f.State
            is FilterState.RegisteredButFileMissing or FilterState.FilesPresentButNotRegistered);
        bool allInstalled = ours.Count > 0 && ours.All(f => f.State == FilterState.Installed);

        InstallButton.Content = anyBroken ? "Repair" : allInstalled ? "Remove them" : "Install EvoMods filters";
        InstallButton.IsEnabled = true;
    }

    // ---- acting

    private async void OnUnlock(object sender, RoutedEventArgs e)
    {
        bool unlock = _survey?.ShippedHidden.Any(f => f.State == FilterState.StockHidden) == true;
        await Run(installer => unlock ? installer.ShowShippedHidden() : installer.RestoreShippedHidden(),
            unlock ? "Unlocked {0} filter(s)." : "Hid {0} filter(s) again.",
            unlock ? "nothing to unlock." : "nothing to hide.");
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        List<FilterStatus> ours = [.. _survey?.Filters.Where(IsOurs) ?? []];
        bool remove = ours.Count > 0 && ours.All(f => f.State == FilterState.Installed);

        await Run(installer => remove ? installer.Uninstall(_bundle) : installer.Install(_bundle),
            remove ? "Removed {0} filter(s)." : "Installed {0} filter(s).",
            remove ? "nothing to remove." : "nothing to install.");
    }

    /// <summary>
    /// Run one operation off the UI thread, then re-read from disk rather than trusting what we
    /// think we just did.
    /// </summary>
    private async Task Run(Func<FilterInstaller, int> work, string done, string nothing)
    {
        if (_gameRoot is not { } root)
            return;

        UnlockButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        StatusText.Text = "Working…";
        Notice.IsOpen = false;

        try
        {
            var log = new List<string>();
            int changed = await Task.Run(() => work(new FilterInstaller(root, log.Add)));
            StatusText.Text = changed == 0 ? nothing : string.Format(done, changed);
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Notice.Title = "That didn't work";
            Notice.Message = ex.Message;
            Notice.Severity = InfoBarSeverity.Error;
            Notice.IsOpen = true;
        }

        Refresh();
    }

    // ---- state as the reader sees it

    private FilterRowView View(FilterStatus f)
    {
        (string chip, string fg, string bg) = f.State switch
        {
            FilterState.Installed => ("Installed", "SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush"),
            FilterState.NotInstalled => ("Not installed", "TextFillColorSecondaryBrush", "CardBackgroundFillColorSecondaryBrush"),
            FilterState.FilesPresentButNotRegistered => ("Files present, not registered", "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
            FilterState.RegisteredButFileMissing => ("Registered, file missing", "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush"),
            FilterState.StockShown => ("Shipped", "TextFillColorSecondaryBrush", "CardBackgroundFillColorSecondaryBrush"),
            FilterState.StockHidden => ("Hidden by the game", "TextFillColorSecondaryBrush", "CardBackgroundFillColorSecondaryBrush"),
            FilterState.StockUnhidden => ("Unlocked", "SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush"),
            FilterState.StockSuppressed => ("Hidden here", "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
            _ => ("Another mod", "TextFillColorSecondaryBrush", "CardBackgroundFillColorSecondaryBrush"),
        };

        return new FilterRowView
        {
            Name = f.Name,
            Path = f.Path,
            Chip = chip,
            ChipForeground = Themed(fg),
            ChipBackground = Themed(bg),
        };
    }

    /// <summary>
    /// A brush from the system theme, so the chips follow light and dark without a palette here.
    /// </summary>
    /// <remarks>Falls back rather than throwing: a missing resource key should cost colour, not the screen.</remarks>
    private static Brush Themed(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? brush) && brush is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
