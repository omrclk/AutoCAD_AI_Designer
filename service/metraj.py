"""Metraj (quantity takeoff) post-processing: unit prices (keşif), revision snapshots/diff, dependency-free XLSX export.

The AutoCAD plugin computes the raw quantities (items) and posts them to /metraj; everything here is pure Python
(standard library only) so it can be unit tested without AutoCAD.

Item: {"kategori": str, "kalem": str, "birim": "adet|m|m²", "miktar": float, "adet": int (optional), "katman": str (optional)}
"""
import csv
import datetime
import io
import json
import os
import re
import zipfile
from xml.sax.saxutils import escape

LABEL_RE = re.compile(r'^[\w.\-]{1,40}$', re.UNICODE)
_TR = str.maketrans('ıİşŞğĞüÜöÖçÇâÂîÎûÛ', 'iissgguuoocCaaiiuu')


def norm(s):
    s = str(s or '').translate(_TR).casefold()
    return ' '.join(re.sub(r'[^0-9a-z]+', ' ', s).split())


def item_key(it):
    return '%s|%s|%s' % (it.get('kategori', ''), it.get('kalem', ''), it.get('katman', '') or '')


def to_num(v):
    if v is None:
        return None
    if isinstance(v, (int, float)):
        return float(v)
    s = str(v).strip().replace(' ', '').replace('\u00a0', '').replace('TL', '').replace('₺', '')
    if not s:
        return None
    if ',' in s and '.' in s:
        s = s.replace('.', '').replace(',', '.') if s.rfind(',') > s.rfind('.') else s.replace(',', '')
    elif ',' in s:
        s = s.replace(',', '.')
    try:
        return float(s)
    except ValueError:
        return None


# ---------------------------------------------------------------- prices
_COLS = {'kalem': ('kalem', 'anahtar', 'aciklama', 'tanim', 'item', 'key'),
         'birim': ('birim', 'unit'),
         'birim_fiyat': ('birim fiyat', 'fiyat', 'price', 'unit price'),
         'poz_no': ('poz no', 'poz', 'poz numarasi')}


def _map_header(header):
    m = {}
    for i, h in enumerate(header):
        n = norm(h)
        for k, names in _COLS.items():
            if k not in m and n in names:
                m[k] = i
    return m


def _rows_to_prices(rows):
    rows = [r for r in rows if any(str(c or '').strip() for c in r)]
    if not rows:
        return []
    m = _map_header(rows[0])
    if 'kalem' not in m or 'birim_fiyat' not in m:
        raise ValueError('birim fiyat dosyasında "kalem" ve "birim_fiyat" sütunları gerekli')
    out = []
    for r in rows[1:]:
        g = lambda k: (r[m[k]] if k in m and m[k] < len(r) else None)  # noqa: E731
        kalem = str(g('kalem') or '').strip()
        if not kalem:
            continue
        out.append({'kalem': kalem, 'birim': str(g('birim') or '').strip(), 'birim_fiyat': to_num(g('birim_fiyat')),
                    'poz_no': str(g('poz_no') or '').strip()})
    return out


def read_xlsx_rows(path):
    """First worksheet of an .xlsx as a list of rows (strings/numbers); no openpyxl needed."""
    ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
    import xml.etree.ElementTree as ET
    with zipfile.ZipFile(path) as z:
        shared = []
        if 'xl/sharedStrings.xml' in z.namelist():
            for si in ET.fromstring(z.read('xl/sharedStrings.xml')).iter(ns + 'si'):
                shared.append(''.join(t.text or '' for t in si.iter(ns + 't')))
        sheets = sorted(n for n in z.namelist() if re.match(r'xl/worksheets/sheet\d+\.xml$', n))
        root = ET.fromstring(z.read(sheets[0]))
    rows = []
    for row in root.iter(ns + 'row'):
        cells = {}
        for c in row.iter(ns + 'c'):
            ref = re.match(r'([A-Z]+)', c.get('r', 'A'))
            col = 0
            for ch in ref.group(1):
                col = col * 26 + ord(ch) - 64
            t = c.get('t')
            v = c.find(ns + 'v')
            if t == 's' and v is not None:
                val = shared[int(v.text)]
            elif t == 'inlineStr':
                val = ''.join(x.text or '' for x in c.iter(ns + 't'))
            else:
                val = v.text if v is not None else ''
            cells[col - 1] = val
        rows.append([cells.get(i, '') for i in range(max(cells) + 1)] if cells else [])
    return rows


def load_prices(path):
    if not path or not os.path.isfile(path):
        return []
    if path.lower().endswith(('.xlsx', '.xlsm')):
        return _rows_to_prices(read_xlsx_rows(path))
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        text = f.read()
    delim = ';' if text.split('\n', 1)[0].count(';') >= text.split('\n', 1)[0].count(',') else ','
    return _rows_to_prices(list(csv.reader(io.StringIO(text), delimiter=delim)))


def write_price_template(path, items):
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
    seen = set()
    with io.open(path, 'w', encoding='utf-8-sig', newline='') as f:
        w = csv.writer(f, delimiter=';')
        w.writerow(['poz_no', 'kalem', 'birim', 'birim_fiyat'])
        for it in items:
            k = it.get('kalem', '')
            if k not in seen:
                seen.add(k)
                w.writerow(['', k, it.get('birim', ''), ''])


def match_price(item, prices):
    k = norm(item.get('kalem'))
    best = None
    for p in prices:
        pk = norm(p['kalem'])
        if not pk:
            continue
        if pk == k:
            return p
        if re.search(r'(^| )' + re.escape(pk) + r'( |$)', k) and (best is None or len(pk) > len(norm(best['kalem']))):
            best = p
    return best


def kesif(items, prices):
    rows, missing, total = [], [], 0.0
    for it in items:
        p = match_price(it, prices) if prices else None
        price = p['birim_fiyat'] if p else None
        amount = round(price * it['miktar'], 2) if price is not None else None
        if amount is not None:
            total += amount
        else:
            missing.append(it['kalem'])
        rows.append({'poz_no': p['poz_no'] if p else '', 'kalem': it['kalem'], 'birim': it.get('birim', ''),
                     'miktar': it['miktar'], 'birim_fiyat': price, 'tutar': amount})
    return rows, missing, round(total, 2)


def unit_prices(items, prices):
    out = {}
    for it in items:
        p = match_price(it, prices) if prices else None
        out[item_key(it)] = p['birim_fiyat'] if p else None
    return out


def diff(old_items, new_items, prices=None):
    old = {item_key(i): i for i in old_items}
    new = {item_key(i): i for i in new_items}
    up = unit_prices(list(old.values()) + list(new.values()), prices or [])
    rows = []
    for k in list(old) + [k for k in new if k not in old]:
        o, n = old.get(k), new.get(k)
        a, b = (o or {}).get('miktar', 0.0), (n or {}).get('miktar', 0.0)
        if abs(a - b) < 1e-9:
            continue
        ref = n or o
        price = up.get(k)
        rows.append({'kalem': ref['kalem'], 'kategori': ref.get('kategori', ''), 'katman': ref.get('katman', ''),
                     'birim': ref.get('birim', ''), 'eski': a, 'yeni': b, 'fark': round(b - a, 4),
                     'durum': 'yeni' if o is None else ('kaldırıldı' if n is None else ('arttı' if b > a else 'azaldı')),
                     'tutar_farki': round((b - a) * price, 2) if price is not None else None})
    return rows


# ---------------------------------------------------------------- snapshots
def _snap_path(folder, label):
    if not LABEL_RE.match(label or ''):
        raise ValueError('revizyon etiketi geçersiz: %r (harf/rakam/._- , en fazla 40)' % label)
    return os.path.join(folder, label + '.json')


def save_snapshot(folder, label, items, dwg=''):
    os.makedirs(folder, exist_ok=True)
    p = _snap_path(folder, label)
    with io.open(p, 'w', encoding='utf-8') as f:
        json.dump({'label': label, 'dwg': dwg, 'date': datetime.datetime.now().isoformat(timespec='seconds'),
                   'items': items}, f, ensure_ascii=False, indent=1)
    return p


def load_snapshot(folder, label):
    p = _snap_path(folder, label)
    if not os.path.isfile(p):
        raise ValueError('revizyon bulunamadı: %s (mevcut: %s)' % (label, ', '.join(list_snapshots(folder)) or 'yok'))
    with io.open(p, encoding='utf-8') as f:
        return json.load(f)


def list_snapshots(folder):
    if not folder or not os.path.isdir(folder):
        return []
    return sorted(f[:-5] for f in os.listdir(folder) if f.endswith('.json'))


# ---------------------------------------------------------------- xlsx writer
def _col(i):
    s = ''
    i += 1
    while i:
        i, r = divmod(i - 1, 26)
        s = chr(65 + r) + s
    return s


def _sheet_xml(rows):
    out = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
           '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
           '<cols><col min="1" max="1" width="48" customWidth="1"/><col min="2" max="12" width="16" customWidth="1"/></cols><sheetData>']
    for r, row in enumerate(rows, 1):
        out.append('<row r="%d">' % r)
        for c, v in enumerate(row):
            ref = '%s%d' % (_col(c), r)
            style = ' s="1"' if r == 1 else ''
            if v is None or v == '':
                continue
            if isinstance(v, bool):
                v = 'evet' if v else 'hayır'
            if isinstance(v, (int, float)):
                out.append('<c r="%s"%s><v>%s</v></c>' % (ref, style, repr(float(v)) if isinstance(v, float) else v))
            else:
                out.append('<c r="%s" t="inlineStr"%s><is><t xml:space="preserve">%s</t></is></c>' % (ref, style, escape(str(v))))
        out.append('</row>')
    out.append('</sheetData></worksheet>')
    return ''.join(out)


def write_xlsx(path, sheets):
    """sheets: list of (name, rows); rows[0] is the bold header row."""
    names = [re.sub(r'[\[\]:*?/\\]', '', n)[:31] for n, _ in sheets]
    ct = ''.join('<Override PartName="/xl/worksheets/sheet%d.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' % (i + 1) for i in range(len(sheets)))
    files = {
        '[Content_Types].xml': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>' + ct + '</Types>',
        '_rels/.rels': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        'xl/workbook.xml': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>' + ''.join('<sheet name="%s" sheetId="%d" r:id="rId%d"/>' % (escape(n, {'"': '&quot;'}), i + 1, i + 1) for i, n in enumerate(names)) + '</sheets></workbook>',
        'xl/_rels/workbook.xml.rels': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + ''.join('<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet%d.xml"/>' % (i + 1, i + 1) for i in range(len(sheets))) + '<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>' % (len(sheets) + 1),
        'xl/styles.xml': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>',
    }
    for i, (_, rows) in enumerate(sheets):
        files['xl/worksheets/sheet%d.xml' % (i + 1)] = _sheet_xml(rows)
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        for n, s in files.items():
            z.writestr(n, s.encode('utf-8'))
    return path


# ---------------------------------------------------------------- report
def _f(v):
    if v is None:
        return '-'
    return ('%.2f' % v).rstrip('0').rstrip('.') if isinstance(v, float) else str(v)


def report(req):
    items = [i for i in (req.get('items') or []) if isinstance(i, dict) and 'kalem' in i]
    for i in items:
        i['miktar'] = float(i.get('miktar') or 0)
    folder = req.get('snapshot_dir') or ''
    notes = [str(n) for n in (req.get('notes') or [])]
    prices_path = req.get('prices_path') or ''
    prices = []
    if prices_path and os.path.isfile(prices_path):
        try:
            prices = load_prices(prices_path)
        except (ValueError, OSError, zipfile.BadZipFile) as e:
            notes.append('Birim fiyat dosyası okunamadı: %s' % e)
    elif prices_path and items:
        try:
            write_price_template(prices_path, items)
            notes.append('Birim fiyat dosyası yoktu; bu çizimin kalemleriyle şablon oluşturuldu: %s (birim_fiyat sütununu doldurun).' % prices_path)
        except OSError as e:
            notes.append('Şablon yazılamadı: %s' % e)

    saved = None
    if req.get('save_revision'):
        saved = save_snapshot(folder, req['save_revision'], items, req.get('dwg', ''))

    diff_rows, diff_title = None, ''
    pair = req.get('compare_revisions')
    if pair:
        a, b = load_snapshot(folder, pair[0]), load_snapshot(folder, pair[1])
        diff_rows, diff_title = diff(a['items'], b['items'], prices), '%s → %s' % (pair[0], pair[1])
    elif req.get('compare_to'):
        a = load_snapshot(folder, req['compare_to'])
        diff_rows, diff_title = diff(a['items'], items, prices), '%s → güncel çizim' % req['compare_to']
    elif req.get('compare_items') is not None:
        other = [i for i in req['compare_items'] if isinstance(i, dict) and 'kalem' in i]
        diff_rows, diff_title = diff(other, items, prices), '%s → güncel çizim' % req.get('compare_name', 'diğer DWG')

    k_rows, missing, total = kesif(items, prices)

    sheets = [('Metraj', [['Kategori', 'Kalem', 'Katman', 'Birim', 'Miktar', 'Adet']] +
               [[i.get('kategori', ''), i['kalem'], i.get('katman', ''), i.get('birim', ''), round(i['miktar'], 3), i.get('adet')] for i in items]),
              ('Keşif', [['Poz No', 'Kalem', 'Birim', 'Miktar', 'Birim Fiyat', 'Tutar']] +
               [[r['poz_no'], r['kalem'], r['birim'], round(r['miktar'], 3), r['birim_fiyat'], r['tutar']] for r in k_rows] +
               [[], ['', 'TOPLAM', '', '', '', total]] + ([[], ['', 'Fiyatı olmayan kalem sayısı: %d' % len(missing)]] if missing else []))]
    if diff_rows is not None:
        sheets.append(('Revizyon Farkı', [['Kalem (' + diff_title + ')', 'Katman', 'Birim', 'Eski', 'Yeni', 'Fark', 'Durum', 'Tutar Farkı']] +
                       [[r['kalem'], r['katman'], r['birim'], r['eski'], r['yeni'], r['fark'], r['durum'], r['tutar_farki']] for r in diff_rows]))
    else:
        sheets.append(('Revizyon Farkı', [['Karşılaştırma yapılmadı']]))

    xlsx = None
    if req.get('export_path'):
        xlsx = write_xlsx(req['export_path'], sheets)

    lines = ['METRAJ (%d kalem):' % len(items)]
    for i in items[:30]:
        lines.append('  • %s%s: %s %s%s' % (i['kalem'], (' [' + i['katman'] + ']') if i.get('katman') else '', _f(i['miktar']),
                                           i.get('birim', ''), (' (%d adet)' % i['adet']) if i.get('adet') and i.get('birim') != 'adet' else ''))
    if len(items) > 30:
        lines.append('  ... (+%d kalem Excel\'de)' % (len(items) - 30))
    if prices:
        lines.append('KEŞİF TOPLAMI: %s  (fiyatı eksik %d kalem%s)' % (_f(total), len(missing), (': ' + ', '.join(missing[:8])) if missing else ''))
    if diff_rows is not None:
        lines.append('REVİZYON FARKI (%s): %d değişiklik' % (diff_title, len(diff_rows)))
        for r in diff_rows[:25]:
            lines.append('  %s %s: %s → %s (%s%s %s)%s' % ('+' if r['fark'] > 0 else '−', r['kalem'], _f(r['eski']), _f(r['yeni']),
                                                     '+' if r['fark'] > 0 else '', _f(r['fark']), r['birim'],
                                                     ('  tutar farkı ' + _f(r['tutar_farki'])) if r['tutar_farki'] is not None else ''))
        cd = [r['tutar_farki'] for r in diff_rows if r['tutar_farki'] is not None]
        if cd:
            lines.append('  Toplam tutar farkı: %s' % _f(round(sum(cd), 2)))
    if saved:
        lines.append('Revizyon kaydedildi: %s' % saved)
    if xlsx:
        lines.append('Excel: %s' % xlsx)
    lines += notes
    return {'summary': '\n'.join(lines), 'xlsx': xlsx, 'total': total, 'missing': missing,
            'diff': diff_rows, 'revisions': list_snapshots(folder)}