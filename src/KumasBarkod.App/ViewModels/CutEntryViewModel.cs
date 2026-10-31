using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Services;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public enum EntryField { Lot, Length }

public sealed record RecentCutItem(string CutId, string LotCode, string Meters, string Time, bool IsVoided);

public partial class CutEntryViewModel : ObservableObject
{
    private readonly CutService _cutService;
    private readonly CutRepository _cuts;
    private readonly SettingsRepository _settings;

    public CutEntryViewModel(CutService cutService, CutRepository cuts, SettingsRepository settings)
    {
        _cutService = cutService;
        _cuts = cuts;
        _settings = settings;
    }

    /// <summary>Görünüme hangi alana odaklanacağını söyler (odak bir görünüm işidir).</summary>
    public event Action<EntryField>? FocusRequested;

    public ObservableCollection<string> RecentLots { get; } = [];
    public ObservableCollection<RecentCutItem> RecentCuts { get; } = [];

    [ObservableProperty]
    public partial string LotText { get; set; } = "";

    /// <summary>Lot metni son lotlardan biriyse onun sırası, değilse -1 (vurgu buna göre).</summary>
    [ObservableProperty]
    public partial int RecentLotIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string LengthText { get; set; } = "";

    /// <summary>Metre kutusunun altındaki canlı açıklama: "= 25,50 m" ya da hata.</summary>
    [ObservableProperty]
    public partial string LengthHint { get; set; } = "";

    [ObservableProperty]
    public partial bool LengthHintIsError { get; set; }

    [ObservableProperty]
    public partial bool ResultIsOpen { get; set; }

    [ObservableProperty]
    public partial string ResultTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ResultMessage { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity ResultSeverity { get; set; }

    public void Load()
    {
        ReloadLists();
        if (LotText.Length == 0 && RecentLots.Count > 0)
            LotText = RecentLots[0];
    }

    partial void OnLotTextChanged(string value) => RecentLotIndex = RecentLots.IndexOf(value.Trim());

    partial void OnRecentLotIndexChanged(int value)
    {
        if (value >= 0 && value < RecentLots.Count && LotText.Trim() != RecentLots[value])
            LotText = RecentLots[value];
    }

    /// <summary>↓ (+1) daha eski lota, ↑ (-1) daha yeni lota geçer.</summary>
    public void StepRecentLot(int step)
    {
        if (RecentLots.Count == 0) return;
        var next = RecentLotIndex < 0 ? (step > 0 ? 0 : -1) : RecentLotIndex + step;
        if (next >= 0 && next < RecentLots.Count)
            LotText = RecentLots[next];
    }

    partial void OnLengthTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            LengthHint = "";
            LengthHintIsError = false;
            return;
        }

        if (Length.TryParseMeters(value, _settings.GetMaxLengthCm(), out var cm, out var error))
        {
            LengthHint = $"= {Length.Format(cm)} m";
            LengthHintIsError = false;
        }
        else
        {
            LengthHint = error;
            LengthHintIsError = true;
        }
    }

    /// <summary>
    /// AsyncRelayCommand çalışırken kendini kapatır: Enter'a art arda basılsa da
    /// ikinci kayıt açılmaz.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        string lot;
        try
        {
            lot = CutService.NormalizeLot(LotText);
        }
        catch (CutValidationException ex)
        {
            ShowError(ex.Message, EntryField.Lot);
            return;
        }

        if (!Length.TryParseMeters(LengthText, _settings.GetMaxLengthCm(), out var cm, out var error))
        {
            ShowError(error, EntryField.Length);
            return;
        }

        Cut cut;
        try
        {
            cut = await Task.Run(() => _cutService.CreateCut(lot, cm));
        }
        catch (CutValidationException ex)
        {
            ShowError(ex.Message, EntryField.Length);
            return;
        }
        catch (Exception ex)
        {
            ShowError($"Kayıt yapılamadı: {ex.Message}", EntryField.Length);
            return;
        }

        ResultSeverity = InfoBarSeverity.Success;
        ResultTitle = $"{cut.CutId} kaydedildi";
        ResultMessage = $"Lot {cut.LotCode} · {Length.Format(cut.LengthCm)} m";
        ResultIsOpen = true;

        LengthText = "";
        LotText = cut.LotCode;      // Son lot seçili kalır
        ReloadLists();

        WeakReferenceMessenger.Default.Send(new CutCreatedMessage(cut));
        FocusRequested?.Invoke(EntryField.Length);
    }

    [RelayCommand]
    private void ClearLength()
    {
        LengthText = "";
        FocusRequested?.Invoke(EntryField.Length);
    }

    private void ShowError(string message, EntryField field)
    {
        ResultSeverity = InfoBarSeverity.Error;
        ResultTitle = "Kaydedilmedi";
        ResultMessage = message;
        ResultIsOpen = true;
        FocusRequested?.Invoke(field);
    }

    private void ReloadLists()
    {
        var lot = LotText;

        RecentLots.Clear();
        foreach (var l in _cuts.GetRecentLots()) RecentLots.Add(l);

        RecentCuts.Clear();
        foreach (var c in _cuts.GetRecentCuts(12))
        {
            RecentCuts.Add(new RecentCutItem(
                c.CutId, c.LotCode, Length.Format(c.LengthCm) + " m",
                Timestamps.Parse(c.CreatedAt).ToLocalTime().ToString("HH:mm:ss"),
                c.IsVoided));
        }

        // Liste yenilenince seçim sıfırlanır; mevcut lota göre vurguyu geri kur.
        LotText = lot;
        RecentLotIndex = RecentLots.IndexOf(lot.Trim());
    }
}
