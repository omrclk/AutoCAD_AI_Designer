using System;
using System.Collections.Generic;

/// <summary>Runs CadAiDesigner.LengthUnion.SelfTest. Not part of the AutoCAD project.</summary>
static class LengthUnionRunner
{
    static int Main()
    {
        List<string> errs = CadAiDesigner.LengthUnion.SelfTest();
        for (int i = 0; i < errs.Count; i++) Console.WriteLine(errs[i]);
        Console.WriteLine(errs.Count == 0 ? "OK" : "FAIL " + errs.Count);
        return errs.Count == 0 ? 0 : 1;
    }
}
