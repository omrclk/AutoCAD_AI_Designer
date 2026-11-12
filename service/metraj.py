"""Metraj (quantity takeoff) post-processing: unit prices (keşif), revision snapshots/diff, dependency-free XLSX export.

The AutoCAD plugin computes the raw quantities (items) and posts them to /metraj; everything here is pure Python
(standard library only) so it can be unit tested without AutoCAD.

Item: {"kategori": str, "kalem": str, "birim": "adet|m|m²", "miktar": float, "adet": int (optional), "katman": str (optional)}
"""
import base64
import csv
import datetime
import io
import json
import os
import re
import zipfile
import xml.etree.ElementTree as ET
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


class XlsxImage:
    """Cell marker: embed the PNG registered under this key (write_xlsx images argument)."""

    def __init__(self, key):
        self.key = '' if key is None else str(key)


# ECMA-376 worksheet child order. Present elements must follow this sequence;
# drawing sits after sheetData (and after pageMargins / headerFooter when those exist).
_WS_CHILD_ORDER = (
    'sheetPr', 'dimension', 'sheetViews', 'sheetFormatPr', 'cols', 'sheetData', 'sheetCalcPr',
    'sheetProtection', 'protectedRanges', 'scenarios', 'autoFilter', 'sortState', 'dataConsolidate',
    'customSheetViews', 'mergeCells', 'phoneticPr', 'conditionalFormatting', 'dataValidations',
    'hyperlinks', 'printOptions', 'pageMargins', 'pageSetup', 'headerFooter', 'rowBreaks',
    'colBreaks', 'customProperties', 'cellWatches', 'ignoredErrors', 'smartTags', 'drawing',
    'legacyDrawing', 'legacyDrawingHF', 'drawingHF', 'picture', 'oleObjects', 'controls',
    'webPublishItems', 'tableParts', 'extLst',
)
_ANCHOR_ORDER = ('from', 'ext', 'pic', 'clientData')
_PIC_ORDER = ('nvPicPr', 'blipFill', 'spPr')
_FROM_ORDER = ('col', 'colOff', 'row', 'rowOff')
_DRAW_NS = 'http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing'
_A_NS = 'http://schemas.openxmlformats.org/drawingml/2006/main'
_REL_NS = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
_PKG_NS = 'http://schemas.openxmlformats.org/package/2006/relationships'
_CT_NS = 'http://schemas.openxmlformats.org/package/2006/content-types'
_EMU = 9525          # 1 px at 96 dpi
_IMG_PX = 90         # drawn size; the PNG itself is 96 px and the row is ~75 pt (~100 px)
_IMG_OFF = 4


def _png_bytes(v):
    """PNG bytes, or None. Accepts raw bytes or a base64 string."""
    if isinstance(v, (bytes, bytearray)):
        b = bytes(v)
    elif isinstance(v, str):
        s = v.strip()
        if s.startswith('data:') and ',' in s:
            s = s.split(',', 1)[1]
        try:
            b = base64.b64decode(s, validate=False)
        except (ValueError, TypeError):
            return None
    else:
        return None
    return b if len(b) >= 8 and b.startswith(b'\x89PNG\r\n\x1a\n') else None


def _anchors(rows, pngs):
    """(0-based row, 0-based col, key) for pictures whose PNG decoded."""
    out = []
    for r, row in enumerate(rows):
        for c, v in enumerate(row):
            if isinstance(v, XlsxImage) and v.key in pngs:
                out.append((r, c, v.key))
    return out


def _resim_cols(rows):
    cols = set()
    if rows:
        for i, v in enumerate(rows[0]):
            if isinstance(v, str) and v.strip().lower() == 'resim':
                cols.add(i)
    return cols


def _cols_xml(image_cols, ncols):
    n = max(ncols, 12, (max(image_cols) + 1) if image_cols else 0)
    widths = [18 if i in image_cols else (48 if i == 0 else 16) for i in range(n)]
    parts = ['<cols>']
    start = 0
    for i in range(1, len(widths) + 1):
        if i == len(widths) or widths[i] != widths[start]:
            parts.append('<col min="%d" max="%d" width="%d" customWidth="1"/>' % (start + 1, i, widths[start]))
            start = i
    parts.append('</cols>')
    return ''.join(parts)


def _cell_xml(ref, v, style):
    if isinstance(v, XlsxImage) or v is None or v == '':
        return ''
    if isinstance(v, bool):
        v = 'evet' if v else 'hayır'
    if isinstance(v, (int, float)):
        return '<c r="%s"%s><v>%s</v></c>' % (ref, style, repr(float(v)) if isinstance(v, float) else v)
    return '<c r="%s" t="inlineStr"%s><is><t xml:space="preserve">%s</t></is></c>' % (ref, style, escape(str(v)))


def _sheet_xml_ext(rows, anchors, resim_cols):
    """Sheet with a Resim column and, when anchors is non-empty, a drawing part reference.
    Child order is cols, sheetData, drawing — the ECMA-376 sequence."""
    image_rows = set(r for r, _, _ in anchors)
    image_cols = set(resim_cols)
    for _, c, _ in anchors:
        image_cols.add(c)
    ncols = max((len(row) for row in rows), default=0)
    ns = ' xmlns:r="%s"' % _REL_NS if anchors else ''
    out = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
           '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"%s>' % ns,
           _cols_xml(image_cols, ncols), '<sheetData>']
    for r, row in enumerate(rows, 1):
        ht = ' ht="75" customHeight="1"' if (r - 1) in image_rows else ''
        out.append('<row r="%d"%s>' % (r, ht))
        for c, v in enumerate(row):
            out.append(_cell_xml('%s%d' % (_col(c), r), v, ' s="1"' if r == 1 else ''))
        out.append('</row>')
    out.append('</sheetData>')
    if anchors:
        out.append('<drawing r:id="rId1"/>')
    out.append('</worksheet>')
    return ''.join(out)


def _sheet_rids(anchors):
    """One relationship id per distinct image key, in first-seen order."""
    rid = {}
    for _, _, key in anchors:
        if key not in rid:
            rid[key] = 'rId%d' % (len(rid) + 1)
    return rid


def _drawing_xml(anchors, rid_of):
    cx = _IMG_PX * _EMU
    off = _IMG_OFF * _EMU
    parts = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
             '<xdr:wsDr xmlns:xdr="%s" xmlns:a="%s" xmlns:r="%s">' % (_DRAW_NS, _A_NS, _REL_NS)]
    for n, (row, col, key) in enumerate(anchors, 1):
        parts.append(
            '<xdr:oneCellAnchor>'
            '<xdr:from><xdr:col>%d</xdr:col><xdr:colOff>%d</xdr:colOff>'
            '<xdr:row>%d</xdr:row><xdr:rowOff>%d</xdr:rowOff></xdr:from>'
            '<xdr:ext cx="%d" cy="%d"/>'
            '<xdr:pic>'
            '<xdr:nvPicPr><xdr:cNvPr id="%d" name="Resim%d"/>'
            '<xdr:cNvPicPr><a:picLocks noChangeAspect="1"/></xdr:cNvPicPr></xdr:nvPicPr>'
            '<xdr:blipFill><a:blip r:embed="%s"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>'
            '<xdr:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="%d" cy="%d"/></a:xfrm>'
            '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr>'
            '</xdr:pic><xdr:clientData/></xdr:oneCellAnchor>'
            % (col, off, row, off, cx, cx, n, n, rid_of[key], cx, cx))
    parts.append('</xdr:wsDr>')
    return ''.join(parts)


def _rels_xml(pairs):
    body = ''.join('<Relationship Id="%s" Type="%s" Target="%s"/>' % p for p in pairs)
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="%s">' % _PKG_NS) + body + '</Relationships>'


def _sheet_document(rows, pngs):
    """Classic sheet XML when nothing about pictures changed; otherwise the extended sheet."""
    anchors = _anchors(rows, pngs)
    resim = _resim_cols(rows)
    if not anchors and not resim:
        return _sheet_xml(rows), []
    return _sheet_xml_ext(rows, anchors, resim), anchors


def _local(tag):
    return tag.rsplit('}', 1)[-1]


def _order_ok(names, allowed):
    idx = []
    for name in names:
        if name not in allowed:
            return False
        idx.append(allowed.index(name))
    return idx == sorted(idx)


def _resolve(part, target):
    if target.startswith('/'):
        return target.lstrip('/')
    bits = part.split('/')[:-1]
    for p in target.split('/'):
        if p == '..':
            if bits:
                bits.pop()
        elif p and p != '.':
            bits.append(p)
    return '/'.join(bits)


def validate_package(path):
    """Raise ValueError when the xlsx would make Excel repair the workbook."""
    with zipfile.ZipFile(path) as z:
        names = set(z.namelist())
        ct = ET.fromstring(z.read('[Content_Types].xml'))
        defaults = {}
        overrides = {}
        for el in list(ct):
            if _local(el.tag) == 'Default':
                defaults.setdefault(el.get('Extension'), []).append(el.get('ContentType'))
            elif _local(el.tag) == 'Override':
                overrides[el.get('PartName', '').lstrip('/')] = el.get('ContentType')
        if 'rels' not in defaults or 'xml' not in defaults:
            raise ValueError('content types: rels/xml varsayılanı yok')
        media = [n for n in names if n.startswith('xl/media/')]
        if media and defaults.get('png') != ['image/png']:
            raise ValueError('content types: png varsayılanı yok veya yinelenmiş')
        if not media and 'png' in defaults:
            raise ValueError('content types: kullanılmayan png varsayılanı')
        for part, ctype in overrides.items():
            if part not in names:
                raise ValueError('content types: eksik parça %s' % part)
            if part.startswith('xl/drawings/') and ctype != 'application/vnd.openxmlformats-officedocument.drawing+xml':
                raise ValueError('content types: drawing türü yanlış %s' % part)
        drawings = sorted(n for n in names if re.match(r'xl/drawings/drawing\d+\.xml$', n))
        for n in drawings:
            if n not in overrides:
                raise ValueError('content types: drawing override yok %s' % n)
        for part in list(names):
            if part.endswith('.xml') or part.endswith('.rels'):
                ET.fromstring(z.read(part))
        for n in media:
            if not z.read(n).startswith(b'\x89PNG\r\n\x1a\n'):
                raise ValueError('media png değil: %s' % n)
        sheets = sorted(n for n in names if re.match(r'xl/worksheets/sheet\d+\.xml$', n))
        for sheet in sheets:
            root = ET.fromstring(z.read(sheet))
            kids = [_local(c.tag) for c in list(root)]
            if not _order_ok(kids, _WS_CHILD_ORDER):
                raise ValueError('sayfa eleman sırası hatalı %s: %s' % (sheet, kids))
            draw_els = [c for c in list(root) if _local(c.tag) == 'drawing']
            rels_name = sheet.rsplit('/', 1)[0] + '/_rels/' + sheet.rsplit('/', 1)[1] + '.rels'
            rels = {}
            if rels_name in names:
                for rel in ET.fromstring(z.read(rels_name)):
                    rels[rel.get('Id')] = (rel.get('Type'), rel.get('Target'))
            if draw_els and not rels:
                raise ValueError('çizim var ama sayfa ilişkisi yok: %s' % sheet)
            if not draw_els and any(t and t.endswith('/drawing') for t, _ in rels.values()):
                raise ValueError('sayfa ilişkisi var ama drawing elemanı yok: %s' % sheet)
            for el in draw_els:
                rid = el.get('{%s}id' % _REL_NS)
                typ, target = rels.get(rid, (None, None))
                if typ != _REL_NS + '/drawing' or not target:
                    raise ValueError('drawing r:id çözülmedi: %s' % sheet)
                draw = _resolve(sheet, target)
                if draw not in names:
                    raise ValueError('drawing parçası yok: %s' % draw)
                droot = ET.fromstring(z.read(draw))
                ids = []
                drels_name = draw.rsplit('/', 1)[0] + '/_rels/' + draw.rsplit('/', 1)[1] + '.rels'
                drels = {}
                if drels_name in names:
                    for rel in ET.fromstring(z.read(drels_name)):
                        drels[rel.get('Id')] = (rel.get('Type'), rel.get('Target'))
                for anch in list(droot):
                    if _local(anch.tag) not in ('oneCellAnchor', 'twoCellAnchor'):
                        raise ValueError('bilinmeyen anchor: %s' % _local(anch.tag))
                    if not _order_ok([_local(c.tag) for c in list(anch)], _ANCHOR_ORDER):
                        raise ValueError('anchor eleman sırası hatalı')
                    pic = next(c for c in list(anch) if _local(c.tag) == 'pic')
                    if not _order_ok([_local(c.tag) for c in list(pic)], _PIC_ORDER):
                        raise ValueError('pic eleman sırası hatalı')
                    frm = next(c for c in list(anch) if _local(c.tag) == 'from')
                    if not _order_ok([_local(c.tag) for c in list(frm)], _FROM_ORDER):
                        raise ValueError('from eleman sırası hatalı')
                    cNv = pic.find('.//{%s}cNvPr' % _DRAW_NS)
                    if cNv is None or not str(cNv.get('id', '0')).isdigit() or int(cNv.get('id')) < 1:
                        raise ValueError('cNvPr id geçersiz')
                    ids.append(int(cNv.get('id')))
                    blip = pic.find('.//{%s}blip' % _A_NS)
                    embed = blip.get('{%s}embed' % _REL_NS) if blip is not None else None
                    typ, target = drels.get(embed, (None, None))
                    if typ != _REL_NS + '/image' or not target:
                        raise ValueError('resim ilişkisi çözülmedi')
                    media_path = _resolve(draw, target)
                    if media_path not in names:
                        raise ValueError('media yok: %s' % media_path)
                if len(ids) != len(set(ids)):
                    raise ValueError('cNvPr id tekrarlı')
        # every relationship target in the package exists
        for rels_name in [n for n in names if n.endswith('.rels')]:
            owner = rels_name.replace('/_rels', '').rsplit('.rels', 1)[0]
            if owner == '_rels/.rels' or rels_name == '_rels/.rels':
                owner = ''
            for rel in ET.fromstring(z.read(rels_name)):
                target = rel.get('Target') or ''
                if target.startswith('http://') or target.startswith('https://'):
                    continue
                resolved = _resolve(owner if owner else 'xl/workbook.xml', target) if owner else target
                if rels_name == '_rels/.rels':
                    resolved = target.lstrip('/')
                if resolved not in names:
                    raise ValueError('ilişki hedefi yok: %s -> %s' % (rels_name, target))


def write_xlsx(path, sheets, images=None):
    """sheets: list of (name, rows); rows[0] is the bold header row.

    images: {key: png bytes or base64}. A cell holding XlsxImage(key) embeds that picture.
    The same key is stored once under xl/media and reused by every anchor.
    Sheets without a Resim column and without pictures keep the original XML.
    """
    pngs = {}
    for k, v in (images or {}).items():
        b = _png_bytes(v)
        if b:
            pngs[str(k)] = b
    names = [re.sub(r'[\[\]:*?/\\]', '', n)[:31] for n, _ in sheets]
    documents, plans = [], []
    for _, rows in sheets:
        xml, anchors = _sheet_document(rows, pngs)
        documents.append(xml)
        plans.append(anchors)
    media_of = {}
    for anchors in plans:
        for _, _, key in anchors:
            if key not in media_of:
                media_of[key] = len(media_of) + 1
    draw_of = {}
    for i, anchors in enumerate(plans):
        if anchors:
            draw_of[i] = len(draw_of) + 1
    ct_sheets = ''.join(
        '<Override PartName="/xl/worksheets/sheet%d.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' % (i + 1)
        for i in range(len(sheets)))
    ct_png = '<Default Extension="png" ContentType="image/png"/>' if media_of else ''
    ct_draw = ''.join(
        '<Override PartName="/xl/drawings/drawing%d.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>' % n
        for n in range(1, len(draw_of) + 1))
    files = {
        '[Content_Types].xml': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>' + ct_png + '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>' + ct_sheets + ct_draw + '</Types>',
        '_rels/.rels': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        'xl/workbook.xml': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>' + ''.join('<sheet name="%s" sheetId="%d" r:id="rId%d"/>' % (escape(n, {'"': '&quot;'}), i + 1, i + 1) for i, n in enumerate(names)) + '</sheets></workbook>',
        'xl/_rels/workbook.xml.rels': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + ''.join('<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet%d.xml"/>' % (i + 1, i + 1) for i in range(len(sheets))) + '<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>' % (len(sheets) + 1),
        'xl/styles.xml': '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>',
    }
    for i, xml in enumerate(documents):
        files['xl/worksheets/sheet%d.xml' % (i + 1)] = xml
    bins = {}
    for i, anchors in enumerate(plans):
        if not anchors:
            continue
        n = draw_of[i]
        rids = _sheet_rids(anchors)
        files['xl/drawings/drawing%d.xml' % n] = _drawing_xml(anchors, rids)
        files['xl/drawings/_rels/drawing%d.xml.rels' % n] = _rels_xml([
            (rid, _REL_NS + '/image', '../media/image%d.png' % media_of[key]) for key, rid in rids.items()])
        files['xl/worksheets/_rels/sheet%d.xml.rels' % (i + 1)] = _rels_xml([
            ('rId1', _REL_NS + '/drawing', '../drawings/drawing%d.xml' % n)])
    for key, n in media_of.items():
        bins['xl/media/image%d.png' % n] = pngs[key]
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        for n, s in files.items():
            z.writestr(n, s.encode('utf-8'))
        for n, b in bins.items():
            z.writestr(n, b)
    try:
        validate_package(path)
    except ValueError:
        try:
            os.remove(path)
        except OSError:
            pass
        raise
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

    raw_images = req.get('images') if isinstance(req.get('images'), dict) else None
    use_images = raw_images is not None
    valid_images = {}
    if use_images:
        for k, v in raw_images.items():
            if _png_bytes(v):
                valid_images[str(k)] = v

    def _pic(it):
        k = it.get('resim')
        if k is not None and str(k) in valid_images:
            return XlsxImage(str(k))
        return ''

    m_head = ['Kategori', 'Kalem', 'Katman', 'Birim', 'Miktar', 'Adet']
    m_rows = []
    for i in items:
        row = [i.get('kategori', ''), i['kalem'], i.get('katman', ''), i.get('birim', ''), round(i['miktar'], 3), i.get('adet')]
        if use_images:
            row = row[:2] + [_pic(i)] + row[2:]
        m_rows.append(row)
    if use_images:
        m_head = m_head[:2] + ['Resim'] + m_head[2:]
    k_head = ['Poz No', 'Kalem', 'Birim', 'Miktar', 'Birim Fiyat', 'Tutar']
    k_rows_out = []
    for it, r in zip(items, k_rows):
        row = [r['poz_no'], r['kalem'], r['birim'], round(r['miktar'], 3), r['birim_fiyat'], r['tutar']]
        if use_images:
            row = row[:2] + [_pic(it)] + row[2:]
        k_rows_out.append(row)
    total_row = ['', 'TOPLAM', '', '', '', total]
    if use_images:
        k_head = k_head[:2] + ['Resim'] + k_head[2:]
        total_row = ['', 'TOPLAM', '', '', '', '', total]
    k_tail = [[], total_row] + ([[], ['', 'Fiyatı olmayan kalem sayısı: %d' % len(missing)]] if missing else [])

    sheets = [('Metraj', [m_head] + m_rows), ('Keşif', [k_head] + k_rows_out + k_tail)]
    if diff_rows is not None:
        sheets.append(('Revizyon Farkı', [['Kalem (' + diff_title + ')', 'Katman', 'Birim', 'Eski', 'Yeni', 'Fark', 'Durum', 'Tutar Farkı']] +
                       [[r['kalem'], r['katman'], r['birim'], r['eski'], r['yeni'], r['fark'], r['durum'], r['tutar_farki']] for r in diff_rows]))
    else:
        sheets.append(('Revizyon Farkı', [['Karşılaştırma yapılmadı']]))

    xlsx = None
    if req.get('export_path'):
        xlsx = write_xlsx(req['export_path'], sheets, valid_images if use_images else None)

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
    if use_images and valid_images:
        used = set(str(i.get('resim')) for i in items if i.get('resim') is not None and str(i.get('resim')) in valid_images)
        if used:
            lines.append('Blok resimleri: %d önizleme eklendi.' % len(used))
    if xlsx:
        lines.append('Excel: %s' % xlsx)
    lines += notes
    return {'summary': '\n'.join(lines), 'xlsx': xlsx, 'total': total, 'missing': missing,
            'diff': diff_rows, 'revisions': list_snapshots(folder)}