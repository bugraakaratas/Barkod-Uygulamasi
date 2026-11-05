// Code.gs'nin gerçek kodunu sahte Google nesneleriyle çalıştırır.
// Çalıştırma: node --test tools/sheets/Code.test.js
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const KEY = 'gizli';

function load(maxRows = 1000) {
  const data = []; // 2B dizi: tablo hücreleri (1. satır başlık)
  const formats = { insertedAfterHeaderBold: false, fontWeights: [] };
  const sheet = {
    getLastRow: () => data.length,
    // Gerçek sayfa gibi: eklenen satırlar sayfayı büyütür, boyutun dışına getRange hata verir.
    insertRowsAfter(after, n) {
      data.splice(after, 0, ...Array.from({ length: n }, () => []));
      maxRows += n;
    },
    getLastColumn: () => Math.max(0, ...data.map((r) => r.length)),
    setFrozenRows: () => {},
    getRange(row, col, nr = 1, nc = 1) {
      if (row + nr - 1 > maxRows) throw new Error('The coordinates of the range are outside the dimensions of the sheet.');
      return {
        getValues: () => Array.from({ length: nr }, (_, i) =>
          Array.from({ length: nc }, (_, j) => ((data[row - 1 + i] || [])[col - 1 + j] ?? ''))),
        setValues(values) {
          values.forEach((v, i) => {
            data[row - 1 + i] = data[row - 1 + i] || [];
            v.forEach((cell, j) => { data[row - 1 + i][col - 1 + j] = cell; });
          });
          return this;
        },
        setNumberFormats() { return this; },
        setFontWeight(w) { formats.fontWeights.push({ row, w }); return this; },
        sort(specs) {
          const block = data.slice(row - 1, row - 1 + nr);
          block.sort((a, b) => {
            for (const s of specs) {
              const x = String(a[s.column - 1]), y = String(b[s.column - 1]);
              if (x !== y) return (x < y ? -1 : 1) * (s.ascending ? 1 : -1);
            }
            return 0;
          });
          data.splice(row - 1, nr, ...block);
          return this;
        },
      };
    },
  };
  const ctx = {
    SpreadsheetApp: { getActiveSpreadsheet: () => ({ getSheetByName: () => sheet, insertSheet: () => sheet }) },
    PropertiesService: { getScriptProperties: () => ({ getProperty: () => KEY }) },
    LockService: { getScriptLock: () => ({ tryLock: () => true, releaseLock: () => {} }) },
    ContentService: {
      MimeType: { JSON: 'json' },
      createTextOutput: (text) => ({ text, setMimeType() { return this; } }),
    },
  };
  vm.createContext(ctx);
  vm.runInContext(fs.readFileSync(path.join(__dirname, 'Code.gs'), 'utf8'), ctx);
  const post = (body) => JSON.parse(ctx.doPost({ postData: { contents: JSON.stringify({ key: KEY, ...body }) } }).text);
  const ids = () => data.slice(1).map((r) => r[0]);
  const lot = (id) => data.slice(1).find((r) => r[0] === id)[3];
  return { post, ids, lot, data, formats };
}

function row(cutId, createdAt, lot = 'LOT', extra = {}) {
  return {
    cut_id: cutId, station_code: cutId.slice(0, 2), sequence_no: Number(cutId.slice(3)), lot_code: lot,
    length_cm: 1000, operator: null, status: 'active', void_reason: null, voided_at: null,
    created_at: createdAt, print_count: 0, last_printed_at: null, ...extra,
  };
}
const t = (s) => `2026-11-02T09:00:${String(s).padStart(2, '0')}.000Z`;

test('sürüm 5, boş paket sayaçları döndürür', () => {
  const s = load();
  const r = s.post({ rows: [] });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.version, 5);
  assert.deepStrictEqual(r.max_sequences, {});
  s.post({ rows: [row('K1-000007', t(1)), row('K2-000003', t(2))] });
  assert.deepStrictEqual(s.post({ rows: [] }).max_sequences, { K1: 7, K2: 3 });
});

test('aynı ID + farklı kayıt zamanı: eski satır ezilmez, çakışma döner', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1), 'ESKI')] });
  const r = s.post({ rows: [row('K1-000001', t(5), 'YENI'), row('K1-000002', t(6), 'YENI-2')] });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.upserted, 1);
  assert.deepStrictEqual(r.conflicts, [{ cut_id: 'K1-000001', created_at: t(5), existing_created_at: t(1) }]);
  assert.strictEqual(s.lot('K1-000001'), 'ESKI');
  assert.deepStrictEqual(s.ids(), ['K1-000002', 'K1-000001']);
  assert.deepStrictEqual(r.max_sequences, { K1: 2 });
});

test('aynı kesim tekrar gelince kendi satırı güncellenir (tekrar satır yok)', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1))] });
  s.post({ rows: [row('K1-000001', t(1), 'LOT', { status: 'voided', void_reason: 'x', voided_at: t(9) })] });
  s.post({ rows: [row('K1-000001', t(1), 'LOT', { status: 'voided', void_reason: 'x', voided_at: t(9) })] });
  assert.deepStrictEqual(s.ids(), ['K1-000001']);
  assert.strictEqual(s.data[1][6], 'voided');
});

test('aynı pakette aynı ID iki farklı kesimle gelirse ikincisi çakışma', () => {
  const s = load();
  const r = s.post({ rows: [row('K1-000001', t(1), 'A'), row('K1-000001', t(2), 'B')] });
  assert.strictEqual(r.conflicts.length, 1);
  assert.deepStrictEqual(s.ids(), ['K1-000001']);
  assert.strictEqual(s.lot('K1-000001'), 'A');
});

test('yeniden eskiye sıralı: yeni kayıt en üstte, geç gelen eski kayıt araya girer', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(2)), row('K1-000002', t(3))] });
  s.post({ rows: [row('K2-000002', t(9)), row('K2-000001', t(1))] }); // eski + yeni, karışık sırada
  assert.deepStrictEqual(s.ids(), ['K2-000002', 'K1-000002', 'K1-000001', 'K2-000001']);
  s.post({ rows: [row('K1-000003', t(10))] });
  assert.deepStrictEqual(s.ids()[0], 'K1-000003');
});

test('created_at eksikse reddedilir, hiçbir şey yazılmaz', () => {
  const s = load();
  const r = s.post({ rows: [row('K1-000001', t(1)), row('K1-000002', '')] });
  assert.strictEqual(r.ok, false);
  assert.deepStrictEqual(s.ids(), []);
});

test('eski hatadan kalan sırasız tablo boş pakette (uygulama açılışı) kendiliğinden sıralanır', () => {
  const s = load();
  s.post({ rows: [] }); // başlık
  // Gerçek tablodaki durum: üzerine yazılmış K1-000001..3 (sonra), test satırları K1-000004..5 (önce)
  const bad = [row('K1-000001', t(30)), row('K1-000002', t(40)), row('K1-000004', t(10)), row('K1-000005', t(11))];
  bad.forEach((r, i) => { s.data[i + 1] = Object.values(r).concat([r.length_cm / 100]); });
  const r = s.post({ rows: [] });
  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(s.ids(), ['K1-000002', 'K1-000001', 'K1-000005', 'K1-000004']);
  assert.deepStrictEqual(r.max_sequences, { K1: 5 });
});

test('sıralı tabloda yalnızca güncelleme yapılınca sıralama gerekmez, sıra bozulmaz', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1)), row('K1-000002', t(2))] });
  s.post({ rows: [row('K1-000001', t(1), 'LOT', { print_count: 3 })] });
  assert.deepStrictEqual(s.ids(), ['K1-000002', 'K1-000001']);
  assert.strictEqual(s.data[2][10], 3);   // eklemeden önce güncellenen satır doğru yerde
});

test('güncelleme ve yeni kayıt aynı pakette: güncelleme doğru satıra yazılır', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1), 'A'), row('K1-000002', t(2), 'B')] });
  s.post({ rows: [row('K1-000001', t(1), 'A', { status: 'voided' }), row('K1-000003', t(3), 'C')] });
  assert.deepStrictEqual(s.ids(), ['K1-000003', 'K1-000002', 'K1-000001']);
  assert.strictEqual(s.data[3][6], 'voided');
  assert.strictEqual(s.lot('K1-000002'), 'B');
});

test('başlıklar Türkçe, yeni satır başlığın kalınlığını almaz', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1))] });
  assert.deepStrictEqual(s.data[0], ['ID', 'İstasyon', 'Sıra No', 'Lot', 'Uzunluk (cm)', 'Operatör', 'Durum',
    'İptal Sebebi', 'İptal Zamanı', 'Kayıt Zamanı', 'Baskı Sayısı', 'Son Baskı', 'Metre']);
  assert.deepStrictEqual(s.formats.fontWeights.at(-1), { row: 2, w: 'normal' });
});

const OLD_HEADER = ['cut_id', 'station_code', 'sequence_no', 'lot_code', 'length_cm', 'operator', 'status',
  'void_reason', 'voided_at', 'created_at', 'print_count', 'last_printed_at', 'metre'];

test('eski tablo: ilk bağlantıda başlık çevrilir, satırlar yeniden eskiye döner, veri bozulmaz', () => {
  const s = load();
  s.post({ rows: [] });
  s.data[0] = [...OLD_HEADER];
  [row('K1-000001', t(1), 'A'), row('K1-000002', t(2), 'B'), row('K1-000003', t(3), 'C')]
    .forEach((r, i) => { s.data[i + 1] = Object.values(r).concat([r.length_cm / 100]); });
  const before = s.data.slice(1).map((r) => [...r]);

  const r = s.post({ rows: [] });

  assert.strictEqual(r.ok, true);
  assert.strictEqual(s.data[0][0], 'ID');
  assert.strictEqual(s.data[0][12], 'Metre');
  assert.deepStrictEqual(s.ids(), ['K1-000003', 'K1-000002', 'K1-000001']);
  assert.deepStrictEqual(s.data.slice(1), before.reverse());
});

test('geri kurma Türkçe başlıklı tabloyu tam ve eskiden yeniye okur', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1), 'A', { operator: 'Ayşe' }), row('K1-000002', t(2), 'B', { status: 'voided', void_reason: 'x', voided_at: t(3) })] });
  const r = s.post({ action: 'export', offset: 0, limit: 100 });
  assert.strictEqual(r.total, 2);
  assert.deepStrictEqual(r.rows.map((x) => x.cut_id), ['K1-000001', 'K1-000002']);
  assert.strictEqual(r.rows[0].operator, 'Ayşe');
  assert.strictEqual(r.rows[0].length_cm, 1000);
  assert.strictEqual(r.rows[1].void_reason, 'x');
  assert.strictEqual(r.rows[1].metre, undefined);
});

test('geri kurma eski İngilizce başlıkları da okur', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1), 'A')] });
  s.data[0] = [...OLD_HEADER];
  const r = s.post({ action: 'export', offset: 0, limit: 100 });
  assert.strictEqual(r.rows[0].lot_code, 'A');
});

test('geri kurma sürerken yeni kesim gelirse sayfalar kaymaz, kayıt atlanmaz veya tekrar etmez', () => {
  const s = load();
  s.post({ rows: [1, 2, 3, 4, 5].map((n) => row(`K1-00000${n}`, t(n))) });
  const first = s.post({ action: 'export', offset: 0, limit: 2 });
  s.post({ rows: [row('K1-000006', t(6))] });      // okuma sürerken üste satır girdi
  const second = s.post({ action: 'export', offset: 2, limit: 2 });
  const third = s.post({ action: 'export', offset: 4, limit: 2 });
  assert.deepStrictEqual([...first.rows, ...second.rows, ...third.rows].map((x) => x.cut_id),
    ['K1-000001', 'K1-000002', 'K1-000003', 'K1-000004', 'K1-000005', 'K1-000006']);
});

test('sayfanın satır sınırı dolunca yeni kayıt yine eklenir', () => {
  const s = load(3);   // başlık + 2 satırlık sayfa
  s.post({ rows: [row('K1-000001', t(1)), row('K1-000002', t(2))] });
  const r = s.post({ rows: [row('K1-000003', t(3))] });
  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(s.ids(), ['K1-000003', 'K1-000002', 'K1-000001']);
});
