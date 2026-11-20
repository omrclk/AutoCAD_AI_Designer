using System;
using CadAiDesigner;

class TestBirim
{
    static int Main()
    {
        BirimOlcu.Check();
        Console.WriteLine("BirimOlcu OK: scale check, cm/mm/m plans, ambiguous fallback");
        return 0;
    }
}
