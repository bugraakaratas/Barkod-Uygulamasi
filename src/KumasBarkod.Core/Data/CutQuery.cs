using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Data;

public enum CutStatusFilter { All, Active, Voided }

/// <summary>
/// Geçmiş ekranı filtresi. Tarihler yerel gün olarak verilir (dahil); sorguda UTC aralığa çevrilir.
/// <see cref="Search"/>: kesim ID'si gibi görünüyorsa tam ID araması, değilse lotta "içerir" araması.
/// </summary>
public sealed record CutQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Search = null,
    CutStatusFilter Status = CutStatusFilter.All,
    int Page = 0,
    int PageSize = 200);

public sealed record CutSearchResult(
    IReadOnlyList<Cut> Items,
    long TotalCount,
    long ActiveCount,
    long VoidedCount,
    /// <summary>Yalnızca aktif kesimlerin toplam uzunluğu (iptal edilenler geçersizdir).</summary>
    long ActiveLengthCm,
    int Page,
    int PageSize)
{
    public int PageCount => TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
    public long FirstRow => TotalCount == 0 ? 0 : (long)Page * PageSize + 1;
    public long LastRow => (long)Page * PageSize + Items.Count;
}
