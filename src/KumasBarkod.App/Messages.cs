using KumasBarkod.Core.Domain;

namespace KumasBarkod.App;

/// <summary>Bir kesim oluşturuldu, basıldı veya iptal edildi; listeler ve durum çubuğu yenilenir.</summary>
public sealed record CutChangedMessage(Cut Cut);

/// <summary>Kaydı okutma ekranında aç: barkod okutuldu (herhangi bir ekranda) veya geçmişten seçildi.</summary>
public sealed record OpenCutMessage(string CutId);

/// <summary>İptal edilen kaydın yerine yeni kayıt: giriş ekranı bu lotla açılır.</summary>
public sealed record StartNewCutMessage(string LotCode);

/// <summary>Yeni yedek alındı (Ayarlar ekranındaki yedek bilgisi yenilenir).</summary>
public sealed record BackupChangedMessage;

/// <summary>Veritabanı Sheets'ten geri kuruldu: tüm listeler ve sayılar yeniden okunur.</summary>
public sealed record DatabaseRestoredMessage;

/// <summary>Bir ayar değişti (ör. istasyon kodu): durum çubuğu yenilenir.</summary>
public sealed record SettingsChangedMessage;
