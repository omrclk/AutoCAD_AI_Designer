"""Plan (action) validation shared by the planner service.

The C# add-in re-validates everything (it is the authority). This module is a
first line of defence so that obviously invalid model output is retried
instead of being shown to the user.
"""
import math
import re

LABEL_RE = re.compile(r"^[\w.\-]{1,40}$", re.UNICODE)

MAX_ACTIONS = 30
MAX_HANDLES = 1000000
ACTION_TYPES = (
    'move', 'copy', 'rotate', 'scale', 'set_layer', 'create_layer',
    'line', 'polyline', 'rectangle', 'circle', 'text', 'insert_block', 'erase', 'command', 'place_block_along', 'takeoff', 'place_fittings', 'export_attributes', 'plan_takeoff', 'plan_copy',
)
# AutoCAD commands the AI may never run (file/app/session control, code loading, security settings).
DENIED_COMMANDS = {
    'QUIT', 'EXIT', 'CLOSE', 'CLOSEALL', 'OPEN', 'NEW', 'QNEW', 'RECOVER', 'RECOVERALL', 'SAVE', 'SAVEAS', 'QSAVE',
    'SAVEALL', 'WBLOCK', 'EXPORT', 'EXPORTPDF', 'DXFOUT', 'PUBLISH', 'PLOT', 'ETRANSMIT', 'ARCHIVE',
    'SHELL', 'SH', 'START', 'BROWSER', 'HYPERLINK', 'NETLOAD', 'APPLOAD', 'ARX', 'LOAD', 'SCRIPT', 'SCR',
    'VBARUN', 'VBALOAD', 'VBAIDE', 'VBAMAN', 'VLISP', 'VLIDE', 'ACTUSERMESSAGE', 'ACTSTOP',
    'SECURELOAD', 'TRUSTEDPATHS', 'OPTIONS', 'CUI', 'CUILOAD', 'CUIUNLOAD', 'MENU', 'MENULOAD', 'MENUUNLOAD',
    'AIDAPPLY', 'AIDSELECT', 'AIDESIGNER', 'XREF', 'XATTACH', 'ATTACH', 'IMAGEATTACH', 'DATAEXTRACTION',
}
DENIED_ARG_WORDS = {'SECURELOAD', 'TRUSTEDPATHS', 'LEGACYCODESEARCH', 'ACADLSPASDOC', 'CMDECHO'}
_CMD_NAME = re.compile(r"^[_.+\-']*([A-Za-z][A-Za-z0-9_]*)$")
SEL = '$SEL'
_BAD_LAYER_CHARS = re.compile(r'[<>/\\":;?*|=`]')
_HEX = re.compile(r'^[0-9A-Fa-f]{1,16}$')


class PlanError(ValueError):
    pass


def _num(v, what, lo=-1e8, hi=1e8):
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        raise PlanError('%s sayı olmalı' % what)
    v = float(v)
    if math.isnan(v) or math.isinf(v) or v < lo or v > hi:
        raise PlanError('%s geçersiz' % what)
    return v


def _opt_num(a, key, default, lo=-1e8, hi=1e8):
    return default if a.get(key) is None else _num(a[key], key, lo, hi)


def _pt(v, what):
    if not isinstance(v, (list, tuple)) or len(v) not in (2, 3):
        raise PlanError('%s [x,y] veya [x,y,z] olmalı' % what)
    p = [_num(x, what) for x in v]
    return p + [0.0] if len(p) == 2 else p


def _handles(v, allowed):
    if not isinstance(v, list) or not 1 <= len(v) <= MAX_HANDLES:
        raise PlanError('handles 1-%d elemanlı liste olmalı' % MAX_HANDLES)
    out = []
    for h in v:
        s = str(h).strip()
        if s.lower().startswith('0x'):
            s = s[2:]
        if not _HEX.match(s):
            raise PlanError('geçersiz handle: %s' % h)
        s = s.upper()
        if s not in allowed:
            raise PlanError('seçili olmayan handle: %s' % s)
        out.append(s)
    return out


def layer_names(ctx):
    names = []
    for l in ctx.get('layers') or []:
        names.append(l['name'] if isinstance(l, dict) else str(l))
    return names


def validate_actions(actions, ctx):
    """Return normalized actions or raise PlanError."""
    if not isinstance(actions, list):
        raise PlanError('actions liste olmalı')
    if len(actions) > MAX_ACTIONS:
        raise PlanError('en fazla %d işlem' % MAX_ACTIONS)
    allowed = {str(s.get('handle', '')).upper() for s in ctx.get('selected') or [] if isinstance(s, dict)}
    comp = ctx.get('selected_compact')
    if isinstance(comp, dict):
        allowed |= {str(r[0]).upper() for r in comp.get('rows') or [] if isinstance(r, (list, tuple)) and r}
    extra = set(allowed) | duplicate_handles(ctx)
    layers = {n.lower() for n in layer_names(ctx)}
    blocks = {str(b).lower() for b in ctx.get('blocks') or []}
    out = []
    for i, a in enumerate(actions, 1):
        try:
            out.append(_one(a, allowed, layers, blocks, extra))
        except PlanError as e:
            raise PlanError('işlem %d: %s' % (i, e))
    return out


def _opt_layer(a, layers, n):
    layer = a.get('layer')
    if layer is None:
        return
    if not isinstance(layer, str) or layer.lower() not in layers:
        raise PlanError('katman bulunamadı: %s' % layer)
    n['layer'] = layer


def duplicate_handles(ctx):
    out = set()
    summ = ctx.get('drawing_summary') or {}
    for g in summ.get('duplicate_blocks') or []:
        if isinstance(g, dict):
            out.update(str(h).upper() for h in g.get('extra_handles') or [])
    return out


def _command(a, extra):
    name = a.get('command')
    if not isinstance(name, str):
        raise PlanError('command metin olmalı')
    m = _CMD_NAME.match(name.strip())
    if not m:
        raise PlanError('geçersiz komut adı: %s' % name)
    if m.group(1).upper() in DENIED_COMMANDS:
        raise PlanError('güvenlik nedeniyle yasaklı komut: %s' % m.group(1).upper())
    args = a.get('args', [])
    if args is None:
        args = []
    if not isinstance(args, list) or len(args) > 60:
        raise PlanError('args en fazla 60 elemanlı liste olmalı')
    clean = []
    for x in args:
        if isinstance(x, bool) or not isinstance(x, (str, int, float)):
            raise PlanError('args yalnızca metin/sayı içerebilir')
        s = str(x)
        if len(s) > 256 or '\n' in s or '\r' in s:
            raise PlanError('geçersiz argüman')
        t = s.strip()
        if t.startswith('(') or t.startswith('!') or t.startswith("'") :
            raise PlanError('LISP/ifade argümanı yasak: %s' % t[:30])
        if t.upper().lstrip('_.-') in DENIED_ARG_WORDS:
            raise PlanError('yasaklı argüman: %s' % t)
        clean.append(x if not isinstance(x, str) else s)
    n = {'type': 'command', 'command': name.strip(), 'args': clean}
    if a.get('handles') is not None:
        n['handles'] = _handles(a.get('handles'), extra)
    elif SEL in clean:
        raise PlanError('$SEL kullanıldı ama handles yok')
    return n


TARGETABLE = ('move', 'copy', 'rotate', 'scale', 'erase', 'set_layer', 'place_fittings', 'place_block_along')
ENTITY_TYPES = ('any', 'line', 'polyline', 'block', 'insert', 'text', 'circle', 'arc', 'hatch', 'dimension', 'spline')


def _target(v, layers, blocks):
    """Filter target resolved by the plugin: {block, layer, entity_type, all}."""
    if not isinstance(v, dict):
        raise PlanError('target nesne olmalı')
    out = {}
    b = v.get('block')
    if b is not None:
        if not isinstance(b, str) or b.lower() not in blocks:
            raise PlanError('target blok bulunamadı: %s' % b)
        out['block'] = b
    ly = v.get('layer')
    if ly is not None:
        if not isinstance(ly, str) or ly.lower() not in layers:
            raise PlanError('target katman bulunamadı: %s' % ly)
        out['layer'] = ly
    et = v.get('entity_type')
    if et is not None:
        if not isinstance(et, str) or et.lower() not in ENTITY_TYPES:
            raise PlanError('target entity_type geçersiz: %s' % et)
        out['entity_type'] = et.lower()
    if v.get('all'):
        out['all'] = True
    if not out:
        raise PlanError('target boş')
    return out


def _one(a, allowed, layers, blocks, extra=None):
    if isinstance(a, dict) and a.get('target') is not None and a.get('type') in TARGETABLE:
        tg = _target(a.get('target'), layers, blocks)
        b = dict(a)
        b.pop('target')
        b['handles'] = ['FFFFFFFFFFFF']
        n = _one_inner(b, {'FFFFFFFFFFFF'}, layers, blocks, {'FFFFFFFFFFFF'})
        n['handles'] = []
        n['target'] = tg
        return n
    return _one_inner(a, allowed, layers, blocks, extra)


def _one_inner(a, allowed, layers, blocks, extra=None):
    extra = allowed if extra is None else extra
    if not isinstance(a, dict):
        raise PlanError('işlem nesne olmalı')
    t = a.get('type')
    if t not in ACTION_TYPES:
        raise PlanError('izin verilmeyen işlem: %s' % t)
    n = {'type': t}
    if t in ('move', 'copy'):
        n['handles'] = _handles(a.get('handles'), allowed)
        n['dx'], n['dy'], n['dz'] = (_opt_num(a, k, 0.0) for k in ('dx', 'dy', 'dz'))
        if n['dx'] == n['dy'] == n['dz'] == 0:
            raise PlanError('öteleme sıfır')
    elif t == 'rotate':
        n['handles'] = _handles(a.get('handles'), allowed)
        n['angle_deg'] = _num(a.get('angle_deg'), 'angle_deg', -3600, 3600)
        n['center'] = _pt(a.get('center'), 'center')
    elif t == 'scale':
        n['handles'] = _handles(a.get('handles'), allowed)
        n['factor'] = _num(a.get('factor'), 'factor', 0.001, 1000)
        n['base'] = _pt(a.get('base'), 'base')
    elif t == 'plan_takeoff':
        pass
    elif t == 'plan_copy':
        n['mode'] = 'move' if a.get('mode') == 'move' else 'copy'
    elif t == 'export_attributes':
        scope = a.get('scope', 'auto') or 'auto'
        if scope not in ('auto', 'selection', 'all', 'attributed', 'block'):
            raise PlanError('export_attributes scope auto/selection/all/attributed/block olmalı')
        n['scope'] = scope
        for k in ('block', 'group_by'):
            v = a.get(k)
            if v is not None:
                if not isinstance(v, str) or len(v) > 255:
                    raise PlanError('export_attributes %s metin olmalı' % k)
                n[k] = v
        if scope == 'block' and not n.get('block'):
            raise PlanError('export_attributes block kapsamı için block adı gerekli')
        n['all_layouts'] = bool(a.get('all_layouts', False))
    elif t == 'takeoff':
        scope = a.get('scope', 'all') or 'all'
        if scope not in ('all', 'selection', 'layer'):
            raise PlanError('takeoff scope all/selection/layer olmalı')
        n['scope'] = scope
        if scope == 'selection':
            n['handles'] = _handles(a.get('handles'), allowed) if a.get('handles') else []
        if scope == 'layer':
            ls = a.get('layers')
            if not isinstance(ls, list) or not ls or not all(isinstance(x, str) and 0 < len(x) <= 255 for x in ls):
                raise PlanError('takeoff layer kapsamı için layers listesi gerekli')
            n['layers'] = ls[:50]
        for k in ('save_revision', 'compare_to'):
            v = a.get(k)
            if v:
                if not isinstance(v, str) or not LABEL_RE.match(v):
                    raise PlanError('%s geçersiz revizyon etiketi' % k)
                n[k] = v
        pair = a.get('compare_revisions')
        if pair:
            if not isinstance(pair, list) or len(pair) != 2 or not all(isinstance(x, str) and LABEL_RE.match(x) for x in pair):
                raise PlanError('compare_revisions iki revizyon etiketi olmalı')
            n['compare_revisions'] = pair
        dwg = a.get('compare_dwg')
        if dwg:
            if not isinstance(dwg, str) or not dwg.lower().endswith('.dwg') or len(dwg) > 400:
                raise PlanError('compare_dwg .dwg dosya yolu olmalı')
            n['compare_dwg'] = dwg
        n['include_texts'] = bool(a.get('include_texts', False))
        n['export'] = bool(a.get('export', True))
    elif t == 'place_block_along':
        name = a.get('name')
        if not isinstance(name, str) or name.lower() not in blocks:
            raise PlanError('blok bulunamadı: %s' % name)
        n['name'] = name
        n['handles'] = _handles(a.get('handles'), allowed)
        al = a.get('alignment', 'start') or 'start'
        if al not in ('start', 'center'):
            raise PlanError('alignment start/center olmalı')
        n['alignment'] = al
        if a.get('width') is not None:
            n['width'] = _num(a.get('width'), 'width', 1e-9, 1e8)
        for k in ('length_param', 'width_param'):
            v = a.get(k)
            if v is not None:
                if not isinstance(v, str) or not 1 <= len(v) <= 100:
                    raise PlanError('%s geçersiz' % k)
                n[k] = v
        for k in ('use_target_layer', 'keep_source'):
            if a.get(k) is not None:
                n[k] = bool(a.get(k))
        _opt_layer(a, layers, n)
    elif t == 'erase':
        n['handles'] = _handles(a.get('handles'), extra)
    elif t == 'place_fittings':
        if a.get('handles'):
            n['handles'] = _handles(a.get('handles'), allowed)
        if a.get('trim') is not None:
            n['trim'] = bool(a.get('trim'))
    elif t == 'command':
        return _command(a, extra)
    elif t == 'set_layer':
        n['handles'] = _handles(a.get('handles'), allowed)
        _opt_layer(a, layers, n)
        if 'layer' not in n:
            raise PlanError('layer gerekli')
    elif t == 'create_layer':
        name = a.get('name')
        if not isinstance(name, str) or not 1 <= len(name.strip()) <= 255 or _BAD_LAYER_CHARS.search(name):
            raise PlanError('geçersiz katman adı')
        n['name'] = name.strip()
        n['color_index'] = int(_opt_num(a, 'color_index', 7, 1, 255))
        layers.add(n['name'].lower())
    elif t == 'line':
        n['start'], n['end'] = _pt(a.get('start'), 'start'), _pt(a.get('end'), 'end')
        if n['start'] == n['end']:
            raise PlanError('başlangıç ve bitiş aynı')
        _opt_layer(a, layers, n)
    elif t == 'polyline':
        pts = a.get('points')
        if not isinstance(pts, list) or not 2 <= len(pts) <= 200:
            raise PlanError('points 2-200 nokta olmalı')
        n['points'] = [_pt(p, 'point') for p in pts]
        n['closed'] = bool(a.get('closed', False))
        _opt_layer(a, layers, n)
    elif t == 'rectangle':
        n['corner1'], n['corner2'] = _pt(a.get('corner1'), 'corner1'), _pt(a.get('corner2'), 'corner2')
        if n['corner1'][0] == n['corner2'][0] or n['corner1'][1] == n['corner2'][1]:
            raise PlanError('dikdörtgen sıfır alanlı')
        _opt_layer(a, layers, n)
    elif t == 'circle':
        n['center'] = _pt(a.get('center'), 'center')
        n['radius'] = _num(a.get('radius'), 'radius', 1e-9, 1e8)
        _opt_layer(a, layers, n)
    elif t == 'text':
        txt = a.get('text')
        if not isinstance(txt, str) or not 1 <= len(txt) <= 500:
            raise PlanError('text 1-500 karakter olmalı')
        n['text'] = txt
        n['position'] = _pt(a.get('position'), 'position')
        n['height'] = _num(a.get('height'), 'height', 1e-9, 1e6)
        n['rotation_deg'] = _opt_num(a, 'rotation_deg', 0.0, -3600, 3600)
        _opt_layer(a, layers, n)
    elif t == 'insert_block':
        name = a.get('name')
        if not isinstance(name, str) or name.lower() not in blocks:
            raise PlanError('blok bulunamadı: %s' % name)
        n['name'] = name
        n['position'] = _pt(a.get('position'), 'position')
        n['scale'] = _opt_num(a, 'scale', 1.0, 1e-6, 1e6)
        n['rotation_deg'] = _opt_num(a, 'rotation_deg', 0.0, -3600, 3600)
        _opt_layer(a, layers, n)
    return n
