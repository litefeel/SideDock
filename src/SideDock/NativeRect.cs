using System.Runtime.InteropServices;

namespace SideDock;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public NativeRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}
