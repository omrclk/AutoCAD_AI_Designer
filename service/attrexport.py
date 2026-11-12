"""Attribute (etiket) block export to Excel. Pure Python; the AutoCAD plugin posts the raw rows to /attributes."""
import os
import re

import metraj

_NUM = re.compile(r'^\s*-?(\d{1,3}(\.\d{3})+|\d+)(,\d+)?\s*$|^\s*-?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?\s*$')


def num(v):
    """'12', '12,5', '12.5', '1.234,5', '1,234.5' -> float; anything else -> None."""
    if not isinstance(v, str) or not _NUM.match(v):
        return None
    s = v.strip()
    if ',' in s and '.' in s:
        s = s.replace('.', '').replace(',', '.') if s.rfind(',') > s.rfind('.') else s.replace(',', '')
    elif ',' in s:
        s = s.replace(',', '.') if s.count(',') == 1 else s.replace(',', '')
    elif s.count('.') > 1:
        s = s.replace('.', '')
    try:
        return float(s)
    except ValueError:
        return None


def _cell(v):
    n = num(v)
    if n is None:
        return v
    return int(n) if n.is_integer() and abs(n) < 1e15 else n


def _union(lists):
    out = []
    for l in lists:
        for t in l:
            if t not in out:
                out.append(t)
    return out


HANDLE_COL = 'Handle (değiştirmeyin)'
TYPE_COL = 'Tip'  # entity type: MLEADER (block content) or BLOK (insert). Exact header, so a TIP attribute tag is a different column.
RESIM_COL = 'Resim'  # block preview; exact header, so a RESIM attribute tag stays its own column
FIXED = ['Blok', TYPE_COL, 'Katman', HANDLE_COL, 'X', 'Y']


def _etype(b):
    t = b.get('type')
    if t is None or str(t).strip() == '':
        return 'BLOK'
    return str(t).strip().upper()


def _rows(blocks, tags, with_block=True, image_keys=None):
    """image_keys: set of picture keys to embed, or None to leave the sheet unchanged (no Resim column)."""
    images_on = image_keys is not None
    fixed = list(FIXED)
    if images_on:
        fixed.insert(1, RESIM_COL)
    head = fixed + (['Alan'] if any(b.get('space') not in (None, '', 'Model') for b in blocks) else []) + tags
    space = 'Alan' in head
    rows = [head]
    numeric = {t: True for t in tags}
    seen = {t: False for t in tags}
    for b in blocks:
        av = dict((str(k), '' if v is None else str(v)) for k, v in b.get('attrs', []))
        r = [b.get('block', ''), _etype(b), b.get('layer', ''), b.get('handle', ''), round(float(b.get('x') or 0), 3), round(float(b.get('y') or 0), 3)]
        if images_on:
            key = b.get('resim')
            r.insert(1, metraj.XlsxImage(str(key)) if key is not None and str(key) in image_keys else '')
        if space:
            r.append(b.get('space') or 'Model')
        for t in tags:
            v = av.get(t, '')
            if v.strip():
                seen[t] = True
                if num(v) is None:
                    numeric[t] = False
            r.append(_cell(v) if v.strip() else '')
        rows.append(r)
    num_tags = [t for t in tags if numeric[t] and seen[t]]
    if num_tags and len(rows) > 1:
        tot = ['TOPLAM'] + [''] * (len(head) - 1)
        for t in num_tags:
            i = head.index(t)
            s = sum(x[i] for x in rows[1:] if isinstance(x[i], (int, float)))
            tot[i] = round(s, 4)
        rows += [[], tot]
    return rows, num_tags


def _summary(blocks, tags, group_by, num_tags):
    """Group by one tag; count + sum of every numeric tag."""
    sums = [t for t in num_tags if t != group_by]
    groups = {}
    for b in blocks:
        av = dict((str(k), '' if v is None else str(v)) for k, v in b.get('attrs', []))
        if group_by not in av:
            continue
        key = av[group_by].strip() or '(boş)'
        g = groups.setdefault(key, [0] + [0.0] * len(sums))
        g[0] += 1
        for i, t in enumerate(sums):
            n = num(av.get(t, ''))
            if n is not None:
                g[i + 1] += n
    rows = [[group_by, 'Adet'] + sums]
    for k in sorted(groups):
        rows.append([_cell(k)] + [groups[k][0]] + [round(x, 4) for x in groups[k][1:]])
    if groups:
        rows += [[], ['TOPLAM', sum(g[0] for g in groups.values())] + [round(sum(g[i + 1] for g in groups.values()), 4) for i in range(len(sums))]]
    return rows


def find_tag(tags, want):
    if not want:
        return None
    w = metraj.norm(want)
    for t in tags:
        if metraj.norm(t) == w:
            return t
    return None


def export(req):
    blocks = [b for b in (req.get('blocks') or []) if isinstance(b, dict)]
    if not blocks:
        raise ValueError('aktarılacak attribute bloğu bulunamadı')
    order = req.get('tag_order') or {}
    names = []
    for b in blocks:
        if b.get('block', '') not in names:
            names.append(b.get('block', ''))
    per = {}
    for n in names:
        bl = [b for b in blocks if b.get('block', '') == n]
        per[n] = _union([order.get(n) or []] + [[str(k) for k, _ in b.get('attrs', [])] for b in bl])
    all_tags = _union([per[n] for n in names])
    raw_images = req.get('images') if isinstance(req.get('images'), dict) else None
    image_keys = None
    valid_images = None
    if raw_images is not None:
        image_keys = set()
        valid_images = {}
        for k, v in raw_images.items():
            if metraj._png_bytes(v):
                image_keys.add(str(k))
                valid_images[str(k)] = v
    sheets = []
    rows, all_num = _rows(blocks, all_tags, image_keys=image_keys)
    sheets.append(('Tümü', rows))
    used = {'tümü', 'özet'}
    for n in names:
        nm = re.sub(r'[\[\]:*?/\\]', '_', n)[:28] or 'Blok'
        base, k = nm, 2
        while nm.lower() in used:
            nm = '%s_%d' % (base[:26], k)
            k += 1
        used.add(nm.lower())
        sheets.append((nm, _rows([b for b in blocks if b.get('block', '') == n], per[n], image_keys=image_keys)[0]))
    gb = find_tag(all_tags, req.get('group_by'))
    notes = []
    if req.get('group_by') and not gb:
        notes.append('Gruplama etiketi "%s" bulunamadı; özet sayfası eklenmedi.' % req.get('group_by'))
    if gb:
        sheets.insert(0, ('Özet', _summary(blocks, all_tags, gb, all_num)))
    path = req.get('export_path') or ''
    if not path:
        raise ValueError('export_path gerekli')
    metraj.write_xlsx(path, sheets, valid_images)
    summary = '%d blok (%d blok adı, %d etiket) Excel\'e aktarıldı: %s' % (len(blocks), len(names), len(all_tags), path)
    n_ml = sum(1 for b in blocks if _etype(b) == 'MLEADER')
    if n_ml:
        summary += '\nMultileader (blok içerik): %d. Tip sütunu: MLEADER.' % n_ml
    if all_num:
        summary += '\nSayısal sütunlar (toplamı var): ' + ', '.join(all_num)
    if gb:
        summary += '\nÖzet: %s gruplarına göre.' % gb
    if image_keys:
        shown = set(str(b.get('resim')) for b in blocks if b.get('resim') is not None and str(b.get('resim')) in image_keys)
        if shown:
            summary += '\nBlok resimleri: %d önizleme.' % len(shown)
    if notes:
        summary += '\n' + '\n'.join(notes)
    return {'summary': summary, 'path': path, 'count': len(blocks), 'tags': all_tags, 'numeric': all_num}
