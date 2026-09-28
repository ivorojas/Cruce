using System;
namespace Cruce
{
    static class TestMain
    {
        static int Main(string[] a)
        {
            Log.Init();
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            return SelfTest.Run(a.Length > 0 && a[0] == "inject");
        }
    }
}
