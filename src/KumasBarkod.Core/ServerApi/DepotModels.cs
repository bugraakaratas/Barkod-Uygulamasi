namespace KumasBarkod.Core.ServerApi;

/// <summary>Depo (stok + lot) uç noktaları. WPF ekranları ve ileride el terminali aynılarını kullanır.</summary>
public static class DepotPaths
{
    public const string Version = "api/v1/version";
    public const string Lots = "api/v1/lots";
    public const string UnmatchedLots = "api/v1/lots/unmatched";
    public const string Stock = "api/v1/stock";
    public const string Rolls = "api/v1/rolls";

    /// <summary>İsteği yapan cihazın adı (kim yaptı kaydı için; kullanıcı girişi Faz 4'te).</summary>
    public const string DeviceHeader = "X-Kumas-Device";
}

/// <summary>Top durumları (sunucudaki <c>rolls.state</c>).</summary>
public static class RollStateNames
{
    public const string InStock = "in_stock";
    public const string Reserved = "reserved";
    public const string Shipped = "shipped";
    public const string Voided = "voided";
    public const string Quarantined = "quarantined";
    public const string All = "all";

    public static string Display(string state) => state switch
    {
        InStock => "Stokta",
        Reserved => "Sevkiyatta (onay bekliyor)",
        Shipped => "Sevk edildi",
        Voided => "İptal",
        Quarantined => "Karantina",
        _ => state,
    };
}

/// <summary>Veri değişince artan sayaç: ekranlar bunu birkaç saniyede bir sorar, değiştiyse yeniden okur.</summary>
public sealed record DataVersionResponse(long DataVersion);

public sealed record LotInfo(
    long LotId, string Name, bool Active,
    long InStockCount, long InStockCm, long TotalCount);

public sealed record LotCreateRequest(string Name);

/// <summary>Ad değiştirme ve/veya etkinlik. Boş alan = değişmez.</summary>
public sealed record LotUpdateRequest(string? Name, bool? Active);

public sealed record LotAliasRequest(string RawText);

public sealed record LotAliasResponse(long LotId, string RawText, int AssignedRolls);

public sealed record LotSuggestion(long LotId, string Name);

/// <summary>Hiçbir lota bağlanmamış, QC'de yazılmış lot metni.</summary>
public sealed record UnmatchedLot(
    string RawText, long RollCount, long InStockCount, long InStockCm,
    string FirstSeenAt, string LastSeenAt, IReadOnlyList<LotSuggestion> Suggestions);

public sealed record StockSummary(long Count, long LengthCm);

public sealed record StockRow(
    string CutId, string Barcode, long? LotId, string? LotName, string LotRaw,
    long LengthCm, string QcCreatedAt, string FirstSeenAt, string State, string QcStatus, string? VoidReason);

/// <param name="Filtered">Filtreye uyan topların sayısı ve uzunluğu.</param>
/// <param name="InStock">Bütün depo: stoktaki top sayısı ve uzunluğu (filtreden bağımsız).</param>
public sealed record StockResponse(
    long DataVersion,
    StockSummary InStock,
    StockSummary Filtered,
    long UnmatchedLotCount,
    long QuarantinedCount,
    IReadOnlyList<StockRow> Rows,
    int Offset,
    int Limit);

public sealed record MovementInfo(
    long MovementId, string Kind, string? FromState, string ToState,
    string OccurredAt, string RecordedAt, string? StationCode, string? Note);

public sealed record RollDetail(StockRow Roll, string StationCode, long PrintCount, IReadOnlyList<MovementInfo> Movements);
