using Dapper;
using KumasBarkod.Core.Data;

namespace KumasBarkod.Server.Data;

/// <summary>
/// Veri bütünlüğü kontrolü: her testin sonunda ve sunucuda günde bir çalışır. Boş liste = tutarlı.
/// Kısıtlar ve tetikleyiciler yanlış yazmayı engeller; bu kontrol "her şey birbiriyle uyuşuyor mu" sorusunu yanıtlar.
/// </summary>
public static class ServerInvariants
{
    public static IReadOnlyList<string> Check(SqliteConnectionFactory db)
    {
        using var c = db.Open();
        var problems = new List<string>();

        // 1) Topun durumu = son hareketin vardığı durum (durum hareket geçmişinden türetilebilir olmalı).
        problems.AddRange(c.Query<string>("""
            SELECT r.cut_id || ': durum ' || r.state || ', son hareket ' || coalesce(m.to_state, 'YOK')
            FROM rolls r
            LEFT JOIN stock_movements m ON m.movement_id =
                (SELECT max(movement_id) FROM stock_movements WHERE cut_id = r.cut_id)
            WHERE m.to_state IS NOT r.state
            """));

        // 2) Her topun tam bir 'qc_created' hareketi var ve ilk hareket odur.
        problems.AddRange(c.Query<string>("""
            SELECT r.cut_id || ': qc_created hareketi ' || count(m.movement_id) || ' adet'
            FROM rolls r LEFT JOIN stock_movements m ON m.cut_id = r.cut_id AND m.kind = 'qc_created'
            GROUP BY r.cut_id HAVING count(m.movement_id) <> 1
            """));
        problems.AddRange(c.Query<string>("""
            SELECT r.cut_id || ': ilk hareket qc_created değil'
            FROM rolls r JOIN stock_movements m ON m.movement_id =
                (SELECT min(movement_id) FROM stock_movements WHERE cut_id = r.cut_id)
            WHERE m.kind <> 'qc_created'
            """));

        // 3) Hareket zinciri kesintisiz: her hareketin çıkış durumu bir öncekinin vardığı durum.
        problems.AddRange(c.Query<string>("""
            SELECT m.cut_id || ': hareket ' || m.movement_id || ' ' || m.from_state || ' durumundan çıkıyor, önceki ' || p.to_state
            FROM stock_movements m
            JOIN stock_movements p ON p.movement_id =
                (SELECT max(movement_id) FROM stock_movements WHERE cut_id = m.cut_id AND movement_id < m.movement_id)
            WHERE m.from_state IS NOT p.to_state
            """));

        // 4) Karantinadaki her topun çözülmemiş bir çakışma kaydı var.
        problems.AddRange(c.Query<string>("""
            SELECT r.cut_id || ': karantinada ama çakışma kaydı yok'
            FROM rolls r
            WHERE r.state = 'quarantined'
              AND NOT EXISTS (SELECT 1 FROM cut_conflicts k WHERE k.cut_id = r.cut_id AND k.resolved_at IS NULL)
            """));

        return problems;
    }
}
