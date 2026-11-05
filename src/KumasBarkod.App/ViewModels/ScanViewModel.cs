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

/// <summary>İptal panelindeki bir sebep seçeneği (numara = klavye tuşu).</summary>
public sealed partial class VoidReasonOption(int number, string text, Action<VoidReasonOption> selected) : ObservableObject
{
    public int Number { get; } = number;
    public string Text { get; } = text;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

public partial class ScanViewModel : ObservableObject
{
    private readonly CutService _cutService;

    public ScanViewModel(CutService cutService)
    {
        _cutService = cutService;
        VoidReasonOptions = VoidReasons.All
            .Select((text, i) => new VoidReasonOption(i + 1, text, OnVoidReasonSelected))
            .ToList();
    }

    /// <summary>İptal sebepleri (sabit liste, tek seçim).</summary>
    public IReadOnlyList<VoidReasonOption> VoidReasonOptions { get; }

    /// <summary>İptal panelinin içinde (seçeneklerin altında) gösterilen hata; boşsa gizli.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVoidPanelError))]
    public partial string VoidPanelError { get; set; } = "";

    public bool HasVoidPanelError => VoidPanelError.Length > 0;

    /// <summary>Seçili iptal sebebi; seçilmemişse null.</summary>
    public string? SelectedVoidReason => VoidReasonOptions.FirstOrDefault(o => o.IsSelected)?.Text;

    /// <summary>Numarayla (1..n) sebep seçer; geçersiz numara yok sayılır.</summary>
    public void SelectVoidReason(int number)
    {
        if (!IsVoidPanelOpen || number < 1 || number > VoidReasonOptions.Count) return;
        VoidReasonOptions[number - 1].IsSelected = true;
    }

    /// <summary>Seçimi yukarı/aşağı kaydırır (seçim yoksa ilk/son seçenek).</summary>
    public void MoveVoidReason(int delta)
    {
        if (!IsVoidPanelOpen) return;
        var current = VoidReasonOptions.FirstOrDefault(o => o.IsSelected)?.Number;
        var next = current is null
            ? (delta > 0 ? 1 : VoidReasonOptions.Count)
            : Math.Clamp(current.Value + delta, 1, VoidReasonOptions.Count);
        SelectVoidReason(next);
    }

    private void OnVoidReasonSelected(VoidReasonOption chosen)
    {
        foreach (var o in VoidReasonOptions)
            if (!ReferenceEquals(o, chosen)) o.IsSelected = false;
        VoidPanelError = "";
        OnPropertyChanged(nameof(SelectedVoidReason));
    }

    private void ClearVoidReason()
    {
        foreach (var o in VoidReasonOptions) o.IsSelected = false;
        VoidPanelError = "";
        OnPropertyChanged(nameof(SelectedVoidReason));
    }

    public event Action<ScanField>? FocusRequested;

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent))]
    [NotifyCanExecuteChangedFor(nameof(BeginVoidCommand))]
    public partial CutDetails? Current { get; set; }

    public bool HasCurrent => Current is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginVoidCommand))]
    public partial bool IsVoidPanelOpen { get; set; }

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
        ClearVoidReason();
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

    [RelayCommand(CanExecute = nameof(CanBeginVoid))]
    private void BeginVoid()
    {
        ClearVoidReason();
        IsVoidPanelOpen = true;
        FocusRequested?.Invoke(ScanField.VoidReason);
    }

    [RelayCommand]
    private void ConfirmVoid()
    {
        if (Current is null || !IsVoidPanelOpen) return;
        if (SelectedVoidReason is null)
        {
            VoidPanelError = $"Önce iptal sebebini seçin (1–{VoidReasonOptions.Count} tuşu veya tıklayarak), sonra Enter.";
            FocusRequested?.Invoke(ScanField.VoidReason);
            return;
        }
        try
        {
            var cut = _cutService.VoidCut(Current.CutId, SelectedVoidReason);
            Current = CutDetails.From(cut);
            IsVoidPanelOpen = false;
            ShowMessage(InfoBarSeverity.Success, $"{cut.CutId} iptal edildi",
                "Etiketi kumaştan sökün.");
            WeakReferenceMessenger.Default.Send(new CutChangedMessage(cut));
            FocusRequested?.Invoke(ScanField.Input);
        }
        catch (CutValidationException ex)
        {
            VoidPanelError = "İptal edilmedi: " + ex.Message;
            FocusRequested?.Invoke(ScanField.VoidReason);
        }
    }

    [RelayCommand]
    private void CancelVoid()
    {
        IsVoidPanelOpen = false;
        ClearVoidReason();
        FocusRequested?.Invoke(ScanField.Input);
    }

    private bool CanBeginVoid() => Current is { IsVoided: false } && !IsVoidPanelOpen;

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
