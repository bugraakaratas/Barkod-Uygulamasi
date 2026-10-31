/**
 * Kumaş Barkod → Google Sheets kopyası (tek yönlü).
 *
 * Uygulama kesim kayıtlarını toplu olarak POST eder. Bu script her kaydı cut_id'ye göre
 * "varsa güncelle, yoksa ekle" (upsert) yapar. Aynı paket iki kez gelse de (yanıt kaybolup
 * tekrar gönderilirse) her kesim tabloda TEK satır olur.
 *
 * Kurulum: tools/sheets/KURULUM.md
 * Asıl veri uygulamadaki SQLite'tadır; bu tablo yalnızca kopyadır. Elle değişiklik
 * bir sonraki gönderimde üzerine yazılır.
 */

const VERSION = 1;
const SHEET_NAME = 'Kesimler';

// Sütun sırası sabittir. Geri kurma (Adım 9) sütunları başlık adına göre okur.
const COLUMNS = [
  'cut_id', 'station_code', 'sequence_no', 'lot_code', 'length_cm', 'operator',
  'status', 'void_reason', 'voided_at', 'created_at', 'print_count', 'last_printed_at',
  'metre', // rapor kolaylığı: length_cm / 100 (geri kurmada kullanılmaz)
];

// Sheets'in kendi kafasına göre tarihe/sayıya çevirmemesi için düz metin tutulan sütunlar.
const TEXT_COLUMNS = new Set([
  'cut_id', 'station_code', 'lot_code', 'operator', 'status', 'void_reason',
  'voided_at', 'created_at', 'last_printed_at',
]);

const CUT_ID_PATTERN = /^[A-Z][A-Z0-9]-\d{6}$/;

function doPost(e) {
  try {
    const body = JSON.parse(e.postData.contents);
    checkKey_(body.key);
    const rows = Array.isArray(body.rows) ? body.rows : [];

    const lock = LockService.getScriptLock();
    if (!lock.tryLock(30000)) {
      return json_({ ok: false, error: 'Tablo meşgul, sonra tekrar denenecek.' });
    }
    try {
      const upserted = upsert_(rows);
      return json_({ ok: true, upserted: upserted, version: VERSION });
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
    sh.getRange(1, 1, 1, COLUMNS.length).setValues([COLUMNS]).setFontWeight('bold');
    sh.setFrozenRows(1);
  }
  return sh;
}

function upsert_(rows) {
  const sh = sheet_();
  if (rows.length === 0) return 0; // bağlantı testi: anahtar ve tablo doğrulandı

  // Mevcut cut_id → satır numarası
  const index = new Map();
  const last = sh.getLastRow();
  if (last > 1) {
    sh.getRange(2, 1, last - 1, 1).getValues()
      .forEach((r, i) => index.set(String(r[0]), i + 2));
  }

  const appends = [];
  const appendIndex = new Map(); // aynı pakette iki kez gelen kesim tek satır olsun

  rows.forEach((r) => {
    validate_(r);
    const values = toValues_(r);
    const rowNumber = index.get(r.cut_id);
    if (rowNumber) {
      sh.getRange(rowNumber, 1, 1, COLUMNS.length).setValues([values]);
    } else if (appendIndex.has(r.cut_id)) {
      appends[appendIndex.get(r.cut_id)] = values;
    } else {
      appendIndex.set(r.cut_id, appends.length);
      appends.push(values);
    }
  });

  if (appends.length > 0) {
    const range = sh.getRange(sh.getLastRow() + 1, 1, appends.length, COLUMNS.length);
    const formats = appends.map(() => COLUMNS.map((c) =>
      TEXT_COLUMNS.has(c) ? '@' : (c === 'metre' ? '0.00' : '0')));
    range.setNumberFormats(formats);
    range.setValues(appends);
  }

  return rows.length;
}

function validate_(r) {
  if (!r || !CUT_ID_PATTERN.test(r.cut_id)) {
    throw new Error('Geçersiz cut_id: ' + (r && r.cut_id));
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
