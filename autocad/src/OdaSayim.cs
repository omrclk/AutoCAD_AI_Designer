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
    /// <summary>Count block inserts per room (closed polyline) into an Excel matrix: rows = blocks, columns = rooms + totals.</summary>
    public class OdaSayimCommands
    {
        const double PosTol = 1.0; // drawing units — same name + same XY within tol counted once
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 64000000 };

        class Room
        {
            public string Name; public List<Point2d> Poly; public double Area; public ObjectId Id;
        }

        class Hit
        {
            public string Key; public Point2d P; public int Room;
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
                PromptKeywordOptions ao = new PromptKeywordOptions("\nOda alan\u0131 [Polyline/Icnokta] <Polyline>: ", "Polyline Icnokta") { AllowNone = true };
                PromptResult ar = ed.GetKeywords(ao);
                if (ar.Status != PromptStatus.OK && ar.Status != PromptStatus.None) return;
                bool pick = ar.Status == PromptStatus.OK && ar.StringResult == "Icnokta";

                List<ObjectId> roomIds = new List<ObjectId>();
                List<Curve> roomCurves = new List<Curve>();
                if (pick)
                {
                    while (true)
                    {
                        PromptPointResult pr = ed.GetPoint(new PromptPointOptions("\nOda i\u00e7inde bir nokta (Enter = bitti): ") { AllowNone = true });
                        if (pr.Status != PromptStatus.OK) break;
                        DBObjectCollection col = null;
                        try { col = ed.TraceBoundary(pr.Value, true); } catch (System.Exception) { }
                        Curve best = null;
                        if (col != null) { traced.Add(col); foreach (DBObject o in col) { Curve cv = o as Curve; if (cv != null && (best == null || cv.Area > best.Area)) best = cv; } }
                        if (best == null)
                        {
                            ObjectId cid = ArmaturCommands.EnclosingPublic(db, pr.Value);
                            if (cid.IsNull) { ed.WriteMessage("\nKapal\u0131 s\u0131n\u0131r bulunamad\u0131."); continue; }
                            roomIds.Add(cid); ed.WriteMessage("\nS\u0131n\u0131r: i\u00e7eren polyline kullan\u0131ld\u0131.");
                        }
                        else { roomCurves.Add(best); ed.WriteMessage("\nS\u0131n\u0131r bulundu."); }
                    }
                    if (roomCurves.Count == 0 && roomIds.Count == 0) return;
                }
                else
                {
                    PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nKapal\u0131 oda polyline'lar\u0131n\u0131 se\u00e7in: " },
                        new SelectionFilter(new[] { new TypedValue(-4, "<OR"), new TypedValue(0, "LWPOLYLINE"), new TypedValue(0, "POLYLINE"), new TypedValue(0, "CIRCLE"), new TypedValue(-4, "OR>") }));
                    if (sr.Status != PromptStatus.OK) return;
                    roomIds.AddRange(sr.Value.GetObjectIds());
                }

                ObjectId buildingId = ObjectId.Null;
                PromptEntityOptions beo = new PromptEntityOptions("\nBina d\u0131\u015f s\u0131n\u0131r\u0131 polyline'\u0131 (Enter = yok): ") { AllowNone = true };
                beo.SetRejectMessage("\nE\u011fri de\u011fil."); beo.AddAllowedClass(typeof(Curve), false);
                PromptEntityResult ber = ed.GetEntity(beo);
                if (ber.Status == PromptStatus.OK) buildingId = ber.ObjectId;
                else if (ber.Status != PromptStatus.None) return;

                PromptSelectionOptions bso = new PromptSelectionOptions { MessageForAdding = "\nSay\u0131lacak bloklar (Enter = modeldeki t\u00fcm\u00fc): ", AllowDuplicates = false };
                PromptSelectionResult bsr = ed.GetSelection(bso, new SelectionFilter(new[] { new TypedValue(0, "INSERT") }));
                HashSet<ObjectId> onlyBlocks = null;
                if (bsr.Status == PromptStatus.OK) onlyBlocks = new HashSet<ObjectId>(bsr.Value.GetObjectIds());
                else if (bsr.Status != PromptStatus.Error && bsr.Status != PromptStatus.Cancel && bsr.Status != PromptStatus.None)
                { /* GetSelection returns Error when empty Enter in some builds; treat as all */ }
                else if (bsr.Status == PromptStatus.Cancel) return;
                // None / Error with no pick = all inserts

                PromptResult vr = ed.GetKeywords(new PromptKeywordOptions("\nDinamik g\u00f6r\u00fcn\u00fcrl\u00fck durumu blok ad\u0131na eklensin mi? [Evet/Hayir] <Hayir>: ", "Evet Hayir") { AllowNone = true });
                if (vr.Status == PromptStatus.Cancel) return;
                bool withVis = vr.Status == PromptStatus.OK && vr.StringResult == "Evet";

                bool outCol = false;
                if (!buildingId.IsNull)
                {
                    PromptResult or = ed.GetKeywords(new PromptKeywordOptions("\n'Oda d\u0131\u015f\u0131 (bina i\u00e7i)' s\u00fctunu eklensin mi? [Evet/Hayir] <Evet>: ", "Evet Hayir") { AllowNone = true });
                    if (or.Status == PromptStatus.Cancel) return;
                    outCol = !(or.Status == PromptStatus.OK && or.StringResult == "Hayir");
                }

                string path;
                string summary;
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    List<Room> rooms = new List<Room>();
                    foreach (ObjectId id in roomIds)
                    {
                        Curve c = tx.GetObject(id, OpenMode.ForRead) as Curve;
                        if (c == null || !ArmaturCommands.IsClosedPublic(c)) { ed.WriteMessage("\nAtland\u0131 (a\u00e7\u0131k e\u011fri): " + id.Handle); continue; }
                        List<Point2d> poly = ArmaturCommands.SamplePublic(c);
                        if (poly.Count < 3) continue;
                        rooms.Add(new Room { Poly = poly, Area = AbsArea(poly), Id = id });
                    }
                    foreach (Curve c in roomCurves)
                    {
                        List<Point2d> poly = ArmaturCommands.SamplePublic(c);
                        if (poly.Count < 3) continue;
                        rooms.Add(new Room { Poly = poly, Area = AbsArea(poly), Id = ObjectId.Null });
                    }
                    if (rooms.Count == 0) { ed.WriteMessage("\nKapal\u0131 oda yok.\n"); return; }
                    rooms = rooms.OrderBy(r => r.Area).ToList(); // smallest first for nested assignment

                    List<Point2d> building = null;
                    if (!buildingId.IsNull)
                    {
                        Curve bc = tx.GetObject(buildingId, OpenMode.ForRead) as Curve;
                        if (bc != null && ArmaturCommands.IsClosedPublic(bc)) building = ArmaturCommands.SamplePublic(bc);
                        else ed.WriteMessage("\nBina s\u0131n\u0131r\u0131 kapal\u0131 de\u011fil; yok say\u0131ld\u0131.");
                    }

                    NameRooms(tx, db, rooms);

                    List<Hit> hits = new List<Hit>();
                    BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (onlyBlocks != null && !onlyBlocks.Contains(id)) continue;
                        if (id.ObjectClass.DxfName != "INSERT") continue;
                        BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null || !Keep(tx, br)) continue;
                        Point2d p = new Point2d(br.Position.X, br.Position.Y);
                        if (building != null && !ArmaturGeo.Inside(building, p)) continue; // outside building / legend

                        int ri = -1;
                        for (int i = 0; i < rooms.Count; i++)
                            if (ArmaturGeo.Inside(rooms[i].Poly, p)) { ri = i; break; } // smallest first
                        if (ri < 0 && !(outCol && building != null)) continue;

                        string key = BlockKey(tx, br, withVis);
                        hits.Add(new Hit { Key = key, P = p, Room = ri });
                    }

                    // dedupe: same key + same position (tol) once; keep first room assignment
                    List<Hit> uniq = new List<Hit>();
                    foreach (Hit h in hits.OrderBy(h => h.Room < 0 ? 9999 : h.Room).ThenBy(h => h.Key))
                    {
                        bool dup = false;
                        foreach (Hit u in uniq)
                            if (u.Key == h.Key && Math.Abs(u.P.X - h.P.X) <= PosTol && Math.Abs(u.P.Y - h.P.Y) <= PosTol) { dup = true; break; }
                        if (!dup) uniq.Add(h);
                    }

                    List<string> roomNames = rooms.Select(r => r.Name).ToList();
                    if (outCol && building != null) roomNames.Add("Oda d\u0131\u015f\u0131 (bina i\u00e7i)");
                    int nOut = outCol && building != null ? rooms.Count : -1; // index for outside column

                    Dictionary<string, int[]> grid = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (Hit h in uniq)
                    {
                        int col = h.Room >= 0 ? h.Room : nOut;
                        if (col < 0) continue;
                        int[] row;
                        if (!grid.TryGetValue(h.Key, out row)) { row = new int[roomNames.Count]; grid[h.Key] = row; }
                        row[col]++;
                    }

                    List<object> sheet = new List<object>();
                    List<object> header = new List<object> { "Blok" };
                    foreach (string n in roomNames) header.Add(n);
                    header.Add("Toplam");
                    sheet.Add(header.ToArray());

                    int[] colTot = new int[roomNames.Count];
                    foreach (string bk in grid.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    {
                        int[] row = grid[bk];
                        List<object> cells = new List<object> { bk };
                        int rt = 0;
                        for (int i = 0; i < roomNames.Count; i++) { cells.Add(row[i]); rt += row[i]; colTot[i] += row[i]; }
                        cells.Add(rt);
                        sheet.Add(cells.ToArray());
                    }
                    List<object> tot = new List<object> { "TOPLAM" };
                    int grand = 0;
                    for (int i = 0; i < roomNames.Count; i++) { tot.Add(colTot[i]); grand += colTot[i]; }
                    tot.Add(grand);
                    sheet.Add(tot.ToArray());

                    path = ExportPath(doc);
                    ServiceHost.EnsureStarted(Settings.Load());
                    Dictionary<string, object> resp = Js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/xlsx",
                        Js.Serialize(new Dictionary<string, object> { { "path", path }, { "sheets", new object[] { new object[] { "Oda say\u0131m\u0131", sheet } } } })));
                    if (resp == null || resp.ContainsKey("error")) throw new InvalidOperationException(resp == null ? "bo\u015f yan\u0131t" : Convert.ToString(resp["error"]));
                    if (resp.ContainsKey("path")) path = Convert.ToString(resp["path"]);

                    int nBlocks = grid.Count, nHits = uniq.Count, nDup = hits.Count - uniq.Count;
                    summary = "Oda blok say\u0131m\u0131: " + rooms.Count + " oda, " + nBlocks + " blok ad\u0131, " + nHits + " adet"
                        + (nDup > 0 ? " (" + nDup + " m\u00fckerrer atland\u0131)" : "")
                        + (building != null ? ", bina s\u0131n\u0131r\u0131 uyguland\u0131" : "")
                        + ".\nExcel: " + path;
                    foreach (Room r in rooms)
                    {
                        int c = 0; foreach (Hit h in uniq) if (h.Room == rooms.IndexOf(r)) c++;
                        // IndexOf on value type room after reorder — use name match instead below
                    }
                    // per-room counts for message
                    for (int i = 0; i < rooms.Count; i++)
                    {
                        int c = uniq.Count(h => h.Room == i);
                        summary += "\n  " + rooms[i].Name + ": " + c;
                    }
                    if (nOut >= 0) summary += "\n  Oda d\u0131\u015f\u0131 (bina i\u00e7i): " + uniq.Count(h => h.Room < 0);

                    tx.Commit();
                }

                if (Environment.GetEnvironmentVariable("CADAI_NOOPEN") == null && File.Exists(path))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (System.Exception) { }

                ed.WriteMessage("\n" + summary + "\n");
                try { Metraj.Raise(summary); } catch (System.Exception) { }
            }
            catch (System.Exception ex) { ed.WriteMessage("\nOda say\u0131m\u0131 hatas\u0131: " + ex.Message + "\n"); }
            finally { foreach (DBObjectCollection col in traced) foreach (DBObject o in col) { try { o.Dispose(); } catch (System.Exception) { } } }
        }

        static double AbsArea(List<Point2d> p)
        {
            double a = 0; for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X + p[i].X) * (p[j].Y - p[i].Y); return Math.Abs(a) / 2;
        }

        static bool Keep(Transaction tx, BlockReference br)
        {
            try
            {
                if (br.BlockTableRecord.IsNull) return false;
                BlockTableRecord btr = (BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (btr.IsFromExternalReference || btr.IsFromOverlayReference) return false;
                string n = EffName(tx, br);
                if (string.IsNullOrEmpty(n)) return false;
                if (n.StartsWith("*")) return false; // anonymous / xref clones without effective name
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

        static string VisState(Transaction tx, BlockReference br)
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
            string v = VisState(tx, br);
            return string.IsNullOrEmpty(v) ? n : n + " [" + v + "]";
        }

        /// <summary>Prefer TEXT/MTEXT whose insertion is inside the room (largest height, then shortest clean text); else attribute of a block inside; else Oda n.</summary>
        static void NameRooms(Transaction tx, Database db, List<Room> rooms)
        {
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var texts = new List<Tuple<Point2d, string, double>>();
            foreach (ObjectId id in ms)
            {
                string dxf = id.ObjectClass.DxfName;
                if (dxf == "TEXT")
                {
                    DBText t = (DBText)tx.GetObject(id, OpenMode.ForRead);
                    string s = CleanLabel(t.TextString); if (s == null) continue;
                    texts.Add(Tuple.Create(new Point2d(t.Position.X, t.Position.Y), s, t.Height));
                }
                else if (dxf == "MTEXT")
                {
                    MText t = (MText)tx.GetObject(id, OpenMode.ForRead);
                    string s = CleanLabel(t.Contents); if (s == null) continue;
                    texts.Add(Tuple.Create(new Point2d(t.Location.X, t.Location.Y), s, t.TextHeight));
                }
                else if (dxf == "INSERT")
                {
                    BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                        if (ar == null) continue;
                        string tag = TrayKesif.Norm(ar.Tag);
                        if (!(tag.Contains("oda") || tag.Contains("room") || tag.Contains("mekan") || tag.Contains("name") || tag.Contains("ad") || tag == "no" || tag == "numara")) continue;
                        string s = CleanLabel(ar.TextString); if (s == null) continue;
                        Point3d ap = ar.Position;
                        texts.Add(Tuple.Create(new Point2d(ap.X, ap.Y), s, ar.Height > 0 ? ar.Height : 1));
                    }
                }
            }

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // assign largest rooms first so big labels don't steal from nested tiny rooms incorrectly — actually rooms sorted small→large; for naming, prefer label uniquely inside
            for (int i = 0; i < rooms.Count; i++)
            {
                Room r = rooms[i];
                var cand = texts.Where(t => ArmaturGeo.Inside(r.Poly, t.Item1)
                    && !rooms.Any(o => !object.ReferenceEquals(o, r) && o.Area < r.Area - 1e-6 && ArmaturGeo.Inside(o.Poly, t.Item1)))
                    .OrderByDescending(t => t.Item3).ThenBy(t => t.Item2.Length).ToList();
                string name = null;
                foreach (var t in cand) if (used.Add(t.Item2)) { name = t.Item2; break; }
                if (name == null)
                {
                    int n = 1; while (!used.Add("Oda " + n)) n++;
                    name = "Oda " + n;
                }
                r.Name = name;
            }
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
