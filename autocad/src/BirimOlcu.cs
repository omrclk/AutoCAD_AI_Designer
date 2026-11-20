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
        }
    }
}
