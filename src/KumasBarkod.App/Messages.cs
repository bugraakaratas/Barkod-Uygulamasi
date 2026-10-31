using KumasBarkod.Core.Domain;

namespace KumasBarkod.App;

/// <summary>Bir kesim oluşturuldu, basıldı veya iptal edildi; listeler ve durum çubuğu yenilenir.</summary>
public sealed record CutChangedMessage(Cut Cut);

/// <summary>Kaydı okutma ekranında aç: barkod okutuldu (herhangi bir ekranda) veya geçmişten seçildi.</summary>
public sealed record OpenCutMessage(string CutId);

/// <summary>İptal edilen kaydın yerine yeni kayıt: giriş ekranı bu lotla açılır.</summary>
public sealed record StartNewCutMessage(string LotCode);
