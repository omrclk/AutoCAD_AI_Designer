import base64
import os
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'service'))
import attrexport  # noqa: E402
import actions  # noqa: E402


def B(name, h, **attrs):
    return {'block': name, 'layer': 'ETIKET', 'handle': h, 'x': 1.0, 'y': 2.0, 'space': 'Model', 'attrs': [[k, v] for k, v in attrs.items()]}


class TestAttrExport(unittest.TestCase):
    def test_num(self):
        self.assertEqual(attrexport.num('12,5'), 12.5)
        self.assertEqual(attrexport.num('12.5'), 12.5)
        self.assertEqual(attrexport.num('1.234,5'), 1234.5)
        self.assertIsNone(attrexport.num('3x2,5'))
        self.assertIsNone(attrexport.num('NYY'))

    def test_export(self):
        blocks = [B('etiket', '1A', PANO='P1', KABLO='NYY 3x2,5', **{'LINYE NO': 'L1', 'KABLO METRAJI': '12,5'}),
                  B('etiket', '1B', PANO='P1', KABLO='NYY 3x2,5', **{'LINYE NO': 'L2', 'KABLO METRAJI': '7.5'}),
                  B('etiket', '1C', PANO='P2', KABLO='NYY 5x6', **{'LINYE NO': 'L3', 'KABLO METRAJI': '20'}),
                  B('baska', '2A', NOT='x')]
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, 'a_attribute.xlsx')
            r = attrexport.export({'blocks': blocks, 'tag_order': {'etiket': ['PANO', 'KABLO', 'LINYE NO', 'KABLO METRAJI']},
                                   'export_path': p, 'group_by': 'kablo'})
            self.assertEqual(r['count'], 4)
            self.assertEqual(r['numeric'], ['KABLO METRAJI'])
            with zipfile.ZipFile(p) as z:
                wb = z.read('xl/workbook.xml').decode('utf-8')
                self.assertIn('Özet', wb); self.assertIn('Tümü', wb); self.assertIn('etiket', wb); self.assertIn('baska', wb)
                tum = z.read('xl/worksheets/sheet2.xml').decode('utf-8')
                self.assertIn('<v>40.0</v>', tum)  # total of KABLO METRAJI
                ozet = z.read('xl/worksheets/sheet1.xml').decode('utf-8')
                self.assertIn('NYY 3x2,5', ozet); self.assertIn('<v>20.0</v>', ozet)

    def test_rows_order_and_numbers(self):
        rows, num = attrexport._rows([B('e', '1', A='5', B='x')], ['A', 'B'])
        self.assertEqual(rows[0], ['Blok', 'Tip', 'Katman', attrexport.HANDLE_COL, 'X', 'Y', 'A', 'B'])
        self.assertEqual(rows[1][1], 'BLOK')
        self.assertEqual(rows[1][6], 5)
        self.assertEqual(rows[-1][6], 5)

    def test_mleader_type_column(self):
        b = B('KABLO', 'AB', **{'TIP': 'N2XH', 'KESIT': '4x50 mm2'})
        b['type'] = 'MLEADER'
        plain = B('KABLO', 'AC', **{'TIP': 'NYY', 'KESIT': '3x2,5'})
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, 'a_attribute.xlsx')
            attrexport.export({'blocks': [b, plain], 'tag_order': {'KABLO': ['TIP', 'KESIT']}, 'export_path': p})
            sheets = __import__('attrimport').read_sheets(p)
            tum = [rows for name, rows in sheets if name == 'Tümü'][0]
            head = [c[1] for c in tum[0]]
            self.assertEqual(head[1], 'Tip')
            ti, tip = head.index('Tip'), head.index('TIP')
            self.assertNotEqual(ti, tip)
            self.assertEqual(tum[1][ti][1], 'MLEADER')
            self.assertEqual(tum[1][tip][1], 'N2XH')
            self.assertEqual(tum[2][ti][1], 'BLOK')

    def test_resim_column_reused_across_sheets(self):
        png = base64.b64decode(
            'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
        blocks = [B('etiket', '1A', PANO='P1', ADET='2'), B('etiket', '1B', PANO='P2', ADET='3'), B('baska', '2A', NOT='x')]
        blocks[0]['resim'] = blocks[1]['resim'] = 'same'
        blocks[2]['resim'] = 'other'
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, 'a_attribute.xlsx')
            r = attrexport.export({'blocks': blocks, 'export_path': p, 'images': {'same': png, 'other': png, 'nope': 'xx'}})
            self.assertIn('Blok resimleri: 2 önizleme', r['summary'])
            sheets = __import__('attrimport').read_sheets(p)
            tum = [rows for name, rows in sheets if name == 'Tümü'][0]
            head = [c[1] for c in tum[0]]
            self.assertEqual(head[:3], ['Blok', 'Resim', 'Tip'])
            self.assertEqual(tum[1][0][1], 'etiket')
            self.assertEqual(tum[-1][0][1], 'TOPLAM')
            with zipfile.ZipFile(p) as z:
                media = [n for n in z.namelist() if n.startswith('xl/media/')]
                self.assertEqual(len(media), 2)
                # Tümü + two block sheets; Özet is absent
                drawings = [n for n in z.namelist() if n.startswith('xl/drawings/drawing')]
                self.assertEqual(len(drawings), 3)
            __import__('metraj').validate_package(p)
            diff = __import__('attrimport').diff({'path': p, 'blocks': blocks})
            self.assertEqual(diff['changes'], [])

    def test_no_images_keeps_columns(self):
        rows, _ = attrexport._rows([B('e', '1', A='5')], ['A'])
        self.assertNotIn('Resim', rows[0])

    def test_resim_header_is_not_an_attribute(self):
        import attrimport
        self.assertTrue(attrimport._meta('Resim'))
        self.assertTrue(attrimport._meta('Tip'))
        self.assertFalse(attrimport._meta('RESIM'))
        self.assertFalse(attrimport._meta('TIP'))

    def test_no_blocks(self):
        with self.assertRaises(ValueError):
            attrexport.export({'blocks': [], 'export_path': 'x.xlsx'})

    def test_action_validation(self):
        out = actions.validate_actions([{'type': 'export_attributes', 'scope': 'block', 'block': 'etiket', 'group_by': 'KABLO'}], {})
        self.assertEqual(out[0]['block'], 'etiket')
        with self.assertRaises(Exception):
            actions.validate_actions([{'type': 'export_attributes', 'scope': 'block'}], {})


if __name__ == '__main__':
    unittest.main()

import os as _os, sys as _sys
_sys.path.insert(0, _os.path.join(_os.path.dirname(__file__), '..', 'service'))
import actions as _actions
import unittest as _ut


class TestPlanTakeoffAction(_ut.TestCase):
    def test_plan_takeoff_allowed(self):
        out = _actions.validate_actions([{'type': 'plan_takeoff'}], {})
        self.assertEqual(out[0]['type'], 'plan_takeoff')

    def test_prompt_mentions_plan_takeoff(self):
        src = open(_os.path.join(_os.path.dirname(__file__), '..', 'service', 'server.py'), encoding='utf-8-sig').read()
        self.assertIn('"plan_takeoff"', src)
