import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'service'))
from actions import validate_actions, PlanError


def ctx():
    return {
        'selected': [{'handle': 'A1'}, {'handle': '2b'}],
        'layers': [{'name': '0'}, {'name': 'Duvar'}],
        'blocks': ['Priz'],
    }


class ActionTests(unittest.TestCase):
    def ok(self, a):
        return validate_actions([a], ctx())[0]

    def bad(self, a):
        with self.assertRaises(PlanError):
            validate_actions([a], ctx())

    def test_move_normalizes(self):
        n = self.ok({'type': 'move', 'handles': ['a1', '0x2B'], 'dx': 5})
        self.assertEqual(n['handles'], ['A1', '2B'])
        self.assertEqual((n['dx'], n['dy'], n['dz']), (5.0, 0.0, 0.0))

    def test_unselected_handle_rejected(self):
        self.bad({'type': 'move', 'handles': ['FF'], 'dx': 1})

    def test_zero_move_rejected(self):
        self.bad({'type': 'move', 'handles': ['A1']})

    def test_delete_rejected(self):
        self.bad({'type': 'erase', 'handles': ['FF']})
        self.ok({'type': 'erase', 'handles': ['A1']})

    def test_layer_must_exist_or_be_created(self):
        self.bad({'type': 'circle', 'center': [0, 0], 'radius': 1, 'layer': 'Yok'})
        r = validate_actions([
            {'type': 'create_layer', 'name': 'Yeni', 'color_index': 3},
            {'type': 'circle', 'center': [0, 0], 'radius': 1, 'layer': 'yeni'}], ctx())
        self.assertEqual(len(r), 2)

    def test_bad_layer_name(self):
        self.bad({'type': 'create_layer', 'name': 'a<b'})

    def test_nan_and_bool_rejected(self):
        self.bad({'type': 'circle', 'center': [0, 0], 'radius': float('nan')})
        self.bad({'type': 'circle', 'center': [True, 0], 'radius': 1})

    def test_polyline_rectangle_text_block(self):
        self.ok({'type': 'polyline', 'points': [[0, 0], [1, 1]], 'closed': True})
        self.bad({'type': 'polyline', 'points': [[0, 0]]})
        self.ok({'type': 'rectangle', 'corner1': [0, 0], 'corner2': [2, 3]})
        self.bad({'type': 'rectangle', 'corner1': [0, 0], 'corner2': [0, 3]})
        self.ok({'type': 'text', 'position': [0, 0], 'text': 'Merhaba', 'height': 2.5})
        self.ok({'type': 'insert_block', 'name': 'priz', 'position': [1, 1]})
        self.bad({'type': 'insert_block', 'name': 'Bilinmeyen', 'position': [1, 1]})

    def test_scale_range(self):
        self.bad({'type': 'scale', 'handles': ['A1'], 'factor': 0, 'base': [0, 0]})
        self.ok({'type': 'scale', 'handles': ['A1'], 'factor': 2, 'base': [0, 0]})

    def test_command_action(self):
        n = self.ok({'type': 'command', 'command': '_.-OVERKILL', 'handles': ['A1'], 'args': ['$SEL', '', '']})
        self.assertEqual(n['handles'], ['A1'])
        self.ok({'type': 'command', 'command': '_.LINE', 'args': ['0,0', '100,0', '']})
        self.ok({'type': 'command', 'command': "'_.ZOOM", 'args': ['_E']})
        for bad in ('_.QUIT', 'SAVEAS', '_.NETLOAD', 'shell', '(command "erase")', '_.-VBARUN', 'AIDAPPLY'):
            self.bad({'type': 'command', 'command': bad})
        self.bad({'type': 'command', 'command': '_.LINE', 'args': ['(getvar "x")']})
        self.bad({'type': 'command', 'command': '_.LINE', 'args': ['!a']})
        self.bad({'type': 'command', 'command': '_.SETVAR', 'args': ['SECURELOAD', '0']})
        self.bad({'type': 'command', 'command': '_.ERASE', 'args': ['$SEL', '']})  # $SEL without handles
        self.bad({'type': 'command', 'command': '_.ERASE', 'handles': ['FF']})

    def test_erase_duplicates_without_selection(self):
        c = ctx()
        c['selected'] = []
        c['drawing_summary'] = {'duplicate_blocks': [{'block': 'blok', 'keep_handle': '1A', 'extra_handles': ['1B', '1C']}]}
        r = validate_actions([{'type': 'erase', 'handles': ['1b', '1C']}], c)
        self.assertEqual(r[0]['handles'], ['1B', '1C'])
        with self.assertRaises(PlanError):
            validate_actions([{'type': 'erase', 'handles': ['1A']}], c)
        with self.assertRaises(PlanError):
            validate_actions([{'type': 'move', 'handles': ['1B'], 'dx': 1}], c)

    def test_takeoff(self):
        n = self.ok({'type': 'takeoff', 'scope': 'layer', 'layers': ['E-KABLO'], 'compare_revisions': ['R1', 'R2']})
        self.assertTrue(n['export'])
        self.ok({'type': 'takeoff', 'scope': 'selection', 'save_revision': 'R3', 'compare_dwg': 'C:\\x\\eski.dwg'})
        self.bad({'type': 'takeoff', 'scope': 'layer'})
        self.bad({'type': 'takeoff', 'save_revision': '../x'})
        self.bad({'type': 'takeoff', 'compare_dwg': 'C:\\x\\a.exe'})
        self.bad({'type': 'takeoff', 'scope': 'other'})

    def test_place_block_along(self):
        n = self.ok({'type': 'place_block_along', 'name': 'priz', 'handles': ['a1'], 'width': 100, 'length_param': 'Uzunluk'})
        self.assertEqual((n['alignment'], n['handles'], n['width']), ('start', ['A1'], 100.0))
        self.ok({'type': 'place_block_along', 'name': 'Priz', 'handles': ['A1'], 'alignment': 'center', 'keep_source': False, 'layer': 'Duvar'})
        self.bad({'type': 'place_block_along', 'name': 'Yok', 'handles': ['A1']})
        self.bad({'type': 'place_block_along', 'name': 'Priz', 'handles': ['FF']})
        self.bad({'type': 'place_block_along', 'name': 'Priz', 'handles': ['A1'], 'alignment': 'end'})
        self.bad({'type': 'place_block_along', 'name': 'Priz', 'handles': ['A1'], 'width': 0})

    def test_too_many(self):
        a = {'type': 'line', 'start': [0, 0], 'end': [1, 1]}
        with self.assertRaises(PlanError):
            validate_actions([a] * 31, ctx())


if __name__ == '__main__':
    unittest.main()


class TargetFittingTests(unittest.TestCase):
    def v(self, a):
        return validate_actions([a], ctx())[0]

    def test_set_layer_by_block_target(self):
        n = self.v({'type': 'set_layer', 'target': {'block': 'priz'}, 'layer': 'Duvar'})
        self.assertEqual(n['target'], {'block': 'priz'})
        self.assertEqual(n['handles'], [])

    def test_target_unknown_block_rejected(self):
        with self.assertRaises(PlanError):
            self.v({'type': 'erase', 'target': {'block': 'Yok'}})

    def test_target_empty_rejected(self):
        with self.assertRaises(PlanError):
            self.v({'type': 'move', 'target': {}, 'dx': 1})

    def test_target_not_for_command(self):
        with self.assertRaises(PlanError):
            self.v({'type': 'command', 'command': '_.ERASE', 'args': ['$SEL', ''], 'target': {'all': True}})

    def test_place_fittings_all(self):
        n = self.v({'type': 'place_fittings'})
        self.assertNotIn('handles', n)

    def test_place_fittings_handles_checked(self):
        self.assertEqual(self.v({'type': 'place_fittings', 'handles': ['a1'], 'trim': False}), {'type': 'place_fittings', 'handles': ['A1'], 'trim': False})
        with self.assertRaises(PlanError):
            self.v({'type': 'place_fittings', 'handles': ['FF']})


class TestBigSelection(unittest.TestCase):
    def test_compact_rows_and_many_handles(self):
        rows = [['%X' % (0x1000 + i), 'BlockReference', 'L', 'cabletray', 0, 0, 0, 0, 0, 0, 1, 1, None] for i in range(1522)]
        c = {'layers': [{'name': '0'}], 'blocks': [], 'selected': [], 'selected_compact': {'columns': [], 'rows': rows}}
        hs = [r[0] for r in rows]
        n = validate_actions([{'type': 'move', 'handles': hs, 'dx': 1}], c)[0]
        self.assertEqual(len(n['handles']), 1522)
        with self.assertRaises(PlanError):
            validate_actions([{'type': 'move', 'handles': ['FFFFF'], 'dx': 1}], c)


class TestPlanCopy(unittest.TestCase):
    def test_plan_copy_accepted(self):
        c = {'layers': [{'name': '0'}], 'blocks': [], 'selected': []}
        self.assertEqual(validate_actions([{'type': 'plan_copy'}], c)[0], {'type': 'plan_copy', 'mode': 'copy'})
        self.assertEqual(validate_actions([{'type': 'plan_copy', 'mode': 'move'}], c)[0]['mode'], 'move')

import unittest as _ut
import metraj as _m

class TestTrayLabels(_ut.TestCase):
    def test_comma_thickness_matches_price(self):
        prices = [{'poz_no': 'P1', 'kalem': 'Kablo tavas\u0131 5x60 t=1.5', 'birim': 'm', 'birim_fiyat': 100.0},
                  {'poz_no': 'P2', 'kalem': 'Kablo tavas\u0131 5x60 t=2', 'birim': 'm', 'birim_fiyat': 120.0}]
        p = _m.match_price({'kalem': 'Kablo tavas\u0131 5x60 t=1,5'}, prices)
        self.assertEqual(p['poz_no'], 'P1')
        p = _m.match_price({'kalem': 'Kablo tavas\u0131 5x60 t=2'}, prices)
        self.assertEqual(p['poz_no'], 'P2')

    def test_small_en_not_confused(self):
        prices = [{'poz_no': 'A', 'kalem': 'Kablo tavas\u0131 50x60 t=1,5', 'birim': 'm', 'birim_fiyat': 1.0},
                  {'poz_no': 'B', 'kalem': 'Kablo tavas\u0131 5x60 t=1,5', 'birim': 'm', 'birim_fiyat': 2.0}]
        self.assertEqual(_m.match_price({'kalem': 'Kablo tavas\u0131 5x60 t=1,5'}, prices)['poz_no'], 'B')
        self.assertEqual(_m.match_price({'kalem': 'Kablo tavas\u0131 50x60 t=1,5'}, prices)['poz_no'], 'A')
        self.assertIsNone(_m.match_price({'kalem': 'Kablo tavas\u0131 10x60 t=1,5'}, prices))

    def test_separate_lines_kept(self):
        items = [{'kategori': 'Kablo tavas\u0131', 'kalem': 'Kablo tavas\u0131 5x60 t=1,5', 'birim': 'm', 'miktar': 3.5},
                 {'kategori': 'Kablo tavas\u0131', 'kalem': 'Kablo tavas\u0131 5x? t=?', 'birim': 'm', 'miktar': 1.0}]
        rows, missing, total = _m.kesif(items, [])
        self.assertEqual(len(rows), 2)
