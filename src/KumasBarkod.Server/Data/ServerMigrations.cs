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

        new(2, "Lot (= firma) tanımları: lots, lot_aliases, rolls.lot_id, veri sürümü", """
            -- Lot = firma. Ad ekranda yazıldığı gibi; lot_key yazım farkını yok sayan anahtar (LotKeys.Key).
            CREATE TABLE lots (
                lot_id     INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                name       TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 50 AND name = trim(name)),
                lot_key    TEXT    NOT NULL UNIQUE CHECK (length(lot_key) > 0),
                active     INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
                created_at TEXT    NOT NULL,
                created_by TEXT,
                updated_at TEXT    NOT NULL,
                updated_by TEXT
            ) STRICT;

            -- Lot silinmez (geçmiş topları gösteriyor); kullanılmayacaksa pasif yapılır.
            CREATE TRIGGER trg_lots_no_delete BEFORE DELETE ON lots
            BEGIN
                SELECT RAISE(ABORT, 'lot silinemez; pasif yapın');
            END;

            -- QC'de yazılmış lot metni → lot (yetkili eşleştirdi). Yazım farkından fazlası olan metinler için.
            CREATE TABLE lot_aliases (
                raw_text   TEXT    NOT NULL PRIMARY KEY,
                lot_id     INTEGER NOT NULL REFERENCES lots (lot_id),
                created_at TEXT    NOT NULL,
                created_by TEXT
            ) STRICT;

            ALTER TABLE rolls ADD COLUMN lot_id INTEGER REFERENCES lots (lot_id);
            CREATE INDEX ix_rolls_lotid_state ON rolls (lot_id, state);
            CREATE INDEX ix_rolls_unmatched ON rolls (lot_code_raw) WHERE lot_id IS NULL;

            -- Her veri değişikliğinde artar; ekranlar yalnızca bunu sorup değiştiyse yeniden okur.
            INSERT INTO server_settings (key, value) VALUES ('data_version', '0');
            """),

        new(3, "Sevkiyat: users, auth_sessions, shipments, shipment_lots, shipment_items, scan_events", """
            -- Personel: kart kodu (okutulur ya da yazılır) + PIN. PIN düz saklanmaz (PBKDF2 + tuz).
            CREATE TABLE users (
                user_id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                name            TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 60 AND name = trim(name)),
                role            TEXT    NOT NULL CHECK (role IN ('warehouse', 'supervisor')),
                badge_code      TEXT    NOT NULL UNIQUE CHECK (length(badge_code) BETWEEN 3 AND 32),
                pin_hash        TEXT    NOT NULL,
                pin_salt        TEXT    NOT NULL,
                active          INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
                failed_attempts INTEGER NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
                locked_until    TEXT,
                created_at      TEXT    NOT NULL,
                created_by      TEXT
            ) STRICT;
            CREATE TRIGGER trg_users_no_delete BEFORE DELETE ON users
            BEGIN
                SELECT RAISE(ABORT, 'personel silinemez; pasif yapın');
            END;

            -- Oturum: belirteç düz saklanmaz (SHA-256).
            CREATE TABLE auth_sessions (
                token_hash   TEXT    NOT NULL PRIMARY KEY,
                user_id      INTEGER NOT NULL REFERENCES users (user_id),
                device       TEXT,
                created_at   TEXT    NOT NULL,
                last_seen_at TEXT    NOT NULL,
                expires_at   TEXT    NOT NULL,
                ended_at     TEXT
            ) STRICT;

            CREATE TABLE shipment_counter (
                year INTEGER NOT NULL PRIMARY KEY,
                last INTEGER NOT NULL CHECK (last >= 0)
            ) STRICT;

            CREATE TABLE shipments (
                shipment_id      INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                shipment_no      TEXT    NOT NULL UNIQUE,
                status           TEXT    NOT NULL CHECK (status IN ('open', 'confirmed', 'cancelled', 'reversed')),
                opened_by        INTEGER NOT NULL REFERENCES users (user_id),
                opened_at        TEXT    NOT NULL,
                opened_device    TEXT,
                last_activity_at TEXT    NOT NULL,
                closed_by        INTEGER REFERENCES users (user_id),
                closed_at        TEXT,
                close_request_id TEXT,
                close_reason     TEXT,
                version          INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
                CHECK ((status = 'open') = (closed_at IS NULL)),
                CHECK (status <> 'open' OR closed_by IS NULL)
            ) STRICT;

            CREATE TRIGGER trg_shipments_status
            BEFORE UPDATE OF status ON shipments
            WHEN NEW.status <> OLD.status AND NOT (
                   (OLD.status = 'open'      AND NEW.status IN ('confirmed', 'cancelled'))
                OR (OLD.status = 'confirmed' AND NEW.status = 'reversed')
            )
            BEGIN
                SELECT RAISE(ABORT, 'izin verilmeyen sevkiyat durumu geçişi');
            END;
            CREATE TRIGGER trg_shipments_no_delete BEFORE DELETE ON shipments
            BEGIN
                SELECT RAISE(ABORT, 'sevkiyat silinemez');
            END;

            CREATE TABLE shipment_lots (
                shipment_id INTEGER NOT NULL REFERENCES shipments (shipment_id),
                lot_id      INTEGER NOT NULL REFERENCES lots (lot_id),
                PRIMARY KEY (shipment_id, lot_id)
            ) STRICT;

            -- Okutma kaydı (kabul + ret). Yalnızca eklenir; yanıt bir kez yazılır (tekrar gönderimde aynen döner).
            CREATE TABLE scan_events (
                scan_event_id  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                client_scan_id TEXT    NOT NULL UNIQUE CHECK (length(client_scan_id) BETWEEN 1 AND 64),
                shipment_id    INTEGER NOT NULL REFERENCES shipments (shipment_id),
                kind           TEXT    NOT NULL CHECK (kind IN ('add', 'remove')),
                raw_input      TEXT    NOT NULL,
                cut_id         TEXT,
                result_code    TEXT    NOT NULL,
                accepted       INTEGER NOT NULL CHECK (accepted IN (0, 1)),
                user_id        INTEGER NOT NULL REFERENCES users (user_id),
                device         TEXT,
                server_time    TEXT    NOT NULL,
                response_json  TEXT    NOT NULL
            ) STRICT;
            CREATE INDEX ix_scan_events_shipment ON scan_events (shipment_id, scan_event_id);
            CREATE TRIGGER trg_scan_events_no_update BEFORE UPDATE ON scan_events
            WHEN NOT (OLD.response_json = '' AND NEW.response_json <> ''
                      AND NEW.scan_event_id = OLD.scan_event_id AND NEW.client_scan_id = OLD.client_scan_id
                      AND NEW.result_code = OLD.result_code AND NEW.accepted = OLD.accepted
                      AND NEW.cut_id IS OLD.cut_id AND NEW.shipment_id = OLD.shipment_id)
            BEGIN
                SELECT RAISE(ABORT, 'okutma kaydı değiştirilemez');
            END;
            CREATE TRIGGER trg_scan_events_no_delete BEFORE DELETE ON scan_events
            BEGIN
                SELECT RAISE(ABORT, 'okutma kaydı silinemez');
            END;

            -- Sevkiyat kalemi. ASIL KİLİT: bir top aynı anda yalnızca BİR aktif kalemde (açık ya da onaylı sevkiyat).
            CREATE TABLE shipment_items (
                item_id       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                shipment_id   INTEGER NOT NULL REFERENCES shipments (shipment_id),
                cut_id        TEXT    NOT NULL REFERENCES rolls (cut_id),
                status        TEXT    NOT NULL CHECK (status IN ('active', 'removed', 'cancelled', 'reversed')),
                added_at      TEXT    NOT NULL,
                added_by      INTEGER NOT NULL REFERENCES users (user_id),
                scan_event_id INTEGER NOT NULL REFERENCES scan_events (scan_event_id),
                ended_at      TEXT,
                ended_by      INTEGER REFERENCES users (user_id),
                end_reason    TEXT,
                CHECK ((status = 'active') = (ended_at IS NULL))
            ) STRICT;
            CREATE UNIQUE INDEX ux_items_one_active_per_roll ON shipment_items (cut_id) WHERE status = 'active';
            CREATE INDEX ix_items_shipment ON shipment_items (shipment_id, status);
            CREATE TRIGGER trg_items_status
            BEFORE UPDATE ON shipment_items
            WHEN OLD.status <> 'active' OR NEW.cut_id <> OLD.cut_id OR NEW.shipment_id <> OLD.shipment_id
            BEGIN
                SELECT RAISE(ABORT, 'kapanmış sevkiyat kalemi değiştirilemez');
            END;
            CREATE TRIGGER trg_items_no_delete BEFORE DELETE ON shipment_items
            BEGIN
                SELECT RAISE(ABORT, 'sevkiyat kalemi silinemez');
            END;
            """),

        new(4, "Onayı geri alma ve çakışma çözümü bilgisi", """
            -- Onayı geri alma (yalnızca yetkili): kim, ne zaman, neden. Kapanış bilgisi (closed_*) korunur.
            ALTER TABLE shipments ADD COLUMN reversed_by INTEGER REFERENCES users (user_id);
            ALTER TABLE shipments ADD COLUMN reversed_at TEXT;
            ALTER TABLE shipments ADD COLUMN reversal_reason TEXT;
            ALTER TABLE shipments ADD COLUMN reversal_request_id TEXT;

            -- Çakışmayı çözen yetkili
            ALTER TABLE cut_conflicts ADD COLUMN resolved_by INTEGER REFERENCES users (user_id);

            CREATE INDEX ix_shipments_closed ON shipments (status, closed_at);
            """),

        new(5, "Google Sheets kopyası sunucudan: sheets_pending kuyruğu", """
            -- Sheets'in "Kesimler" sayfasına gönderilecek toplar. Satır içeriği gönderim anında rolls'tan
            -- okunur (her zaman güncel hâli). seq: gönderim sürerken top yeniden değişirse satır silinmesin.
            CREATE TABLE sheets_pending (
                cut_id TEXT    NOT NULL PRIMARY KEY,
                seq    INTEGER NOT NULL
            ) STRICT;

            -- Kuyruğa yazmayı unutan kod yolu olmasın: QC alanı değişen her top veritabanının kendisince kuyruğa girer.
            CREATE TRIGGER trg_rolls_sheets_insert AFTER INSERT ON rolls
            BEGIN
                INSERT OR REPLACE INTO sheets_pending (cut_id, seq)
                VALUES (NEW.cut_id, (SELECT coalesce(max(seq), 0) + 1 FROM sheets_pending));
            END;
            CREATE TRIGGER trg_rolls_sheets_update
            AFTER UPDATE OF lot_code_raw, length_cm, operator, qc_status, void_reason, voided_at,
                            qc_created_at, print_count, last_printed_at ON rolls
            BEGIN
                INSERT OR REPLACE INTO sheets_pending (cut_id, seq)
                VALUES (NEW.cut_id, (SELECT coalesce(max(seq), 0) + 1 FROM sheets_pending));
            END;

            -- Mevcut bütün toplar: Sheets ilk kez ayarlandığında tabloya hepsi gider.
            INSERT INTO sheets_pending (cut_id, seq) SELECT cut_id, row_number() OVER (ORDER BY cut_id) FROM rolls;
            """),
    ];

    public static int LatestVersion => All[^1].Version;
}
