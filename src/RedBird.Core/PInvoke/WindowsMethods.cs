using System;
using System.Runtime.InteropServices;

namespace RedBird.Core.PInvoke;

internal static class WindowsMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandleA(string lpModuleName);

}
