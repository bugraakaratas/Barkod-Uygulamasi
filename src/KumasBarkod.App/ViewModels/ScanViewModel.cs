using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public enum ScanField { Input, VoidReason }

/// <summary>Ekranda gösterilen kesim ayrıntısı (biçimlenmiş).</summary>
public sealed record CutDetails(
    string CutId,
    string LotCode,
    string Meters,
    string CreatedAt,
    string StationCode,
    string PrintInfo,
    bool IsPrinted,
    string? Operator,
    bool IsVoided,
    string? VoidReason,
    string? VoidedAt,
    LabelData Label)
{
    public static CutDetails From(Cut c) => new(
        c.CutId,
        c.LotCode,
        Length.Format(c.LengthCm) + " m",
        Local(c.CreatedAt),
        c.StationCode,
        c.PrintCount == 0 ? "Basılmadı" : $"{c.PrintCount} kez · son {Local(c.LastPrintedAt!)}",
        c.PrintCount > 0,
        c.Operator,
        c.IsVoided,
        c.VoidReason,
        c.VoidedAt is null ? null : Local(c.VoidedAt),
        LabelData.FromCut(c, TimeZoneInfo.Local));

    private static string Local(string utc) => Timestamps.Parse(utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
}

public partial class ScanViewModel : ObservableObject
{
    private readonly CutService _cutService;
    private readonly PrintService _printService;

    public ScanViewModel(CutService cutService, PrintService printService)
    {
        _cutService = cutService;
        _printService = printService;
    }

    public event Action<ScanField>? FocusRequested;

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent))]
    [NotifyCanExecuteChangedFor(nameof(ReprintCommand), nameof(BeginVoidCommand), nameof(StartReplacementCommand))]
    public partial CutDetails? Current { get; set; }

    public bool HasCurrent => Current is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginVoidCommand))]
    public partial bool IsVoidPanelOpen { get; set; }

    [ObservableProperty]
    public partial string VoidReason { get; set; } = "";

    [ObservableProperty]
    public partial bool MessageIsOpen { get; set; }

    [ObservableProperty]
    public partial string MessageTitle { get; set; } = "";

    [ObservableProperty]
    public partial string MessageText { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity MessageSeverity { get; set; }

    /// <summary>Okutma veya elle yazılan girdiyle kaydı açar.</summary>
    public void Open(string input)
    {
        IsVoidPanelOpen = false;
        VoidReason = "";
        InputText = "";

        if (!CutIds.TryParseScan(input, out var cutId))
        {
            Current = null;
            ShowMessage(InfoBarSeverity.Error, "Geçersiz barkod",
                $"\"{input.Trim()}\" bir kesim ID'si değil. Beklenen biçim: K1-000123");
            FocusRequested?.Invoke(ScanField.Input);
            return;
        }

        var cut = _cutService.FindById(cutId);
        if (cut is null)
        {
            Current = null;
            ShowMessage(InfoBarSeverity.Error, $"{cutId} bulunamadı",
                "Bu bilgisayarda böyle bir kayıt yok. Etiket başka istasyonda basılmış olabilir.");
        }
        else
        {
            Current = CutDetails.From(cut);
            if (cut.IsVoided)
                ShowMessage(InfoBarSeverity.Warning, $"{cutId} iptal edilmiş", "Bu etiket geçersiz; kumaştan sökülmeli.");
            else
                MessageIsOpen = false;
        }

        FocusRequested?.Invoke(ScanField.Input);
    }

    [RelayCommand]
    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            FocusRequested?.Invoke(ScanField.Input);
            return;
        }
        Open(InputText);
    }

    [RelayCommand(CanExecute = nameof(CanActOnActive))]
    private async Task ReprintAsync()
    {
        var cutId = Current!.CutId;
        try
        {
            var cut = await Task.Run(() => _printService.Print(cutId));
            Current = CutDetails.From(cut);
            ShowMessage(InfoBarSeverity.Success, $"{cutId} etiketi tekrar basıldı",
                _printService.Printer.IsPhysical ? "" : "Test yazıcısı: etiket dosyaya yazıldı.");
            WeakReferenceMessenger.Default.Send(new CutChangedMessage(cut));
        }
        catch (Exception ex) when (ex is PrinterException or CutValidationException)
        {
            ShowMessage(InfoBarSeverity.Warning, $"{cutId} etiketi basılamadı", ex.Message);
        }
        FocusRequested?.Invoke(ScanField.Input);
    }

    [RelayCommand(CanExecute = nameof(CanBeginVoid))]
    private void BeginVoid()
    {
        VoidReason = "";
        IsVoidPanelOpen = true;
        FocusRequested?.Invoke(ScanField.VoidReason);
    }

    [RelayCommand]
    private void ConfirmVoid()
    {
        if (Current is null) return;
        try
        {
            var cut = _cutService.VoidCut(Current.CutId, VoidReason);
            Current = CutDetails.From(cut);
            IsVoidPanelOpen = false;
            ShowMessage(InfoBarSeverity.Success, $"{cut.CutId} iptal edildi",
                "Etiketi kumaştan sökün. Doğru bilgiyle yeni kayıt için \"Yerine yeni kayıt\" (F9).");
            WeakReferenceMessenger.Default.Send(new CutChangedMessage(cut));
            FocusRequested?.Invoke(ScanField.Input);
        }
        catch (CutValidationException ex)
        {
            ShowMessage(InfoBarSeverity.Error, "İptal edilmedi", ex.Message);
            FocusRequested?.Invoke(ScanField.VoidReason);
        }
    }

    [RelayCommand]
    private void CancelVoid()
    {
        IsVoidPanelOpen = false;
        VoidReason = "";
        FocusRequested?.Invoke(ScanField.Input);
    }

    /// <summary>İptal edilen kaydın lotuyla giriş ekranını açar.</summary>
    [RelayCommand(CanExecute = nameof(CanStartReplacement))]
    private void StartReplacement() =>
        WeakReferenceMessenger.Default.Send(new StartNewCutMessage(Current!.LotCode));

    private bool CanActOnActive() => Current is { IsVoided: false };
    private bool CanBeginVoid() => Current is { IsVoided: false } && !IsVoidPanelOpen;
    private bool CanStartReplacement() => Current is { IsVoided: true };

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
