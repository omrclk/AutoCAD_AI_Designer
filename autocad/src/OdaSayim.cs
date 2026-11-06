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
        const double SnapTol = 5.0; // base node snap (drawing units)
        const double DefaultGapMm = 1200.0; // door / T-junction tolerance

        static readonly Dictionary<string, double> GapMem = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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

        const string OdaNodKey = "CADAI_ODA";
        const string WallLayersRec = "WALL_LAYERS";

        /// <summary>Optional wall-layer allow-list and exclude-list (NOD XRecord, per drawing).</summary>
        class WallLayerCfg
        {
            public List<string> Include = new List<string>();
            public List<string> Exclude = new List<string>();
            public bool HasInclude { get { return Include != null && Include.Count > 0; } }
            public override string ToString()
            {
                string a = HasInclude ? ("duvar=" + string.Join(",", Include.Take(8))) : "duvar=otomatik";
                if (Exclude != null && Exclude.Count > 0) a += " haric=" + string.Join(",", Exclude.Take(8));
                return a;
            }
        }

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

        /// <summary>Uniform grid index over segment indices for near-O(k) neighbor queries.</summary>
        sealed class SegGrid
        {
            readonly double Cell;
            readonly Dictionary<long, List<int>> Buckets = new Dictionary<long, List<int>>();
            public readonly List<Point2d[]> Segs;
            public SegGrid(List<Point2d[]> segs, double cell)
            {
                Segs = segs; Cell = Math.Max(cell, 1e-3);
                for (int i = 0; i < segs.Count; i++) Add(i);
            }
            static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }
            void Add(int i)
            {
                Point2d a = Segs[i][0], b = Segs[i][1];
                int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / Cell), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / Cell);
                int y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / Cell), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / Cell);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        long k = K(x, y); List<int> L;
                        if (!Buckets.TryGetValue(k, out L)) Buckets[k] = L = new List<int>(4);
                        L.Add(i);
                    }
            }
            public void QueryPoint(Point2d p, double r, HashSet<int> into)
            {
                int x0 = (int)Math.Floor((p.X - r) / Cell), x1 = (int)Math.Floor((p.X + r) / Cell);
                int y0 = (int)Math.Floor((p.Y - r) / Cell), y1 = (int)Math.Floor((p.Y + r) / Cell);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        List<int> L; if (!Buckets.TryGetValue(K(x, y), out L)) continue;
                        foreach (int i in L) into.Add(i);
                    }
            }
            public void QueryBBox(double minx, double miny, double maxx, double maxy, HashSet<int> into)
            {
                int x0 = (int)Math.Floor(minx / Cell), x1 = (int)Math.Floor(maxx / Cell);
                int y0 = (int)Math.Floor(miny / Cell), y1 = (int)Math.Floor(maxy / Cell);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        List<int> L; if (!Buckets.TryGetValue(K(x, y), out L)) continue;
                        foreach (int i in L) into.Add(i);
                    }
            }
        }

        static bool CheckCancel()
        {
            try { return HostApplicationServices.Current.UserBreak(); }
            catch (System.Exception) { return false; }
        }

        static void Prog(Editor ed, ProgressMeter pm, string msg)
        {
            try { if (pm != null) pm.MeterProgress(); } catch (System.Exception) { }
            if (ed != null && msg != null) try { ed.WriteMessage("\n" + msg); } catch (System.Exception) { }
        }

        [CommandMethod("AIDODASAYIM", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void Run() { Do(); }

        [CommandMethod("OSAY", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void RunShort() { Do(); }

        [CommandMethod("AIDODASINIR", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void RunSinir() { DoSinir(); }

        [CommandMethod("OSINIR", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void RunSinirShort() { DoSinir(); }

        /// <summary>Headless perf check: synthetic ~10k-entity plan + DetectRooms timing.</summary>
        [CommandMethod("OSINIRBENCH", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void RunBench() { DoBench(); }


        const string SinirLayer = "A_ODA_SINIR";
        const string SinirRegApp = "CADAI_ODA";

        static void DoSinir()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            List<DBObjectCollection> traced = new List<DBObjectCollection>();
            try
            {
                PromptKeywordOptions mo = new PromptKeywordOptions("\nPlan\u0131 se\u00e7in (pencere) veya [Ayarlar] <Se\u00e7im>: ") { AllowNone = true };
                mo.Keywords.Add("Secim", "Secim", "Se\u00e7im");
                mo.Keywords.Add("Ayarlar", "Ayarlar", "Ayarlar");
                mo.Keywords.Default = "Secim"; mo.AppendKeywordsToMessage = true;
                PromptResult mr = ed.GetKeywords(mo);
                if (mr.Status == PromptStatus.Cancel) return;
                string mode = mr.Status == PromptStatus.OK ? NormKey(mr.StringResult) : "secim";
                bool force = mode == "ayarlar" || mode == "ayarlari" || mode == "ayar";

                MahalCfg cfg;
                if (!AskMahalCfg(ed, db, doc, force, out cfg)) return;
                double gap = ResolveGap(ed, db, doc, force);
                if (!AskWallLayerCfg(ed, db, doc, force)) return;

                PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nPlan\u0131 se\u00e7in (oda etiketleri, \u00e7izgiler): " });
                if (sr.Status != PromptStatus.OK) return;
                ObjectId[] ids = sr.Value.GetObjectIds();

                int drawn = 0, skipped = 0;
                List<string> missing;
                List<string> drawnNames = new List<string>();
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    List<Room> rooms;
                    DetectRooms(ed, tx, db, ids, cfg, gap, traced, out rooms, out missing);
                    EnsureSinirLayer(tx, db);
                    BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    List<List<Point2d>> existing = ExistingClosed(tx, db, ids);
                    HashSet<string> existingNames = ExistingSinirNames(tx, db);
                    double nodeTol = Math.Max(SnapTol, Math.Min(gap * 0.05, 50 * ArmaturCommands.MmPublic(db)));
                    foreach (Room r in rooms)
                    {
                        if ((!string.IsNullOrEmpty(r.Name) && existingNames.Contains(r.Name)) || HasSameBoundary(existing, r, nodeTol)) { skipped++; continue; }
                        Polyline pl = new Polyline();
                        pl.SetDatabaseDefaults(db);
                        pl.Layer = SinirLayer;
                        for (int i = 0; i < r.Poly.Count; i++) pl.AddVertexAt(i, r.Poly[i], 0, 0, 0);
                        pl.Closed = true;
                        space.AppendEntity(pl); tx.AddNewlyCreatedDBObject(pl, true);
                        SetRoomXData(tx, db, pl, r.Name ?? "");
                        drawn++; drawnNames.Add(r.Name ?? ("Oda?"));
                        existing.Add(r.Poly);
                    }
                    tx.Commit();
                }
                string msg = "Oda s\u0131n\u0131r\u0131: " + drawn + " polyline \u00e7izildi"
                    + (skipped > 0 ? ", " + skipped + " atland\u0131 (mevcut)" : "")
                    + " (katman " + SinirLayer + ", bo\u015fluk " + Fmm(gap, db) + " mm).";
                if (drawnNames.Count > 0) msg += "\n  " + string.Join(", ", drawnNames.Take(20));
                if (missing != null && missing.Count > 0) msg += "\n  Bulunamayan: " + string.Join(", ", missing.Take(12));
                ed.WriteMessage("\n" + msg + "\n");
                try { Metraj.Raise(msg); } catch (System.Exception) { }
            }
            catch (System.Exception ex) { ed.WriteMessage("\nOda s\u0131n\u0131r\u0131 hatas\u0131: " + ex.Message + "\n"); }
            finally { foreach (DBObjectCollection col in traced) foreach (DBObject o in col) { try { o.Dispose(); } catch (System.Exception) { } } }
        }

        static void EnsureSinirLayer(Transaction tx, Database db)
        {
            LayerTable lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(SinirLayer)) return;
            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord { Name = SinirLayer };
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 3); // green
            lt.Add(ltr); tx.AddNewlyCreatedDBObject(ltr, true);
        }

        static void SetRoomXData(Transaction tx, Database db, Entity ent, string name)
        {
            try
            {
                RegAppTable rat = (RegAppTable)tx.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has(SinirRegApp))
                {
                    rat.UpgradeOpen();
                    RegAppTableRecord r = new RegAppTableRecord { Name = SinirRegApp };
                    rat.Add(r); tx.AddNewlyCreatedDBObject(r, true);
                }
                ResultBuffer rb = new ResultBuffer(
                    new TypedValue(1001, SinirRegApp),
                    new TypedValue(1000, name ?? ""));
                ent.XData = rb;
            }
            catch (System.Exception) { }
        }

        static HashSet<string> ExistingSinirNames(Transaction tx, Database db)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (id.ObjectClass.DxfName != "LWPOLYLINE" && id.ObjectClass.DxfName != "POLYLINE") continue;
                Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null || !string.Equals(e.Layer, SinirLayer, StringComparison.OrdinalIgnoreCase)) continue;
                ResultBuffer rb = e.GetXDataForApplication(SinirRegApp);
                if (rb == null) continue;
                foreach (TypedValue tv in rb) if (tv.TypeCode == 1000 && tv.Value != null) { names.Add(Convert.ToString(tv.Value)); break; }
            }
            return names;
        }

        static List<List<Point2d>> ExistingClosed(Transaction tx, Database db, ObjectId[] sel)
        {
            List<List<Point2d>> r = new List<List<Point2d>>();
            HashSet<ObjectId> seen = new HashSet<ObjectId>();
            Action<ObjectId> add = id =>
            {
                if (!seen.Add(id)) return;
                Curve c = tx.GetObject(id, OpenMode.ForRead) as Curve;
                if (c == null || !ArmaturCommands.IsClosedPublic(c)) return;
                List<Point2d> poly = ArmaturCommands.SamplePublic(c);
                if (poly.Count >= 3) r.Add(poly);
            };
            foreach (ObjectId id in sel) add(id);
            // also current space closed polys on sinir layer
            BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (id.ObjectClass.DxfName != "LWPOLYLINE" && id.ObjectClass.DxfName != "POLYLINE") continue;
                Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null) continue;
                if (!string.Equals(e.Layer, SinirLayer, StringComparison.OrdinalIgnoreCase)) continue;
                add(id);
            }
            return r;
        }

        static bool HasSameBoundary(List<List<Point2d>> existing, Room room, double tol)
        {
            Point2d c = Centroid(room.Poly);
            foreach (List<Point2d> ex in existing)
            {
                double a = AbsArea(ex);
                if (Math.Abs(a - room.Area) / Math.Max(a, room.Area) > 0.08) continue;
                if (ArmaturGeo.Inside(ex, c) && ArmaturGeo.Inside(room.Poly, Centroid(ex))) return true;
            }
            return false;
        }

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
                double gap = ResolveGap(ed, db, doc, false);
                if (!AskWallLayerCfg(ed, db, doc, false)) return;

                PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nPlan\u0131 se\u00e7in (oda etiketleri, \u00e7izgiler, bloklar): " });
                if (sr.Status != PromptStatus.OK) return;
                ObjectId[] ids = sr.Value.GetObjectIds();

                bool withVis = false;

                string path, summary;
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    List<Room> rooms; List<Hit> hits; List<string> missing;
                    BuildFromSelection(ed, tx, db, ids, withVis, cfg, gap, traced, out rooms, out hits, out missing);
                    if (rooms.Count == 0)
                    {
                        ed.WriteMessage("\nOda bulunamad\u0131 (duvar bo\u015fluklar\u0131 / etiket). Bo\u015fluk tolerans\u0131=" + Fmm(gap, db) + " mm. Ayarlar ile art\u0131r\u0131n.\n");
                        if (missing != null && missing.Count > 0) ed.WriteMessage("  Bulunamayan: " + string.Join(", ", missing.Take(12)) + "\n");
                        return;
                    }
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
            double gap = ResolveGap(ed, db, doc, true);
            if (!AskWallLayerCfg(ed, db, doc, true)) return false;

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
            bool withVis, MahalCfg cfg, double gap, List<DBObjectCollection> traced, out List<Room> rooms, out List<Hit> hits, out List<string> missing)
        {
            List<BlockReference> blocks;
            HashSet<ObjectId> labelIds;
            DetectRooms(ed, tx, db, ids, cfg, gap, traced, out rooms, out missing, out blocks, out labelIds);
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

        static void DetectRooms(Editor ed, Transaction tx, Database db, ObjectId[] ids, MahalCfg cfg, double gap,
            List<DBObjectCollection> traced, out List<Room> rooms, out List<string> missing)
        {
            List<BlockReference> blocks; HashSet<ObjectId> labelIds;
            DetectRooms(ed, tx, db, ids, cfg, gap, traced, out rooms, out missing, out blocks, out labelIds);
        }

        static void DetectRooms(Editor ed, Transaction tx, Database db, ObjectId[] ids, MahalCfg cfg, double gap,
            List<DBObjectCollection> traced, out List<Room> rooms, out List<string> missing,
            out List<BlockReference> blocks, out HashSet<ObjectId> labelIds)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProgressMeter pm = null;
            try
            {
                pm = new ProgressMeter();
                pm.Start("Oda tespiti");
                pm.SetLimit(6);
            }
            catch (System.Exception) { pm = null; }

            List<ObjectId> closedPl = new List<ObjectId>();
            List<object> segs = new List<object>();
            List<Label> labels = new List<Label>();
            blocks = new List<BlockReference>();
            labelIds = new HashSet<ObjectId>();
            double minLen = Math.Max(1.0, ArmaturCommands.MmPublic(db)); // skip <~1mm scraps
            int nWall = 0, nSkip = 0, nIns = 0, nAxis = 0, nLayEx = 0, nNotAllow = 0;
            WallLayerCfg wallCfg = LoadWallLayers(tx, db);

            Prog(ed, pm, "Tarama: " + ids.Length + " nesne...");
            if (CheckCancel()) { rooms = new List<Room>(); missing = new List<string>(); if (pm != null) pm.Stop(); return; }

            foreach (ObjectId id in ids)
            {
                if (CheckCancel()) break;
                string dxf = id.ObjectClass.DxfName;
                // early reject: hatches, dims, solids, leaders, etc.
                if (dxf != "LINE" && dxf != "ARC" && dxf != "LWPOLYLINE" && dxf != "POLYLINE"
                    && dxf != "INSERT" && dxf != "TEXT" && dxf != "MTEXT" && dxf != "CIRCLE")
                { nSkip++; continue; }

                Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                if (dxf == "INSERT")
                {
                    nIns++;
                    BlockReference br = ent as BlockReference;
                    if (br == null) continue;
                    string bn = EffName(tx, br);
                    bool pinned = cfg != null && !string.IsNullOrEmpty(cfg.BlockName);
                    string mahal; double pri;
                    bool maybeLabel = pinned
                        ? string.Equals(bn, cfg.BlockName, StringComparison.OrdinalIgnoreCase)
                        : (br.AttributeCollection != null && br.AttributeCollection.Count > 0);
                    if (maybeLabel)
                    {
                        if (pinned)
                        {
                            labelIds.Add(id);
                            if (TryMahalLabel(tx, br, cfg, out mahal, out pri) && mahal != null)
                                labels.Add(new Label { P = new Point2d(br.Position.X, br.Position.Y), Name = mahal, Pri = 10000 + pri, BlockId = id, BlockName = bn });
                        }
                        else if (TryMahalLabel(tx, br, null, out mahal, out pri) && mahal != null)
                        {
                            labels.Add(new Label { P = new Point2d(br.Position.X, br.Position.Y), Name = mahal, Pri = 10000 + pri, BlockId = id, BlockName = bn });
                            labelIds.Add(id);
                        }
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
                else if (dxf == "CIRCLE")
                {
                    Curve c = ent as Curve;
                    if (c != null && ArmaturCommands.IsClosedPublic(c) && !string.Equals(ent.Layer, SinirLayer, StringComparison.OrdinalIgnoreCase))
                        closedPl.Add(id);
                }
                else if (dxf == "LINE" || dxf == "ARC" || dxf == "LWPOLYLINE" || dxf == "POLYLINE")
                {
                    if (string.Equals(ent.Layer, SinirLayer, StringComparison.OrdinalIgnoreCase)) continue;
                    int skipWhy;
                    if (SkipWallEntity(tx, db, ent, wallCfg, out skipWhy))
                    {
                        if (skipWhy == 1) nAxis++;
                        else if (skipWhy == 2) nLayEx++;
                        else if (skipWhy == 3) nNotAllow++;
                        continue;
                    }
                    Curve c = ent as Curve;
                    if (c != null && ArmaturCommands.IsClosedPublic(c)) closedPl.Add(id);
                    int before = segs.Count;
                    CollectEdges(ent, segs, minLen);
                    nWall += segs.Count - before;
                }
            }

            Prog(ed, pm, "Duvar: " + segs.Count + " segment, etiket: " + labels.Count
                + " (dxf-atlanan " + nSkip + ", aks " + nAxis + ", haric " + nLayEx + ", allow-disi " + nNotAllow
                + ", " + sw.ElapsedMilliseconds + " ms)");
            if (CheckCancel()) { rooms = new List<Room>(); missing = new List<string>(); if (pm != null) pm.Stop(); return; }

            rooms = new List<Room>();
            missing = new List<string>();
            foreach (ObjectId id in closedPl)
            {
                Curve c = tx.GetObject(id, OpenMode.ForRead) as Curve;
                List<Point2d> poly = ArmaturCommands.SamplePublic(c);
                if (poly.Count >= 3) AddUniqueRoom(rooms, poly);
            }

            double nodeTol = Math.Max(SnapTol, Math.Min(gap * 0.05, 50 * ArmaturCommands.MmPublic(db)));
            long t0 = sw.ElapsedMilliseconds;
            List<object> closedSegs = CloseGaps(segs, gap, ed);
            Prog(ed, pm, "Bo\u015fluk kapatma: " + closedSegs.Count + " seg (" + (sw.ElapsedMilliseconds - t0) + " ms)");
            if (CheckCancel()) { if (pm != null) pm.Stop(); return; }

            t0 = sw.ElapsedMilliseconds;
            foreach (List<Point2d> f in FindFaces(closedSegs, nodeTol, ed)) AddUniqueRoom(rooms, f);
            Prog(ed, pm, "Y\u00fczler: " + rooms.Count + " oda aday\u0131 (" + (sw.ElapsedMilliseconds - t0) + " ms)");
            if (CheckCancel()) { if (pm != null) pm.Stop(); return; }

            // TraceBoundary only for labels still outside — hard cap
            const int MaxTrace = 40;
            int tracedN = 0;
            object oldGap = null;
            t0 = sw.ElapsedMilliseconds;
            try
            {
                try { oldGap = Application.GetSystemVariable("HPGAPTOL"); Application.SetSystemVariable("HPGAPTOL", gap); } catch (System.Exception) { oldGap = null; }
                foreach (Label lb in labels.OrderByDescending(l => l.Pri))
                {
                    if (tracedN >= MaxTrace || CheckCancel()) break;
                    if (rooms.Any(r => ArmaturGeo.Inside(r.Poly, lb.P))) continue;
                    Curve best = TraceAt(ed, new Point3d(lb.P.X, lb.P.Y, 0), traced);
                    tracedN++;
                    if (best == null) continue;
                    List<Point2d> poly = ArmaturCommands.SamplePublic(best);
                    if (poly.Count >= 3) AddUniqueRoom(rooms, poly);
                }
            }
            finally
            {
                if (oldGap != null) try { Application.SetSystemVariable("HPGAPTOL", oldGap); } catch (System.Exception) { }
            }
            Prog(ed, pm, "TraceBoundary: " + tracedN + " (" + (sw.ElapsedMilliseconds - t0) + " ms)");

            // ray-cast fallback capped
            const int MaxRay = 30;
            int rayN = 0;
            t0 = sw.ElapsedMilliseconds;
            List<Point2d[]> segArr = closedSegs.Select(o => (Point2d[])o).ToList();
            SegGrid rayGrid = segArr.Count > 0 ? new SegGrid(segArr, Math.Max(gap, 500 * ArmaturCommands.MmPublic(db))) : null;
            foreach (Label lb in labels.OrderByDescending(l => l.Pri))
            {
                if (rayN >= MaxRay || CheckCancel()) break;
                if (rooms.Any(r => ArmaturGeo.Inside(r.Poly, lb.P))) continue;
                List<Point2d> poly = RoomFromRays(lb.P, segArr, Math.Max(gap * 20, 5000 * ArmaturCommands.MmPublic(db)), rayGrid);
                rayN++;
                if (poly != null && poly.Count >= 3) AddUniqueRoom(rooms, poly);
            }
            Prog(ed, pm, "I\u015f\u0131n: " + rayN + " (" + (sw.ElapsedMilliseconds - t0) + " ms)");

            rooms = FilterOuter(rooms);
            rooms = MergeOverlapping(rooms);
            HashSet<ObjectId> usedLabels;
            NameRooms(rooms, labels, out usedLabels);
            foreach (ObjectId id in usedLabels) labelIds.Add(id);
            foreach (Label lb in labels)
                if (!string.IsNullOrEmpty(lb.Name) && !rooms.Any(r => ArmaturGeo.Inside(r.Poly, lb.P)))
                    if (!missing.Contains(lb.Name)) missing.Add(lb.Name);
            rooms = rooms.OrderBy(r => r.Area).ToList();

            if (pm != null) try { pm.Stop(); } catch (System.Exception) { }
            Prog(ed, null, "Oda tespiti bitti: " + rooms.Count + " oda, " + sw.ElapsedMilliseconds + " ms" + (CheckCancel() ? " (iptal)" : ""));
        }


        static void DoBench()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            List<DBObjectCollection> traced = new List<DBObjectCollection>();
            var swAll = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int nx = 40, ny = 25; // 1000 rooms
                double w = 4000, h = 3000; // mm-ish units
                int noiseTarget = 10000;
                List<ObjectId> ids = new List<ObjectId>();
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    Action<Entity> add = ent =>
                    {
                        space.AppendEntity(ent); tx.AddNewlyCreatedDBObject(ent, true); ids.Add(ent.ObjectId);
                    };
                    for (int j = 0; j <= ny; j++)
                        for (int i = 0; i < nx; i++)
                        {
                            Line ln = new Line(new Point3d(i * w, j * h, 0), new Point3d((i + 1) * w, j * h, 0));
                            add(ln);
                        }
                    for (int i = 0; i <= nx; i++)
                        for (int j = 0; j < ny; j++)
                        {
                            Line ln = new Line(new Point3d(i * w, j * h, 0), new Point3d(i * w, (j + 1) * h, 0));
                            add(ln);
                        }
                    for (int j = 0; j < ny; j++)
                        for (int i = 0; i < nx; i++)
                        {
                            DBText tlab = new DBText();
                            tlab.Position = new Point3d(i * w + w * 0.5, j * h + h * 0.5, 0);
                            tlab.Height = 200;
                            tlab.TextString = "ODA-" + (j * nx + i + 1).ToString("0000");
                            add(tlab);
                        }
                    int n = ids.Count;
                    int k = 0;
                    while (n + k < noiseTarget)
                    {
                        double x = (k % (nx + 1)) * w + (k % 17) * 11;
                        double y = ((k / (nx + 1)) % (ny + 1)) * h + (k % 13) * 7;
                        int kind = k % 7;
                        if (kind == 0) { Line scrap = new Line(new Point3d(x, y, 0), new Point3d(x + 0.2, y + 0.2, 0)); add(scrap); }
                        else if (kind == 1) { Circle c = new Circle(new Point3d(x + 50, y + 50, 0), Vector3d.ZAxis, 30); add(c); }
                        else if (kind == 2) { Arc a = new Arc(new Point3d(x, y, 0), 80, 0, 1.2); add(a); }
                        else if (kind == 3)
                        {
                            DBText junk = new DBText(); junk.Position = new Point3d(x, y, 0); junk.Height = 50; junk.TextString = "#" + k; add(junk);
                        }
                        else if (kind == 4)
                        {
                            Line noise = new Line(new Point3d(x, y, 0), new Point3d(x + 120, y + 40, 0)); add(noise);
                        }
                        else if (kind == 5)
                        {
                            Polyline pl = new Polyline();
                            pl.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
                            pl.AddVertexAt(1, new Point2d(x + 90, y), 0, 0, 0);
                            pl.AddVertexAt(2, new Point2d(x + 90, y + 60), 0, 0, 0);
                            add(pl);
                        }
                        else
                        {
                            Line noise2 = new Line(new Point3d(x + 10, y + 10, 0), new Point3d(x + 200, y + 10, 0)); add(noise2);
                        }
                        k++;
                    }
                    // long axis (AKS) lines crossing many rooms — auto-excluded
                    try
                    {
                        try { db.LoadLineTypeFile("CENTER", "acad.lin"); } catch (System.Exception) { }
                        LayerTable ltAbs = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForWrite);
                        if (!ltAbs.Has("A-AKS"))
                        {
                            LayerTableRecord ltr = new LayerTableRecord { Name = "A-AKS" };
                            try
                            {
                                LinetypeTable ltt = (LinetypeTable)tx.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                                if (ltt.Has("CENTER")) ltr.LinetypeObjectId = ltt["CENTER"];
                            }
                            catch (System.Exception) { }
                            ltAbs.Add(ltr); tx.AddNewlyCreatedDBObject(ltr, true);
                        }
                    }
                    catch (System.Exception) { }
                    for (int ai = 0; ai <= nx; ai += 5)
                    {
                        Line aks = new Line(new Point3d(ai * w, -h, 0), new Point3d(ai * w, (ny + 1) * h, 0));
                        aks.Layer = "A-AKS";
                        try { aks.Linetype = "CENTER"; } catch (System.Exception) { }
                        add(aks);
                    }
                    for (int aj = 0; aj <= ny; aj += 5)
                    {
                        Line aks = new Line(new Point3d(-w, aj * h, 0), new Point3d((nx + 1) * w, aj * h, 0));
                        aks.Layer = "A-AKS";
                        try { aks.Linetype = "CENTER"; } catch (System.Exception) { }
                        add(aks);
                    }
                    tx.Commit();
                }
                ed.WriteMessage("\nOSINIRBENCH: " + ids.Count + " nesne olusturuldu (" + swAll.ElapsedMilliseconds + " ms). DetectRooms...\n");
                double gap = DefaultGapMm * ArmaturCommands.MmPublic(db);
                List<Room> rooms; List<string> missing;
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    DetectRooms(ed, tx, db, ids.ToArray(), null, gap, traced, out rooms, out missing);
                    tx.Commit();
                }
                ed.WriteMessage("\nOSINIRBENCH SONUC: nesne=" + ids.Count + " oda=" + rooms.Count
                    + " toplam_ms=" + swAll.ElapsedMilliseconds + "\n");

                // focused 2x2 rooms + crossing AKS (must not shatter rooms)
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    List<ObjectId> ids2 = new List<ObjectId>();
                    Action<Entity> add2 = ent => { space.AppendEntity(ent); tx.AddNewlyCreatedDBObject(ent, true); ids2.Add(ent.ObjectId); };
                    double s = 5000;
                    double ox = 100000, oy = 100000;
                    add2(new Line(new Point3d(ox, oy, 0), new Point3d(ox + 2 * s, oy, 0)));
                    add2(new Line(new Point3d(ox, oy + s, 0), new Point3d(ox + 2 * s, oy + s, 0)));
                    add2(new Line(new Point3d(ox, oy + 2 * s, 0), new Point3d(ox + 2 * s, oy + 2 * s, 0)));
                    add2(new Line(new Point3d(ox, oy, 0), new Point3d(ox, oy + 2 * s, 0)));
                    add2(new Line(new Point3d(ox + s, oy, 0), new Point3d(ox + s, oy + 2 * s, 0)));
                    add2(new Line(new Point3d(ox + 2 * s, oy, 0), new Point3d(ox + 2 * s, oy + 2 * s, 0)));
                    for (int i = 0; i < 4; i++)
                    {
                        DBText lab = new DBText();
                        lab.Position = new Point3d(ox + (i % 2) * s + s * 0.5, oy + (i / 2) * s + s * 0.5, 0);
                        lab.Height = 200; lab.TextString = "R" + (i + 1); add2(lab);
                    }
                    Line ax1 = new Line(new Point3d(ox - s, oy + s, 0), new Point3d(ox + 3 * s, oy + s, 0));
                    ax1.Layer = "A-AKS"; try { ax1.Linetype = "CENTER"; } catch (System.Exception) { } add2(ax1);
                    Line ax2 = new Line(new Point3d(ox + s, oy - s, 0), new Point3d(ox + s, oy + 3 * s, 0));
                    ax2.Layer = "A-AKS"; try { ax2.Linetype = "CENTER"; } catch (System.Exception) { } add2(ax2);
                    List<Room> rooms2; List<string> miss2;
                    DetectRooms(ed, tx, db, ids2.ToArray(), null, gap, traced, out rooms2, out miss2);
                    ed.WriteMessage("\nOSINIRAKS: 2x2+aks -> oda=" + rooms2.Count + " (beklenen ~4)\n");
                    tx.Commit();
                }
            }
            catch (System.Exception ex) { ed.WriteMessage("\nOSINIRBENCH hata: " + ex.Message + "\n"); }
            finally { foreach (DBObjectCollection col in traced) foreach (DBObject o in col) { try { o.Dispose(); } catch (System.Exception) { } } }
        }


        static bool LayerNameLooksAxis(string layer)
        {
            string n = TrayKesif.Norm(layer ?? "");
            if (n.Length == 0) return false;
            return n.Contains("aks") || n.Contains("axis") || n.Contains("grid") || n.Contains("eksen");
        }

        static bool LinetypeLooksAxis(string lt)
        {
            string n = TrayKesif.Norm(lt ?? "");
            if (n.Length == 0) return false;
            if (n == "continuous" || n == "bylayer" || n == "byblock") return false;
            if (n.Contains("center") || n.Contains("centre")) return true;
            if (n.Contains("dashdot")) return true;
            if (n.Contains("aks") || n.Contains("axis") || n.Contains("eksen") || n.Contains("grid")) return true;
            if (n.Contains("iso04") || n.Contains("iso05") || n.Contains("iso07")) return true;
            if (n.Contains("phantom") || n == "dot" || n.StartsWith("dot")) return true;
            return false;
        }

        static string EffectiveLinetypeName(Transaction tx, Database db, Entity ent)
        {
            try
            {
                string raw = ent.Linetype ?? "";
                if (string.Equals(raw, "ByLayer", StringComparison.OrdinalIgnoreCase)
                    || ent.LinetypeId == db.ByLayerLinetype)
                {
                    LayerTableRecord ltr = (LayerTableRecord)tx.GetObject(ent.LayerId, OpenMode.ForRead);
                    LinetypeTableRecord rec = (LinetypeTableRecord)tx.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead);
                    return rec.Name ?? "";
                }
                if (string.Equals(raw, "ByBlock", StringComparison.OrdinalIgnoreCase)
                    || ent.LinetypeId == db.ByBlockLinetype)
                    return "Continuous";
                if (!string.IsNullOrEmpty(raw)) return raw;
                if (!ent.LinetypeId.IsNull)
                {
                    LinetypeTableRecord rec = (LinetypeTableRecord)tx.GetObject(ent.LinetypeId, OpenMode.ForRead);
                    return rec.Name ?? "";
                }
            }
            catch (System.Exception) { }
            return "Continuous";
        }

        /// <summary>skipWhy: 1=auto aks, 2=hariç list, 3=not in duvar allow-list.</summary>
        static bool SkipWallEntity(Transaction tx, Database db, Entity ent, WallLayerCfg cfg, out int skipWhy)
        {
            skipWhy = 0;
            string layer = ent.Layer ?? "";
            if (cfg != null && cfg.Exclude != null)
            {
                foreach (string x in cfg.Exclude)
                    if (string.Equals(x, layer, StringComparison.OrdinalIgnoreCase)) { skipWhy = 2; return true; }
            }
            if (cfg != null && cfg.HasInclude)
            {
                bool ok = false;
                foreach (string x in cfg.Include)
                    if (string.Equals(x, layer, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                if (!ok) { skipWhy = 3; return true; }
                return false;
            }
            if (LayerNameLooksAxis(layer) && LinetypeLooksAxis(EffectiveLinetypeName(tx, db, ent)))
            { skipWhy = 1; return true; }
            return false;
        }

        static List<string> SplitLayerList(string s)
        {
            List<string> r = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return r;
            foreach (string part in s.Split(new[] { '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string x = part.Trim();
                if (x.Length == 0) continue;
                if (!r.Any(y => string.Equals(y, x, StringComparison.OrdinalIgnoreCase))) r.Add(x);
            }
            return r;
        }

        static WallLayerCfg LoadWallLayers(Transaction tx, Database db)
        {
            WallLayerCfg cfg = new WallLayerCfg();
            try
            {
                DBDictionary nod = (DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(OdaNodKey)) return cfg;
                DBDictionary d = tx.GetObject(nod.GetAt(OdaNodKey), OpenMode.ForRead) as DBDictionary;
                if (d == null || !d.Contains(WallLayersRec)) return cfg;
                Xrecord xr = tx.GetObject(d.GetAt(WallLayersRec), OpenMode.ForRead) as Xrecord;
                if (xr == null || xr.Data == null) return cfg;
                foreach (TypedValue tv in xr.Data)
                {
                    if (tv.TypeCode != (int)DxfCode.Text || tv.Value == null) continue;
                    string s = Convert.ToString(tv.Value) ?? "";
                    if (s.StartsWith("I|", StringComparison.OrdinalIgnoreCase))
                        cfg.Include = SplitLayerList(s.Substring(2));
                    else if (s.StartsWith("E|", StringComparison.OrdinalIgnoreCase))
                        cfg.Exclude = SplitLayerList(s.Substring(2));
                }
            }
            catch (System.Exception) { }
            return cfg;
        }

        static void SaveWallLayers(Database db, WallLayerCfg cfg)
        {
            if (cfg == null) cfg = new WallLayerCfg();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            using (DocumentLock lk = doc != null ? doc.LockDocument() : null)
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                DBDictionary nod = (DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                DBDictionary d;
                if (nod.Contains(OdaNodKey))
                    d = (DBDictionary)tx.GetObject(nod.GetAt(OdaNodKey), OpenMode.ForWrite);
                else
                {
                    d = new DBDictionary();
                    nod.SetAt(OdaNodKey, d);
                    tx.AddNewlyCreatedDBObject(d, true);
                }
                Xrecord xr;
                if (d.Contains(WallLayersRec))
                    xr = (Xrecord)tx.GetObject(d.GetAt(WallLayersRec), OpenMode.ForWrite);
                else
                {
                    xr = new Xrecord();
                    d.SetAt(WallLayersRec, xr);
                    tx.AddNewlyCreatedDBObject(xr, true);
                }
                string inc = "I|" + string.Join("|", cfg.Include ?? new List<string>());
                string exc = "E|" + string.Join("|", cfg.Exclude ?? new List<string>());
                xr.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, inc),
                    new TypedValue((int)DxfCode.Text, exc));
                tx.Commit();
            }
        }

        static HashSet<string> PickLayersFromEntities(Editor ed, string msg)
        {
            HashSet<string> layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = msg });
            if (sr.Status != PromptStatus.OK) return layers;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            using (DocumentLock lk = doc.LockDocument())
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in sr.Value.GetObjectIds())
                {
                    Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e != null && !string.IsNullOrEmpty(e.Layer)) layers.Add(e.Layer);
                }
                tx.Commit();
            }
            return layers;
        }

        /// <summary>Ayarlar: click wall entities for allow-list / hariç; NOD-persisted. force=false only reports.</summary>
        static bool AskWallLayerCfg(Editor ed, Database db, Document doc, bool force)
        {
            WallLayerCfg cfg;
            using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
            {
                cfg = LoadWallLayers(tx, db);
                tx.Commit();
            }
            if (!force)
            {
                ed.WriteMessage("\nDuvar katmanlari (" + cfg + "). Degistirmek icin Ayarlar.\n");
                return true;
            }

            PromptKeywordOptions ko = new PromptKeywordOptions(
                "\nDuvar katmani [Secim/Otomatik] <" + (cfg.HasInclude ? "Secim" : "Otomatik") + ">: ")
            { AllowNone = true };
            ko.Keywords.Add("Secim", "Secim", "Se\u00e7im");
            ko.Keywords.Add("Otomatik", "Otomatik", "Otomatik");
            ko.Keywords.Default = cfg.HasInclude ? "Secim" : "Otomatik";
            ko.AppendKeywordsToMessage = true;
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status == PromptStatus.Cancel) return false;
            string mode = kr.Status == PromptStatus.OK ? NormKey(kr.StringResult) : (cfg.HasInclude ? "secim" : "otomatik");
            if (mode.Contains("oto") || mode == "auto")
            {
                cfg.Include = new List<string>();
                ed.WriteMessage("\nDuvar katmani: otomatik (aks otomatik elenir).\n");
            }
            else
            {
                HashSet<string> picked = PickLayersFromEntities(ed, "\nDuvar ornegi secin (bir/birden fazla nesne): ");
                if (picked.Count == 0)
                    ed.WriteMessage("\nDuvar katmani secilmedi; mevcut ayar korundu (" + cfg + ").\n");
                else
                {
                    cfg.Include = picked.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    ed.WriteMessage("\nDuvar katmanlari: " + string.Join(", ", cfg.Include) + "\n");
                }
            }

            PromptKeywordOptions ho = new PromptKeywordOptions(
                "\nHaric katman [Secim/Yok] <" + ((cfg.Exclude != null && cfg.Exclude.Count > 0) ? "Secim" : "Yok") + ">: ")
            { AllowNone = true };
            ho.Keywords.Add("Secim", "Secim", "Se\u00e7im");
            ho.Keywords.Add("Yok", "Yok", "Yok");
            ho.Keywords.Default = (cfg.Exclude != null && cfg.Exclude.Count > 0) ? "Secim" : "Yok";
            ho.AppendKeywordsToMessage = true;
            PromptResult hr = ed.GetKeywords(ho);
            if (hr.Status == PromptStatus.Cancel) return false;
            string hm = hr.Status == PromptStatus.OK ? NormKey(hr.StringResult) : ((cfg.Exclude != null && cfg.Exclude.Count > 0) ? "secim" : "yok");
            if (hm == "yok" || hm == "none")
            {
                cfg.Exclude = new List<string>();
                ed.WriteMessage("\nHaric katman: yok.\n");
            }
            else
            {
                HashSet<string> picked = PickLayersFromEntities(ed, "\nHaric tutulacak katman ornegi secin: ");
                if (picked.Count == 0)
                    ed.WriteMessage("\nHaric katman secilmedi; mevcut ayar korundu.\n");
                else
                {
                    cfg.Exclude = picked.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    ed.WriteMessage("\nHaric katmanlar: " + string.Join(", ", cfg.Exclude) + "\n");
                }
            }

            try { SaveWallLayers(db, cfg); }
            catch (System.Exception ex) { ed.WriteMessage("\nKatman ayari kaydedilemedi: " + ex.Message + "\n"); }
            ed.WriteMessage("\nKaydedildi (cizim NOD): " + cfg + "\n");
            return true;
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

        static void CollectEdges(Entity ent, List<object> segs, double minLen = 0)
        {
            Line ln = ent as Line;
            if (ln != null)
            {
                Point2d a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y), b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                if (a.GetDistanceTo(b) >= minLen) segs.Add(new[] { a, b });
                return;
            }
            Polyline pl = ent as Polyline;
            if (pl != null)
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n - 1; i++)
                {
                    Point2d a = pl.GetPoint2dAt(i), b = pl.GetPoint2dAt(i + 1);
                    if (a.GetDistanceTo(b) >= minLen) segs.Add(new[] { a, b });
                }
                if (pl.Closed && n > 2)
                {
                    Point2d a = pl.GetPoint2dAt(n - 1), b = pl.GetPoint2dAt(0);
                    if (a.GetDistanceTo(b) >= minLen) segs.Add(new[] { a, b });
                }
                return;
            }
            // skip heavy spline resampling for detection walls
            Arc arc = ent as Arc;
            if (arc != null)
            {
                Point2d prev = default(Point2d); bool has = false;
                int steps = 8;
                for (int i = 0; i <= steps; i++)
                {
                    double a0 = arc.StartAngle, a1 = arc.EndAngle;
                    if (a1 < a0) a1 += 2 * Math.PI;
                    double a = a0 + (a1 - a0) * i / steps;
                    Point3d q = arc.Center + new Vector3d(Math.Cos(a), Math.Sin(a), 0) * arc.Radius;
                    Point2d p = new Point2d(q.X, q.Y);
                    if (has && prev.GetDistanceTo(p) >= minLen) segs.Add(new[] { prev, p });
                    prev = p; has = true;
                }
            }
        }

        static double ResolveGap(Editor ed, Database db, Document doc, bool force)
        {
            double def = DefaultGapMm * ArmaturCommands.MmPublic(db);
            string key = MemKey(doc);
            double g;
            if (!force && GapMem.TryGetValue(key, out g) && g > 0)
            {
                ed.WriteMessage("\nBo\u015fluk tolerans\u0131 (bellek): " + Fmm(g, db) + " mm.\n");
                return g;
            }
            if (!force) { GapMem[key] = def; return def; }
            double mm = DefaultGapMm;
            if (GapMem.TryGetValue(key, out g) && g > 0) mm = g / Math.Max(1e-12, ArmaturCommands.MmPublic(db));
            PromptDoubleOptions po = new PromptDoubleOptions("\nDuvar bo\u015fluk / kap\u0131 tolerans\u0131 (mm)") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = mm, UseDefaultValue = true };
            PromptDoubleResult pr = ed.GetDouble(po);
            if (pr.Status == PromptStatus.Cancel) { /* keep previous */ return GapMem.TryGetValue(key, out g) ? g : def; }
            if (pr.Status == PromptStatus.OK) mm = pr.Value;
            g = mm * ArmaturCommands.MmPublic(db);
            GapMem[key] = g;
            ed.WriteMessage("\nBo\u015fluk tolerans\u0131: " + Fmm(g, db) + " mm.\n");
            return g;
        }

        static string Fmm(double units, Database db)
        {
            double mm = units / Math.Max(1e-12, ArmaturCommands.MmPublic(db));
            return mm.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Close door openings / short T-junctions / near-miss crossings within gap (spatial index).</summary>
        static List<object> CloseGaps(List<object> segs, double gap, Editor ed = null)
        {
            List<Point2d[]> S = segs.Select(o => (Point2d[])o).Where(ab => ab[0].GetDistanceTo(ab[1]) > 1e-6).Select(ab => new[] { ab[0], ab[1] }).ToList();
            if (S.Count == 0) return new List<object>();
            double tol = Math.Max(gap, SnapTol);
            double near = Math.Max(SnapTol, Math.Min(tol * 0.08, 100));
            double tJoin = Math.Min(tol, Math.Max(near * 4, tol * 0.55));
            double cell = Math.Max(tol, 1);
            HashSet<int> cand = new HashSet<int>();

            // A) extend
            SegGrid g = new SegGrid(S, cell);
            for (int i = 0; i < S.Count; i++)
            {
                if ((i & 255) == 0 && CheckCancel()) break;
                for (int e = 0; e < 2; e++)
                {
                    Point2d p = S[i][e], o = S[i][1 - e];
                    Vector2d dir = p - o; double len = dir.Length; if (len < 1e-9) continue;
                    dir = dir / len;
                    Point2d far = p + dir * tJoin;
                    cand.Clear();
                    g.QueryBBox(Math.Min(p.X, far.X) - 1, Math.Min(p.Y, far.Y) - 1, Math.Max(p.X, far.X) + 1, Math.Max(p.Y, far.Y) + 1, cand);
                    double bestT = tJoin + 1; Point2d bestP = p;
                    foreach (int j in cand)
                    {
                        if (j == i) continue;
                        Point2d a = S[j][0], b = S[j][1];
                        Vector2d s = b - a; double den = dir.X * s.Y - dir.Y * s.X;
                        if (Math.Abs(den) < 1e-12) continue;
                        double sl = s.Length; if (sl < 1e-12) continue;
                        Vector2d sn = s / sl;
                        if (Math.Abs(dir.X * sn.Y - dir.Y * sn.X) < 0.35) continue;
                        double tt = ((a.X - p.X) * s.Y - (a.Y - p.Y) * s.X) / den;
                        double u = ((a.X - p.X) * dir.Y - (a.Y - p.Y) * dir.X) / den;
                        if (tt < 1e-6 || tt > tJoin || u < -0.02 || u > 1.02) continue;
                        u = Math.Max(0, Math.Min(1, u));
                        Point2d ip = a + s * u;
                        if (tt < bestT) { bestT = tt; bestP = ip; }
                    }
                    if (bestT <= tJoin) S[i][e] = bestP;
                }
            }

            // B) T-junction project
            g = new SegGrid(S, cell);
            for (int i = 0; i < S.Count; i++)
            {
                if ((i & 255) == 0 && CheckCancel()) break;
                Vector2d di = S[i][1] - S[i][0]; double li = di.Length; if (li < 1e-9) continue;
                di = di / li;
                for (int e = 0; e < 2; e++)
                {
                    Point2d p = S[i][e];
                    cand.Clear(); g.QueryPoint(p, tJoin, cand);
                    double best = tJoin; Point2d foot = p; bool hit = false;
                    foreach (int j in cand)
                    {
                        if (j == i) continue;
                        Point2d a = S[j][0], b = S[j][1];
                        Vector2d ab = b - a; double l2 = ab.DotProduct(ab); if (l2 < 1e-12) continue;
                        Vector2d dj = ab / Math.Sqrt(l2);
                        if (Math.Abs(di.X * dj.Y - di.Y * dj.X) < 0.15) continue;
                        double tt = (p - a).DotProduct(ab) / l2;
                        double pad = tJoin / Math.Sqrt(l2);
                        if (tt < -pad || tt > 1 + pad) continue;
                        tt = Math.Max(0, Math.Min(1, tt));
                        Point2d fpt = a + ab * tt;
                        double d = p.GetDistanceTo(fpt);
                        if (d > 1e-9 && d <= best) { best = d; foot = fpt; hit = true; }
                    }
                    if (hit) S[i][e] = foot;
                }
            }

            // C) collinear bridges via endpoint proximity in grid
            g = new SegGrid(S, cell);
            List<Point2d[]> bridges = new List<Point2d[]>();
            HashSet<long> seenBridge = new HashSet<long>();
            for (int i = 0; i < S.Count; i++)
            {
                if ((i & 255) == 0 && CheckCancel()) break;
                Vector2d di = S[i][1] - S[i][0]; double li = di.Length; if (li < 1e-9) continue;
                di = di / li;
                for (int e = 0; e < 2; e++)
                {
                    Point2d p = S[i][e];
                    cand.Clear(); g.QueryPoint(p, tol, cand);
                    foreach (int j in cand)
                    {
                        if (j <= i) continue;
                        Vector2d dj = S[j][1] - S[j][0]; double lj = dj.Length; if (lj < 1e-9) continue;
                        dj = dj / lj;
                        double cross = Math.Abs(di.X * dj.Y - di.Y * dj.X);
                        double dot = di.DotProduct(dj);
                        if (cross > 0.08 || Math.Abs(dot) < 0.95) continue;
                        Vector2d ij = S[j][0] - S[i][0];
                        double lat = Math.Abs(di.X * ij.Y - di.Y * ij.X);
                        if (lat > Math.Max(SnapTol * 2, tol * 0.05)) continue;
                        double bd = tol + 1; Point2d ba = default(Point2d), bb = default(Point2d); bool ok = false;
                        for (int f = 0; f < 2; f++)
                        {
                            double d = p.GetDistanceTo(S[j][f]);
                            if (d > 1e-6 && d < bd) { bd = d; ba = p; bb = S[j][f]; ok = true; }
                        }
                        if (!ok || bd > tol) continue;
                        Vector2d gv = bb - ba; double gl = gv.Length; if (gl < 1e-9) continue;
                        if (Math.Abs((gv / gl).DotProduct(di)) < 0.9) continue;
                        if (SegmentsOverlapCollinear(S[i], S[j], di)) continue;
                        long key = ((long)i << 32) | (uint)j;
                        if (!seenBridge.Add(key)) continue;
                        bridges.Add(new[] { ba, bb });
                    }
                }
            }
            S.AddRange(bridges);

            // D) near-coincident endpoint merge via grid
            g = new SegGrid(S, Math.Max(near * 2, 1));
            for (int i = 0; i < S.Count; i++)
            {
                if ((i & 255) == 0 && CheckCancel()) break;
                for (int e = 0; e < 2; e++)
                {
                    Point2d p = S[i][e];
                    cand.Clear(); g.QueryPoint(p, near, cand);
                    foreach (int j in cand)
                    {
                        if (j == i) continue;
                        for (int f = 0; f < 2; f++)
                        {
                            double d = p.GetDistanceTo(S[j][f]);
                            if (d > 1e-9 && d <= near)
                            {
                                Point2d m = new Point2d((p.X + S[j][f].X) / 2, (p.Y + S[j][f].Y) / 2);
                                S[i][e] = m; S[j][f] = m; p = m;
                            }
                        }
                    }
                }
            }

            return S.Where(ab => ab[0].GetDistanceTo(ab[1]) > 1e-4).Cast<object>().ToList();
        }

        static bool SegmentsOverlapCollinear(Point2d[] a, Point2d[] b, Vector2d dir)
        {
            double a0 = a[0].X * dir.X + a[0].Y * dir.Y, a1 = a[1].X * dir.X + a[1].Y * dir.Y;
            double b0 = b[0].X * dir.X + b[0].Y * dir.Y, b1 = b[1].X * dir.X + b[1].Y * dir.Y;
            double alo = Math.Min(a0, a1), ahi = Math.Max(a0, a1), blo = Math.Min(b0, b1), bhi = Math.Max(b0, b1);
            return !(ahi < blo - 1e-6 || bhi < alo - 1e-6);
        }

        /// <summary>Last-resort room: cast rays from label (optional spatial grid).</summary>
        static List<Point2d> RoomFromRays(Point2d o, List<Point2d[]> segs, double maxR, SegGrid grid = null)
        {
            if (segs.Count == 0 || maxR <= 0) return null;
            int n = 36; List<Point2d> hits = new List<Point2d>(); int escaped = 0;
            HashSet<int> cand = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                double ang = 2 * Math.PI * i / n;
                Vector2d dir = new Vector2d(Math.Cos(ang), Math.Sin(ang));
                double best = maxR; Point2d hp = o + dir * maxR;
                Point2d far = o + dir * maxR;
                IEnumerable<int> js;
                if (grid != null)
                {
                    cand.Clear();
                    grid.QueryBBox(Math.Min(o.X, far.X) - 1, Math.Min(o.Y, far.Y) - 1, Math.Max(o.X, far.X) + 1, Math.Max(o.Y, far.Y) + 1, cand);
                    js = cand;
                }
                else js = Enumerable.Range(0, segs.Count);
                foreach (int ji in js)
                {
                    Point2d[] ab = segs[ji];
                    Vector2d s = ab[1] - ab[0]; double den = dir.X * s.Y - dir.Y * s.X;
                    if (Math.Abs(den) < 1e-12) continue;
                    double tt = ((ab[0].X - o.X) * s.Y - (ab[0].Y - o.Y) * s.X) / den;
                    double u = ((ab[0].X - o.X) * dir.Y - (ab[0].Y - o.Y) * dir.X) / den;
                    if (tt < 1e-4 || tt > best || u < -0.02 || u > 1.02) continue;
                    best = tt; hp = o + dir * tt;
                }
                if (best >= maxR * 0.98) escaped++;
                hits.Add(hp);
            }
            if (escaped > n / 3) return null;
            if (AbsArea(hits) < maxR) return null;
            return hits;
        }

        /// <summary>Split raw segments at crossings / T-joints (spatial index).</summary>
        static List<Point2d[]> Planarize(List<object> segs, double nodeTol, Editor ed = null)
        {
            List<Point2d[]> raw = segs.Select(o => (Point2d[])o).Where(ab => ab[0].GetDistanceTo(ab[1]) >= nodeTol * 0.25).ToList();
            if (raw.Count == 0) return raw;
            List<Point2d> pts = new List<Point2d>();
            // node snap grid
            double ncell = Math.Max(nodeTol, 1e-3);
            Dictionary<long, int> nodeBucket = new Dictionary<long, int>();
            Func<Point2d, int> add = p =>
            {
                int ix = (int)Math.Floor(p.X / ncell), iy = (int)Math.Floor(p.Y / ncell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int bi;
                        if (nodeBucket.TryGetValue(((long)(ix + dx) << 32) ^ (uint)(iy + dy), out bi))
                            if (pts[bi].GetDistanceTo(p) <= nodeTol) return bi;
                    }
                int id = pts.Count; pts.Add(p);
                nodeBucket[((long)ix << 32) ^ (uint)iy] = id;
                return id;
            };
            foreach (Point2d[] ab in raw) { add(ab[0]); add(ab[1]); }

            SegGrid g = new SegGrid(raw, Math.Max(nodeTol * 4, 1));
            HashSet<int> cand = new HashSet<int>();
            for (int i = 0; i < raw.Count; i++)
            {
                if ((i & 255) == 0 && CheckCancel()) break;
                Point2d a = raw[i][0], b = raw[i][1];
                cand.Clear();
                g.QueryBBox(Math.Min(a.X, b.X) - nodeTol, Math.Min(a.Y, b.Y) - nodeTol, Math.Max(a.X, b.X) + nodeTol, Math.Max(a.Y, b.Y) + nodeTol, cand);
                foreach (int j in cand)
                {
                    if (j <= i) continue;
                    Point2d c = raw[j][0], d = raw[j][1];
                    Point2d ip; if (SegIntersect(a, b, c, d, out ip)) add(ip);
                }
            }

            List<Point2d[]> outp = new List<Point2d[]>();
            foreach (Point2d[] ab in raw)
            {
                List<Point2d> mid = new List<Point2d>();
                // only test pts near this segment bbox
                double minx = Math.Min(ab[0].X, ab[1].X) - nodeTol, maxx = Math.Max(ab[0].X, ab[1].X) + nodeTol;
                double miny = Math.Min(ab[0].Y, ab[1].Y) - nodeTol, maxy = Math.Max(ab[0].Y, ab[1].Y) + nodeTol;
                foreach (Point2d p in pts)
                {
                    if (p.X < minx || p.X > maxx || p.Y < miny || p.Y > maxy) continue;
                    if (OnSeg(ab[0], ab[1], p, nodeTol) && p.GetDistanceTo(ab[0]) > nodeTol && p.GetDistanceTo(ab[1]) > nodeTol) mid.Add(p);
                }
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
            if (Math.Abs(den) < 1e-12) return false;
            Vector2d ca = c - a;
            double t = (ca.X * s.Y - ca.Y * s.X) / den, u = (ca.X * r.Y - ca.Y * r.X) / den;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return false;
            ip = a + r * Math.Max(0, Math.Min(1, t)); return true;
        }

        static bool OnSeg(Point2d a, Point2d b, Point2d p, double tol)
        {
            if (p.GetDistanceTo(a) <= tol || p.GetDistanceTo(b) <= tol) return true;
            Vector2d ab = b - a; double l2 = ab.DotProduct(ab); if (l2 < 1e-12) return false;
            double t = (p - a).DotProduct(ab) / l2; if (t < 0 || t > 1) return false;
            return (a + ab * t).GetDistanceTo(p) <= tol;
        }

        static List<List<Point2d>> FindFaces(List<object> segs, double nodeTol, Editor ed = null)
        {
            List<List<Point2d>> faces = new List<List<Point2d>>();
            List<Point2d[]> planar = Planarize(segs, nodeTol, ed);
            if (planar.Count == 0) return faces;
            List<Node> nodes = new List<Node>();
            double ncell = Math.Max(nodeTol, 1e-3);
            Dictionary<long, int> nb = new Dictionary<long, int>();
            Func<Point2d, int> nid = p =>
            {
                int ix = (int)Math.Floor(p.X / ncell), iy = (int)Math.Floor(p.Y / ncell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int bi;
                        if (nb.TryGetValue(((long)(ix + dx) << 32) ^ (uint)(iy + dy), out bi))
                            if (nodes[bi].P.GetDistanceTo(p) <= nodeTol) return bi;
                    }
                int id = nodes.Count; nodes.Add(new Node { P = p, Id = id });
                nb[((long)ix << 32) ^ (uint)iy] = id;
                return id;
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
                if (a < nodeTol * nodeTol) continue;
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
