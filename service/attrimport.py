"""Attribute import: read an (edited) AIDATTEXPORT workbook and diff it against the drawing's current values.
The plugin posts {path, blocks:[{handle, block, x, y, attrs:[[tag, value]], const_tags:[...]}]} to /attributes_import;
the plugin applies the returned changes after the user confirms."""
import re
import zipfile
import xml.etree.ElementTree as ET

import attrexport

NS = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
RNS = '{http://schemas.openxmlformats.org/officeDocument/2006/relationships}'
NON_ATTR = {'blok', 'katman', 'handle', 'x', 'y', 'alan'}
SKIP_SHEETS = {'\u00f6zet', 'ozet'}
ALL_SHEET = {'t\u00fcm\u00fc', 'tumu'}


def _key(s):
    return re.sub(r'\s*\(.*\)\s*$', '', str(s or '')).strip().lower()


def _meta(raw):
    """Identity columns. 'Tip' is the entity-type column (MLEADER/BLOK); attribute tag TIP is kept (different header text)."""
    if raw is None:
        return True
    s = str(raw).strip()
    if s == attrexport.TYPE_COL or s == attrexport.RESIM_COL:
        return True
    return _key(s) in NON_ATTR


def read_sheets(path):
    """[(sheet name, rows)] in workbook order. Cells: ('s', text) for text, ('n', raw) for numbers."""
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        shared = []
        if 'xl/sharedStrings.xml' in names:
            for si in ET.fromstring(z.read('xl/sharedStrings.xml')).iter(NS + 'si'):
                shared.append(''.join(t.text or '' for t in si.iter(NS + 't')))
        wb = ET.fromstring(z.read('xl/workbook.xml'))
        rels = {}
        if 'xl/_rels/workbook.xml.rels' in names:
            for r in ET.fromstring(z.read('xl/_rels/workbook.xml.rels')):
                t = r.get('Target', '')
                t = t.lstrip('/') if t.startswith('/') else 'xl/' + t
                rels[r.get('Id')] = t.replace('xl/xl/', 'xl/')
        out = []
        for i, sh in enumerate(wb.iter(NS + 'sheet')):
            target = rels.get(sh.get(RNS + 'id')) or 'xl/worksheets/sheet%d.xml' % (i + 1)
            if target not in names:
                continue
            out.append((sh.get('name', ''), _rows(ET.fromstring(z.read(target)), shared)))
    return out


def _rows(root, shared):
    rows = []
    for row in root.iter(NS + 'row'):
        cells = {}
        for c in row.iter(NS + 'c'):
            m = re.match(r'([A-Z]+)', c.get('r', '') or '')
            if m:
                col = 0
                for ch in m.group(1):
                    col = col * 26 + ord(ch) - 64
                col -= 1
            else:
                col = max(cells) + 1 if cells else 0
            t = c.get('t')
            v = c.find(NS + 'v')
            if t == 's' and v is not None:
                val = ('s', shared[int(v.text)])
            elif t == 'inlineStr':
                val = ('s', ''.join(x.text or '' for x in c.iter(NS + 't')))
            elif t in ('str', 'e'):
                val = ('s', v.text if v is not None and v.text else '')
            elif t == 'b':
                val = ('s', 'evet' if v is not None and v.text == '1' else 'hay\u0131r')
            else:
                val = ('n', v.text) if v is not None and v.text not in (None, '') else ('s', '')
            cells[col] = val
        r = int(row.get('r')) - 1 if row.get('r') else len(rows)
        while len(rows) < r:
            rows.append([])
        rows.append([cells.get(i, ('s', '')) for i in range(max(cells) + 1)] if cells else [])
    return rows


def fmt_number(raw, old):
    """Excel number -> text in the drawing's style: integers without '.0', separator like the old value (default comma)."""
    try:
        f = float(raw)
    except (TypeError, ValueError):
        return str(raw)
    s = ('%.12g' % f)
    if 'e' in s or 'E' in s:
        s = ('%.12f' % f).rstrip('0').rstrip('.')
    if s == '-0':
        s = '0'
    old = old or ''
    if '.' in s and not ('.' in old and ',' not in old):
        s = s.replace('.', ',')
    return s


def cell_text(cell, old):
    kind, v = cell
    if kind == 'n':
        n = attrexport.num(old) if old else None
        try:
            f = float(v)
        except ValueError:
            return str(v)
        if n is not None and abs(n - f) <= 1e-9 * max(1.0, abs(f)):
            return old                      # same number, keep the drawing's text exactly ("1,5", "60.0")
        return fmt_number(v, old)
    return v


def parse(sheets):
    """-> (per_block {handle_or_rowid: row}, tumu {...}). row = {handle, block, x, y, cells{tag: cell}, sheet, line}."""
    per, allrows = [], []
    for name, rows in sheets:
        if _key(name) in SKIP_SHEETS or not rows:
            continue
        head = [_key(c[1]) if c else '' for c in rows[0]]
        raw_head = [c[1] if c else '' for c in rows[0]]
        if 'handle' not in head:
            continue
        hi = head.index('handle')
        bi = head.index('blok') if 'blok' in head else None
        xi = head.index('x') if 'x' in head else None
        yi = head.index('y') if 'y' in head else None
        tags = [(i, raw_head[i]) for i in range(len(head)) if raw_head[i] and not _meta(raw_head[i])]
        target = allrows if _key(name) in ALL_SHEET else per
        for ln, r in enumerate(rows[1:], 2):
            if not r or all(not c[1] for c in r):
                continue
            g = lambda i: r[i][1] if i is not None and i < len(r) else ''
            if str(g(0)).strip().upper() == 'TOPLAM' or str(g(bi)).strip().upper() == 'TOPLAM':
                continue
            def fl(i):
                try:
                    return float(str(g(i)).replace(',', '.'))
                except ValueError:
                    return None
            h = str(g(hi)).strip().upper()
            if h.endswith('.0') and re.match(r'^\d+\.0$', h):
                h = h[:-2]
            target.append({'handle': h, 'block': str(g(bi)).strip(), 'x': fl(xi), 'y': fl(yi), 'sheet': name, 'line': ln,
                           'cells': dict((t, r[i] if i < len(r) else ('s', '')) for i, t in tags)})
    return per, allrows


def _match(row, blocks, by_handle, tol=0.01):
    b = by_handle.get(row['handle']) if row['handle'] else None
    if b is not None:
        return b, False
    if row['block'] and row['x'] is not None and row['y'] is not None:
        hits = [c for c in blocks if str(c.get('block', '')).lower() == row['block'].lower()
                and abs(float(c.get('x') or 0) - row['x']) <= tol and abs(float(c.get('y') or 0) - row['y']) <= tol]
        if len(hits) == 1:
            return hits[0], True
    return None, False


def diff(req):
    path = req.get('path') or ''
    if not path:
        raise ValueError('path gerekli')
    try:
        sheets = read_sheets(path)
    except (zipfile.BadZipFile, KeyError) as e:
        raise ValueError('xlsx okunamad\u0131: %s' % e)
    blocks = [b for b in (req.get('blocks') or []) if isinstance(b, dict)]
    by_handle = dict((str(b.get('handle', '')).upper(), b) for b in blocks)
    per, allrows = parse(sheets)
    if not per and not allrows:
        raise ValueError('Handle s\u00fctunu olan sayfa bulunamad\u0131 (AIDATTEXPORT dosyas\u0131 m\u0131?)')
    # proposed[handle][tag] = (new, source); per-block sheets win over T\u00fcm\u00fc when both changed
    proposed, notfound, fallback, conflicts, seen_rows = {}, [], [], [], 0
    for src_rows, prio in ((allrows, 0), (per, 1)):
        for row in src_rows:
            seen_rows += 1
            b, fb = _match(row, blocks, by_handle)
            if b is None:
                notfound.append('%s!%d %s %s' % (row['sheet'], row['line'], row['handle'] or '-', row['block']))
                continue
            h = str(b['handle']).upper()
            if fb:
                fallback.append('%s!%d -> %s' % (row['sheet'], row['line'], h))
            cur = dict((str(k).lower(), (str(k), '' if v is None else str(v))) for k, v in b.get('attrs', []))
            consts = set(str(t).lower() for t in b.get('const_tags') or [])
            for tag, cell in row['cells'].items():
                k = tag.strip().lower()
                if k not in cur or k in consts:
                    continue
                realtag, old = cur[k]
                new = cell_text(cell, old)
                if new == old or (new.strip() == old.strip() and new.strip() != ''):
                    continue
                d = proposed.setdefault(h, {})
                if realtag in d and d[realtag][1] != prio:
                    if d[realtag][0] != new:
                        conflicts.append('%s %s: T\u00fcm\u00fc="%s", %s="%s" (blok sayfas\u0131 kullan\u0131ld\u0131)' % (h, realtag, d[realtag][0], row['sheet'], new))
                d[realtag] = (new, prio, old, row['sheet'])
    changes = []
    for h, d in proposed.items():
        b = by_handle[h]
        for tag, (new, _, old, sheet) in d.items():
            changes.append({'handle': h, 'block': b.get('block', ''), 'tag': tag, 'old': old, 'new': new, 'sheet': sheet})
    changes.sort(key=lambda c: (c['block'], c['handle'], c['tag']))
    touched = set(c['handle'] for c in changes)
    matched = set()
    for row in allrows + per:
        b, _ = _match(row, blocks, by_handle)
        if b is not None:
            matched.add(str(b['handle']).upper())
    return {'changes': changes, 'changed_blocks': len(touched), 'unchanged_blocks': len(matched - touched),
            'not_found': notfound, 'fallback': fallback, 'conflicts': conflicts,
            'source': 'blok sayfalar\u0131 + T\u00fcm\u00fc' if per and allrows else ('blok sayfalar\u0131' if per else 'T\u00fcm\u00fc')}
