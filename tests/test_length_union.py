"""Geometry cases for hat-uzunluğu overlap removal. Mirrors LengthUnion.SelfTest in C#."""
import math
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import length_union as lu  # noqa: E402


def P(x, y, z=0.0):
    return (x, y, z)


Z = (0.0, 0.0, 1.0)
NZ = (0.0, 0.0, -1.0)


class LengthUnionTests(unittest.TestCase):
    def calc(self, build, tol=0.001):
        u = lu.LengthUnion(tol=tol)
        build(u)
        return u.result()

    def assertNet(self, r, raw, net, places=6):
        self.assertAlmostEqual(r['raw'], raw, places=places)
        self.assertAlmostEqual(r['net'], net, places=places)
        self.assertAlmostEqual(r['overlap'], raw - net, places=places)

    def test_identical_lines_count_once(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(0, 0), P(10, 0))
        self.assertNet(self.calc(b), 20, 10)

    def test_partial_collinear_overlap(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(5, 0), P(15, 0))
        self.assertNet(self.calc(b), 20, 15)

    def test_reversed_duplicate(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(10, 0), P(0, 0))
        self.assertNet(self.calc(b), 20, 10)

    def test_contained_segment(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(2, 0), P(4, 0))
        self.assertNet(self.calc(b), 12, 10)

    def test_polyline_shares_segment_with_line(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(10, 0), P(10, 5))
        self.assertNet(self.calc(b), 25, 15)

    def test_crossing_lines_both_count(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(5, -5), P(5, 5))
        self.assertNet(self.calc(b), 20, 20)

    def test_zero_length_ignored(self):
        def b(u):
            self.assertFalse(u.add_line(P(1, 1, 1), P(1, 1, 1)))
            u.add_line(P(0, 0), P(4, 0))
        self.assertNet(self.calc(b), 4, 4)

    def test_zero_length_only(self):
        def b(u):
            u.add_line(P(0, 0), P(0, 0))
        r = self.calc(b)
        self.assertEqual(r['count'], 0)
        self.assertEqual(r['net'], 0.0)

    def test_gap_is_not_bridged(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(10.05, 0), P(20, 0))
        self.assertNet(self.calc(b), 19.95, 19.95)

    def test_parallel_outside_tolerance_both_count(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(0, 0.01), P(10, 0.01))
        self.assertNet(self.calc(b), 20, 20)

    def test_parallel_inside_tolerance_once(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(0, 0.0005), P(10, 0.0005))
        self.assertNet(self.calc(b), 20, 10)

    def test_chain_of_three(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_line(P(8, 0), P(18, 0))
            u.add_line(P(16, 0), P(20, 0))
        self.assertNet(self.calc(b), 24, 20)

    def test_3d_collinear_partial(self):
        def b(u):
            u.add_line(P(0, 0, 0), P(0, 0, 10))
            u.add_line(P(0, 0, 5), P(0, 0, 15))
        self.assertNet(self.calc(b), 20, 15)

    def test_3d_parallel_offset_not_merged(self):
        def b(u):
            u.add_line(P(0, 0, 0), P(10, 0, 0))
            u.add_line(P(0, 0, 1), P(10, 0, 1))
        self.assertNet(self.calc(b), 20, 20)

    def test_diagonal_reversed_duplicate(self):
        s = math.sqrt(3.0)
        def b(u):
            u.add_line(P(0, 0, 0), P(1, 1, 1))
            u.add_line(P(1, 1, 1), P(0, 0, 0))
        self.assertNet(self.calc(b), 2 * s, s)

    def test_arc_duplicate(self):
        half = 10 * math.pi / 2
        def b(u):
            u.add_arc((0, 0, 0), Z, 10, 0.0, math.pi / 2)
            u.add_arc((0, 0, 0), Z, 10, 0.0, math.pi / 2)
        self.assertNet(self.calc(b), 2 * half, half)

    def test_arc_partial_overlap(self):
        # sweeps π and π starting at π/2, R=2 → union 3π/2
        one = 2 * math.pi
        def b(u):
            u.add_arc((0, 0, 0), Z, 2, 0.0, math.pi)
            u.add_arc((0, 0, 0), Z, 2, math.pi / 2, math.pi)
        self.assertNet(self.calc(b), 2 * one, 2 * 1.5 * math.pi)

    def test_arc_wrap_duplicate(self):
        r = 4.0
        sweep = 0.4
        def b(u):
            u.add_arc((0, 0, 0), Z, r, 2 * math.pi - 0.2, sweep)
            u.add_arc((0, 0, 0), Z, r, 2 * math.pi - 0.2, sweep)
        self.assertNet(self.calc(b), 2 * r * sweep, r * sweep)

    def test_arc_opposite_normal_same_locus(self):
        r = 10.0
        sweep = math.pi / 2
        end = lu.arc_point((0, 0, 0), Z, r, sweep)
        uax, vax = lu.basis(lu.as_v(NZ))
        rel = lu.sub(end, lu.V(0, 0, 0))
        a0 = math.atan2(lu.dot(rel, vax), lu.dot(rel, uax))
        def b(u):
            u.add_arc((0, 0, 0), Z, r, 0.0, sweep)
            u.add_arc((0, 0, 0), NZ, r, a0, sweep)
        self.assertNet(self.calc(b), 2 * r * sweep, r * sweep)

    def test_circle_covers_arc(self):
        circ = 2 * math.pi * 5
        arc = 5 * math.pi
        def b(u):
            u.add_circle((1, 2, 3), Z, 5)
            u.add_arc((1, 2, 3), Z, 5, 0.3, math.pi)
        self.assertNet(self.calc(b), circ + arc, circ)

    def test_two_circles_once(self):
        circ = 2 * math.pi * 3
        def b(u):
            u.add_circle((0, 0, 0), Z, 3)
            u.add_circle((0, 0, 0), NZ, 3)
        self.assertNet(self.calc(b), 2 * circ, circ)

    def test_adjacent_arcs_sum(self):
        r = 5.0
        def b(u):
            u.add_arc((0, 0, 0), Z, r, 0.0, math.pi / 2)
            u.add_arc((0, 0, 0), Z, r, math.pi / 2, math.pi / 2)
        self.assertNet(self.calc(b), r * math.pi, r * math.pi)

    def test_different_radius_both(self):
        a = 2 * math.pi
        b_ = 3 * math.pi / 2
        def b(u):
            u.add_arc((0, 0, 0), Z, 2, 0.0, math.pi)
            u.add_arc((0, 0, 0), Z, 3, 0.0, math.pi / 2)
        self.assertNet(self.calc(b), a + b_, a + b_)

    def test_sampled_exact_when_alone(self):
        def b(u):
            u.add_sampled([P(0, 0), P(5, 0), P(5, 5)], 10.2)
        self.assertNet(self.calc(b), 10.2, 10.2)

    def test_sampled_reversed_duplicate_keeps_exact(self):
        pts = [P(0, 0), P(3, 0), P(3, 4)]
        def b(u):
            u.add_sampled(pts, 10.2)
            u.add_sampled(list(reversed(pts)), 10.2)
        self.assertNet(self.calc(b), 20.4, 10.2)

    def test_sampled_closed_reversed(self):
        a = [P(0, 0), P(1, 0), P(1, 1), P(0, 1)]
        def build(u):
            u.add_sampled(a, 4.5, closed=True)
            u.add_sampled(list(reversed(a)), 4.5, closed=True)
        self.assertNet(self.calc(build), 9.0, 4.5)

    def test_sampled_closed_cyclic_duplicate(self):
        a = [P(0, 0), P(1, 0), P(1, 1), P(0, 1)]
        b = [P(1, 0), P(1, 1), P(0, 1), P(0, 0)]
        def build(u):
            u.add_sampled(a, 4.5, closed=True)
            u.add_sampled(b, 4.5, closed=True)
        self.assertNet(self.calc(build), 9.0, 4.5)

    def test_sampled_partial_overlap(self):
        def b(u):
            u.add_sampled([P(0, 0), P(10, 0), P(10, 10)], 20)
            u.add_sampled([P(10, 0), P(10, 10), P(20, 10)], 20)
        self.assertNet(self.calc(b), 40, 30)

    def test_sampled_overlap_with_line(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0))
            u.add_sampled([P(0, 0), P(10, 0), P(10, 5)], 15)
        self.assertNet(self.calc(b), 25, 15)

    def test_sampled_different_vertex_count_on_same_line(self):
        def b(u):
            u.add_sampled([P(0, 0), P(10, 0)], 10)
            u.add_sampled([P(0, 0), P(5, 0), P(10, 0)], 10)
        self.assertNet(self.calc(b), 20, 10)

    def test_layers_overlap_only_in_overall(self):
        def b(u):
            u.add_line(P(0, 0), P(10, 0), 'A')
            u.add_line(P(0, 0), P(10, 0), 'B')
            u.add_line(P(0, 5), P(4, 5), 'A')
        r = self.calc(b)
        self.assertNet(r, 24, 14)
        layers = {row['name']: row for row in r['layers']}
        self.assertEqual(list(layers), ['A', 'B'])
        self.assertAlmostEqual(layers['A']['net'], 14, places=6)
        self.assertAlmostEqual(layers['A']['raw'], 14, places=6)
        self.assertAlmostEqual(layers['B']['net'], 10, places=6)

    def test_bulge_semicircle_and_quarter(self):
        semi = lu.bulge_arc(P(0, 0), P(2, 0), 1.0)
        self.assertIsNotNone(semi)
        center, normal, radius, sweep = semi
        self.assertAlmostEqual(center.x, 1.0, places=9)
        self.assertAlmostEqual(center.y, 0.0, places=9)
        self.assertAlmostEqual(center.z, 0.0, places=9)
        self.assertAlmostEqual(radius, 1.0, places=9)
        self.assertAlmostEqual(sweep, math.pi, places=9)
        self.assertGreater(normal.z, 0.0)

        b = math.tan(math.pi / 8.0)
        q = lu.bulge_arc(P(1, 0), P(0, 1), b)
        center, normal, radius, sweep = q
        self.assertAlmostEqual(center.x, 0.0, places=9)
        self.assertAlmostEqual(center.y, 0.0, places=9)
        self.assertAlmostEqual(radius, 1.0, places=9)
        self.assertAlmostEqual(sweep, math.pi / 2, places=9)
        self.assertAlmostEqual(lu.dist(center, lu.as_v(P(1, 0))), radius, places=9)
        self.assertAlmostEqual(lu.dist(center, lu.as_v(P(0, 1))), radius, places=9)

        neg = lu.bulge_arc(P(1, 0), P(0, 1), -b)
        center, normal, radius, sweep = neg
        self.assertAlmostEqual(radius, 1.0, places=9)
        self.assertAlmostEqual(sweep, math.pi / 2, places=9)
        self.assertLess(normal.z, 0.0)
        # CW quarter from (1,0) to (0,1) is centered at (0,0) as well? 
        # Negative bulge bows the other way: center is (1,1), radius 1.
        self.assertAlmostEqual(center.x, 1.0, places=9)
        self.assertAlmostEqual(center.y, 1.0, places=9)

    def test_tiny_z_within_tolerance(self):
        def b(u):
            u.add_line(P(0, 0, 0), P(10, 0, 0))
            u.add_line(P(0, 0, 0.0004), P(10, 0, 0.0004))
        self.assertNet(self.calc(b), 20, 10)


if __name__ == '__main__':
    unittest.main()
