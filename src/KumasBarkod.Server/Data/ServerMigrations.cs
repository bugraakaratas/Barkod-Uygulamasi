using KumasBarkod.Core.Data;

namespace KumasBarkod.Server.Data;

/// <summary>Top (stok) durumları. Değerler veritabanı CHECK'iyle aynı olmalı.</summary>
public static class RollStates
{
    /// <summary>Depoda, sevkiyata uygun (etiketi basılan top doğrudan stoktadır).</summary>
    public const string InStock = "in_stock";
    /// <summary>Açık bir sevkiyatta okutuldu, onay bekliyor (Faz 4).</summary>
    public const string Reserved = "reserved";
    /// <summary>Sevkiyat onaylandı.</summary>
    public const string Shipped = "shipped";
    /// <summary>QC'de iptal edildi; stokta sayılmaz.</summary>
    public const string Voided = "voided";
    /// <summary>Aynı ID'yi taşıyan iki top var; yetkili çözene kadar sevk edilemez.</summary>
    public const string Quarantined = "quarantined";
}

/// <summary>Hareket türleri. Değerler veritabanı CHECK'iyle aynı olmalı.</summary>
public static class MovementKinds
{
    public const string QcCreated = "qc_created";
    public const string QcVoided = "qc_voided";
    public const string LotAssigned = "lot_assigned";
    public const string Received = "received";
    public const string Reserved = "reserved";
    public const string Unreserved = "unreserved";
    public const string Shipped = "shipped";
    public const string ShipReversed = "ship_reversed";
    public const string Quarantined = "quarantined";
    public const string Released = "released";
    public const string Adjusted = "adjusted";
}

/// <summary>
/// Depo sunucusu şeması. Yayınlanmış göç ASLA değiştirilmez; değişiklik için yeni sürüm eklenir.
/// SQLite CHECK kısıtını sonradan değiştiremediği için durum/hareket listeleri ilk sürümde tamdır.
/// </summary>
public static class ServerMigrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "Sunucu ilk şema: stations, rolls, stock_movements, cut_conflicts", """
            -- İstasyon kodu → bilgisayar. Aynı kodla ikinci bir bilgisayar kesim gönderemez
            -- (aynı ID'nin iki ayrı topa verilmesinin kaynağı kapanır).
            CREATE TABLE stations (
                station_code  TEXT NOT NULL PRIMARY KEY
                              CHECK (length(station_code) = 2 AND station_code = upper(station_code)),
                install_id    TEXT NOT NULL CHECK (length(install_id) BETWEEN 1 AND 100),
                app_version   TEXT,
                registered_at TEXT NOT NULL,
                last_seen_at  TEXT NOT NULL
            ) STRICT;

            -- Top = QC kesiminin sunucudaki yansısı VE stok satırının kendisi (ayrı stok tablosu yok).
            -- QC alanları istasyondan gelir ve sunucuda değiştirilmez; state yalnızca sunucuda değişir.
            CREATE TABLE rolls (
                cut_id          TEXT    NOT NULL PRIMARY KEY,
                station_code    TEXT    NOT NULL,
                sequence_no     INTEGER NOT NULL CHECK (sequence_no BETWEEN 1 AND 999999),
                lot_code_raw    TEXT    NOT NULL CHECK (length(lot_code_raw) > 0 AND lot_code_raw = trim(lot_code_raw)),
                length_cm       INTEGER NOT NULL CHECK (length_cm > 0),
                operator        TEXT,
                qc_status       TEXT    NOT NULL CHECK (qc_status IN ('active', 'voided')),
                void_reason     TEXT,
                voided_at       TEXT,
                qc_created_at   TEXT    NOT NULL,
                print_count     INTEGER NOT NULL DEFAULT 0 CHECK (print_count >= 0),
                last_printed_at TEXT,
                state           TEXT    NOT NULL
                                CHECK (state IN ('in_stock', 'reserved', 'shipped', 'voided', 'quarantined')),
                first_seen_at   TEXT    NOT NULL,
                updated_at      TEXT    NOT NULL,
                version         INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),

                UNIQUE (station_code, sequence_no),
                CHECK (cut_id = station_code || '-' || printf('%06d', sequence_no)),
                CHECK ((qc_status = 'active' AND voided_at IS NULL AND void_reason IS NULL)
                    OR (qc_status = 'voided' AND voided_at IS NOT NULL)),
                -- 'voided' durumu yalnızca QC iptalinden gelir
                CHECK (state <> 'voided' OR qc_status = 'voided')
            ) STRICT;

            CREATE INDEX ix_rolls_state_lot ON rolls (state, lot_code_raw);
            CREATE INDEX ix_rolls_lot       ON rolls (lot_code_raw);
            CREATE INDEX ix_rolls_created   ON rolls (qc_created_at);

            -- Durum makinesi veritabanında da korunur: uygulamada hata olsa bile yanlış geçiş yazılamaz.
            CREATE TRIGGER trg_rolls_state_transition
            BEFORE UPDATE OF state ON rolls
            WHEN NEW.state <> OLD.state AND NOT (
                   (OLD.state = 'in_stock'    AND NEW.state IN ('reserved', 'voided', 'quarantined'))
                OR (OLD.state = 'reserved'    AND NEW.state IN ('in_stock', 'shipped', 'voided', 'quarantined'))
                OR (OLD.state = 'shipped'     AND NEW.state IN ('in_stock', 'quarantined'))
                OR (OLD.state = 'voided'      AND NEW.state IN ('quarantined'))
                OR (OLD.state = 'quarantined' AND NEW.state IN ('in_stock', 'reserved', 'shipped', 'voided'))
            )
            BEGIN
                SELECT RAISE(ABORT, 'izin verilmeyen durum geçişi');
            END;

            -- QC alanları sunucuda değişmez (etiket ile kayıt hiçbir zaman ayrışmasın).
            CREATE TRIGGER trg_rolls_qc_immutable
            BEFORE UPDATE OF cut_id, station_code, sequence_no, lot_code_raw, length_cm, operator, qc_created_at ON rolls
            WHEN NEW.cut_id IS NOT OLD.cut_id OR NEW.station_code IS NOT OLD.station_code
              OR NEW.sequence_no IS NOT OLD.sequence_no OR NEW.lot_code_raw IS NOT OLD.lot_code_raw
              OR NEW.length_cm IS NOT OLD.length_cm OR NEW.operator IS NOT OLD.operator
              OR NEW.qc_created_at IS NOT OLD.qc_created_at
            BEGIN
                SELECT RAISE(ABORT, 'QC alanları değiştirilemez');
            END;

            -- QC iptali geri alınamaz.
            CREATE TRIGGER trg_rolls_void_permanent
            BEFORE UPDATE OF qc_status ON rolls
            WHEN OLD.qc_status = 'voided' AND NEW.qc_status <> 'voided'
            BEGIN
                SELECT RAISE(ABORT, 'QC iptali geri alınamaz');
            END;

            CREATE TRIGGER trg_rolls_no_delete BEFORE DELETE ON rolls
            BEGIN
                SELECT RAISE(ABORT, 'top kaydı silinemez');
            END;

            -- Hareket defteri: yalnızca eklenir. Bir topun kalite kontrolden itibaren bütün geçmişi.
            CREATE TABLE stock_movements (
                movement_id   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                cut_id        TEXT    NOT NULL REFERENCES rolls (cut_id),
                kind          TEXT    NOT NULL CHECK (kind IN ('qc_created', 'qc_voided', 'lot_assigned', 'received',
                                  'reserved', 'unreserved', 'shipped', 'ship_reversed', 'quarantined', 'released', 'adjusted')),
                from_state    TEXT    CHECK (from_state IN ('in_stock', 'reserved', 'shipped', 'voided', 'quarantined')),
                to_state      TEXT    NOT NULL CHECK (to_state IN ('in_stock', 'reserved', 'shipped', 'voided', 'quarantined')),
                shipment_id   INTEGER,
                scan_event_id INTEGER,
                user_id       INTEGER,
                device_id     TEXT,
                station_code  TEXT,
                occurred_at   TEXT    NOT NULL,
                recorded_at   TEXT    NOT NULL,
                note          TEXT,
                CHECK ((kind = 'qc_created') = (from_state IS NULL))
            ) STRICT;

            CREATE INDEX ix_movements_cut      ON stock_movements (cut_id, movement_id);
            CREATE INDEX ix_movements_recorded ON stock_movements (recorded_at);

            CREATE TRIGGER trg_movements_no_update BEFORE UPDATE ON stock_movements
            BEGIN
                SELECT RAISE(ABORT, 'hareket kaydı değiştirilemez');
            END;
            CREATE TRIGGER trg_movements_no_delete BEFORE DELETE ON stock_movements
            BEGIN
                SELECT RAISE(ABORT, 'hareket kaydı silinemez');
            END;

            -- Aynı ID'yi taşıyan ikinci top (farklı kayıt zamanı/verisi). Ezilmez; kanıt olarak saklanır.
            CREATE TABLE cut_conflicts (
                conflict_id   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                cut_id        TEXT    NOT NULL REFERENCES rolls (cut_id),
                created_at    TEXT    NOT NULL,
                station_code  TEXT    NOT NULL,
                install_id    TEXT    NOT NULL,
                payload_json  TEXT    NOT NULL,
                received_at   TEXT    NOT NULL,
                resolved_at   TEXT,
                resolution    TEXT,
                UNIQUE (cut_id, created_at, payload_json)
            ) STRICT;

            CREATE TABLE server_settings (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            ) STRICT;
            """),
    ];

    public static int LatestVersion => All[^1].Version;
}
