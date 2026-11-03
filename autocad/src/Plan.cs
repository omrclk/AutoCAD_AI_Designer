using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Geometry;

namespace CadAiDesigner
{
    /// <summary>Authoritative allow-list validation of an AI plan. Nothing outside this list can ever run.</summary>
    internal static class PlanValidator
    {
        public const int MaxActions = 30;
        static readonly Regex BadLayerChars = new Regex(@"[<>/\\"":;?*|=`]");

        public static void Validate(IList<Dictionary<string, object>> actions, DrawingContext ctx)
        {
            if (actions.Count > MaxActions)
                throw new InvalidOperationException("En fazla " + MaxActions + " işlem destekleniyor.");
            HashSet<string> layers = new HashSet<string>(ctx.Layers.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
            HashSet<string> blocks = new HashSet<string>(ctx.Blocks, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < actions.Count; i++)
            {
                try { ValidateOne(actions[i], ctx, layers, blocks); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException("İşlem " + (i + 1) + ": " + ex.Message); }
            }
        }

        static void CheckLayer(Dictionary<string, object> a, DrawingContext ctx, HashSet<string> layers, bool required)
        {
            string l = J.Str(a, "layer");
            if (l == null)
            {
                if (required) throw new InvalidOperationException("layer gerekli.");
                return;
            }
            if (!layers.Contains(l)) throw new InvalidOperationException("Katman bulunamadı: " + l);
            LayerInfo info = ctx.Layers.FirstOrDefault(x => string.Equals(x.Name, l, StringComparison.OrdinalIgnoreCase));
            if (info != null && info.Frozen) throw new InvalidOperationException("Katman dondurulmuş: " + l);
        }

        static void ValidateOne(Dictionary<string, object> a, DrawingContext ctx, HashSet<string> layers, HashSet<string> blocks)
        {
            string type = J.Str(a, "type");
            switch (type)
            {
                case "move":
                case "copy":
                    {
                        J.Handles(a, ctx.HandleSet);
                        double dx = J.NumOr(a, "dx", 0), dy = J.NumOr(a, "dy", 0), dz = J.NumOr(a, "dz", 0);
                        if (dx == 0 && dy == 0 && dz == 0) throw new InvalidOperationException("Öteleme sıfır.");
                        break;
                    }
                case "rotate":
                    J.Handles(a, ctx.HandleSet);
                    J.Num(J.Get(a, "angle_deg"), "angle_deg");
                    J.Pt(J.Get(a, "center"), "center");
                    break;
                case "scale":
                    {
                        J.Handles(a, ctx.HandleSet);
                        double f = J.Num(J.Get(a, "factor"), "factor");
                        if (f < 0.001 || f > 1000) throw new InvalidOperationException("Ölçek 0.001-1000 arasında olmalı.");
                        J.Pt(J.Get(a, "base"), "base");
                        break;
                    }
                case "set_layer":
                    J.Handles(a, ctx.HandleSet);
                    CheckLayer(a, ctx, layers, true);
                    break;
                case "create_layer":
                    {
                        string name = (J.Str(a, "name") ?? "").Trim();
                        if (name.Length == 0 || name.Length > 255 || BadLayerChars.IsMatch(name))
                            throw new InvalidOperationException("Geçersiz katman adı.");
                        double ci = J.NumOr(a, "color_index", 7);
                        if (ci < 1 || ci > 255) throw new InvalidOperationException("Renk 1-255 olmalı.");
                        layers.Add(name);
                        break;
                    }
                case "line":
                    {
                        double[] s = J.Pt(J.Get(a, "start"), "start"), e = J.Pt(J.Get(a, "end"), "end");
                        if (s[0] == e[0] && s[1] == e[1] && s[2] == e[2]) throw new InvalidOperationException("Başlangıç ve bitiş aynı.");
                        CheckLayer(a, ctx, layers, false);
                        break;
                    }
                case "polyline":
                    {
                        List<object> pts = J.Arr(J.Get(a, "points"));
                        if (pts == null || pts.Count < 2 || pts.Count > 200) throw new InvalidOperationException("points 2-200 nokta olmalı.");
                        foreach (object p in pts) J.Pt(p, "nokta");
                        CheckLayer(a, ctx, layers, false);
                        break;
                    }
                case "rectangle":
                    {
                        double[] c1 = J.Pt(J.Get(a, "corner1"), "corner1"), c2 = J.Pt(J.Get(a, "corner2"), "corner2");
                        if (c1[0] == c2[0] || c1[1] == c2[1]) throw new InvalidOperationException("Dikdörtgen sıfır alanlı.");
                        CheckLayer(a, ctx, layers, false);
                        break;
                    }
                case "circle":
                    J.Pt(J.Get(a, "center"), "center");
                    if (J.Num(J.Get(a, "radius"), "radius") <= 0) throw new InvalidOperationException("Yarıçap pozitif olmalı.");
                    CheckLayer(a, ctx, layers, false);
                    break;
                case "text":
                    {
                        string t = J.Str(a, "text");
                        if (string.IsNullOrEmpty(t) || t.Length > 500) throw new InvalidOperationException("Metin 1-500 karakter olmalı.");
                        J.Pt(J.Get(a, "position"), "position");
                        if (J.Num(J.Get(a, "height"), "height") <= 0) throw new InvalidOperationException("Yazı yüksekliği pozitif olmalı.");
                        J.NumOr(a, "rotation_deg", 0);
                        CheckLayer(a, ctx, layers, false);
                        break;
                    }
                case "insert_block":
                    {
                        string bn = J.Str(a, "name");
                        if (bn == null || !blocks.Contains(bn)) throw new InvalidOperationException("Blok bulunamadı: " + bn);
                        J.Pt(J.Get(a, "position"), "position");
                        if (J.NumOr(a, "scale", 1) <= 0) throw new InvalidOperationException("Blok ölçeği pozitif olmalı.");
                        J.NumOr(a, "rotation_deg", 0);
                        CheckLayer(a, ctx, layers, false);
                        break;
                    }
                case "place_fittings":
                    {
                        List<object> hs = J.Arr(J.Get(a, "handles"));
                        if (hs != null && hs.Count > 0) J.Handles(a, ctx.HandleSet);
                        break;
                    }
                case "place_block_along":
                    {
                        string bn = J.Str(a, "name");
                        if (bn == null || !blocks.Contains(bn)) throw new InvalidOperationException("Blok bulunamadı: " + bn);
                        J.Handles(a, ctx.HandleSet);
                        string al = J.Str(a, "alignment");
                        if (al != null && al != "start" && al != "center") throw new InvalidOperationException("alignment start/center olmalı.");
                        if (J.Get(a, "width") != null && J.Num(J.Get(a, "width"), "width") <= 0) throw new InvalidOperationException("width pozitif olmalı.");
                        CheckLayer(a, ctx, layers, false);
                        break;
                    }
                case "erase":
                    J.Handles(a, ctx.EraseSet);
                    break;
                case "command":
                    CommandRules.Validate(a, ctx.EraseSet);
                    break;
                default:
                    throw new InvalidOperationException("İzin verilmeyen işlem: " + type);
            }
        }
    }

    /// <summary>Rules for the "command" action (AutoCAD command line). Mirrors service/actions.py.</summary>
    internal static class CommandRules
    {
        public const string Sel = "$SEL";
        static readonly Regex Name = new Regex(@"^[_.+\-']*([A-Za-z][A-Za-z0-9_]*)$");
        public static readonly HashSet<string> Denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QUIT", "EXIT", "CLOSE", "CLOSEALL", "OPEN", "NEW", "QNEW", "RECOVER", "RECOVERALL", "SAVE", "SAVEAS", "QSAVE",
            "SAVEALL", "WBLOCK", "EXPORT", "EXPORTPDF", "DXFOUT", "PUBLISH", "PLOT", "ETRANSMIT", "ARCHIVE",
            "SHELL", "SH", "START", "BROWSER", "HYPERLINK", "NETLOAD", "APPLOAD", "ARX", "LOAD", "SCRIPT", "SCR",
            "VBARUN", "VBALOAD", "VBAIDE", "VBAMAN", "VLISP", "VLIDE", "ACTUSERMESSAGE", "ACTSTOP",
            "SECURELOAD", "TRUSTEDPATHS", "OPTIONS", "CUI", "CUILOAD", "CUIUNLOAD", "MENU", "MENULOAD", "MENUUNLOAD",
            "AIDAPPLY", "AIDSELECT", "AIDESIGNER", "XREF", "XATTACH", "ATTACH", "IMAGEATTACH", "DATAEXTRACTION",
        };
        static readonly HashSet<string> DeniedArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SECURELOAD", "TRUSTEDPATHS", "LEGACYCODESEARCH", "ACADLSPASDOC", "CMDECHO" };

        public static void Validate(Dictionary<string, object> a, HashSet<string> allowed)
        {
            string cmd = (J.Str(a, "command") ?? "").Trim();
            Match m = Name.Match(cmd);
            if (!m.Success) throw new InvalidOperationException("Geçersiz komut adı: " + cmd);
            if (Denied.Contains(m.Groups[1].Value)) throw new InvalidOperationException("Güvenlik nedeniyle yasaklı komut: " + m.Groups[1].Value.ToUpperInvariant());
            List<object> args = J.Arr(J.Get(a, "args")) ?? new List<object>();
            if (args.Count > 60) throw new InvalidOperationException("En fazla 60 argüman.");
            foreach (object o in args)
            {
                if (o is bool || o is IDictionary<string, object> || (o != null && !(o is string) && J.Arr(o) != null))
                    throw new InvalidOperationException("Argümanlar yalnızca metin/sayı olabilir.");
                string s = Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
                if (s.Length > 256 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0) throw new InvalidOperationException("Geçersiz argüman.");
                string t = s.Trim();
                if (t.StartsWith("(") || t.StartsWith("!") || t.StartsWith("'")) throw new InvalidOperationException("LISP/ifade argümanı yasak: " + t);
                if (DeniedArgs.Contains(t.TrimStart('_', '.', '-'))) throw new InvalidOperationException("Yasaklı argüman: " + t);
            }
            bool hasSel = args.Any(o => Convert.ToString(o, CultureInfo.InvariantCulture) == Sel);
            if (J.Get(a, "handles") != null) J.Handles(a, allowed);
            else if (hasSel) throw new InvalidOperationException("$SEL kullanıldı ama handles yok.");
        }
    }

    internal static class ActionMath
    {
        public static Point3d P(double[] v) { return new Point3d(v[0], v[1], v[2]); }

        public static double Rad(double deg) { return deg * Math.PI / 180.0; }

        /// <summary>Transformation for move/copy/rotate/scale actions.</summary>
        public static Matrix3d Transform(Dictionary<string, object> a)
        {
            switch (J.Str(a, "type"))
            {
                case "move":
                case "copy":
                    return Matrix3d.Displacement(new Vector3d(J.NumOr(a, "dx", 0), J.NumOr(a, "dy", 0), J.NumOr(a, "dz", 0)));
                case "rotate":
                    return Matrix3d.Rotation(Rad(J.Num(J.Get(a, "angle_deg"), "angle_deg")), Vector3d.ZAxis, P(J.Pt(J.Get(a, "center"), "center")));
                case "scale":
                    return Matrix3d.Scaling(J.Num(J.Get(a, "factor"), "factor"), P(J.Pt(J.Get(a, "base"), "base")));
                default:
                    throw new InvalidOperationException("Dönüşüm yok: " + J.Str(a, "type"));
            }
        }
    }

    /// <summary>Human readable (Turkish) one-line description of an action.</summary>
    internal static class PlanDescriber
    {
        static string F(double d) { return d.ToString("0.####", CultureInfo.InvariantCulture); }
        static string V(object o) { double[] p = J.Pt(o, "nokta"); return "(" + F(p[0]) + ", " + F(p[1]) + ", " + F(p[2]) + ")"; }
        static int N(Dictionary<string, object> a) { List<object> h = J.Arr(J.Get(a, "handles")); return h == null ? 0 : h.Count; }
        static string Lay(Dictionary<string, object> a) { string l = J.Str(a, "layer"); return l == null ? "" : " [katman: " + l + "]"; }

        public static string Describe(Dictionary<string, object> a)
        {
            switch (J.Str(a, "type"))
            {
                case "move": return N(a) + " nesne taşınacak: dX=" + F(J.NumOr(a, "dx", 0)) + ", dY=" + F(J.NumOr(a, "dy", 0)) + ", dZ=" + F(J.NumOr(a, "dz", 0));
                case "copy": return N(a) + " nesne kopyalanacak: dX=" + F(J.NumOr(a, "dx", 0)) + ", dY=" + F(J.NumOr(a, "dy", 0)) + ", dZ=" + F(J.NumOr(a, "dz", 0));
                case "rotate": return N(a) + " nesne " + F(J.NumOr(a, "angle_deg", 0)) + "° döndürülecek, merkez " + V(J.Get(a, "center"));
                case "scale": return N(a) + " nesne ×" + F(J.NumOr(a, "factor", 1)) + " ölçeklenecek, taban " + V(J.Get(a, "base"));
                case "set_layer": return N(a) + " nesne '" + J.Str(a, "layer") + "' katmanına alınacak";
                case "create_layer": return "Yeni katman: '" + J.Str(a, "name") + "' (renk " + F(J.NumOr(a, "color_index", 7)) + ")";
                case "line": return "Çizgi " + V(J.Get(a, "start")) + " → " + V(J.Get(a, "end")) + Lay(a);
                case "polyline": { List<object> p = J.Arr(J.Get(a, "points")); return "Polyline, " + (p == null ? 0 : p.Count) + " nokta" + (J.Bool(a, "closed") ? ", kapalı" : "") + Lay(a); }
                case "rectangle": return "Dikdörtgen " + V(J.Get(a, "corner1")) + " – " + V(J.Get(a, "corner2")) + Lay(a);
                case "circle": return "Daire merkez " + V(J.Get(a, "center")) + ", yarıçap " + F(J.NumOr(a, "radius", 0)) + Lay(a);
                case "text": return "Yazı '" + J.Str(a, "text") + "' " + V(J.Get(a, "position")) + ", yükseklik " + F(J.NumOr(a, "height", 0)) + Lay(a);
                case "insert_block": return "Blok '" + J.Str(a, "name") + "' " + V(J.Get(a, "position")) + Lay(a);
                case "erase": return N(a) + " nesne SİLİNECEK";
                case "place_fittings":
                    return "Kablo tavası ek parçaları (YD / YT / yarım T / AD haç) yerleştirilecek" + (J.Arr(J.Get(a, "handles")) != null && J.Arr(J.Get(a, "handles")).Count > 0 ? " (" + N(a) + " tava)" : " (tüm tavalar)") +
                        (J.Get(a, "trim") != null && !J.Bool(a, "trim") ? ", tavalar kısaltılmayacak" : ", tavalar parça boyu kadar kısaltılacak");
                case "place_block_along":
                    return "Blok '" + J.Str(a, "name") + "' " + N(a) + " polyline/çizgi boyunca yerleştirilip segment boyuna uzatılacak" +
                        (J.Str(a, "alignment") == "center" ? " (orta hizalı)" : "") +
                        (J.Get(a, "width") != null ? ", genişlik " + F(J.NumOr(a, "width", 0)) : "") +
                        (J.Get(a, "keep_source") != null && !J.Bool(a, "keep_source") ? ", kaynak polyline SİLİNECEK" : "");
                case "command":
                    {
                        List<object> args = J.Arr(J.Get(a, "args")) ?? new List<object>();
                        string s = string.Join(" ", args.Select(o => { string x = Convert.ToString(o, CultureInfo.InvariantCulture); return x == "" ? "↵" : (x == CommandRules.Sel ? "<" + N(a) + " nesne>" : x); }));
                        return "AutoCAD komutu: " + J.Str(a, "command") + (s.Length > 0 ? " " + s : "") + (N(a) > 0 && !args.Any(o => Convert.ToString(o, CultureInfo.InvariantCulture) == CommandRules.Sel) ? " [" + N(a) + " nesne seçili]" : "");
                    }
                default: return J.Str(a, "type");
            }
        }
    }
}
