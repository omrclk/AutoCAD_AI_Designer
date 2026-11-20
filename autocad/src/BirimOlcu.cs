using System;

namespace CadAiDesigner
{
    /// <summary>Pure millimetre ↔ drawing-unit conversion. No coordinates are scaled; only sizes are.</summary>
    internal static class BirimOlcu
    {
        /// <summary>Millimetres in one drawing unit. Mm=1, Cm=10, Metre=1000.</summary>
        public static double MmPerUnit(string canon)
        {
            if (canon == "Cm") return 10;
            if (canon == "Metre") return 1000;
            if (canon == "Dm") return 100;
            if (canon == "In") return 25.4;
            if (canon == "Ft") return 304.8;
            return 1;
        }

        public static double BlockScale(double mmPerUnit)
        {
            return mmPerUnit > 0 ? 1.0 / mmPerUnit : 1;
        }

        /// <summary>How wide the tray is in the drawing. 300 mm in Cm is 30.</summary>
        public static double WidthDrawing(double widthMm, double mmPerUnit)
        {
            return mmPerUnit > 0 ? widthMm / mmPerUnit : widthMm;
        }

        /// <summary>Block length parameter. A 500-unit run in Cm is 5000 mm.</summary>
        public static double BoyMm(double drawingLength, double mmPerUnit)
        {
            return drawingLength * (mmPerUnit > 0 ? mmPerUnit : 1);
        }

        /// <summary>World offset of the block centre. Local centre is in millimetres; multiply by the reference scale only.</summary>
        public static double CenterOffsetDrawing(double localCenterMm, double mmPerUnit)
        {
            return localCenterMm * BlockScale(mmPerUnit);
        }

        public class BirimKarar
        {
            public string Ad = "Mm";
            public bool Emin;
            public int Parca;
            public double ToplamM, MedyanM;
            public double PuanMm, PuanCm, PuanM;
            public string Metin = "";
            public string Skor = "";
        }

        /// <summary>Score Mm/Cm/Metre from drawing-unit lengths and a selection box.
        /// Segments of about 0.5–60 m and a total of about 5–2000 m score highest, peaked near a 4 m piece and a 40 m floor.
        /// <paramref name="fallback"/> is used when the top two scores are close. <paramref name="insHint"/> is a weak tie-break only.</summary>
        public static BirimKarar Decide(double[] lens, double minX, double minY, double maxX, double maxY, string insHint, string fallback, bool fallbackSaved = false)
        {
            BirimKarar karar = new BirimKarar();
            int n = lens == null ? 0 : lens.Length;
            karar.Parca = n;
            double[] copy = new double[n];
            double toplam = 0;
            for (int i = 0; i < n; i++) { copy[i] = lens[i]; toplam += lens[i]; }
            if (n > 1) Array.Sort(copy);
            double med = 0;
            if (n == 1) med = copy[0];
            else if (n > 1) med = (n % 2 == 1) ? copy[n / 2] : (copy[n / 2 - 1] + copy[n / 2]) * 0.5;
            double bw = maxX - minX, bh = maxY - minY;
            if (bw < 0) bw = 0;
            if (bh < 0) bh = 0;
            double cap = Math.Max(bw, bh);
            if (cap <= 0 && n > 0) cap = copy[n - 1];

            string[] ads = new string[] { "Mm", "Cm", "Metre" };
            double[] fac = new double[] { 1, 10, 1000 };
            double[] puan = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double f = fac[i];
                double medM = med * f / 1000.0;
                double totM = toplam * f / 1000.0;
                double capM = cap * f / 1000.0;
                double seg = Peak(medM, 4.0, 0.55);
                double tot = Peak(totM, 80.0, 0.85);
                double box = Peak(capM, 40.0, 0.60);
                puan[i] = n == 0 ? 0 : (0.50 * seg + 0.30 * tot + 0.20 * box);
                if (insHint != null && string.Equals(insHint, ads[i], StringComparison.OrdinalIgnoreCase)) puan[i] += 0.04;
                if (puan[i] > 1) puan[i] = 1;
            }
            int best = 0, second = 1;
            if (puan[1] > puan[0]) { best = 1; second = 0; }
            if (puan[2] > puan[best]) { second = best; best = 2; }
            else if (best != 2 && puan[2] > puan[second]) second = 2;
            karar.PuanMm = puan[0];
            karar.PuanCm = puan[1];
            karar.PuanM = puan[2];
            karar.Skor = "Mm=" + P(puan[0]) + " Cm=" + P(puan[1]) + " Metre=" + P(puan[2]);
            double gap = puan[best] - puan[second];
            karar.Emin = n >= 3 && gap >= 0.08 && puan[best] >= 0.35;
            string chosen = karar.Emin ? ads[best] : TrayUnit(fallback, TrayUnit(insHint, ads[best]));
            double chosenF = MmPerUnit(chosen);
            karar.Ad = chosen;
            karar.ToplamM = toplam * chosenF / 1000.0;
            karar.MedyanM = med * chosenF / 1000.0;
            string olcu = "toplam " + M(karar.ToplamM) + " m, " + n + " parça, medyan " + M(karar.MedyanM) + " m";
            if (karar.Emin)
                karar.Metin = "Birim (oto): " + chosen + " — " + olcu;
            else
            {
                string neden = (fallbackSaved && chosen == TrayUnit(fallback, "")) ? " (son kayıt)" : (string.Equals(chosen, insHint, StringComparison.OrdinalIgnoreCase) ? " (INSUNITS)" : "");
                karar.Metin = "Birim (oto): emin değil (" + karar.Skor + "). " + chosen + " kullanıldı" + neden + ". — " + olcu;
            }
            return karar;
        }

        static string TrayUnit(string s, string elseAd)
        {
            if (s == "Mm" || s == "Cm" || s == "Metre") return s;
            return elseAd;
        }

        static double Peak(double metres, double center, double sigma)
        {
            if (metres <= 1e-12 || center <= 0 || sigma <= 0) return 0;
            double d = Math.Log10(metres / center) / sigma;
            return Math.Exp(-0.5 * d * d);
        }

        static string M(double v) { return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }
        static string P(double v) { return v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }

        public static void Check()
        {
            double cm = MmPerUnit("Cm");
            if (Math.Abs(cm - 10) > 1e-9) throw new InvalidOperationException("Cm factor");
            if (Math.Abs(BlockScale(cm) - 0.1) > 1e-9) throw new InvalidOperationException("Cm scale");
            if (Math.Abs(WidthDrawing(300, cm) - 30) > 1e-9) throw new InvalidOperationException("Cm width");
            if (Math.Abs(BoyMm(500, cm) - 5000) > 1e-9) throw new InvalidOperationException("Cm boy");
            if (Math.Abs(BoyMm(500, cm) * BlockScale(cm) - 500) > 1e-6) throw new InvalidOperationException("Cm visual boy");
            if (Math.Abs(CenterOffsetDrawing(150, cm) - 15) > 1e-9) throw new InvalidOperationException("Cm centre");
            if (Math.Abs(WidthDrawing(300, 1) - 300) > 1e-9) throw new InvalidOperationException("Mm width");
            if (Math.Abs(BlockScale(1) - 1) > 1e-9) throw new InvalidOperationException("Mm scale");
            if (Math.Abs(BoyMm(500, 1) - 500) > 1e-9) throw new InvalidOperationException("Mm boy");
            if (Math.Abs(CenterOffsetDrawing(150, 1) - 150) > 1e-9) throw new InvalidOperationException("Mm centre");
            double[] planM = new double[] { 0.8, 1.2, 1.5, 2.1, 2.4, 3.0, 3.5, 4.2, 5.0, 6.5, 8.0, 1.1, 2.2, 2.8, 7.5, 12, 1.8, 2.0, 3.3, 4.8 };
            ExpectUnit("cm", Scale(planM, 100), 0, 0, 3000, 1800, "Cm", true);
            ExpectUnit("mm", Scale(planM, 1000), 0, 0, 30000, 18000, "Mm", true);
            ExpectUnit("m", Scale(planM, 1), 0, 0, 30, 18, "Metre", true);
            double[] mid = new double[] { 2000, 2000, 2000, 2000, 2000, 2000, 2000, 2000 };
            BirimKarar amb = Decide(mid, 0, 0, 2000, 1200, "Mm", "Cm");
            if (amb.Emin) throw new InvalidOperationException("ambiguous should be unsure: " + amb.Skor);
            if (amb.Ad != "Cm") throw new InvalidOperationException("ambiguous fallback " + amb.Ad + " " + amb.Skor);
        }

        static double[] Scale(double[] metres, double duPerMetre)
        {
            double[] d = new double[metres.Length];
            for (int i = 0; i < metres.Length; i++) d[i] = metres[i] * duPerMetre;
            return d;
        }

        static void ExpectUnit(string name, double[] lens, double x0, double y0, double x1, double y1, string ad, bool emin)
        {
            BirimKarar k = Decide(lens, x0, y0, x1, y1, "Mm", "Mm");
            if (k.Ad != ad || k.Emin != emin)
                throw new InvalidOperationException(name + " -> " + k.Ad + " emin=" + k.Emin + " " + k.Skor + " | " + k.Metin);
        }
    }
}
