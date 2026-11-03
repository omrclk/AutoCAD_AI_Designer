using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CadAiDesigner
{
    /// <summary>Small helpers for reading the loosely-typed JSON produced by JavaScriptSerializer.</summary>
    internal static class J
    {
        static readonly Regex HexRx = new Regex("^[0-9A-F]{1,16}$");

        public static object Get(IDictionary<string, object> a, string key)
        {
            object v;
            return a != null && a.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(IDictionary<string, object> a, string key)
        {
            object v = Get(a, key);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(object o, string what)
        {
            if (o == null) throw new InvalidOperationException(what + " eksik.");
            if (o is bool) throw new InvalidOperationException(what + " sayı olmalı.");
            double d;
            try { d = Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch (System.Exception) { throw new InvalidOperationException(what + " sayı olmalı."); }
            if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e8)
                throw new InvalidOperationException(what + " geçersiz.");
            return d;
        }

        public static double NumOr(IDictionary<string, object> a, string key, double def)
        {
            object v = Get(a, key);
            return v == null ? def : Num(v, key);
        }

        public static bool Bool(IDictionary<string, object> a, string key)
        {
            object v = Get(a, key);
            return v is bool && (bool)v;
        }

        /// <summary>Returns a list for any JSON array representation (ArrayList, object[], List&lt;object&gt;).</summary>
        public static List<object> Arr(object o)
        {
            if (o == null || o is string || o is IDictionary) return null;
            IEnumerable e = o as IEnumerable;
            return e == null ? null : e.Cast<object>().ToList();
        }

        public static double[] Pt(object o, string what)
        {
            List<object> a = Arr(o);
            if (a == null || (a.Count != 2 && a.Count != 3))
                throw new InvalidOperationException(what + " [x,y] veya [x,y,z] olmalı.");
            double[] p = new double[3];
            for (int i = 0; i < a.Count; i++) p[i] = Num(a[i], what);
            return p;
        }

        public static string NormalizeHandle(string h)
        {
            string s = (h ?? "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            s = s.ToUpperInvariant();
            if (!HexRx.IsMatch(s)) throw new InvalidOperationException("Geçersiz handle: " + h);
            return s;
        }

        /// <summary>Reads "handles"; if <paramref name="allowed"/> is given every handle must be in it.</summary>
        public static List<string> Handles(IDictionary<string, object> a, HashSet<string> allowed)
        {
            List<object> arr = Arr(Get(a, "handles"));
            if (arr == null || arr.Count == 0 || arr.Count > 1000000)
                throw new InvalidOperationException("handles 1-1000000 elemanlı liste olmalı.");
            List<string> res = new List<string>();
            foreach (object o in arr)
            {
                string h = NormalizeHandle(Convert.ToString(o, CultureInfo.InvariantCulture));
                if (allowed != null && !allowed.Contains(h))
                    throw new InvalidOperationException("Seçili olmayan nesne: " + h);
                res.Add(h);
            }
            return res;
        }
    }

    internal class LayerInfo
    {
        public string Name;
        public bool Frozen, Locked, Off;
    }

    /// <summary>Snapshot of the drawing sent to the planner and used to validate its answer.</summary>
    internal class DrawingContext
    {
        public string DrawingName = "", Units = "", Space = "Model", CurrentLayer = "0";
        public List<LayerInfo> Layers = new List<LayerInfo>();
        public List<string> Blocks = new List<string>();
        public List<Dictionary<string, object>> Selected = new List<Dictionary<string, object>>();
        public HashSet<string> HandleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Extra copies of overlapping block references (same name + insertion point); may be erased without selection.</summary>
        public HashSet<string> DuplicateHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Handles that erase/command actions may target.</summary>
        public HashSet<string> EraseSet { get { HashSet<string> s = new HashSet<string>(HandleSet, StringComparer.OrdinalIgnoreCase); s.UnionWith(DuplicateHandles); return s; } }
        public int SelectionTotal;
        /// <summary>Compact table of the selection (columns + rows) used instead of Selected for big selections.</summary>
        public Dictionary<string, object> SelectedTable;
        public int SelectionSent;
        /// <summary>Extra top-level context keys (reference points, ...).</summary>
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        public Dictionary<string, object> Summary = new Dictionary<string, object>();
        public List<Dictionary<string, object>> BlockDefinitions = new List<Dictionary<string, object>>();
        public List<string> Revisions = new List<string>();
        public string Fingerprint = "";

        public Dictionary<string, object> ToJson()
        {
            List<object> layers = new List<object>();
            foreach (LayerInfo l in Layers.Take(300))
                layers.Add(new Dictionary<string, object> { { "name", l.Name }, { "frozen", l.Frozen }, { "locked", l.Locked }, { "off", l.Off } });
            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "drawing", System.IO.Path.GetFileName(DrawingName) },
                { "units", Units },
                { "space", Space },
                { "coordinate_system", "WCS" },
                { "current_layer", CurrentLayer },
                { "layers", layers },
                { "blocks", Blocks.Take(300).ToList() },
                { "drawing_summary", Summary },
                { "block_definitions", BlockDefinitions },
                { "revisions", Revisions },
                { "selection_total", SelectionTotal },
                { "selection_sent", SelectionSent },
                { "selection_truncated", SelectionSent < SelectionTotal ? (object)("only " + SelectionSent + " of " + SelectionTotal + " selected objects are listed (max_objects setting)") : false },
                { "selected", Selected },
                { "selected_compact", SelectedTable }
            };
            foreach (KeyValuePair<string, object> kv in Extra) res[kv.Key] = kv.Value;
            return res;
        }
    }
}
