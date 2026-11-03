import os
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'service'))
import attrexport  # noqa: E402
import attrimport  # noqa: E402
import metraj  # noqa: E402


def blk(h, name, attrs, x=1.0, y=2.0, consts=()):
    return {'block': name, 'layer': 'ETIKET', 'handle': h, 'x': x, 'y': y, 'space': 'Model',
            'attrs': [[k, v] for k, v in attrs.items()], 'const_tags': list(consts)}


def excel_style(src, dst, edit):
    """Re-save like Excel: shared strings instead of inline strings; edit(sheet_name, rows) mutates rows of ('s'|'n', v)."""
    sheets = attrimport.read_sheets(src)
    out = []
    for name, rows in sheets:
        rows = [list(r) for r in rows]
        edit(name, rows)
        out.append((name, rows))
    shared = []
    def sid(s):
        if s not in shared:
            shared.append(s)
        return shared.index(s)
    with zipfile.ZipFile(src) as z:
        files = dict((n, z.read(n)) for n in z.namelist())
    for i, (name, rows) in enumerate(out):
        x = ['<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>']
        for r, row in enumerate(rows, 1):
            x.append('<row r="%d">' % r)
            for c, (k, v) in enumerate(row):
                ref = '%s%d' % (metraj._col(c), r)
                if v in ('', None):
                    continue
                if k == 'n':
                    x.append('<c r="%s" s="3"><v>%s</v></c>' % (ref, v))
                else:
                    x.append('<c r="%s" t="s"><v>%d</v></c>' % (ref, sid(v)))
            x.append('</row>')
        x.append('</sheetData></worksheet>')
        files['xl/worksheets/sheet%d.xml' % (i + 1)] = ''.join(x).encode('utf-8')
    from xml.sax.saxutils import escape
    files['xl/sharedStrings.xml'] = ('<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
                                     + ''.join('<si><t>%s</t></si>' % escape(s) for s in shared) + '</sst>').encode('utf-8')
    with zipfile.ZipFile(dst, 'w') as z:
        for n, b in files.items():
            z.writestr(n, b)


class TestAttImport(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.blocks = [blk('1A', 'PRIZ', {'NO': 'P1', 'GUC': '1,5', 'ADET': '60', 'KOD': '007'}, 0, 0, consts=['SABIT']),
                       blk('1B', 'PRIZ', {'NO': 'P2', 'GUC': '2', 'ADET': '60.0', 'KOD': '008'}, 10, 0),
                       blk('2C', 'LAMBA', {'NO': 'L1', 'W': '36', 'NOT': 'eski'}, 0, 10)]
        self.blocks[0]['attrs'].append(['SABIT', 'X'])
        self.src = os.path.join(self.d, 'a_attribute_1.xlsx')
        attrexport.export({'blocks': self.blocks, 'export_path': self.src, 'group_by': 'NO'})

    def col(self, rows, name):
        return [attrimport._key(c[1]) for c in rows[0]].index(attrimport._key(name))

    def run_edit(self, edit):
        dst = os.path.join(self.d, 'edited.xlsx')
        excel_style(self.src, dst, edit)
        return attrimport.diff({'path': dst, 'blocks': self.blocks})

    def test_handle_column_label(self):
        rows = attrimport.read_sheets(self.src)[1][1]
        self.assertIn('de\u011fi\u015ftirmeyin', rows[0][2][1])

    def test_untouched_roundtrip_no_changes(self):
        r = self.run_edit(lambda n, rows: None)
        self.assertEqual(r['changes'], [])
        self.assertEqual(r['unchanged_blocks'], 3)
        self.assertEqual(r['not_found'], [])

    def test_edit_in_block_sheet_number_formatting(self):
        def e(n, rows):
            if n == 'PRIZ':
                rows[1][self.col(rows, 'GUC')] = ('n', '2.5')
                rows[2][self.col(rows, 'ADET')] = ('n', '75')
                rows[1][self.col(rows, 'NO')] = ('s', 'P1-YEN\u0130')
        r = self.run_edit(e)
        got = dict(((c['handle'], c['tag']), c['new']) for c in r['changes'])
        self.assertEqual(got, {('1A', 'GUC'): '2,5', ('1B', 'ADET'): '75', ('1A', 'NO'): 'P1-YEN\u0130'})
        self.assertEqual(r['changed_blocks'], 2)
        self.assertEqual(r['unchanged_blocks'], 1)

    def test_edit_in_tumu_only_applied_once(self):
        def e(n, rows):
            if n == 'T\u00fcm\u00fc':
                rows[3][self.col(rows, 'NOT')] = ('s', 'yeni')
        r = self.run_edit(e)
        self.assertEqual([(c['handle'], c['tag'], c['new']) for c in r['changes']], [('2C', 'NOT', 'yeni')])

    def test_conflict_block_sheet_wins(self):
        def e(n, rows):
            if n == 'T\u00fcm\u00fc':
                rows[3][self.col(rows, 'W')] = ('n', '40')
            if n == 'LAMBA':
                rows[1][self.col(rows, 'W')] = ('n', '58')
        r = self.run_edit(e)
        self.assertEqual([(c['tag'], c['new']) for c in r['changes']], [('W', '58')])
        self.assertEqual(len(r['conflicts']), 1)

    def test_cleared_cell_and_float_artifact_and_const(self):
        def e(n, rows):
            if n == 'LAMBA':
                rows[1][self.col(rows, 'NOT')] = ('s', '')
            if n == 'PRIZ':
                rows[1][self.col(rows, 'GUC')] = ('n', '1.5000000000000002')
                rows[1][self.col(rows, 'SABIT')] = ('s', 'Y')
        r = self.run_edit(e)
        self.assertEqual([(c['handle'], c['tag'], c['new']) for c in r['changes']], [('2C', 'NOT', '')])

    def test_totals_and_ozet_ignored_and_not_found_fallback(self):
        def e(n, rows):
            if n == 'LAMBA':
                rows[1][self.col(rows, 'Handle')] = ('s', '')
                rows[1][self.col(rows, 'W')] = ('n', '18')
            if n == 'PRIZ':
                rows[2][self.col(rows, 'Handle')] = ('s', 'FFFF')
                rows[2][self.col(rows, 'X')] = ('n', '999')
        r = self.run_edit(e)
        self.assertEqual([(c['handle'], c['tag'], c['new']) for c in r['changes']], [('2C', 'W', '18')])
        self.assertEqual(len(r['fallback']), 1)
        self.assertEqual(len(r['not_found']), 1)

    def test_fmt_number(self):
        self.assertEqual(attrimport.fmt_number('60', '1'), '60')
        self.assertEqual(attrimport.fmt_number('60.0', ''), '60')
        self.assertEqual(attrimport.fmt_number('1.25', 'abc'), '1,25')
        self.assertEqual(attrimport.fmt_number('1.25', '0.5'), '1.25')
        self.assertEqual(attrimport.fmt_number('0.30000000000000004', ''), '0,3')


if __name__ == '__main__':
    unittest.main()
