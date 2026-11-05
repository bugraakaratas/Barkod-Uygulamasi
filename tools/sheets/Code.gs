/**
 * Kumaş Barkod → Google Sheets kopyası (tek yönlü).
 *
 * Uygulama kesim kayıtlarını toplu olarak POST eder. Bu script her kaydı cut_id'ye göre
 * "varsa güncelle, yoksa ekle" (upsert) yapar. Aynı paket iki kez gelse de (yanıt kaybolup
 * tekrar gönderilirse) her kesim tabloda TEK satır olur.
 *
 * v3 — ID benzersizliği ve sıra:
 *  - Bir kesimin kimliği cut_id + created_at'tir. Tabloda aynı cut_id BAŞKA bir created_at ile
 *    varsa (başka veritabanı aynı ID'yi üretmiş) eski satırın üzerine YAZILMAZ; satır
 *    "conflicts" listesinde döner ve uygulama hata gösterir.
 *  - Yeni satırlar created_at (kayıt zamanı) sırasına göre yerleşir.
 *  - Yanıtta "max_sequences": tablodaki istasyon başına en büyük sıra numarası. Uygulama
 *    sayacını bunun altında tutmaz (yeni kurulum eski ID'leri tekrar üretmesin).
 *
 * v5 — okunabilirlik:
 *  - En yeni kesim EN ÜSTTE: yeni satır başlığın hemen altına girer, sıralama yeniden eskiye.
 *    Binlerce satırda tabloya bakan kişi en son kaydı aramak için aşağı inmez.
 *  - Görünen başlıklar Türkçe. Uygulamayla konuşulan JSON anahtarları İngilizce kalır.
 *  - Eski tablolar elle müdahale gerektirmez: uygulamanın ilk bağlantısında başlıklar çevrilir,
 *    satırlar yeniden eskiye dizilir.
 *
 * Kurulum: tools/sheets/KURULUM.md
 * Asıl veri uygulamadaki SQLite'tadır; bu tablo yalnızca kopyadır. Elle değişiklik
 * bir sonraki gönderimde üzerine yazılır.
 */

// v2: "export" eylemi (geri kurma için satırları sayfa sayfa okur; anahtar gerekir).
// v3: çakışan ID'nin üzerine yazmaz, kayıt zamanına göre sıralar, max_sequences döner.
// v4: tablo herhangi bir sebeple sırasızsa (eski hata, elle sıralama) kendiliğinden yeniden sıralar.
// v5: en yeni kayıt en üstte, Türkçe başlıklar; "export" yine eskiden yeniye verir.
const VERSION = 5;
const EXPORT_MAX_LIMIT = 2000;
const SHEET_NAME = 'Kesimler';

// İç anahtarlar (JSON ile aynı). Sütun sırası sabittir. Geri kurma sütunları başlık adına göre okur.
const COLUMNS = [
  'cut_id', 'station_code', 'sequence_no', 'lot_code', 'length_cm', 'operator',
  'status', 'void_reason', 'voided_at', 'created_at', 'print_count', 'last_printed_at',
  'metre', // rapor kolaylığı: length_cm / 100 (geri kurmada kullanılmaz)
];

// Tabloda görünen başlıklar.
const HEADERS = {
  cut_id: 'ID', station_code: 'İstasyon', sequence_no: 'Sıra No', lot_code: 'Lot',
  length_cm: 'Uzunluk (cm)', operator: 'Operatör', status: 'Durum', void_reason: 'İptal Sebebi',
  voided_at: 'İptal Zamanı', created_at: 'Kayıt Zamanı', print_count: 'Baskı Sayısı',
  last_printed_at: 'Son Baskı', metre: 'Metre',
};

// Sheets'in kendi kafasına göre tarihe/sayıya çevirmemesi için düz metin tutulan sütunlar.
const TEXT_COLUMNS = new Set([
  'cut_id', 'station_code', 'lot_code', 'operator', 'status', 'void_reason',
  'voided_at', 'created_at', 'last_printed_at',
]);

const CUT_ID_PATTERN = /^[A-Z][A-Z0-9]-\d{6}$/;
const COL_CUT_ID = COLUMNS.indexOf('cut_id') + 1;         // 1 tabanlı sütun numaraları
const COL_CREATED_AT = COLUMNS.indexOf('created_at') + 1;

function doPost(e) {
  try {
    const body = JSON.parse(e.postData.contents);
    checkKey_(body.key);

    if (body.action === 'export') {
      return json_(export_(Number(body.offset) || 0, Number(body.limit) || EXPORT_MAX_LIMIT));
    }

    const rows = Array.isArray(body.rows) ? body.rows : [];

    const lock = LockService.getScriptLock();
    if (!lock.tryLock(30000)) {
      return json_({ ok: false, error: 'Tablo meşgul, sonra tekrar denenecek.' });
    }
    try {
      const result = upsert_(rows);
      return json_({
        ok: true, upserted: result.upserted, conflicts: result.conflicts,
        max_sequences: result.maxSequences, version: VERSION,
      });
    } finally {
      lock.releaseLock();
    }
  } catch (err) {
    return json_({ ok: false, error: String((err && err.message) || err) });
  }
}

/** Sağlık kontrolü: veri döndürmez. */
function doGet() {
  return json_({ ok: true, service: 'kumas-barkod', version: VERSION });
}

function checkKey_(key) {
  const expected = PropertiesService.getScriptProperties().getProperty('API_KEY');
  if (!expected) throw new Error('Script özelliklerinde API_KEY tanımlı değil.');
  if (key !== expected) throw new Error('Anahtar geçersiz.');
}

function sheet_() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  let sh = ss.getSheetByName(SHEET_NAME);
  if (!sh) sh = ss.insertSheet(SHEET_NAME);
  if (sh.getLastRow() === 0) {
    sh.getRange(1, 1, 1, COLUMNS.length).setValues([COLUMNS.map((c) => HEADERS[c])]).setFontWeight('bold');
    sh.setFrozenRows(1);
  }
  return sh;
}

/** Eski (İngilizce) başlıkları Türkçeye çevirir; veri satırlarına dokunmaz. */
function upgradeHeader_(sh) {
  const range = sh.getRange(1, 1, 1, COLUMNS.length);
  const current = range.getValues()[0].map(String);
  const upgraded = current.map((h) => (COLUMNS.includes(h) ? HEADERS[h] : h));
  if (upgraded.some((h, i) => h !== current[i])) range.setValues([upgraded]);
}

function upsert_(rows) {
  const sh = sheet_();
  rows.forEach(validate_);
  upgradeHeader_(sh);

  // Mevcut satırlar: cut_id → { satır numarası, created_at }
  const index = new Map();
  let newest = null;      // tablodaki en yeni satır
  let previous = null;
  let unsorted = false;   // tablo şu an kayıt zamanı sırasında değil
  const last = sh.getLastRow();
  if (last > 1) {
    sh.getRange(2, 1, last - 1, COLUMNS.length).getValues().forEach((v, i) => {
      const created = String(v[COL_CREATED_AT - 1]);
      index.set(String(v[COL_CUT_ID - 1]), { row: i + 2, created: created });
      if (newest === null || compare_(v, newest) > 0) newest = v;
      if (previous !== null && compare_(previous, v) < 0) unsorted = true;
      previous = v;
    });
  }

  const conflicts = [];
  const appends = [];
  const appendIndex = new Map(); // aynı pakette iki kez gelen kesim tek satır olsun
  let upserted = 0;

  rows.forEach((r) => {
    const values = toValues_(r);
    const existing = index.get(r.cut_id);
    if (existing) {
      if (existing.created !== String(r.created_at)) {
        // Aynı ID, farklı kesim: eski kaydı silmek yerine reddet.
        conflicts.push({ cut_id: r.cut_id, created_at: r.created_at, existing_created_at: existing.created });
        return;
      }
      sh.getRange(existing.row, 1, 1, COLUMNS.length).setValues([values]);
    } else if (appendIndex.has(r.cut_id)) {
      const prev = appends[appendIndex.get(r.cut_id)];
      if (String(prev[COL_CREATED_AT - 1]) !== String(r.created_at)) {
        conflicts.push({ cut_id: r.cut_id, created_at: r.created_at, existing_created_at: String(prev[COL_CREATED_AT - 1]) });
        return;
      }
      appends[appendIndex.get(r.cut_id)] = values;
    } else {
      appendIndex.set(r.cut_id, appends.length);
      appends.push(values);
    }
    upserted++;
  });

  if (appends.length > 0) {
    // Yeniden eskiye (eşitse ID'ye göre); created_at sabit uzunluklu ISO metin.
    appends.sort((a, b) => compare_(b, a));
    // Başlığın hemen altına. Mevcut satırlar kayar, bu yüzden güncellemeler yukarıda yapıldı.
    // insertRowsAfter sayfayı da büyütür: sayfa sonuna getRange ile yazmak 1000 satırda hata verirdi.
    sh.insertRowsAfter(1, appends.length);
    const range = sh.getRange(2, 1, appends.length, COLUMNS.length);
    const formats = appends.map(() => COLUMNS.map((c) =>
      TEXT_COLUMNS.has(c) ? '@' : (c === 'metre' ? '0.00' : '0')));
    range.setNumberFormats(formats);
    range.setFontWeight('normal');   // eklenen satır biçimini kalın başlıktan alır
    range.setValues(appends);
  }

  // Yeni satırlardan biri tablodaki en yeni kayıttan eskiyse (ör. başka istasyonun internetsiz
  // birikmiş kayıtları) ya da tablo zaten sırasızsa tüm veri satırlarını yeniden eskiye diz.
  // Uygulama her açılışta boş paket gönderdiği için sırasız (eski düzendeki) tablo kendiliğinden düzelir.
  const olderAppended = appends.length > 0 && newest !== null
    && compare_(appends[appends.length - 1], newest) < 0;
  if ((olderAppended || unsorted) && sh.getLastRow() > 2) {
    sh.getRange(2, 1, sh.getLastRow() - 1, sh.getLastColumn())
      .sort([{ column: COL_CREATED_AT, ascending: false }, { column: COL_CUT_ID, ascending: false }]);
  }

  // İstasyon başına en büyük sıra numarası (tablodaki + bu paketteki)
  const maxSequences = {};
  const note = (id) => {
    const station = id.substring(0, 2);
    const seq = Number(id.substring(3));
    if (!(maxSequences[station] >= seq)) maxSequences[station] = seq;
  };
  index.forEach((_, id) => { if (CUT_ID_PATTERN.test(id)) note(id); });
  appendIndex.forEach((_, id) => note(id));

  return { upserted: upserted, conflicts: conflicts, maxSequences: maxSequences };
}

function compare_(a, b) {
  const ca = String(a[COL_CREATED_AT - 1]), cb = String(b[COL_CREATED_AT - 1]);
  if (ca !== cb) return ca < cb ? -1 : 1;
  const ia = String(a[COL_CUT_ID - 1]), ib = String(b[COL_CUT_ID - 1]);
  return ia < ib ? -1 : (ia > ib ? 1 : 0);
}

/**
 * Geri kurma: satırları ESKİDEN YENİYE, [offset, offset+limit) aralığında döndürür (offset tablonun
 * altından sayılır). Yeni satırlar üste girdiği için okuma sürerken kesim gönderilse de sayfalar
 * kaymaz. Sütunlar başlık adına göre okunur (sıra değişse de çalışır; eski İngilizce başlıklar da
 * tanınır). Boş hücre → null. Okuma kilit gerektirmez.
 */
function export_(offset, limit) {
  const sh = sheet_();
  limit = Math.max(1, Math.min(limit, EXPORT_MAX_LIMIT));
  const total = Math.max(0, sh.getLastRow() - 1);
  if (offset >= total) return { ok: true, total: total, rows: [], version: VERSION };

  const width = sh.getLastColumn();
  const header = sh.getRange(1, 1, 1, width).getValues()[0].map(String);
  const count = Math.min(limit, total - offset);
  const values = sh.getRange(2 + total - offset - count, 1, count, width).getValues().reverse();

  const rows = values.map((v) => {
    const row = {};
    COLUMNS.forEach((c) => {
      if (c === 'metre') return;
      const turkish = header.indexOf(HEADERS[c]);
      const i = turkish >= 0 ? turkish : header.indexOf(c);
      const cell = i < 0 ? '' : v[i];
      row[c] = cell === '' || cell === null ? null : cell;
    });
    return row;
  });
  return { ok: true, total: total, rows: rows, version: VERSION };
}

function validate_(r) {
  if (!r || !CUT_ID_PATTERN.test(r.cut_id)) {
    throw new Error('Geçersiz cut_id: ' + (r && r.cut_id));
  }
  if (!r.created_at) {
    throw new Error('created_at eksik: ' + r.cut_id);
  }
}

function toValues_(r) {
  return COLUMNS.map((c) => {
    if (c === 'metre') return r.length_cm / 100;
    const v = r[c];
    return v === null || v === undefined ? '' : v;
  });
}

function json_(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj))
    .setMimeType(ContentService.MimeType.JSON);
}
