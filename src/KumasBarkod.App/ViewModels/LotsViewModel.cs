using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public sealed record LotRowView(long LotId, string Name, bool Active, string StatusText, string InStock, string InStockMeters, long TotalCount)
{
    public static LotRowView From(LotInfo l) => new(l.LotId, l.Name, l.Active, l.Active ? "Etkin" : "Pasif",
        l.InStockCount.ToString("N0", CultureInfo.GetCultureInfo("tr-TR")), Length.FormatGrouped(l.InStockCm) + " m", l.TotalCount);
}

/// <summary>Öneri düğmesi: hangi yazımın hangi lota bağlanacağını kendisi taşır (seçili satırdan bağımsız).</summary>
public sealed record SuggestionView(string RawText, long LotId, string Name);

public sealed record UnmatchedView(string RawText, string Counts, string Dates, IReadOnlyList<SuggestionView> Suggestions)
{
    public bool HasSuggestions => Suggestions.Count > 0;

    public static UnmatchedView From(UnmatchedLot u) => new(
        u.RawText,
        $"{u.RollCount} top · stokta {u.InStockCount} ({Length.FormatGrouped(u.InStockCm)} m)",
        u.FirstSeenAt == u.LastSeenAt ? StockViewModel.Local(u.FirstSeenAt)
            : $"{StockViewModel.Local(u.FirstSeenAt)} – {StockViewModel.Local(u.LastSeenAt)}",
        u.Suggestions.Select(x => new SuggestionView(u.RawText, x.LotId, x.Name)).ToList());
}

/// <summary>
/// Lotlar (= firmalar) — yönetim bilgisayarı. Lot ekle / adını düzelt / pasif yap; kalite kontrolde farklı yazılmış
/// lot adlarını doğru lota bağla. Her işlem sunucuda kaydedilir ve topların geçmişine yazılır; bağlama düzeltilebilir.
/// </summary>
public partial class LotsViewModel : ObservableObject
{
    private readonly DepotApiClient _api;
    private readonly DataVersionPoller _poller;
    private int _requestId;

    public LotsViewModel(DepotApiClient api)
    {
        _api = api;
        _poller = new DataVersionPoller(api);
        _poller.Changed += ReloadAsync;
        _poller.ConnectionChanged += e => { ConnectionProblem = e is not null; ConnectionText = e is null ? "" : "Depoya ulaşılamıyor"; };
    }

    public ObservableCollection<LotRowView> Lots { get; } = [];
    public ObservableCollection<UnmatchedView> Unmatched { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(ToggleActiveCommand), nameof(MapToSelectedLotCommand))]
    public partial LotRowView? SelectedLot { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MapToSelectedLotCommand), nameof(CreateFromUnmatchedCommand))]
    public partial UnmatchedView? SelectedUnmatched { get; set; }

    [ObservableProperty] public partial string NewLotName { get; set; } = "";
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string ToggleText { get; set; } = "Pasif yap";
    [ObservableProperty] public partial bool HasUnmatched { get; set; }
    [ObservableProperty] public partial string UnmatchedTitle { get; set; } = "";
    [ObservableProperty] public partial bool ConnectionProblem { get; set; }
    [ObservableProperty] public partial string ConnectionText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(RenameCommand), nameof(ToggleActiveCommand),
        nameof(MapToSelectedLotCommand), nameof(MapToSuggestionCommand), nameof(CreateFromUnmatchedCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    public bool IsConfigured => _api.IsConfigured;

    public void Activate()
    {
        OnPropertyChanged(nameof(IsConfigured));
        if (IsConfigured) _poller.Start();
    }

    public void Deactivate() => _poller.Stop();

    partial void OnSelectedLotChanged(LotRowView? value)
    {
        EditName = value?.Name ?? "";
        ToggleText = value is { Active: false } ? "Etkinleştir" : "Pasif yap";
    }

    public async Task ReloadAsync()
    {
        var id = ++_requestId;
        var lots = await _api.GetLotsAsync();
        var unmatched = await _api.GetUnmatchedAsync();
        if (id != _requestId) return;

        var selectedLot = SelectedLot?.LotId;
        var selectedRaw = SelectedUnmatched?.RawText;
        Lots.Clear();
        foreach (var l in lots) Lots.Add(LotRowView.From(l));
        Unmatched.Clear();
        foreach (var u in unmatched) Unmatched.Add(UnmatchedView.From(u));
        SelectedLot = Lots.FirstOrDefault(l => l.LotId == selectedLot);
        SelectedUnmatched = Unmatched.FirstOrDefault(u => u.RawText == selectedRaw);

        HasUnmatched = Unmatched.Count > 0;
        UnmatchedTitle = HasUnmatched
            ? $"Hiçbir lota bağlanmamış {Unmatched.Count} yazım"
            : "Bütün toplar bir lota bağlı";
    }

    private bool CanAct() => !IsBusy;
    private bool CanActOnLot() => !IsBusy && SelectedLot is not null;
    private bool CanMapToSelected() => !IsBusy && SelectedLot is not null && SelectedUnmatched is not null;
    private bool CanActOnUnmatched() => !IsBusy && SelectedUnmatched is not null;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task CreateAsync() => Run(async () =>
    {
        var lot = await _api.CreateLotAsync(NewLotName);
        NewLotName = "";
        return ("Lot eklendi", $"{lot.Name}" + (lot.TotalCount > 0 ? $" — aynı yazımlı {lot.TotalCount} top bu lota bağlandı." : ""));
    });

    [RelayCommand(CanExecute = nameof(CanActOnLot))]
    private Task RenameAsync() => Run(async () =>
    {
        var lot = await _api.UpdateLotAsync(SelectedLot!.LotId, EditName, null);
        return ("Lot adı kaydedildi", lot.Name);
    });

    [RelayCommand(CanExecute = nameof(CanActOnLot))]
    private Task ToggleActiveAsync() => Run(async () =>
    {
        var lot = await _api.UpdateLotAsync(SelectedLot!.LotId, null, !SelectedLot.Active);
        return (lot.Active ? "Lot etkinleştirildi" : "Lot pasif yapıldı",
            lot.Active ? lot.Name : $"{lot.Name} yeni seçimlerde görünmez; eski topları bağlı kalır.");
    });

    [RelayCommand(CanExecute = nameof(CanMapToSelected))]
    private Task MapToSelectedLotAsync() => Map(SelectedUnmatched!.RawText, SelectedLot!.LotId, SelectedLot.Name);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task MapToSuggestionAsync(SuggestionView? suggestion) =>
        suggestion is null ? Task.CompletedTask : Map(suggestion.RawText, suggestion.LotId, suggestion.Name);

    /// <summary>Yazımı olduğu gibi yeni lot adı yapar (gerçekten yeni bir firmaysa).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnUnmatched))]
    private Task CreateFromUnmatchedAsync() => Run(async () =>
    {
        var raw = SelectedUnmatched!.RawText;
        var lot = await _api.CreateLotAsync(raw.Trim());
        // Ad yazımla birebir aynı değilse (ör. boşluk) bağlama elle yapılır
        if (lot.TotalCount == 0) await _api.MapRawTextAsync(lot.LotId, raw);
        return ("Yeni lot", $"\"{raw}\" yeni lot olarak eklendi ve topları bağlandı.");
    });

    private Task Map(string raw, long lotId, string lotName) => Run(async () =>
    {
        var r = await _api.MapRawTextAsync(lotId, raw);
        return ("Bağlandı", $"\"{raw}\" → {lotName}: {r.AssignedRolls} top. Bundan sonra bu yazımla gelen toplar da bu lota bağlanır.");
    });

    private async Task Run(Func<Task<(string Title, string Text)>> action)
    {
        IsBusy = true;
        try
        {
            var (title, text) = await action();
            Show(InfoBarSeverity.Success, title, text);
            _poller.Invalidate();
            await ReloadAsync();
        }
        catch (SyncException ex)
        {
            Show(ex.Kind == SyncFailureKind.Offline ? InfoBarSeverity.Warning : InfoBarSeverity.Error, "Yapılamadı", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
