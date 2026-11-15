using System.Collections.ObjectModel;
using System.Windows;
using KumasBarkod.App.ServerSync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public enum EntryField { Lot, Length, ReprintPanel, LotCheckPanel }

public sealed record RecentCutItem(string CutId, string LotCode, string Meters, string Time, bool IsVoided, long PrintCount);

public partial class CutEntryViewModel : ObservableObject
{
    private readonly CutService _cutService;
    private readonly CutRepository _cuts;
    private readonly SettingsRepository _settings;
    private readonly PrintService _printService;
    private readonly LotCatalog _catalog;
    private bool _busy;

    /// <summary>Son lotlar (bu bilgisayarın kayıtlarından). Yazılan metne uyan tanımlı lot yoksa bunlar gösterilir.</summary>
    private readonly List<string> _recent = [];

    /// <summary>Depo sunucusundaki etkin lotlar (önbellekten). Boşsa lot kontrolü yapılmaz.</summary>
    private IReadOnlyList<CatalogLot> _activeLots = [];

    /// <summary>
    /// Bu oturumda "yazıldığı gibi kaydet" denmiş tanımsız lotlar: anahtar → onaylanan yazım. Her topta tekrar
    /// sorulmaz ve aynı lot hep ilk onaylanan yazımla basılır ("YENI LOT" / "yeni lot" karışmasın).
    /// </summary>
    private readonly Dictionary<string, string> _confirmedUndefined = new(StringComparer.Ordinal);

    public CutEntryViewModel(CutService cutService, CutRepository cuts, SettingsRepository settings, PrintService printService,
        LotCatalog catalog, ServerConnection server)
    {
        _cutService = cutService;
        _cuts = cuts;
        _settings = settings;
        _printService = printService;
        _catalog = catalog;
        _activeLots = catalog.GetActive();

        // Lot listesi sunucudan yenilenince (arka plan) öneriler güncellensin.
        server.Service.LotCatalogRefreshed += () =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _activeLots = _catalog.GetActive();
                UpdateChoices(LotText.Trim());
            });

        // Başka ekranda iptal/baskı olunca son kayıtlar listesi güncel kalsın.
        WeakReferenceMessenger.Default.Register<CutEntryViewModel, CutChangedMessage>(
            this, static (vm, m) => vm.OnCutChanged(m.Cut));
        WeakReferenceMessenger.Default.Register<CutEntryViewModel, DatabaseRestoredMessage>(
            this, static (vm, _) => vm.ReloadLists());
    }

    /// <summary>
    /// Bu oturumda en son kaydedilen kesim. F5 (sıradaki parça) ve F6 (bozulan etiket) bunun
    /// üzerinde çalışır; uygulama yeni açıldığında boştur, ilk kayıt elle girilir.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCutId), nameof(HasLastCut), nameof(NextPieceHint))]
    [NotifyCanExecuteChangedFor(nameof(NextPieceCommand), nameof(BeginReprintCommand), nameof(DemoScanCommand))]
    public partial Cut? LastCut { get; set; }

    public string? LastCutId => LastCut?.CutId;

    public bool HasLastCut => LastCut is not null;

    /// <summary>Sıradaki parça düğmesinin altındaki açıklama: ne basılacağı ya da neden basılamayacağı.</summary>
    public string NextPieceHint => LastCut switch
    {
        null => "İlk kaydı elle girin",
        { IsVoided: true } c => $"{c.CutId} iptal edildi: lot ve uzunluğu elle girin",
        { PrintCount: 0 } c when !_busy => $"Önce {c.CutId} etiketini basın (F6)",
        var c => $"{c.LotCode} · {Length.FormatCm(c.LengthCm)} cm · yeni ID",
    };

    /// <summary>"Bozulan etiket" onayı açık mı? Yeni parça için kullanılmasın diye tek tuşla basmaz.</summary>
    [ObservableProperty]
    public partial bool IsReprintPanelOpen { get; set; }

    /// <summary>Son kaydın etiket önizlemesi (yazıcı türünden bağımsız her zaman gösterilir).</summary>
    [ObservableProperty]
    public partial LabelData? LastLabel { get; set; }

    /// <summary>
    /// Gösterim: son etiketi "okut". Okuyucunun yaptığının sonucunu üretir: kayıt okutma ekranında açılır.
    /// (Gerçek okuyucu da aynı yoldan gelir: <see cref="OpenCutMessage"/>.)
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasLastCut))]
    private void DemoScan() => WeakReferenceMessenger.Default.Send(new OpenCutMessage(LastCutId!));

    /// <summary>Görünüme hangi alana odaklanacağını söyler (odak bir görünüm işidir).</summary>
    public event Action<EntryField>? FocusRequested;

    /// <summary>Lot kutusunun altındaki seçenekler: son lotlar ya da yazılana uyan tanımlı lotlar (↑ ↓, tıklama).</summary>
    public ObservableCollection<string> RecentLots { get; } = [];

    [ObservableProperty]
    public partial string ChoicesTitle { get; set; } = "Son lotlar";

    // ---------- Tanımsız lot onayı ----------

    /// <summary>Yazılan lot tanımlı değil: kaydetmeden önce onay (1 önerilen, 2 yazıldığı gibi, Esc düzelt).</summary>
    [ObservableProperty]
    public partial bool IsLotCheckOpen { get; set; }

    [ObservableProperty]
    public partial string LotCheckTitle { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLotSuggestion))]
    public partial string? LotSuggestion { get; set; }

    public bool HasLotSuggestion => LotSuggestion is not null;

    [ObservableProperty]
    public partial string PendingLot { get; set; } = "";

    /// <summary>0 = seçilmedi, 1 = önerilen lot, 2 = yazıldığı gibi.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoiceSuggestion), nameof(IsChoiceTyped))]
    public partial int LotCheckChoice { get; set; }

    public bool IsChoiceSuggestion { get => LotCheckChoice == 1; set { if (value) LotCheckChoice = 1; } }
    public bool IsChoiceTyped { get => LotCheckChoice == 2; set { if (value) LotCheckChoice = 2; } }

    [ObservableProperty]
    public partial string LotCheckHint { get; set; } = "";

    private long _pendingCm;
    public ObservableCollection<RecentCutItem> RecentCuts { get; } = [];

    [ObservableProperty]
    public partial string LotText { get; set; } = "";

    /// <summary>Lot metni son lotlardan biriyse onun sırası, değilse -1 (vurgu buna göre).</summary>
    [ObservableProperty]
    public partial int RecentLotIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string LengthText { get; set; } = "";

    /// <summary>Uzunluk kutusunun altındaki canlı açıklama: "= 25,50 m" ya da hata. Yanlışlıkla metre yazılırsa ("25" = 25 cm) burada görünür.</summary>
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

    partial void OnLotTextChanged(string value)
    {
        var text = value.Trim();
        // Seçeneklerden biri seçildiyse (↑ ↓, tıklama) liste olduğu gibi kalır; yazılıyorsa yeniden süzülür.
        if (!RecentLots.Contains(text)) UpdateChoices(text);
        RecentLotIndex = RecentLots.IndexOf(text);
    }

    /// <summary>
    /// Yazılan metne uyan tanımlı lotlar varsa onlar, yoksa son lotlar gösterilir. Tanımlı lot yoksa
    /// (sunucu ayarlanmadı / henüz lot tanımlanmadı) her zaman son lotlar: ekran eskisi gibi.
    /// </summary>
    private void UpdateChoices(string text)
    {
        var matches = LotMatcher.Suggest(text, _activeLots);
        var useMatches = matches.Count > 0 && !(matches.Count == 1 && matches[0] == text && _recent.Contains(text));
        IReadOnlyList<string> target = useMatches ? matches : _recent;
        ChoicesTitle = useMatches ? "Tanımlı lotlar" : "Son lotlar";
        if (RecentLots.SequenceEqual(target)) return;
        RecentLots.Clear();
        foreach (var l in target) RecentLots.Add(l);
    }

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

        if (Length.TryParseCentimeters(value, _settings.GetMaxLengthCm(), out var cm, out var error))
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
    private Task SaveAsync() => RunExclusiveAsync(async () =>
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

        if (!Length.TryParseCentimeters(LengthText, _settings.GetMaxLengthCm(), out var cm, out var error))
        {
            ShowError(error, EntryField.Length);
            return;
        }

        _activeLots = _catalog.GetActive();
        var check = LotMatcher.Check(lot, _activeLots);
        string? note = null;
        switch (check.Kind)
        {
            case LotCheckKind.Defined when check.Match!.Name != lot:
                // Yalnızca yazım farkı: etikete ve kayda tanımlı ad yazılır (bütün etiketler aynı görünsün).
                note = $"yazılan \"{lot}\" → tanımlı ad";
                lot = check.Match.Name;
                break;
            case LotCheckKind.Undefined when _confirmedUndefined.TryGetValue(LotKeys.Key(lot), out var confirmed):
                if (confirmed != lot) note = $"yazılan \"{lot}\" → onaylanan yazım";
                lot = confirmed;
                break;
            case LotCheckKind.Undefined:
                OpenLotCheck(lot, cm, check.Suggestion?.Name);
                return;
        }

        await CreateAndShowAsync(lot, cm, note);
    });

    private async Task CreateAndShowAsync(string lot, long cm, string? note)
    {
        var cut = await TryCreateAsync(() => _cutService.CreateCut(lot, cm));
        if (cut is null) return;

        LengthText = "";
        LotText = cut.LotCode;      // Son lot seçili kalır
        await ShowAndPrintAsync(cut, note);
    }

    private void OpenLotCheck(string lot, long cm, string? suggestion)
    {
        PendingLot = lot;
        _pendingCm = cm;
        LotSuggestion = suggestion;
        LotCheckTitle = $"\"{lot}\" tanımlı bir lot değil.";
        // Öneri varsa kendiliğinden SEÇİLMEZ: yanlış firmaya bağlamak en kötü hata, bilerek seçilsin.
        LotCheckChoice = suggestion is null ? 2 : 0;
        LotCheckHint = suggestion is null ? "Enter: yazıldığı gibi kaydet · Esc: düzelt" : "1 ya da 2 seçip Enter · Esc: düzelt";
        IsLotCheckOpen = true;
        FocusRequested?.Invoke(EntryField.LotCheckPanel);
    }

    public void SelectLotChoice(int choice)
    {
        if (!IsLotCheckOpen) return;
        if (choice == 1 && LotSuggestion is null) return;
        LotCheckChoice = choice;
    }

    [RelayCommand]
    private Task ConfirmLotCheckAsync() => RunExclusiveAsync(async () =>
    {
        if (!IsLotCheckOpen) return;
        if (LotCheckChoice == 0)
        {
            LotCheckHint = "Önce seçin: 1 = önerilen lot, 2 = yazıldığı gibi.";
            return;
        }

        var lot = LotCheckChoice == 1 ? LotSuggestion! : PendingLot;
        if (LotCheckChoice == 2) _confirmedUndefined[LotKeys.Key(lot)] = lot;
        var note = LotCheckChoice == 1 ? $"yazılan \"{PendingLot}\" → {lot}" : "tanımsız lot, yönetim bağlayacak";
        CloseLotCheckPanel();
        await CreateAndShowAsync(lot, _pendingCm, note);
    });

    [RelayCommand]
    private void CancelLotCheck()
    {
        CloseLotCheckPanel();
        FocusRequested?.Invoke(EntryField.Lot);
    }

    public void CloseLotCheckPanel() => IsLotCheckOpen = false;

    /// <summary>
    /// Sıradaki parça: son kaydın lotu ve uzunluğuyla yeni ID'li kesim + etiket. Değerler son
    /// kayıttan alınır; kutulardaki yarım yazılar kullanılmaz ve değiştirilmez.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNextPiece))]
    private Task NextPieceAsync() => RunExclusiveAsync(async () =>
    {
        CloseReprintPanel();
        CloseLotCheckPanel();
        var sourceId = LastCut!.CutId;
        var cut = await TryCreateAsync(() => _cutService.CreateNextPiece(sourceId));
        if (cut is not null)
            await ShowAndPrintAsync(cut);
    });

    private bool CanNextPiece() => LastCut is { IsVoided: false };

    /// <summary>
    /// Bozulan etiket: onay ister. Hiç etiket çıkmamışsa (baskı hatası) onay gerekmez, doğrudan basar.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReprint))]
    private async Task BeginReprintAsync()
    {
        CloseLotCheckPanel();
        if (LastCut!.PrintCount == 0)
        {
            await RunExclusiveAsync(ReprintLastAsync);
            return;
        }
        IsReprintPanelOpen = true;
        FocusRequested?.Invoke(EntryField.ReprintPanel);
    }

    private bool CanReprint() => LastCut is { IsVoided: false };

    [RelayCommand]
    private Task ConfirmReprintAsync() => RunExclusiveAsync(async () =>
    {
        if (!IsReprintPanelOpen) return;
        CloseReprintPanel();
        await ReprintLastAsync();
    });

    [RelayCommand]
    private void CancelReprint()
    {
        CloseReprintPanel();
        FocusRequested?.Invoke(EntryField.Length);
    }

    public void CloseReprintPanel() => IsReprintPanelOpen = false;

    /// <summary>
    /// Enter ve F5 farklı komutlardır, komutun kendi kilidi ikisini birlikte durdurmaz: aynı anda
    /// iki kayıt açılmasın, baskı sürerken ikinci işlem başlamasın.
    /// </summary>
    private async Task RunExclusiveAsync(Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await work();
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(NextPieceHint));
        }
    }

    /// <returns>Oluşan kesim; hata gösterildiyse null.</returns>
    private async Task<Cut?> TryCreateAsync(Func<Cut> create)
    {
        try
        {
            return await Task.Run(create);
        }
        catch (CutValidationException ex)
        {
            ShowError(ex.Message, EntryField.Length);
        }
        catch (Exception ex)
        {
            ShowError($"Kayıt yapılamadı: {ex.Message}", EntryField.Length);
        }
        return null;
    }

    /// <summary>Kayıt tamam: ekranı hemen hazırlar, sonra etiketi basar.</summary>
    private async Task ShowAndPrintAsync(Cut cut, string? note = null)
    {
        LastCut = cut;
        LastLabel = LabelData.FromCut(cut, TimeZoneInfo.Local);
        WeakReferenceMessenger.Default.Send(new CutChangedMessage(cut));   // listeler de bununla yenilenir

        var summary = $"Lot {cut.LotCode} · {Length.Format(cut.LengthCm)} m" + (note is null ? "" : $" ({note})");
        var printError = await PrintAsync(cut.CutId);
        if (printError is null)
            ShowResult(InfoBarSeverity.Success, $"{cut.CutId} kaydedildi", $"{summary} · {PrintedText()}");
        else
            ShowResult(InfoBarSeverity.Warning, $"{cut.CutId} kaydedildi, etiket basılamadı",
                $"{printError}  F6 ile tekrar deneyin.");

        FocusRequested?.Invoke(EntryField.Length);
    }

    /// <summary>Son kaydı AYNI ID ile basar. Yeni parça için değildir; o iş F5'tir.</summary>
    private async Task ReprintLastAsync()
    {
        var cutId = LastCut!.CutId;
        var error = await PrintAsync(cutId);
        if (error is null)
            ShowResult(InfoBarSeverity.Success, $"{cutId} etiketi yeniden basıldı", PrintedText());
        else
            ShowResult(InfoBarSeverity.Warning, $"{cutId} etiketi basılamadı", $"{error}  F6 ile tekrar deneyin.");

        FocusRequested?.Invoke(EntryField.Length);
    }

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
        _printService.Printer.IsPhysical ? "etiket basıldı" : $"etiket → {_printService.Printer.DisplayName}";

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

    private void OnCutChanged(Cut cut)
    {
        ReloadLists();
        // Başka ekrandaki iptal veya baskı düğmelere ve açıklamaya yansısın.
        if (cut.CutId == LastCut?.CutId)
            LastCut = cut;
    }

    private void ReloadLists()
    {
        var lot = LotText;

        _recent.Clear();
        _recent.AddRange(_cuts.GetRecentLots());
        RecentLots.Clear();
        foreach (var l in _recent) RecentLots.Add(l);
        ChoicesTitle = "Son lotlar";

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
        UpdateChoices(lot.Trim());
        RecentLotIndex = RecentLots.IndexOf(lot.Trim());
    }
}
