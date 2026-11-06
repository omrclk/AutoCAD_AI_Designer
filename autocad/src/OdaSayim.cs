using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>Count block inserts per room into Excel. Default: one window selection; auto-detect rooms from
    /// closed polylines, TraceBoundary at labels, or planar-graph faces from lines/polylines/arcs.</summary>
    public class OdaSayimCommands
    {
        const double PosTol = 1.0;
        const double SnapTol = 5.0; // node snap for planar graph (drawing units)
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 64000000 };

        class Room
        {
            public string Name; public List<Point2d> Poly; public double Area;
        }

        class Hit { public string Key; public Point2d P; public int Room; }

        /// <summary>Room-name candidate: label-block attributes preferred over TEXT/MTEXT (higher Pri).</summary>
        class Label
        {
            public Point2d P; public string Name; public double Pri; public ObjectId BlockId; public string BlockName;
        }

        static readonly string[] NameTags = { "mahal", "mahaladi", "oda", "odaadi", "room", "name", "ad", "isim", "mekan", "mekanadi" };
        static readonly string[] NumTags = { "no", "numara", "numarasi", "odano", "roomno", "roomnumber", "mahalno" };

        class MahalCfg
        {
            public string BlockName; // only this block is label + excluded
            public string NameTag;   // attribute tag for room name
            public string NumTag;    // optional number tag
            public override string ToString()
            {
                string s = BlockName ?? "?";
                if (!string.IsNullOrEmpty(NameTag)) s += " / " + NameTag;
                if (!string.IsNullOrEmpty(NumTag)) s += " + " + NumTag;
                return s;
            }
        }

        static readonly Dictionary<string, MahalCfg> MahalMem = new Dictionary<string, MahalCfg>(StringComparer.OrdinalIgnoreCase);

        static string MemKey(Document doc)
        {
            try { return Convert.ToString(doc.Database.FingerprintGuid); }
            catch (System.Exception) { return doc.Name ?? ""; }
        }

        class Node
        {
            public Point2d P; public int Id;
            public List<int> Out = new List<int>(); // directed-edge indices
        }

        class DEdge
        {
            public int U, V, Rev, Next = -1; public bool Used;
            public Point2d A, B;
        }

        [CommandMethod("AIDODASAYIM", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void Run() { Do(); }

        [CommandMethod("OSAY", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void RunShort() { Do(); }

        static void Do()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            List<DBObjectCollection> traced = new List<DBObjectCollection>();
            try
            {
                // Default = one selection of the whole plan. Ayarlar opens the old Polyline/Icnokta flow.
                PromptKeywordOptions mo = new PromptKeywordOptions("\nPlan\u0131 se\u00e7in (pencere) veya [Ayarlar] <Se\u00e7im>: ") { AllowNone = true };
                mo.Keywords.Add("Secim", "Secim", "Se\u00e7im");
                mo.Keywords.Add("Ayarlar", "Ayarlar", "Ayarlar");
                // Turkish dotted/dotless i aliases via Append + Accept (AutoCAD matches global name; local shows)
                mo.Keywords.Default = "Secim";
                mo.AppendKeywordsToMessage = true;
                PromptResult mr = ed.GetKeywords(mo);
                if (mr.Status == PromptStatus.Cancel) return;
                string mode = mr.Status == PromptStatus.OK ? NormKey(mr.StringResult) : "secim";
                if (mode == "ayarlar" || mode == "ayarlari" || mode == "ayar")
                {
                    if (!DoManual(ed, db, doc, traced)) return;
                    return;
                }

                MahalCfg cfg;
                if (!AskMahalCfg(ed, db, doc, false, out cfg)) return;

                PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nPlan\u0131 se\u00e7in (oda etiketleri, \u00e7izgiler, bloklar): " });
                if (sr.Status != PromptStatus.OK) return;
                ObjectId[] ids = sr.Value.GetObjectIds();

                bool withVis = false;

                string path, summary;
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    List<Room> rooms; List<Hit> hits;
                    BuildFromSelection(ed, tx, db, ids, withVis, cfg, traced, out rooms, out hits);
                    if (rooms.Count == 0) { ed.WriteMessage("\nOda bulunamad\u0131 (kapal\u0131 alan / etiket yok).\n"); return; }
                    path = WriteExcel(doc, rooms, hits, out summary);
                    tx.Commit();
                }
                OpenXlsx(path);
                ed.WriteMessage("\n" + summary + "\n");
                try { Metraj.Raise(summary); } catch (System.Exception) { }
            }
            catch (System.Exception ex) { ed.WriteMessage("\nOda say\u0131m\u0131 hatas\u0131: " + ex.Message + "\n"); }
            finally { foreach (DBObjectCollection col in traced) foreach (DBObject o in col) { try { o.Dispose(); } catch (System.Exception) { } } }
        }

        /// <summary>Old multi-step flow kept under Ayarlar.</summary>
        static bool DoManual(Editor ed, Database db, Document doc, List<DBObjectCollection> traced)
        {
            PromptKeywordOptions ao = new PromptKeywordOptions("\nOda alan\u0131 [Polyline/Icnokta] <Polyline>: ") { AllowNone = true };
            ao.Keywords.Add("Polyline", "Polyline", "Polyline");
            ao.Keywords.Add("Icnokta", "Icnokta", "\u0130\u00e7nokta");
            ao.Keywords.Default = "Polyline"; ao.AppendKeywordsToMessage = true;
            PromptResult ar = ed.GetKeywords(ao);
            if (ar.Status != PromptStatus.OK && ar.Status != PromptStatus.None) return false;
            bool pick = ar.Status == PromptStatus.OK && NormKey(ar.StringResult).Contains("nokta");

            List<ObjectId> roomIds = new List<ObjectId>();
            List<Curve> roomCurves = new List<Curve>();
            if (pick)
            {
                while (true)
                {
                    PromptPointResult pr = ed.GetPoint(new PromptPointOptions("\nOda i\u00e7inde bir nokta (Enter = bitti): ") { AllowNone = true });
                    if (pr.Status != PromptStatus.OK) break;
                    Curve best = TraceAt(ed, pr.Value, traced);
                    if (best != null) { roomCurves.Add(best); ed.WriteMessage("\nS\u0131n\u0131r bulundu."); }
                    else
                    {
                        ObjectId cid = ArmaturCommands.EnclosingPublic(db, pr.Value);
                        if (cid.IsNull) { ed.WriteMessage("\nKapal\u0131 s\u0131n\u0131r bulunamad\u0131."); continue; }
                        roomIds.Add(cid);
                    }
                }
                if (roomCurves.Count == 0 && roomIds.Count == 0) return false;
            }
            else
            {
                PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nKapal\u0131 oda polyline'lar\u0131n\u0131 se\u00e7in: " },
                    new SelectionFilter(new[] { new TypedValue(-4, "<OR"), new TypedValue(0, "LWPOLYLINE"), new TypedValue(0, "POLYLINE"), new TypedValue(0, "CIRCLE"), new TypedValue(-4, "OR>") }));
                if (sr.Status != PromptStatus.OK) return false;
                roomIds.AddRange(sr.Value.GetObjectIds());
            }

            ObjectId buildingId = ObjectId.Null;
            PromptEntityOptions beo = new PromptEntityOptions("\nBina d\u0131\u015f s\u0131n\u0131r\u0131 (Enter = yok): ") { AllowNone = true };
            beo.SetRejectMessage("\nE\u011fri de\u011fil."); beo.AddAllowedClass(typeof(Curve), false);
            PromptEntityResult ber = ed.GetEntity(beo);
            if (ber.Status == PromptStatus.OK) buildingId = ber.ObjectId;
            else if (ber.Status != PromptStatus.None) return false;

            PromptSelectionResult bsr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSay\u0131lacak bloklar (Enter = modeldeki t\u00fcm\u00fc): " },
                new SelectionFilter(new[] { new TypedValue(0, "INSERT") }));
            HashSet<ObjectId> only = null;
            if (bsr.Status == PromptStatus.OK) only = new HashSet<ObjectId>(bsr.Value.GetObjectIds());
            else if (bsr.Status == PromptStatus.Cancel) return false;

            PromptResult vr = ed.GetKeywords(new PromptKeywordOptions("\nG\u00f6r\u00fcn\u00fcrl\u00fck durumu eklensin mi? [Evet/Hayir] <Hayir>: ", "Evet Hayir") { AllowNone = true });
            if (vr.Status == PromptStatus.Cancel) return false;
            bool withVis = vr.Status == PromptStatus.OK && NormKey(vr.StringResult) == "evet";

            MahalCfg cfg;
            if (!AskMahalCfg(ed, db, doc, true, out cfg)) return false;

            bool outCol = false;
            if (!buildingId.IsNull)
            {
                PromptResult or = ed.GetKeywords(new PromptKeywordOptions("\n'Oda d\u0131\u015f\u0131 (bina i\u00e7i)' s\u00fctunu? [Evet/Hayir] <Evet>: ", "Evet Hayir") { AllowNone = true });
                if (or.Status == PromptStatus.Cancel) return false;
                outCol = !(or.Status == PromptStatus.OK && NormKey(or.StringResult) == "hayir");
            }

            string path, summary;
            using (doc.LockDocument())
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                List<Room> rooms = new List<Room>();
                foreach (ObjectId id in roomIds)
                {
                    Curve c = tx.GetObject(id, OpenMode.ForRead) as Curve;
                    if (c == null || !ArmaturCommands.IsClosedPublic(c)) continue;
                    List<Point2d> poly = ArmaturCommands.SamplePublic(c);
                    if (poly.Count >= 3) rooms.Add(new Room { Poly = poly, Area = AbsArea(poly) });
                }
                foreach (Curve c in roomCurves)
                {
                    List<Point2d> poly = ArmaturCommands.SamplePublic(c);
                    if (poly.Count >= 3) rooms.Add(new Room { Poly = poly, Area = AbsArea(poly) });
                }
                if (rooms.Count == 0) { ed.WriteMessage("\nKapal\u0131 oda yok.\n"); return false; }
                rooms = rooms.OrderBy(r => r.Area).ToList();

                List<Point2d> building = null;
                if (!buildingId.IsNull)
                {
                    Curve bc = tx.GetObject(buildingId, OpenMode.ForRead) as Curve;
                    if (bc != null && ArmaturCommands.IsClosedPublic(bc)) building = ArmaturCommands.SamplePublic(bc);
                }

                HashSet<ObjectId> labelIds;
                NameRoomsFromModel(tx, db, rooms, cfg, out labelIds);

                List<Hit> hits = CollectHits(tx, db, rooms, building, only, withVis, outCol, labelIds, cfg);
                path = WriteExcel(doc, rooms, hits, out summary, outCol && building != null);
                tx.Commit();
            }
            OpenXlsx(path);
            ed.WriteMessage("\n" + summary + "\n");
            try { Metraj.Raise(summary); } catch (System.Exception) { }
            return true;
        }

        static void BuildFromSelection(Editor ed, Transaction tx, Database db, ObjectId[] ids,
            bool withVis, MahalCfg cfg, List<DBObjectCollection> traced, out List<Room> rooms, out List<Hit> hits)
        {
            List<ObjectId> closedPl = new List<ObjectId>();
            List<object> segs = new List<object>();
            List<Label> labels = new List<Label>();
            List<BlockReference> blocks = new List<BlockReference>();
            HashSet<ObjectId> labelIds = new HashSet<ObjectId>();

            foreach (ObjectId id in ids)
            {
                string dxf = id.ObjectClass.DxfName;
                Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (dxf == "INSERT")
                {
                    BlockReference br = ent as BlockReference;
                    if (br == null) continue;
                    string bn = EffName(tx, br);
                    bool pinned = cfg != null && !string.IsNullOrEmpty(cfg.BlockName);
                    bool isLabelBlk = pinned && string.Equals(bn, cfg.BlockName, StringComparison.OrdinalIgnoreCase);
                    string mahal; double pri;
                    if (pinned)
                    {
                        if (isLabelBlk)
                        {
                            labelIds.Add(id);
                            if (TryMahalLabel(tx, br, cfg, out mahal, out pri) && mahal != null)
                                labels.Add(new Label { P = new Point2d(br.Position.X, br.Position.Y), Name = mahal, Pri = 10000 + pri, BlockId = id, BlockName = bn });
                        }
                    }
                    else if (TryMahalLabel(tx, br, null, out mahal, out pri) && mahal != null)
                    {
                        labels.Add(new Label { P = new Point2d(br.Position.X, br.Position.Y), Name = mahal, Pri = 10000 + pri, BlockId = id, BlockName = bn });
                        labelIds.Add(id);
                    }
                    if (Keep(tx, br)) blocks.Add(br);
                }
                else if (dxf == "TEXT")
                {
                    DBText t = (DBText)ent;
                    string s = CleanLabel(t.TextString); if (s != null) labels.Add(new Label { P = new Point2d(t.Position.X, t.Position.Y), Name = s, Pri = t.Height, BlockId = ObjectId.Null });
                }
                else if (dxf == "MTEXT")
                {
                    MText t = (MText)ent;
                    string s = CleanLabel(t.Contents); if (s != null) labels.Add(new Label { P = new Point2d(t.Location.X, t.Location.Y), Name = s, Pri = t.TextHeight, BlockId = ObjectId.Null });
                }
                else if (dxf == "LWPOLYLINE" || dxf == "POLYLINE" || dxf == "CIRCLE")
                {
                    Curve c = ent as Curve;
                    if (c != null && ArmaturCommands.IsClosedPublic(c)) closedPl.Add(id);
                    CollectEdges(ent, segs);
                }
                else if (dxf == "LINE" || dxf == "ARC" || dxf == "SPLINE")
                    CollectEdges(ent, segs);
            }

            rooms = new List<Room>();
            foreach (ObjectId id in closedPl)
            {
                Curve c = tx.GetObject(id, OpenMode.ForRead) as Curve;
                List<Point2d> poly = ArmaturCommands.SamplePublic(c);
                if (poly.Count >= 3) AddUniqueRoom(rooms, poly);
            }
            foreach (List<Point2d> f in FindFaces(segs)) AddUniqueRoom(rooms, f);

            // TraceBoundary from mahal label inserts first, then other labels not yet inside a room
            foreach (Label lb in labels.OrderByDescending(l => l.Pri))
            {
                if (rooms.Any(r => ArmaturGeo.Inside(r.Poly, lb.P))) continue;
                Curve best = TraceAt(ed, new Point3d(lb.P.X, lb.P.Y, 0), traced);
                if (best == null) continue;
                List<Point2d> poly = ArmaturCommands.SamplePublic(best);
                if (poly.Count >= 3) AddUniqueRoom(rooms, poly);
            }

            rooms = FilterOuter(rooms);
            rooms = MergeOverlapping(rooms);
            HashSet<ObjectId> usedLabels;
            NameRooms(rooms, labels, out usedLabels);
            foreach (ObjectId id in usedLabels) labelIds.Add(id);
            // also exclude every insert of configured label block name / any mahal-attr block seen
            rooms = rooms.OrderBy(r => r.Area).ToList();

            hits = new List<Hit>();
            foreach (BlockReference br in blocks)
            {
                if (labelIds.Contains(br.ObjectId)) continue;
                if (cfg != null && !string.IsNullOrEmpty(cfg.BlockName) && string.Equals(EffName(tx, br), cfg.BlockName, StringComparison.OrdinalIgnoreCase)) continue;
                Point2d p = new Point2d(br.Position.X, br.Position.Y);
                int ri = -1;
                for (int i = 0; i < rooms.Count; i++)
                    if (ArmaturGeo.Inside(rooms[i].Poly, p)) { ri = i; break; }
                if (ri < 0) continue;
                hits.Add(new Hit { Key = BlockKey(tx, br, withVis), P = p, Room = ri });
            }
            hits = Dedupe(hits);
        }

        /// <summary>Ask for mahal label block + name/number tags; remember per drawing. Enter = automatic (heuristics). force=true always re-asks (Ayarlar).</summary>
        static bool AskMahalCfg(Editor ed, Database db, Document doc, bool force, out MahalCfg cfg)
        {
            cfg = null;
            string key = MemKey(doc);
            MahalCfg remembered;
            if (!force && MahalMem.TryGetValue(key, out remembered) && remembered != null && !string.IsNullOrEmpty(remembered.BlockName))
            {
                cfg = remembered;
                ed.WriteMessage("\nMahal etiketi (bellek): " + cfg + " — de\u011fi\u015ftirmek i\u00e7in Ayarlar.\n");
                return true;
            }

            PromptEntityOptions eo = new PromptEntityOptions("\nMahal etiket blo\u011funu se\u00e7in (Enter = otomatik): ") { AllowNone = true };
            eo.SetRejectMessage("\nBlok de\u011fil."); eo.AddAllowedClass(typeof(BlockReference), false);
            PromptEntityResult er = ed.GetEntity(eo);
            if (er.Status == PromptStatus.Cancel) return false;
            if (er.Status == PromptStatus.None)
            {
                // automatic: clear pinned cfg for this drawing so heuristics apply
                MahalMem.Remove(key);
                cfg = null;
                ed.WriteMessage("\nMahal: otomatik (MAHAL/ODA/ROOM attribute veya TEXT).\n");
                return true;
            }
            if (er.Status != PromptStatus.OK) return false;

            List<string> tags = new List<string>();
            string bname;
            using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockReference br = (BlockReference)tx.GetObject(er.ObjectId, OpenMode.ForRead);
                bname = EffName(tx, br);
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar == null || string.IsNullOrWhiteSpace(ar.Tag)) continue;
                    if (!tags.Any(x => string.Equals(x, ar.Tag, StringComparison.OrdinalIgnoreCase))) tags.Add(ar.Tag);
                }
                tx.Commit();
            }
            if (tags.Count == 0) { ed.WriteMessage("\nSe\u00e7ilen blokta attribute yok.\n"); return false; }

            string nameTag = PickTag(ed, "\nOda ad\u0131 attribute etiketi", tags, PreferTag(tags, NameTags));
            if (nameTag == null) return false;
            List<string> numChoices = tags.Where(x => !string.Equals(x, nameTag, StringComparison.OrdinalIgnoreCase)).ToList();
            string numTag = null;
            if (numChoices.Count > 0)
            {
                numTag = PickTag(ed, "\nNumara attribute etiketi (Enter = yok)", numChoices, PreferTag(numChoices, NumTags), allowNone: true);
                if (numTag == null) return false; // cancel
                if (numTag.Length == 0) numTag = null;
            }

            cfg = new MahalCfg { BlockName = bname, NameTag = nameTag, NumTag = numTag };
            MahalMem[key] = cfg;
            ed.WriteMessage("\nMahal etiketi kaydedildi: " + cfg + "\n");
            return true;
        }

        static string PreferTag(List<string> tags, string[] prefer)
        {
            foreach (string p in prefer)
                foreach (string t in tags)
                    if (TrayKesif.Norm(t) == p || TrayKesif.Norm(t).Contains(p)) return t;
            return tags[0];
        }

        /// <summary>Returns chosen tag; empty string if allowNone+Enter; null if cancel.</summary>
        static string PickTag(Editor ed, string msg, List<string> tags, string def, bool allowNone = false)
        {
            if (tags.Count == 1 && !allowNone) return tags[0];
            PromptKeywordOptions ko = new PromptKeywordOptions(msg) { AllowNone = allowNone };
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (string tag in tags)
            {
                string g = new string(tag.Where(char.IsLetterOrDigit).ToArray());
                if (string.IsNullOrEmpty(g) || char.IsDigit(g[0])) g = "T" + g;
                string baseG = g; int n = 2;
                while (!used.Add(g)) g = baseG + (n++);
                // AutoCAD keyword global max ~64; keep short
                if (g.Length > 32) g = g.Substring(0, 32);
                ko.Keywords.Add(g, g, tag);
                map[g] = tag;
                i++;
            }
            if (allowNone) { ko.Keywords.Add("Yok", "Yok", "Yok"); map["Yok"] = ""; }
            string defG = map.FirstOrDefault(kv => string.Equals(kv.Value, def, StringComparison.OrdinalIgnoreCase)).Key;
            if (string.IsNullOrEmpty(defG) && allowNone) defG = "Yok";
            if (string.IsNullOrEmpty(defG)) defG = map.Keys.First();
            ko.Keywords.Default = defG;
            ko.AppendKeywordsToMessage = true;
            PromptResult r = ed.GetKeywords(ko);
            if (r.Status == PromptStatus.Cancel) return null;
            if (r.Status == PromptStatus.None) return allowNone ? "" : map[defG];
            if (r.Status != PromptStatus.OK) return null;
            string chosen;
            return map.TryGetValue(r.StringResult, out chosen) ? chosen : r.StringResult;
        }

        /// <summary>Build room name from attributes. cfg tags if set; else heuristics. "101 Salon" when number present.</summary>
        static bool TryMahalLabel(Transaction tx, BlockReference br, MahalCfg cfg, out string name, out double pri)
        {
            name = null; pri = 1;
            if (br.AttributeCollection == null || br.AttributeCollection.Count == 0) return false;
            string nm = null, num = null; double h = 1;
            string first = null;
            string wantName = cfg != null ? cfg.NameTag : null;
            string wantNum = cfg != null ? cfg.NumTag : null;
            foreach (ObjectId aid in br.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (ar == null) continue;
                string val = CleanLabel(AttPlain(ar)); if (val == null) continue;
                if (first == null) first = val;
                if (!string.IsNullOrEmpty(wantName) && string.Equals(ar.Tag, wantName, StringComparison.OrdinalIgnoreCase))
                { nm = val; if (ar.Height > 0) h = ar.Height; }
                if (!string.IsNullOrEmpty(wantNum) && string.Equals(ar.Tag, wantNum, StringComparison.OrdinalIgnoreCase))
                    num = val;
                if (wantName == null)
                {
                    string tag = TrayKesif.Norm(ar.Tag);
                    if (IsNameTag(tag) && nm == null) { nm = val; if (ar.Height > 0) h = ar.Height; }
                    if (IsNumTag(tag) && num == null) num = val;
                }
            }
            if (nm == null && wantName == null) nm = first;
            if (nm == null) return false;
            if (num != null && !nm.StartsWith(num + " ", StringComparison.OrdinalIgnoreCase) && !string.Equals(nm, num, StringComparison.OrdinalIgnoreCase))
                name = num + " " + nm;
            else name = nm;
            pri = h;
            return true;
        }

        static bool IsNameTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            foreach (string k in NameTags) if (tag == k || tag.StartsWith(k) || tag.EndsWith(k)) return true;
            return false;
        }

        static bool IsNumTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            foreach (string k in NumTags) if (tag == k || tag.StartsWith(k) || tag.EndsWith(k)) return true;
            return false;
        }

        static string AttPlain(AttributeReference ar)
        {
            try { if (ar.IsMTextAttribute && ar.MTextAttribute != null) return ar.MTextAttribute.Text; } catch (System.Exception) { }
            return ar.TextString;
        }

        static void CollectEdges(Entity ent, List<object> segs)
        {
            Line ln = ent as Line;
            if (ln != null) { segs.Add(new[] { new Point2d(ln.StartPoint.X, ln.StartPoint.Y), new Point2d(ln.EndPoint.X, ln.EndPoint.Y) }); return; }
            Arc arc = ent as Arc;
            if (arc != null)
            {
                // approximate arc as polyline samples
                Point2d prev = default(Point2d); bool has = false;
                for (int i = 0; i <= 16; i++)
                {
                    double a = arc.StartAngle + (arc.EndAngle - arc.StartAngle) * i / 16.0;
                    if (arc.EndAngle < arc.StartAngle) a = arc.StartAngle + (arc.EndAngle + 2 * Math.PI - arc.StartAngle) * i / 16.0;
                    Point3d q = arc.Center + new Vector3d(Math.Cos(a), Math.Sin(a), 0) * arc.Radius;
                    Point2d p = new Point2d(q.X, q.Y);
                    if (has) segs.Add(new[] { prev, p });
                    prev = p; has = true;
                }
                return;
            }
            Polyline pl = ent as Polyline;
            if (pl != null)
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n - 1; i++) segs.Add(new[] { pl.GetPoint2dAt(i), pl.GetPoint2dAt(i + 1) });
                if (pl.Closed && n > 2) segs.Add(new[] { pl.GetPoint2dAt(n - 1), pl.GetPoint2dAt(0) });
                return;
            }
            Curve c = ent as Curve;
            if (c != null)
            {
                try
                {
                    List<Point2d> pts = ArmaturCommands.SamplePublic(c);
                    for (int i = 0; i < pts.Count - 1; i++) segs.Add(new[] { pts[i], pts[i + 1] });
                    if (ArmaturCommands.IsClosedPublic(c) && pts.Count > 2) segs.Add(new[] { pts[pts.Count - 1], pts[0] });
                }
                catch (System.Exception) { }
            }
        }

        /// <summary>Split raw segments at crossings / T-joints, then enumerate bounded faces (node snap = SnapTol).</summary>
        static List<Point2d[]> Planarize(List<object> segs)
        {
            List<Point2d[]> raw = segs.Select(o => (Point2d[])o).Where(ab => ab[0].GetDistanceTo(ab[1]) >= SnapTol * 0.5).ToList();
            // candidate split points = all endpoints
            List<Point2d> pts = new List<Point2d>();
            Action<Point2d> add = p => { foreach (Point2d q in pts) if (q.GetDistanceTo(p) <= SnapTol) return; pts.Add(p); };
            foreach (Point2d[] ab in raw) { add(ab[0]); add(ab[1]); }
            // pairwise intersections + endpoints lying on other segments
            for (int i = 0; i < raw.Count; i++)
                for (int j = i + 1; j < raw.Count; j++)
                {
                    Point2d a = raw[i][0], b = raw[i][1], c = raw[j][0], d = raw[j][1];
                    Point2d ip; if (SegIntersect(a, b, c, d, out ip)) add(ip);
                }
            foreach (Point2d[] ab in raw)
                foreach (Point2d p in pts)
                    if (OnSeg(ab[0], ab[1], p)) add(p); // already in pts; OnSeg used below when splitting
            List<Point2d[]> outp = new List<Point2d[]>();
            foreach (Point2d[] ab in raw)
            {
                List<Point2d> mid = new List<Point2d>();
                foreach (Point2d p in pts) if (OnSeg(ab[0], ab[1], p) && p.GetDistanceTo(ab[0]) > SnapTol && p.GetDistanceTo(ab[1]) > SnapTol) mid.Add(p);
                mid = mid.OrderBy(p => p.GetDistanceTo(ab[0])).ToList();
                Point2d prev = ab[0];
                foreach (Point2d p in mid) { outp.Add(new[] { prev, p }); prev = p; }
                outp.Add(new[] { prev, ab[1] });
            }
            return outp;
        }

        static bool SegIntersect(Point2d a, Point2d b, Point2d c, Point2d d, out Point2d ip)
        {
            ip = default(Point2d);
            Vector2d r = b - a, s = d - c; double den = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(den) < 1e-12) return false; // parallel
            Vector2d ca = c - a;
            double t = (ca.X * s.Y - ca.Y * s.X) / den, u = (ca.X * r.Y - ca.Y * r.X) / den;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return false;
            ip = a + r * Math.Max(0, Math.Min(1, t)); return true;
        }

        static bool OnSeg(Point2d a, Point2d b, Point2d p)
        {
            if (p.GetDistanceTo(a) <= SnapTol || p.GetDistanceTo(b) <= SnapTol) return true;
            Vector2d ab = b - a; double l2 = ab.DotProduct(ab); if (l2 < 1e-12) return false;
            double t = (p - a).DotProduct(ab) / l2; if (t < 0 || t > 1) return false;
            return (a + ab * t).GetDistanceTo(p) <= SnapTol;
        }

        static List<List<Point2d>> FindFaces(List<object> segs)
        {
            List<List<Point2d>> faces = new List<List<Point2d>>();
            List<Point2d[]> planar = Planarize(segs);
            if (planar.Count == 0) return faces;
            List<Node> nodes = new List<Node>();
            Func<Point2d, int> nid = p =>
            {
                for (int i = 0; i < nodes.Count; i++)
                    if (nodes[i].P.GetDistanceTo(p) <= SnapTol) return i;
                int id = nodes.Count; nodes.Add(new Node { P = p, Id = id }); return id;
            };
            List<DEdge> E = new List<DEdge>();
            foreach (Point2d[] ab in planar)
            {
                int u = nid(ab[0]), v = nid(ab[1]);
                if (u == v) continue;
                bool dup = false;
                foreach (DEdge e in E) if ((e.U == u && e.V == v) || (e.U == v && e.V == u)) { dup = true; break; }
                if (dup) continue;
                int i0 = E.Count, i1 = E.Count + 1;
                E.Add(new DEdge { U = u, V = v, Rev = i1, A = nodes[u].P, B = nodes[v].P });
                E.Add(new DEdge { U = v, V = u, Rev = i0, A = nodes[v].P, B = nodes[u].P });
                nodes[u].Out.Add(i0); nodes[v].Out.Add(i1);
            }
            if (E.Count == 0) return faces;

            // sort outgoing by angle; set Next = previous in CCW order at destination (= leftmost turn walking)
            foreach (Node n in nodes)
            {
                n.Out.Sort((ia, ib) =>
                {
                    Vector2d va = E[ia].B - E[ia].A, vb = E[ib].B - E[ib].A;
                    return Math.Atan2(va.Y, va.X).CompareTo(Math.Atan2(vb.Y, vb.X));
                });
            }
            foreach (DEdge e in E)
            {
                Node at = nodes[e.V];
                int rev = e.Rev;
                int idx = at.Out.IndexOf(rev);
                // next face edge = previous in CCW angular order (right-hand rule → interior on left for CCW faces)
                int prev = (idx - 1 + at.Out.Count) % at.Out.Count;
                e.Next = at.Out[prev];
            }

            for (int i = 0; i < E.Count; i++)
            {
                if (E[i].Used) continue;
                List<Point2d> poly = new List<Point2d>();
                int cur = i; int guard = 0;
                while (!E[cur].Used && guard++ < E.Count + 2)
                {
                    E[cur].Used = true;
                    poly.Add(E[cur].A);
                    cur = E[cur].Next;
                    if (cur < 0) break;
                    if (cur == i) break;
                }
                if (cur != i || poly.Count < 3) continue;
                double a = AbsArea(poly);
                if (a < SnapTol * SnapTol) continue;
                // keep only CCW (positive shoelace) — CW is the outer/complement walk
                if (SignedArea(poly) >= 0) continue; // keep CCW (negative shoelace); CW = outer walks
                faces.Add(poly);
            }
            return faces;
        }

        static double SignedArea(List<Point2d> p)
        {
            double a = 0; for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X + p[i].X) * (p[j].Y - p[i].Y); return a / 2;
        }

        static void AddUniqueRoom(List<Room> rooms, List<Point2d> poly)
        {
            double a = AbsArea(poly);
            if (a < 1) return;
            Point2d c = Centroid(poly);
            foreach (Room r in rooms)
            {
                if (Math.Abs(r.Area - a) / Math.Max(a, r.Area) < 0.05 && ArmaturGeo.Inside(r.Poly, c) && ArmaturGeo.Inside(poly, Centroid(r.Poly)))
                    return; // same face
            }
            rooms.Add(new Room { Poly = poly, Area = a });
        }

        /// <summary>If two rooms each contain the other's centroid, keep the smaller (or either).</summary>
        static List<Room> MergeOverlapping(List<Room> rooms)
        {
            List<Room> list = rooms.OrderBy(r => r.Area).ToList();
            bool[] drop = new bool[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                if (drop[i]) continue;
                Point2d ci = Centroid(list[i].Poly);
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (drop[j]) continue;
                    Point2d cj = Centroid(list[j].Poly);
                    if (ArmaturGeo.Inside(list[i].Poly, cj) && ArmaturGeo.Inside(list[j].Poly, ci))
                        drop[j] = true; // keep smaller i
                }
            }
            List<Room> keep = new List<Room>();
            for (int i = 0; i < list.Count; i++) if (!drop[i]) keep.Add(list[i]);
            return keep;
        }

        static List<Room> FilterOuter(List<Room> rooms)
        {
            if (rooms.Count <= 1) return rooms;
            // drop any room that contains every other room's centroid (building outline / infinite-face proxy)
            List<Room> keep = new List<Room>();
            foreach (Room r in rooms)
            {
                bool containsAll = rooms.Where(o => !object.ReferenceEquals(o, r)).All(o => ArmaturGeo.Inside(r.Poly, Centroid(o.Poly)));
                if (containsAll && rooms.Count > 1) continue;
                keep.Add(r);
            }
            if (keep.Count == 0) keep = rooms.OrderBy(r => r.Area).Take(rooms.Count - 1).ToList();
            // also drop faces much larger than the next ( > 4x sum of others)
            double sumSmall = keep.OrderByDescending(r => r.Area).Skip(1).Sum(r => r.Area);
            if (keep.Count > 1 && keep.Max(r => r.Area) > Math.Max(sumSmall * 4, 1))
                keep = keep.Where(r => r.Area <= sumSmall * 4).ToList();
            return keep;
        }

        static Point2d Centroid(List<Point2d> p)
        {
            double x = 0, y = 0; foreach (Point2d q in p) { x += q.X; y += q.Y; }
            return new Point2d(x / p.Count, y / p.Count);
        }

        static void NameRooms(List<Room> rooms, List<Label> labels, out HashSet<ObjectId> usedLabelBlocks)
        {
            usedLabelBlocks = new HashSet<ObjectId>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byArea = rooms.OrderBy(r => r.Area).ToList();
            Dictionary<Room, string> map = new Dictionary<Room, string>();
            // higher Pri first (mahal blocks ~10000+); then shorter names
            foreach (Label t in labels.OrderByDescending(l => l.Pri).ThenBy(l => l.Name.Length))
            {
                Room host = byArea.FirstOrDefault(r => ArmaturGeo.Inside(r.Poly, t.P));
                if (host == null || map.ContainsKey(host)) continue;
                if (!used.Add(t.Name)) continue;
                map[host] = t.Name;
                if (!t.BlockId.IsNull) usedLabelBlocks.Add(t.BlockId);
            }
            int n = 1;
            foreach (Room r in rooms)
            {
                string name;
                if (map.TryGetValue(r, out name)) r.Name = name;
                else { while (!used.Add("Oda " + n)) n++; r.Name = "Oda " + n; n++; }
            }
        }

        static void NameRoomsFromModel(Transaction tx, Database db, List<Room> rooms, MahalCfg cfg, out HashSet<ObjectId> labelIds)
        {
            labelIds = new HashSet<ObjectId>();
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            List<Label> labels = new List<Label>();
            bool pinned = cfg != null && !string.IsNullOrEmpty(cfg.BlockName);
            foreach (ObjectId id in ms)
            {
                string dxf = id.ObjectClass.DxfName;
                if (dxf == "INSERT")
                {
                    BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string bn = EffName(tx, br);
                    string mahal; double pri;
                    if (pinned)
                    {
                        if (!string.Equals(bn, cfg.BlockName, StringComparison.OrdinalIgnoreCase)) { /* not label */ }
                        else
                        {
                            labelIds.Add(id);
                            if (TryMahalLabel(tx, br, cfg, out mahal, out pri) && mahal != null)
                                labels.Add(new Label { P = new Point2d(br.Position.X, br.Position.Y), Name = mahal, Pri = 10000 + pri, BlockId = id, BlockName = bn });
                        }
                    }
                    else if (TryMahalLabel(tx, br, null, out mahal, out pri) && mahal != null)
                    {
                        labels.Add(new Label { P = new Point2d(br.Position.X, br.Position.Y), Name = mahal, Pri = 10000 + pri, BlockId = id, BlockName = bn });
                        labelIds.Add(id);
                    }
                }
                else if (dxf == "TEXT")
                {
                    DBText t = (DBText)tx.GetObject(id, OpenMode.ForRead);
                    string s = CleanLabel(t.TextString); if (s != null) labels.Add(new Label { P = new Point2d(t.Position.X, t.Position.Y), Name = s, Pri = t.Height, BlockId = ObjectId.Null });
                }
                else if (dxf == "MTEXT")
                {
                    MText t = (MText)tx.GetObject(id, OpenMode.ForRead);
                    string s = CleanLabel(t.Contents); if (s != null) labels.Add(new Label { P = new Point2d(t.Location.X, t.Location.Y), Name = s, Pri = t.TextHeight, BlockId = ObjectId.Null });
                }
            }
            HashSet<ObjectId> used;
            NameRooms(rooms, labels, out used);
            foreach (ObjectId id in used) labelIds.Add(id);
        }

        static List<Hit> CollectHits(Transaction tx, Database db, List<Room> rooms, List<Point2d> building, HashSet<ObjectId> only, bool withVis, bool outCol, HashSet<ObjectId> labelIds, MahalCfg cfg)
        {
            List<Hit> hits = new List<Hit>();
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (only != null && !only.Contains(id)) continue;
                if (id.ObjectClass.DxfName != "INSERT") continue;
                if (labelIds != null && labelIds.Contains(id)) continue;
                BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null || !Keep(tx, br)) continue;
                if (cfg != null && !string.IsNullOrEmpty(cfg.BlockName) && string.Equals(EffName(tx, br), cfg.BlockName, StringComparison.OrdinalIgnoreCase)) continue;
                Point2d p = new Point2d(br.Position.X, br.Position.Y);
                if (building != null && !ArmaturGeo.Inside(building, p)) continue;
                int ri = -1;
                for (int i = 0; i < rooms.Count; i++) if (ArmaturGeo.Inside(rooms[i].Poly, p)) { ri = i; break; }
                if (ri < 0 && !(outCol && building != null)) continue;
                hits.Add(new Hit { Key = BlockKey(tx, br, withVis), P = p, Room = ri });
            }
            return Dedupe(hits);
        }

        static List<Hit> Dedupe(List<Hit> hits)
        {
            List<Hit> uniq = new List<Hit>();
            foreach (Hit h in hits.OrderBy(h => h.Room < 0 ? 9999 : h.Room).ThenBy(h => h.Key))
            {
                bool dup = false;
                foreach (Hit u in uniq)
                    if (u.Key == h.Key && Math.Abs(u.P.X - h.P.X) <= PosTol && Math.Abs(u.P.Y - h.P.Y) <= PosTol) { dup = true; break; }
                if (!dup) uniq.Add(h);
            }
            return uniq;
        }

        static string WriteExcel(Document doc, List<Room> rooms, List<Hit> hits, out string summary, bool outCol = false)
        {
            List<string> roomNames = rooms.Select(r => r.Name).ToList();
            if (outCol) roomNames.Add("Oda d\u0131\u015f\u0131 (bina i\u00e7i)");
            int nOut = outCol ? rooms.Count : -1;
            Dictionary<string, int[]> grid = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (Hit h in hits)
            {
                int col = h.Room >= 0 ? h.Room : nOut;
                if (col < 0) continue;
                int[] row; if (!grid.TryGetValue(h.Key, out row)) { row = new int[roomNames.Count]; grid[h.Key] = row; }
                row[col]++;
            }
            List<object> sheet = new List<object>();
            List<object> header = new List<object> { "Blok" }; foreach (string n in roomNames) header.Add(n); header.Add("Toplam");
            sheet.Add(header.ToArray());
            int[] colTot = new int[roomNames.Count];
            foreach (string bk in grid.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                int[] row = grid[bk]; List<object> cells = new List<object> { bk }; int rt = 0;
                for (int i = 0; i < roomNames.Count; i++) { cells.Add(row[i]); rt += row[i]; colTot[i] += row[i]; }
                cells.Add(rt); sheet.Add(cells.ToArray());
            }
            List<object> tot = new List<object> { "TOPLAM" }; int grand = 0;
            for (int i = 0; i < roomNames.Count; i++) { tot.Add(colTot[i]); grand += colTot[i]; }
            tot.Add(grand); sheet.Add(tot.ToArray());

            string path = ExportPath(doc);
            ServiceHost.EnsureStarted(Settings.Load());
            Dictionary<string, object> resp = Js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/xlsx",
                Js.Serialize(new Dictionary<string, object> { { "path", path }, { "sheets", new object[] { new object[] { "Oda say\u0131m\u0131", sheet } } } })));
            if (resp == null || resp.ContainsKey("error")) throw new InvalidOperationException(resp == null ? "bo\u015f yan\u0131t" : Convert.ToString(resp["error"]));
            if (resp.ContainsKey("path")) path = Convert.ToString(resp["path"]);

            summary = "Oda blok say\u0131m\u0131: " + rooms.Count + " oda, " + grid.Count + " blok ad\u0131, " + hits.Count + " adet.\nExcel: " + path;
            for (int i = 0; i < rooms.Count; i++) summary += "\n  " + rooms[i].Name + ": " + hits.Count(h => h.Room == i);
            if (outCol) summary += "\n  Oda d\u0131\u015f\u0131 (bina i\u00e7i): " + hits.Count(h => h.Room < 0);
            return path;
        }

        static Curve TraceAt(Editor ed, Point3d pt, List<DBObjectCollection> traced)
        {
            try
            {
                DBObjectCollection col = ed.TraceBoundary(pt, true);
                if (col == null) return null;
                traced.Add(col);
                Curve best = null;
                foreach (DBObject o in col) { Curve cv = o as Curve; if (cv != null && (best == null || cv.Area > best.Area)) best = cv; }
                return best;
            }
            catch (System.Exception) { return null; }
        }

        static void OpenXlsx(string path)
        {
            if (Environment.GetEnvironmentVariable("CADAI_NOOPEN") == null && !string.IsNullOrEmpty(path) && File.Exists(path))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (System.Exception) { }
        }

        static string NormKey(string s)
        {
            if (s == null) return "";
            s = TrayKesif.Norm(s);
            return s.Replace("icnokta", "icnokta").Replace("secim", "secim");
        }

        static double AbsArea(List<Point2d> p) { return Math.Abs(SignedArea(p)); }

        static bool Keep(Transaction tx, BlockReference br)
        {
            try
            {
                if (br.BlockTableRecord.IsNull) return false;
                BlockTableRecord btr = (BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (btr.IsFromExternalReference || btr.IsFromOverlayReference) return false;
                string n = EffName(tx, br);
                if (string.IsNullOrEmpty(n) || n.StartsWith("*")) return false;
                string ln = n.ToLowerInvariant();
                if (ln.Contains("title") || ln.Contains("antet") || ln.Contains("cartouche")) return false;
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static string EffName(Transaction tx, BlockReference br)
        {
            try { return ((BlockTableRecord)tx.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead)).Name; }
            catch (System.Exception) { return br.Name; }
        }

        static string VisState(BlockReference br)
        {
            if (!br.IsDynamicBlock) return null;
            try
            {
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                {
                    string pn = TrayKesif.Norm(p.PropertyName);
                    if (pn.Contains("visibility") || pn.Contains("gorunur") || pn == "gosterim" || pn == "state")
                    {
                        string v = Convert.ToString(p.Value);
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                    }
                }
            }
            catch (System.Exception) { }
            return null;
        }

        static string BlockKey(Transaction tx, BlockReference br, bool withVis)
        {
            string n = EffName(tx, br);
            if (!withVis) return n;
            string v = VisState(br);
            return string.IsNullOrEmpty(v) ? n : n + " [" + v + "]";
        }

        static string CleanLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = Regex.Replace(raw, @"\\[PpCcHhFfTtQqWwAaVvOoKk]|\\f[^;]*;|\\H[\d.]+;|\\S[^;]*;|[{}]", " ");
            s = Regex.Replace(s, @"\\[^\\]*;", " ");
            s = s.Replace("\\P", " ").Replace("\r", " ").Replace("\n", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length == 0 || s.Length > 60) return null;
            return s;
        }

        static string ExportPath(Document doc)
        {
            bool saved = Path.IsPathRooted(doc.Name) && File.Exists(doc.Name);
            string name = Path.GetFileNameWithoutExtension(doc.Name);
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "metraj");
            string dir = saved ? Path.GetDirectoryName(doc.Name) : fallback;
            try { Directory.CreateDirectory(dir); string probe = Path.Combine(dir, ".cadai_w"); File.WriteAllText(probe, ""); File.Delete(probe); }
            catch (System.Exception) { dir = fallback; Directory.CreateDirectory(dir); }
            return Path.Combine(dir, name + "_odasayim_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
        }
    }
}
