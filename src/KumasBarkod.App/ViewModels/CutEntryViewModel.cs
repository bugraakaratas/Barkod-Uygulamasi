using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public enum EntryField { Lot, Length }

public sealed record RecentCutItem(string CutId, string LotCode, string Meters, string Time, bool IsVoided, long PrintCount);

public partial class CutEntryViewModel : ObservableObject
{
    private readonly CutService _cutService;
    private readonly CutRepository _cuts;
    private readonly SettingsRepository _settings;
    private readonly PrintService _printService;

    public CutEntryViewModel(CutService cutService, CutRepository cuts, SettingsRepository settings, PrintService printService)
    {
        _cutService = cutService;
        _cuts = cuts;
        _settings = settings;
        _printService = printService;

        // Başka ekranda iptal/baskı olunca son kayıtlar listesi güncel kalsın.
        WeakReferenceMessenger.Default.Register<CutEntryViewModel, CutChangedMessage>(
            this, static (vm, _) => vm.ReloadLists());
    }

    /// <summary>İptal edilen kaydın yerine yeni kayıt: lot dolu, metre boş, odak metrede.</summary>
    public void PrepareNewCut(string lotCode)
    {
        LotText = lotCode;
        LengthText = "";
        ShowResult(InfoBarSeverity.Informational, "Yerine yeni kayıt",
            $"Lot {lotCode} hazır. Doğru metreyi girip Enter'a basın.");
        FocusRequested?.Invoke(EntryField.Length);
    }

    /// <summary>Bu oturumda en son kaydedilen kesim; F5 bunun etiketini tekrar basar.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReprintLastCommand))]
    public partial string? LastCutId { get; set; }

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

        // Kayıt tamam: ekranı hemen hazırla, sonra etiketi bas.
        LastCutId = cut.CutId;
        LengthText = "";
        LotText = cut.LotCode;      // Son lot seçili kalır
        ReloadLists();
        WeakReferenceMessenger.Default.Send(new CutChangedMessage(cut));

        var summary = $"Lot {cut.LotCode} · {Length.Format(cut.LengthCm)} m";
        var printError = await PrintAsync(cut.CutId);
        if (printError is null)
            ShowResult(InfoBarSeverity.Success, $"{cut.CutId} kaydedildi", $"{summary} · {PrintedText()}");
        else
            ShowResult(InfoBarSeverity.Warning, $"{cut.CutId} kaydedildi, etiket basılamadı",
                $"{printError}  F5 ile tekrar deneyin.");

        FocusRequested?.Invoke(EntryField.Length);
    }

    /// <summary>Son kaydın etiketini aynı ID ile tekrar basar (baskı hatası veya yıpranmış etiket).</summary>
    [RelayCommand(CanExecute = nameof(CanReprintLast))]
    private async Task ReprintLastAsync()
    {
        var cutId = LastCutId!;
        var error = await PrintAsync(cutId);
        if (error is null)
            ShowResult(InfoBarSeverity.Success, $"{cutId} etiketi tekrar basıldı", PrintedText());
        else
            ShowResult(InfoBarSeverity.Warning, $"{cutId} etiketi basılamadı", $"{error}  F5 ile tekrar deneyin.");

        FocusRequested?.Invoke(EntryField.Length);
    }

    private bool CanReprintLast() => LastCutId is not null;

    /// <returns>Hata mesajı; başarılıysa null.</returns>
    private async Task<string?> PrintAsync(string cutId)
    {
        try
        {
            var printed = await Task.Run(() => _printService.Print(cutId));
            WeakReferenceMessenger.Default.Send(new CutChangedMessage(printed));
            return null;
        }
        catch (Exception ex) when (ex is PrinterException or CutValidationException)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return $"Beklenmeyen yazıcı hatası: {ex.Message}";
        }
    }

    private string PrintedText() =>
        _printService.Printer.IsPhysical ? "etiket basıldı" : "etiket dosyaya yazıldı (test yazıcısı)";

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        ResultIsOpen = true;
    }

    [RelayCommand]
    private void ClearLength()
    {
        LengthText = "";
        FocusRequested?.Invoke(EntryField.Length);
    }

    private void ShowError(string message, EntryField field)
    {
        ShowResult(InfoBarSeverity.Error, "Kaydedilmedi", message);
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
                c.IsVoided,
                c.PrintCount));
        }

        // Liste yenilenince seçim sıfırlanır; mevcut lota göre vurguyu geri kur.
        LotText = lot;
        RecentLotIndex = RecentLots.IndexOf(lot.Trim());
    }
}
