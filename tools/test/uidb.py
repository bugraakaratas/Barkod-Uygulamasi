"""Arayüz testi yardımcısı: yalnızca geçici test veritabanını okur/doldurur."""
import datetime as dt
import sqlite3
import sys

mode, db = sys.argv[1], sys.argv[2]
assert 'kumas-uitest' in db, 'yalnizca gecici veritabani'
sys.stdout.reconfigure(encoding='utf-8')
c = sqlite3.connect(db, timeout=10)

if mode == 'query':
    print('|'.join('' if x is None else str(x) for r in c.execute(sys.argv[3]) for x in r))

elif mode == 'seed':
    printer, n = sys.argv[3], int(sys.argv[4])
    c.execute("insert into settings(key,value) values('printer',?) on conflict(key) do update set value=excluded.value", (printer,))
    now = dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=5)
    rows = []
    for i in range(1, n + 1):
        at = (now - dt.timedelta(seconds=(n - i) * (432000 / max(n, 1)))).strftime('%Y-%m-%dT%H:%M:%S.000Z')
        rows.append((f'K1-{i:06d}', 'K1', i, f'LOT-{i % 50:04d}', 100 + i % 900, at, 1))
    c.executemany('insert into cuts(cut_id,station_code,sequence_no,lot_code,length_cm,created_at,print_count) values(?,?,?,?,?,?,?)', rows)
    c.execute('insert into station_counter(station_code,last_sequence) values(?,?)', ('K1', n))
    c.commit()
    print('seed', n)

elif mode == 'prep':
    station, printer = sys.argv[3], sys.argv[4]
    for key, value in (('station_code', station), ('printer', printer)):
        c.execute("insert into settings(key,value) values(?,?) on conflict(key) do update set value=excluded.value", (key, value))
    c.commit()
    print('prep', station)

elif mode == 'wipe':
    c.executescript('delete from sync_outbox; delete from cuts; delete from station_counter;')
    print('wipe')

elif mode == 'badurl':
    c.execute("update settings set value=replace(value,'/exec','/execYOK') where key='sheets_url'")
    c.commit()
    print('badurl')

elif mode == 'goodurl':
    c.execute("update settings set value=replace(value,'/execYOK','/exec') where key='sheets_url'")
    c.commit()
    print('goodurl')

elif mode == 'dump':
    rows = c.execute("select cut_id,lot_code,length_cm,status,coalesce(void_reason,''),print_count,created_at from cuts where station_code=? order by cut_id", (sys.argv[3],))
    print('\n'.join('|'.join(str(x) for x in r) for r in rows))

elif mode == 'exec':
    c.execute(sys.argv[3])
    c.commit()
    print('exec')
