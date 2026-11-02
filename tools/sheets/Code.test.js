// Code.gs'nin gerçek kodunu sahte Google nesneleriyle çalıştırır.
// Çalıştırma: node --test tools/sheets/Code.test.js
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const KEY = 'gizli';

function load() {
  const data = []; // 2B dizi: tablo hücreleri (1. satır başlık)
  const sheet = {
    getLastRow: () => data.length,
    getLastColumn: () => Math.max(0, ...data.map((r) => r.length)),
    setFrozenRows: () => {},
    getRange(row, col, nr = 1, nc = 1) {
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
        setFontWeight() { return this; },
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
  return { post, ids, lot, data };
}

function row(cutId, createdAt, lot = 'LOT', extra = {}) {
  return {
    cut_id: cutId, station_code: cutId.slice(0, 2), sequence_no: Number(cutId.slice(3)), lot_code: lot,
    length_cm: 1000, operator: null, status: 'active', void_reason: null, voided_at: null,
    created_at: createdAt, print_count: 0, last_printed_at: null, ...extra,
  };
}
const t = (s) => `2026-11-02T09:00:${String(s).padStart(2, '0')}.000Z`;

test('sürüm 4, boş paket sayaçları döndürür', () => {
  const s = load();
  const r = s.post({ rows: [] });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.version, 4);
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
  assert.deepStrictEqual(s.ids(), ['K1-000001', 'K1-000002']);
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

test('satırlar kayıt zamanına göre sıralı: geç gelen eski kayıt araya girer, en yeni en altta', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(2)), row('K1-000002', t(3))] });
  s.post({ rows: [row('K2-000002', t(9)), row('K2-000001', t(1))] }); // eski + yeni, karışık sırada
  assert.deepStrictEqual(s.ids(), ['K2-000001', 'K1-000001', 'K1-000002', 'K2-000002']);
  s.post({ rows: [row('K1-000003', t(10))] });
  assert.deepStrictEqual(s.ids().at(-1), 'K1-000003');
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
  assert.deepStrictEqual(s.ids(), ['K1-000004', 'K1-000005', 'K1-000001', 'K1-000002']);
  assert.deepStrictEqual(r.max_sequences, { K1: 5 });
});

test('sıralı tabloda yalnızca güncelleme yapılınca sıralama gerekmez, sıra bozulmaz', () => {
  const s = load();
  s.post({ rows: [row('K1-000001', t(1)), row('K1-000002', t(2))] });
  s.post({ rows: [row('K1-000001', t(1), 'LOT', { print_count: 3 })] });
  assert.deepStrictEqual(s.ids(), ['K1-000001', 'K1-000002']);
});
