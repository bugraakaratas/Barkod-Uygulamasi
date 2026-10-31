using KumasBarkod.Core.Domain;

namespace KumasBarkod.App;

/// <summary>Yeni kesim kaydedildi; durum çubuğu ve diğer ekranlar yenilenir.</summary>
public sealed record CutCreatedMessage(Cut Cut);
