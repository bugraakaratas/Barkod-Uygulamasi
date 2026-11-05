
/* ===== GECICI STRES TESTI: dagitilmaz, test sonunda silinir ===== */
function sheet_() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  let sh = ss.getSheetByName('STRES');
  if (!sh) sh = ss.insertSheet('STRES');
  if (sh.getLastRow() === 0) {
    sh.getRange(1, 1, 1, COLUMNS.length).setValues([COLUMNS.map((c) => HEADERS[c])]).setFontWeight('bold');
    sh.setFrozenRows(1);
  }
  return sh;
}
function stresSatir_(id, seq, at, lot, pc) {
  return { cut_id: id, station_code: id.substring(0, 2), sequence_no: seq, lot_code: lot, length_cm: 1000 + seq % 900, operator: null,
    status: 'active', void_reason: null, voided_at: null, created_at: at, print_count: pc, last_printed_at: at };
}
function stresBuyut_(hedef) {
  const sh = sheet_();
  if (sh.getName() !== 'STRES') throw new Error('yanlis sayfa');
  let mevcut = Math.max(0, sh.getLastRow() - 1);
  const taban = Date.parse('2026-01-01T00:00:00.000Z');
  while (mevcut < hedef) {
    const n = Math.min(25000, hedef - mevcut);
    const blok = [];
    for (let i = 0; i < n; i++) {
      const k = 500000 + mevcut + i, seq = 900000 - k;
      blok.push(toValues_(stresSatir_('S1-' + String(seq).padStart(6, '0'), seq, new Date(taban - k * 60000).toISOString(), 'LOT-' + (k % 500), 1)));
    }
    const son = sh.getLastRow();
    sh.insertRowsAfter(son, n);
    const r = sh.getRange(son + 1, 1, n, COLUMNS.length);
    r.setNumberFormat('@');
    r.setValues(blok);
    SpreadsheetApp.flush();
    mevcut += n;
  }
}
function stresOlc_() {
  const sh = sheet_();
  if (sh.getName() !== 'STRES') throw new Error('yanlis sayfa');
  const N = sh.getLastRow() - 1;
  const sonuc = [];
  const o = (ad, f) => { const t = Date.now(); const r = f(); SpreadsheetApp.flush(); sonuc.push(ad + '=' + (Date.now() - t) + 'ms'); return r; };
  let seq = (Date.now() % 700000) + 1;
  const yeni = (n, geri) => { const a = []; for (let i = 0; i < n; i++) { seq++; a.push(stresSatir_('S2-' + String(seq).padStart(6, '0'), seq, new Date(Date.now() - (geri || 0) + i).toISOString(), 'YENI', 0)); } return a; };
  const guncel = (n) => sh.getRange(2, 1, n, COLUMNS.length).getValues().map((v) => stresSatir_(String(v[0]), Number(v[2]), String(v[9]), String(v[3]), 2));
  o('bos1', () => upsert_([]));
  o('bos2', () => upsert_([]));
  o('yeni1', () => upsert_(yeni(1)));
  o('yeni200', () => upsert_(yeni(200)));
  const g = guncel(200);
  o('guncelle200', () => upsert_(g));
  const g2 = guncel(100);
  const k = yeni(100);
  o('karisik200', () => upsert_(g2.concat(k)));
  o('gecUst1', () => upsert_(yeni(1, 60000)));
  const eski = sh.getRange(N - 5, 1, 1, COLUMNS.length).getValues()[0];
  o('eskiIptal1', () => upsert_([stresSatir_(String(eski[0]), Number(eski[2]), String(eski[9]), String(eski[3]), 9)]));
  const eski200 = sh.getRange(N - 300, 1, 200, COLUMNS.length).getValues().map((v) => stresSatir_(String(v[0]), Number(v[2]), String(v[9]), String(v[3]), 9));
  o('eskiGuncelle200', () => upsert_(eski200));
  o('gecEski1TumSirala', () => upsert_([stresSatir_('S3-' + String(seq % 900000).padStart(6, '0'), 1, '2020-06-01T00:00:00.000Z', 'ESKI', 0)]));
  o('exportIlk', () => export_(0, 2000));
  o('exportSon', () => export_(N - 1800, 1800));
  console.log('STRES N=' + N + ' ' + sonuc.join(' '));
}
function stres010() { stresBuyut_(10000); stresOlc_(); }
function stres050() { stresBuyut_(50000); stresOlc_(); }
function stres100() { stresBuyut_(100000); stresOlc_(); }
function stres200() { stresBuyut_(200000); stresOlc_(); }
function stresBuyut100() { stresBuyut_(100000); }
function stresBuyut200() { stresBuyut_(200000); }
function stresOlc() { stresOlc_(); }
