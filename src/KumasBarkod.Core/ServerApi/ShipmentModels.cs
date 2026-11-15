namespace KumasBarkod.Core.ServerApi;

/// <summary>Sevkiyat ve personel uç noktaları (WPF rıhtım ekranı ve el terminali web sayfası ortak kullanır).</summary>
public static class ShipmentPaths
{
    public const string Login = "api/v1/auth/login";
    public const string Logout = "api/v1/auth/logout";
    public const string Me = "api/v1/auth/me";
    public const string Users = "api/v1/users";
    public const string BootstrapNeeded = "api/v1/users/bootstrap-needed";
    public const string Shipments = "api/v1/shipments";
}

/// <summary>Personel rolleri.</summary>
public static class UserRoles
{
    /// <summary>Depo / sevkiyat personeli: sevkiyat açar, okutur, çıkarır, onaylar, iptal eder.</summary>
    public const string Warehouse = "warehouse";
    /// <summary>Yetkili: yukarıdakiler + personel yönetimi (Faz 5: onayı geri alma, istisnalar).</summary>
    public const string Supervisor = "supervisor";

    public static string Display(string role) => role == Supervisor ? "Yetkili" : "Depo";
}

public sealed record LoginRequest(string BadgeCode, string Pin);

public sealed record UserInfo(long UserId, string Name, string Role, string BadgeCode, bool Active);

public sealed record LoginResponse(string Token, UserInfo User, string ExpiresAt);

public sealed record UserCreateRequest(string Name, string Role, string BadgeCode, string Pin);

/// <summary>Boş alan = değişmez.</summary>
public sealed record UserUpdateRequest(string? Name, string? Role, bool? Active, string? Pin);

public sealed record BootstrapNeededResponse(bool Needed);

/// <summary>Sevkiyat durumları.</summary>
public static class ShipmentStatuses
{
    public const string Open = "open";
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";
    public const string Reversed = "reversed";

    public static string Display(string s) => s switch
    {
        Open => "Açık (yükleniyor)",
        Confirmed => "Onaylandı (sevk edildi)",
        Cancelled => "İptal edildi",
        Reversed => "Onay geri alındı",
        _ => s,
    };
}

/// <summary>Okutma sonuç kodları. Türkçe mesaj sunucudan gelir; istemci yalnızca renk/sesi önem derecesinden seçer.</summary>
public static class ScanCodes
{
    public const string Ok = "OK";
    public const string Removed = "REMOVED";
    public const string InvalidBarcode = "INVALID_BARCODE";
    public const string NotFound = "NOT_FOUND";
    public const string ShipmentNotOpen = "SHIPMENT_NOT_OPEN";
    public const string Quarantined = "QUARANTINED";
    public const string Voided = "VOIDED";
    public const string LotUnassigned = "LOT_UNASSIGNED";
    public const string WrongLot = "WRONG_LOT";
    public const string AlreadyInThisShipment = "ALREADY_IN_THIS_SHIPMENT";
    public const string ReservedInOtherShipment = "RESERVED_IN_OTHER_SHIPMENT";
    public const string AlreadyShipped = "ALREADY_SHIPPED";
    public const string NotAvailable = "NOT_AVAILABLE";
    public const string NotInThisShipment = "NOT_IN_THIS_SHIPMENT";
}

/// <summary>Önem derecesi: success = yeşil, warning = sarı (zararsız), error = kırmızı (top tıra GİRMEMELİ).</summary>
public static class ScanSeverity
{
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";
}

public sealed record ShipmentOpenRequest(IReadOnlyList<long> LotIds);

public sealed record ShipmentLotInfo(long LotId, string Name, long RemainingCount, long RemainingCm);

public sealed record ShipmentStats(long Count, long LengthCm, long Rejected, long RemainingCount, long RemainingCm);

public sealed record ShipmentSummary(
    long ShipmentId, string ShipmentNo, string Status, string LotNames, long Count, long LengthCm,
    string OpenedBy, string OpenedAt, string LastActivityAt, string? ClosedAt, string? ClosedBy);

public sealed record ShipmentItemInfo(string CutId, string Barcode, string LotName, long LengthCm, string AddedAt, string AddedBy);

public sealed record ScanEventInfo(long ScanEventId, string Kind, string RawInput, string? CutId, string Code, bool Accepted,
    string Message, string ServerTime, string User);

public sealed record ShipmentDetail(
    long ShipmentId, string ShipmentNo, string Status, long Version,
    string OpenedBy, string OpenedAt, string LastActivityAt, string? ClosedAt, string? ClosedBy, string? CloseReason,
    IReadOnlyList<ShipmentLotInfo> Lots, ShipmentStats Stats,
    IReadOnlyList<ShipmentItemInfo> Items, IReadOnlyList<ScanEventInfo> RecentScans,
    IReadOnlyList<string> Warnings);

/// <param name="ClientScanId">İstemcinin ürettiği tekil kimlik: ağ kopup tekrar gönderilince aynı okutma ikinci kez işlenmez.</param>
public sealed record ScanRequest(string ClientScanId, string RawInput);

public sealed record ScanRoll(string CutId, string Barcode, string LotName, long LengthCm);

public sealed record ScanResponse(
    bool Accepted, string Code, string Severity, string Message, ScanRoll? Roll, ShipmentStats Stats, long ShipmentVersion);

/// <param name="RequestId">Tekrar gönderimde ikinci kez işlenmesin.</param>
public sealed record ShipmentConfirmRequest(long ExpectedVersion, string RequestId);

public sealed record ShipmentCancelRequest(string Reason, string RequestId);

public static class CancelReasons
{
    public const string TruckNotCame = "Tır gelmedi / yükleme ertelendi";
    public const string WrongLot = "Yanlış lot seçildi";
    public const string Mistake = "Yanlışlıkla açıldı / deneme";

    public static IReadOnlyList<string> All { get; } = [TruckNotCame, WrongLot, Mistake];
}

/// <summary>Sevkiyat hata kodları (4xx).</summary>
public static class ShipmentErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string Locked = "LOCKED";
    public const string NotFound = "SHIPMENT_NOT_FOUND";
    public const string NotOpen = "SHIPMENT_NOT_OPEN";
    public const string VersionMismatch = "VERSION_MISMATCH";
    public const string Empty = "SHIPMENT_EMPTY";
    public const string InvalidLots = "INVALID_LOTS";
    public const string UserInvalid = "USER_INVALID";
    public const string BadgeExists = "BADGE_EXISTS";
}
