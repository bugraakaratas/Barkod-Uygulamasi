namespace KumasBarkod.Core.Data;

public sealed record Migration(int Version, string Description, string Sql);

/// <summary>
/// Şema geçmişi. Yayınlanmış bir göç ASLA değiştirilmez; değişiklik için yeni sürüm eklenir.
/// </summary>
public static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "İlk şema: cuts, sync_outbox, station_counter, settings", """
            CREATE TABLE cuts (
                cut_id          TEXT    NOT NULL PRIMARY KEY,
                station_code    TEXT    NOT NULL CHECK (length(station_code) = 2 AND station_code = upper(station_code)),
                sequence_no     INTEGER NOT NULL CHECK (sequence_no BETWEEN 1 AND 999999),
                lot_code        TEXT    NOT NULL CHECK (length(lot_code) > 0 AND lot_code = trim(lot_code)),
                length_cm       INTEGER NOT NULL CHECK (length_cm > 0),
                operator        TEXT,
                status          TEXT    NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'voided')),
                void_reason     TEXT,
                voided_at       TEXT,
                created_at      TEXT    NOT NULL,
                print_count     INTEGER NOT NULL DEFAULT 0 CHECK (print_count >= 0),
                last_printed_at TEXT,

                UNIQUE (station_code, sequence_no),
                -- ID her zaman istasyon + sıra numarasından türetilir: K1-000123
                CHECK (cut_id = station_code || '-' || printf('%06d', sequence_no)),
                -- İptal bilgisi yalnızca iptal edilmiş kayıtta bulunur
                CHECK ((status = 'active' AND voided_at IS NULL AND void_reason IS NULL)
                    OR (status = 'voided' AND voided_at IS NOT NULL))
            ) STRICT;

            CREATE INDEX ix_cuts_lot_code   ON cuts (lot_code);
            CREATE INDEX ix_cuts_created_at ON cuts (created_at);

            CREATE TABLE sync_outbox (
                id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                cut_id      TEXT    NOT NULL REFERENCES cuts (cut_id),
                operation   TEXT    NOT NULL CHECK (operation IN ('upsert')),
                created_at  TEXT    NOT NULL,
                sent_at     TEXT,
                attempts    INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
                last_error  TEXT
            ) STRICT;

            -- Gönderici yalnızca bekleyenleri okur; küçük kısmi indeks yeterli
            CREATE INDEX ix_sync_outbox_pending ON sync_outbox (id) WHERE sent_at IS NULL;

            CREATE TABLE station_counter (
                station_code  TEXT    NOT NULL PRIMARY KEY CHECK (length(station_code) = 2),
                last_sequence INTEGER NOT NULL DEFAULT 0 CHECK (last_sequence BETWEEN 0 AND 999999)
            ) STRICT;

            CREATE TABLE settings (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            ) STRICT;
            """),
    ];

    public static int LatestVersion => All[^1].Version;
}
