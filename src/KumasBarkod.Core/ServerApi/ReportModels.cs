namespace KumasBarkod.Core.ServerApi;

/// <summary>Faz 5: onayı geri alma, istisnalar (yetkili) ve raporlar.</summary>
public static class ReportPaths
{
    public const string Exceptions = "api/v1/exceptions";
    public const string ResolveConflict = "api/v1/exceptions/conflicts";   // + /{id}/resolve
    public const string StockByLot = "api/v1/reports/stock-by-lot";
    public const string DailyShipments = "api/v1/reports/daily-shipments";
    public const string ShippedRolls = "api/v1/reports/shipped-rolls";
    public const string ShipmentHistory = "api/v1/reports/shipments";
}

/// <param name="RequestId">Tekrar gönderimde ikinci kez işlenmesin.</param>
public sealed record ShipmentReverseRequest(string Reason, string RequestId);

public sealed record ConflictResolveRequest(string Note);

/// <summary>Aynı ID'yi taşıyan iki top: sunucudaki (ilk gelen) ve sonradan gelen.</summary>
public sealed record QuarantineInfo(
    long ConflictId, string CutId,
    string ExistingLot, long ExistingLengthCm, string ExistingCreatedAt, string ExistingStation,
    string IncomingLot, long IncomingLengthCm, string IncomingCreatedAt, string IncomingStation,
    string ReceivedAt, string RollState);

/// <summary>Sevk edildikten (ya da sevkiyata okutulduktan) sonra kalite kontrolde iptal edilen top.</summary>
public sealed record VoidedAfterShipInfo(string CutId, string LotName, long LengthCm, string ShipmentNo, string ShippedAt, string? VoidReason, string? VoidedAt);

public sealed record ExceptionsResponse(
    IReadOnlyList<QuarantineInfo> Quarantined,
    IReadOnlyList<VoidedAfterShipInfo> VoidedAfterShip,
    IReadOnlyList<ShipmentSummary> StaleShipments,
    IReadOnlyList<ShipmentSummary> RecentConfirmed,
    long UnmatchedLotCount);

// ---------- Raporlar (gerçek kayıtlardan; tarih aralığı fabrikanın yerel günüyle) ----------

public sealed record StockByLotRow(string Lot, bool Unmatched, long Count, long LengthCm, string? OldestQcAt);

public sealed record DailyShipmentRow(string Day, long Shipments, long Rolls, long LengthCm);

public sealed record ShippedRollRow(string CutId, string Lot, long LengthCm, string ShipmentNo, string ShippedAt, string ShippedBy);

public sealed record ShipmentHistoryRow(
    string ShipmentNo, string Status, string Lots, long Rolls, long LengthCm,
    string OpenedBy, string OpenedAt, string? ClosedBy, string? ClosedAt, string? Note);
