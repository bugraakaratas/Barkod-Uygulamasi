using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.Core.Scanning;

namespace KumasBarkod.App.Scanning;

/// <summary>
/// Pencere genelinde okutma yakalar: hangi ekranda, hangi kutu odaktayken okutulursa okutulsun
/// kayıt açılır. Okuyucunun hızlı "yazdığı" karakterler o an odaktaki kutuya düşer; okutma
/// anlaşılınca kutu eski hâline döndürülür ve Enter kutuya ulaşmaz (ör. lot alanı bozulmaz,
/// uzunluk kutusunda kayıt tetiklenmez).
/// </summary>
public sealed class ScanCapture
{
    private readonly ScanBurstDetector _detector = new();

    private TextBox? _target;
    private string _savedText = "";
    private int _savedSelectionStart;
    private int _savedSelectionLength;

    public ScanCapture(Window window)
    {
        // handledEventsToo: alt denetimler olayı işlemiş olsa da görmeliyiz.
        window.AddHandler(UIElement.PreviewTextInputEvent, new TextCompositionEventHandler(OnTextInput), true);
        window.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), true);
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;

        if (_detector.StartsNewBurst(e.Timestamp))
            Snapshot(Keyboard.FocusedElement as TextBox);

        _detector.OnText(e.Text, e.Timestamp);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        var cutId = _detector.OnEnter(e.Timestamp);
        if (cutId is null) return;

        e.Handled = true;   // Enter odaktaki kutuya (lot → uzunluk, uzunluk → kaydet) ulaşmasın
        Restore();
        WeakReferenceMessenger.Default.Send(new OpenCutMessage(cutId));
    }

    private void Snapshot(TextBox? box)
    {
        _target = box;
        if (box is null) return;
        _savedText = box.Text;
        _savedSelectionStart = box.SelectionStart;
        _savedSelectionLength = box.SelectionLength;
    }

    private void Restore()
    {
        if (_target is null) return;
        _target.Text = _savedText;
        _target.Select(_savedSelectionStart, _savedSelectionLength);
        _target = null;
    }
}
