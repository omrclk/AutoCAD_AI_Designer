import io
import os
import sys
import tempfile
import unittest
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
        sheet = zipfile.ZipFile(p).read('xl/worksheets/sheet1.xml').decode('utf-8')
        self.assertIn('s="1"', sheet)

    def test_xlsx_newline_stays_one_cell(self):
        p = m.write_xlsx(os.path.join(self.d, 'nl.xlsx'), [('Metin', [['\u00fcst\nalt'], ['tek']])])
        with zipfile.ZipFile(p) as z:
            xml = z.read('xl/worksheets/sheet1.xml').decode('utf-8')
        self.assertIn('&#10;', xml)
        self.assertIn('wrapText="1"', zipfile.ZipFile(p).read('xl/styles.xml').decode('utf-8'))
        self.assertEqual(m.read_xlsx_rows(p), [['\u00fcst\nalt'], ['tek']])

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
