using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server.Ingest;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Server.Auth;

/// <summary>Giriş yapmış personel (her sevkiyat işlemine yazılır).</summary>
public sealed record CurrentUser(long UserId, string Name, string Role)
{
    public bool IsSupervisor => Role == UserRoles.Supervisor;
}

/// <summary>
/// Personel girişi ve yönetimi. Giriş: kart kodu (okutulur ya da yazılır) + 4–6 haneli PIN.
/// <list type="bullet">
/// <item>PIN düz saklanmaz: PBKDF2-SHA256 (100.000 tur) + kişiye özel tuz.</item>
/// <item>5 yanlış PIN → 5 dk kilit. Hata mesajı kartın var olup olmadığını söylemez.</item>
/// <item>Oturum belirteci rastgele 32 bayt; veritabanında yalnızca SHA-256 özeti tutulur. 12 saat geçerli (vardiya).</item>
/// <item>Personel silinmez, pasif yapılır (geçmiş kayıtlarda adı kalır). Son etkin yetkili pasif yapılamaz.</item>
/// <item>Hiç personel yokken ilk kayıt (yalnızca yetkili rolüyle) giriş istemez — kurulumdaki ilk adım.</item>
/// </list>
/// Bu, yerel ağda "kim yaptı" kaydı içindir; gerçek bir güvenlik sınırı değildir (bkz. CLAUDE.md).
/// </summary>
public sealed class AuthService(SqliteConnectionFactory db, TimeProvider clock)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    /// <summary>PBKDF2 tur sayısı. Kayda tur sayısıyla yazılır ("100000:özet"): değer ileride artırılırsa eski PIN'ler çalışmaya devam eder. Testler düşürür.</summary>
    internal static int Iterations { get; set; } = DefaultIterations;

    public const int DefaultIterations = 100_000;

    public LoginResponse Login(LoginRequest? request, string? device)
    {
        var badge = NormalizeBadge(request?.BadgeCode);
        var pin = request?.Pin ?? "";
        var now = clock.GetUtcNow();

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var u = c.QuerySingleOrDefault<UserRow>($"SELECT {UserRow.Columns} FROM users WHERE badge_code = @badge", new { badge }, tx);

        if (u is null || u.Active == 0)
            throw Failed();

        if (u.LockedUntil is not null && string.CompareOrdinal(u.LockedUntil, Timestamps.Format(now)) > 0)
            throw new ApiException(423, ShipmentErrorCodes.Locked,
                $"Çok fazla yanlış PIN. {Local(u.LockedUntil):HH:mm}'e kadar bekleyin ya da yetkiliye başvurun.");

        if (!Verify(pin, u.PinSalt, u.PinHash))
        {
            var failed = u.FailedAttempts + 1;
            string? lockUntil = failed >= MaxFailedAttempts ? Timestamps.Format(now + LockDuration) : null;
            c.Execute("UPDATE users SET failed_attempts = @f, locked_until = @l WHERE user_id = @id",
                new { f = lockUntil is null ? failed : 0, l = lockUntil, id = u.UserId }, tx);
            tx.Commit();
            throw lockUntil is null ? Failed()
                : new ApiException(423, ShipmentErrorCodes.Locked, $"Çok fazla yanlış PIN. {LockDuration.TotalMinutes:0} dakika bekleyin.");
        }

        c.Execute("UPDATE users SET failed_attempts = 0, locked_until = NULL WHERE user_id = @id", new { id = u.UserId }, tx);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = Timestamps.Format(now + SessionLifetime);
        c.Execute("""
            INSERT INTO auth_sessions (token_hash, user_id, device, created_at, last_seen_at, expires_at)
            VALUES (@hash, @id, @device, @now, @now, @expires)
            """, new { hash = Hash(token), id = u.UserId, device, now = Timestamps.Format(now), expires }, tx);
        tx.Commit();
        return new LoginResponse(token, u.ToInfo(), expires);
    }

    public void Logout(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var c = db.Open();
        c.Execute("UPDATE auth_sessions SET ended_at = @now WHERE token_hash = @hash AND ended_at IS NULL",
            new { now = Timestamps.Format(clock.GetUtcNow()), hash = Hash(token) });
    }

    /// <summary>Belirteçten personel. Yoksa/süresi dolduysa/personel pasifse 401.</summary>
    public CurrentUser Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            throw new ApiException(401, ShipmentErrorCodes.Unauthorized, "Giriş yapın (kart + PIN).");
        var now = Timestamps.Format(clock.GetUtcNow());
        using var c = db.Open();
        var rows = c.Query<(long Id, string Name, string Role, long Active, string Expires, string? Ended, string LastSeen)>("""
            SELECT u.user_id, u.name, u.role, u.active, s.expires_at, s.ended_at, s.last_seen_at
            FROM auth_sessions s JOIN users u ON u.user_id = s.user_id WHERE s.token_hash = @hash
            """, new { hash = Hash(token) }).ToList();
        if (rows.Count == 0 || rows[0].Ended is not null || rows[0].Active == 0 || string.CompareOrdinal(rows[0].Expires, now) <= 0)
            throw new ApiException(401, ShipmentErrorCodes.Unauthorized, "Oturum sona erdi; yeniden giriş yapın.");
        var x = rows[0];
        // "Son görülme" en fazla dakikada bir yazılır (her okutmada yazma yükü olmasın).
        if (string.CompareOrdinal(x.LastSeen, Timestamps.Format(clock.GetUtcNow().AddMinutes(-1))) < 0)
            c.Execute("UPDATE auth_sessions SET last_seen_at = @now WHERE token_hash = @hash", new { now, hash = Hash(token) });
        return new CurrentUser(x.Id, x.Name, x.Role);
    }

    // ---------- Personel yönetimi ----------

    public bool BootstrapNeeded()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM users") == 0;
    }

    public IReadOnlyList<UserInfo> List(CurrentUser actor)
    {
        RequireSupervisor(actor);
        using var c = db.Open();
        return c.Query<UserRow>($"SELECT {UserRow.Columns} FROM users ORDER BY active DESC, name COLLATE NOCASE")
            .Select(u => u.ToInfo()).ToList();
    }

    /// <param name="actor">Giriş yapan yetkili; hiç personel yokken null olabilir (ilk yetkili).</param>
    public UserInfo Create(UserCreateRequest? request, CurrentUser? actor)
    {
        var name = ValidateName(request?.Name);
        var role = ValidateRole(request?.Role);
        var badge = NormalizeBadge(request?.BadgeCode);
        ValidateBadge(badge);
        var pin = ValidatePin(request?.Pin);
        var now = Timestamps.Format(clock.GetUtcNow());

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var empty = c.ExecuteScalar<long>("SELECT count(*) FROM users", transaction: tx) == 0;
        if (!empty) RequireSupervisor(actor);
        if (empty && role != UserRoles.Supervisor)
            throw new ApiException(400, ShipmentErrorCodes.UserInvalid, "İlk personel yetkili olmalı.");
        if (c.ExecuteScalar<long>("SELECT count(*) FROM users WHERE badge_code = @badge", new { badge }, tx) > 0)
            throw new ApiException(409, ShipmentErrorCodes.BadgeExists, $"{badge} kart kodu başka bir personelde.");

        var salt = RandomNumberGenerator.GetBytes(16);
        var id = c.ExecuteScalar<long>("""
            INSERT INTO users (name, role, badge_code, pin_hash, pin_salt, created_at, created_by)
            VALUES (@name, @role, @badge, @hash, @salt, @now, @by) RETURNING user_id
            """, new { name, role, badge, hash = HashPin(pin, salt), salt = Convert.ToBase64String(salt), now,
                       by = actor?.Name ?? "ilk kurulum" }, tx);
        tx.Commit();
        return Get(id);
    }

    public UserInfo Update(long userId, UserUpdateRequest? request, CurrentUser actor)
    {
        RequireSupervisor(actor);
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var u = c.QuerySingleOrDefault<UserRow>($"SELECT {UserRow.Columns} FROM users WHERE user_id = @userId", new { userId }, tx)
            ?? throw new ApiException(404, ShipmentErrorCodes.UserInvalid, "Personel bulunamadı.");

        var name = request?.Name is null ? u.Name : ValidateName(request.Name);
        var role = request?.Role is null ? u.Role : ValidateRole(request.Role);
        var active = request?.Active ?? u.Active == 1;

        // Kimse kilitli kalmasın: son etkin yetkili pasif yapılamaz / rolü düşürülemez.
        var losesSupervisor = u.Role == UserRoles.Supervisor && u.Active == 1 && (!active || role != UserRoles.Supervisor);
        if (losesSupervisor && c.ExecuteScalar<long>(
                "SELECT count(*) FROM users WHERE role = 'supervisor' AND active = 1 AND user_id <> @userId", new { userId }, tx) == 0)
            throw new ApiException(400, ShipmentErrorCodes.UserInvalid, "Son etkin yetkili pasif yapılamaz; önce başka bir yetkili ekleyin.");

        c.Execute("UPDATE users SET name = @name, role = @role, active = @a WHERE user_id = @userId",
            new { name, role, a = active ? 1 : 0, userId }, tx);
        if (request?.Pin is not null)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            c.Execute("UPDATE users SET pin_hash = @h, pin_salt = @s, failed_attempts = 0, locked_until = NULL WHERE user_id = @userId",
                new { h = HashPin(ValidatePin(request.Pin), salt), s = Convert.ToBase64String(salt), userId }, tx);
        }
        // Pasif yapılan personelin açık oturumları kapanır.
        if (!active)
            c.Execute("UPDATE auth_sessions SET ended_at = @now WHERE user_id = @userId AND ended_at IS NULL",
                new { now = Timestamps.Format(clock.GetUtcNow()), userId }, tx);
        tx.Commit();
        return Get(userId);
    }

    public static void RequireSupervisor(CurrentUser? actor)
    {
        if (actor is null) throw new ApiException(401, ShipmentErrorCodes.Unauthorized, "Giriş yapın (kart + PIN).");
        if (!actor.IsSupervisor) throw new ApiException(403, ShipmentErrorCodes.Forbidden, "Bu işlem için yetkili girişi gerekir.");
    }

    /// <summary>Kart kodu: kırpılır, büyük harf (Türkçe klavye: ı/i/İ → I), ayırıcılar korunur.</summary>
    public static string NormalizeBadge(string? badge)
    {
        var sb = new StringBuilder();
        foreach (var ch in (badge ?? "").Trim())
            sb.Append(ch is 'ı' or 'i' or 'İ' ? 'I' : char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    private static void ValidateBadge(string badge)
    {
        if (badge.Length is < 3 or > 32 || badge.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ApiException(400, ShipmentErrorCodes.UserInvalid, "Kart kodu 3–32 karakter olmalı: harf, rakam, - ya da _.");
        if (CutIds.TryParseScan(badge, out _))
            throw new ApiException(400, ShipmentErrorCodes.UserInvalid, "Kart kodu bir top barkoduna benziyor; farklı bir kod verin (ör. P0001).");
    }

    private static string ValidatePin(string? pin)
    {
        if (pin is null || pin.Length is < 4 or > 6 || !pin.All(char.IsAsciiDigit))
            throw new ApiException(400, ShipmentErrorCodes.UserInvalid, "PIN 4–6 rakam olmalı.");
        return pin;
    }

    private static string ValidateName(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length is 0 or > 60 || n.Any(char.IsControl))
            throw new ApiException(400, ShipmentErrorCodes.UserInvalid, "Ad 1–60 karakter olmalı.");
        return n;
    }

    private static string ValidateRole(string? role) => role is UserRoles.Warehouse or UserRoles.Supervisor
        ? role
        : throw new ApiException(400, ShipmentErrorCodes.UserInvalid, "Rol: warehouse (depo) ya da supervisor (yetkili).");

    private UserInfo Get(long id)
    {
        using var c = db.Open();
        return c.QuerySingle<UserRow>($"SELECT {UserRow.Columns} FROM users WHERE user_id = @id", new { id }).ToInfo();
    }

    private static ApiException Failed() => new(401, ShipmentErrorCodes.LoginFailed, "Kart kodu ya da PIN hatalı.");

    private static DateTime Local(string utc) => Timestamps.Parse(utc).ToLocalTime();

    private static string HashPin(string pin, byte[] salt)
    {
        var n = Iterations;
        return n.ToString(CultureInfo.InvariantCulture) + ":" +
               Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(pin, salt, n, HashAlgorithmName.SHA256, 32));
    }

    private static bool Verify(string pin, string salt, string stored)
    {
        var sep = stored.IndexOf(':');
        if (sep <= 0 || !int.TryParse(stored[..sep], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return false;
        var computed = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(salt), n, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(stored[(sep + 1)..]));
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class UserRow
    {
        public const string Columns = """
            user_id AS UserId, name AS Name, role AS Role, badge_code AS BadgeCode, pin_hash AS PinHash, pin_salt AS PinSalt,
            active AS Active, failed_attempts AS FailedAttempts, locked_until AS LockedUntil
            """;
        public long UserId { get; init; }
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public string BadgeCode { get; init; } = "";
        public string PinHash { get; init; } = "";
        public string PinSalt { get; init; } = "";
        public long Active { get; init; }
        public long FailedAttempts { get; init; }
        public string? LockedUntil { get; init; }
        public UserInfo ToInfo() => new(UserId, Name, Role, BadgeCode, Active == 1);
    }
}
