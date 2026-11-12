import base64
import io
import os
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'service'))
import metraj as m  # noqa: E402

ITEMS = [
    {'kategori': 'Kablo tavası', 'kalem': 'KABLO_TAVASI (Genişlik=100)', 'birim': 'm', 'miktar': 12.5, 'adet': 3},
    {'kategori': 'Blok', 'kalem': 'PRIZ [TIP=Topraklı]', 'birim': 'adet', 'miktar': 7, 'adet': 7},
    {'kategori': 'Uzunluk', 'kalem': 'Hat uzunluğu', 'katman': 'E-KABLO', 'birim': 'm', 'miktar': 40.0},
]


class MetrajTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()

    def prices(self, text, name='f.csv'):
        p = os.path.join(self.d, name)
        io.open(p, 'w', encoding='utf-8-sig').write(text)
        return p

    def test_num(self):
        self.assertEqual(m.to_num('1.234,50'), 1234.5)
        self.assertEqual(m.to_num('1,234.50'), 1234.5)
        self.assertEqual(m.to_num('12,5'), 12.5)
        self.assertIsNone(m.to_num('abc'))

    def test_csv_and_kesif(self):
        p = self.prices('poz_no;kalem;birim;birim_fiyat\nE.1;Kablo Tavasi (Genislik=100);m;250,5\nE.2;PRIZ;adet;80\n')
        pr = m.load_prices(p)
        rows, missing, total = m.kesif(ITEMS, pr)
        self.assertEqual(rows[0]['poz_no'], 'E.1')
        self.assertEqual(rows[1]['tutar'], 560.0)  # substring match on PRIZ
        self.assertEqual(missing, ['Hat uzunluğu'])
        self.assertAlmostEqual(total, 12.5 * 250.5 + 560, places=2)

    def test_bad_header(self):
        with self.assertRaises(ValueError):
            m.load_prices(self.prices('a,b\n1,2\n'))

    def test_diff(self):
        new = [dict(ITEMS[0], miktar=20.0), ITEMS[2], {'kategori': 'Blok', 'kalem': 'AYDINLATMA', 'birim': 'adet', 'miktar': 4}]
        rows = {r['kalem']: r for r in m.diff(ITEMS, new, [{'kalem': 'AYDINLATMA', 'birim': 'adet', 'birim_fiyat': 10.0, 'poz_no': ''}])}
        self.assertEqual(rows['KABLO_TAVASI (Genişlik=100)']['fark'], 7.5)
        self.assertEqual(rows['PRIZ [TIP=Topraklı]']['durum'], 'kaldırıldı')
        self.assertEqual(rows['AYDINLATMA']['tutar_farki'], 40.0)
        self.assertNotIn('Hat uzunluğu', rows)

    def test_snapshots_and_labels(self):
        m.save_snapshot(self.d, 'R1', ITEMS)
        self.assertEqual(m.list_snapshots(self.d), ['R1'])
        self.assertEqual(len(m.load_snapshot(self.d, 'R1')['items']), 3)
        for bad in ('../x', 'a b', ''):
            with self.assertRaises(ValueError):
                m.save_snapshot(self.d, bad, ITEMS)
        with self.assertRaises(ValueError):
            m.load_snapshot(self.d, 'R9')

    def test_xlsx_roundtrip(self):
        p = m.write_xlsx(os.path.join(self.d, 'x.xlsx'), [('Keşif', [['kalem', 'birim_fiyat'], ['PRIZ & <x>', 5.5]]), ('B', [['a']])])
        with zipfile.ZipFile(p) as z:
            self.assertIn('xl/worksheets/sheet2.xml', z.namelist())
            self.assertIn('Keşif', z.read('xl/workbook.xml').decode('utf-8'))
        self.assertEqual(m.read_xlsx_rows(p)[1], ['PRIZ & <x>', '5.5'])
        self.assertEqual(m.load_prices(p)[0]['birim_fiyat'], 5.5)

    def test_report_full(self):
        pp = os.path.join(self.d, 'fiyat', 'birim_fiyat.csv')
        r = m.report({'items': [dict(i) for i in ITEMS], 'snapshot_dir': self.d, 'prices_path': pp, 'save_revision': 'R1',
                      'export_path': os.path.join(self.d, 'out.xlsx')})
        self.assertTrue(os.path.isfile(pp))  # template created
        self.assertIn('şablon', r['summary'])
        self.assertEqual(r['revisions'], ['R1'])
        self.assertTrue(os.path.isfile(r['xlsx']))
        r2 = m.report({'items': [dict(ITEMS[0], miktar=15.0)], 'snapshot_dir': self.d, 'compare_to': 'R1'})
        self.assertIn('REVİZYON FARKI', r2['summary'])
        self.assertEqual(len(r2['diff']), 3)
        m.save_snapshot(self.d, 'R2', [dict(ITEMS[0], miktar=15.0)])
        r3 = m.report({'items': [], 'snapshot_dir': self.d, 'compare_revisions': ['R1', 'R2']})
        self.assertIn('R1 → R2', r3['summary'])


PNG = base64.b64decode(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
NS = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
DRAW = '{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}'
REL = '{http://schemas.openxmlformats.org/officeDocument/2006/relationships}'


def _local(tag):
    return tag.rsplit('}', 1)[-1]


class XlsxImageTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()

    def test_classic_sheet_has_no_drawing(self):
        p = m.write_xlsx(os.path.join(self.d, 'plain.xlsx'), [('S', [['a'], ['b']])])
        with zipfile.ZipFile(p) as z:
            names = z.namelist()
            xml = z.read('xl/worksheets/sheet1.xml').decode('utf-8')
            ct = z.read('[Content_Types].xml').decode('utf-8')
        self.assertNotIn('drawing', xml)
        self.assertNotIn('xmlns:r', xml)
        self.assertNotIn('png', ct)
        self.assertFalse(any(n.startswith('xl/media/') or n.startswith('xl/drawings/') for n in names))
        m.validate_package(p)

    def test_picture_package_reuses_media_and_keeps_schema_order(self):
        rows = [
            ['Kategori', 'Kalem', 'Resim', 'Birim'],
            ['Blok', 'PRIZ', m.XlsxImage('a'), 'adet'],
            ['Blok', 'PRIZ', m.XlsxImage('a'), 'adet'],
            ['Uzunluk', 'Hat', '', 'm'],
        ]
        other = [
            ['Poz No', 'Kalem', 'Resim'],
            ['', 'PRIZ', m.XlsxImage('a')],
            [],
            ['', 'TOPLAM', ''],
        ]
        p = m.write_xlsx(os.path.join(self.d, 'img.xlsx'), [('Metraj', rows), ('Keşif', other), ('Not', [['yok']])],
                         {'a': PNG, 'bogus': 'not-a-png'})
        m.validate_package(p)
        with zipfile.ZipFile(p) as z:
            names = z.namelist()
            self.assertEqual([n for n in names if n.startswith('xl/media/')], ['xl/media/image1.png'])
            self.assertEqual(z.read('xl/media/image1.png'), PNG)
            self.assertIn('xl/drawings/drawing1.xml', names)
            self.assertIn('xl/drawings/drawing2.xml', names)
            self.assertNotIn('xl/drawings/drawing3.xml', names)
            self.assertIn('xl/drawings/_rels/drawing1.xml.rels', names)
            self.assertIn('xl/worksheets/_rels/sheet1.xml.rels', names)
            self.assertNotIn('xl/worksheets/_rels/sheet3.xml.rels', names)
            ct = z.read('[Content_Types].xml').decode('utf-8')
            self.assertIn('Extension="png"', ct)
            self.assertIn('/xl/drawings/drawing1.xml', ct)
            self.assertLess(ct.find('Extension="png"'), ct.find('PartName="/xl/workbook.xml"'))
            sheet = ET.fromstring(z.read('xl/worksheets/sheet1.xml'))
            kids = [_local(c.tag) for c in list(sheet)]
            self.assertEqual(kids, ['cols', 'sheetData', 'drawing'])
            self.assertLess(m._WS_CHILD_ORDER.index('sheetData'), m._WS_CHILD_ORDER.index('drawing'))
            self.assertEqual(sheet.find(NS + 'drawing').get(REL + 'id'), 'rId1')
            rows_xml = list(sheet.find(NS + 'sheetData'))
            self.assertEqual(rows_xml[1].get('ht'), '75')
            self.assertEqual(rows_xml[1].get('customHeight'), '1')
            self.assertIsNone(rows_xml[3].get('ht'))  # length row, no picture
            self.assertIsNone(rows_xml[0].get('ht'))
            widths = [(c.get('min'), c.get('max'), c.get('width')) for c in sheet.find(NS + 'cols')]
            self.assertIn(('3', '3', '18'), widths)
            drawing = ET.fromstring(z.read('xl/drawings/drawing1.xml'))
            anchors = list(drawing)
            self.assertEqual(len(anchors), 2)
            embeds = [a.find('.//' + '{http://schemas.openxmlformats.org/drawingml/2006/main}blip').get(REL + 'embed') for a in anchors]
            self.assertEqual(embeds, ['rId1', 'rId1'])
            rels = z.read('xl/drawings/_rels/drawing1.xml.rels').decode('utf-8')
            self.assertEqual(rels.count('image1.png'), 1)
            self.assertIn('../media/image1.png', rels)
            # second sheet points at the same media part
            self.assertIn('../media/image1.png', z.read('xl/drawings/_rels/drawing2.xml.rels').decode('utf-8'))
            kesif = ET.fromstring(z.read('xl/worksheets/sheet2.xml'))
            krows = list(kesif.find(NS + 'sheetData'))
            self.assertEqual(krows[1].get('ht'), '75')
            self.assertIsNone(krows[3].get('ht'))

    def test_two_keys_two_media_parts(self):
        rows = [['Ad', 'Resim'], ['A', m.XlsxImage('a')], ['B', m.XlsxImage('b')]]
        p = m.write_xlsx(os.path.join(self.d, 'two.xlsx'), [('S', rows)], {'a': base64.b64encode(PNG).decode('ascii'), 'b': PNG})
        with zipfile.ZipFile(p) as z:
            self.assertEqual(sorted(n for n in z.namelist() if n.startswith('xl/media/')),
                             ['xl/media/image1.png', 'xl/media/image2.png'])
            rels = z.read('xl/drawings/_rels/drawing1.xml.rels').decode('utf-8')
            self.assertIn('rId1', rels)
            self.assertIn('rId2', rels)

    def test_report_adds_resim_only_for_block_items(self):
        items = [
            {'kategori': 'Blok', 'kalem': 'PRIZ', 'birim': 'adet', 'miktar': 2, 'adet': 2, 'resim': 'a'},
            {'kategori': 'Uzunluk', 'kalem': 'Hat uzunluğu', 'katman': 'E', 'birim': 'm', 'miktar': 4, 'adet': 1},
        ]
        r = m.report({'items': [dict(i) for i in items], 'snapshot_dir': self.d, 'prices_path': os.path.join(self.d, 'yok.csv'),
                      'export_path': os.path.join(self.d, 'out.xlsx'), 'images': {'a': PNG, 'x': '!!!'}})
        self.assertIn('Blok resimleri: 1 önizleme', r['summary'])
        rows = m.read_xlsx_rows(r['xlsx'])
        self.assertEqual(rows[0], ['Kategori', 'Kalem', 'Resim', 'Katman', 'Birim', 'Miktar', 'Adet'])
        self.assertEqual(rows[1][1], 'PRIZ')
        self.assertEqual(rows[2][1], 'Hat uzunluğu')
        with zipfile.ZipFile(r['xlsx']) as z:
            book = z.read('xl/workbook.xml').decode('utf-8')
            self.assertIn('Keşif', book)
            kesif = ET.fromstring(z.read('xl/worksheets/sheet2.xml'))
            head = ''.join(t.text or '' for t in kesif.iter(NS + 't'))
            self.assertIn('Resim', head)
            rev = z.read('xl/worksheets/sheet3.xml').decode('utf-8')
            self.assertNotIn('drawing', rev)
            self.assertNotIn('Resim', rev)

    def test_report_without_images_is_unchanged(self):
        r = m.report({'items': [dict(i) for i in ITEMS], 'export_path': os.path.join(self.d, 'out.xlsx'), 'snapshot_dir': self.d})
        self.assertNotIn('Resim', m.read_xlsx_rows(r['xlsx'])[0])
        self.assertNotIn('önizleme', r['summary'])


if __name__ == '__main__':
    unittest.main()
class TestTrayKesifNames(__import__('unittest').TestCase):
    def test_tray_and_fitting_names_match_prices(self):
        import metraj
        prices = [{'kalem': 'Kablo tavası 300x40 t=2', 'birim': 'm', 'birim_fiyat': 10.0, 'poz_no': 'KT.200'},
                  {'kalem': 'Yarım T 100', 'birim': 'adet', 'birim_fiyat': 5.0, 'poz_no': 'Y1'},
                  {'kalem': 'Yatay T (YT) 200', 'birim': 'adet', 'birim_fiyat': 7.0, 'poz_no': 'T2'}]
        self.assertEqual(metraj.match_price({'kalem': 'Kablo tavası 300x40 t=2', 'birim': 'm'}, prices)['poz_no'], 'KT.200')
        self.assertEqual(metraj.match_price({'kalem': 'Yarım T 100', 'birim': 'adet'}, prices)['poz_no'], 'Y1')
        self.assertIsNone(metraj.match_price({'kalem': 'Kablo tavası 100x40 t=2', 'birim': 'm'}, prices))
        self.assertIsNone(metraj.match_price({'kalem': 'Yatay T (YT) 200x100', 'birim': 'adet'}, prices))
