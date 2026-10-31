namespace KumasBarkod.Core.Printing;

/// <summary>
/// Sahte yazıcı: etiket komutunu klasöre dosya olarak yazar.
/// Yazıcı alınana kadar geliştirme ve test bununla yapılır.
/// </summary>
public sealed class FileLabelPrinter(ILabelTemplate template, string outputDirectory, TimeProvider clock) : ILabelPrinter
{
    public string OutputDirectory { get; } = outputDirectory;

    public string DisplayName => "Test yazıcısı (dosyaya yazar)";

    public bool IsPhysical => false;

    /// <summary>Son yazılan dosya; ekranda "dosyayı aç" için.</summary>
    public string? LastFilePath { get; private set; }

    public void Print(LabelData label)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            var stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss-fff");
            var path = Path.Combine(OutputDirectory, $"{label.CutId}_{stamp}.zpl");
            File.WriteAllBytes(path, template.Render(label));
            LastFilePath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PrinterException($"Etiket dosyası yazılamadı ({OutputDirectory}).", ex);
        }
    }
}
