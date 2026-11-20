using System;
using CadAiDesigner;

class TestBirim
{
    static int Main()
    {
        BirimOlcu.Check();
        Console.WriteLine("BirimOlcu OK: Cm 300 mm -> width 30, scale 0.1, boy 500 -> 5000 mm, centre 15");
        return 0;
    }
}
