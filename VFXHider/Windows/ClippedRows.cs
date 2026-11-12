using System;
using Dalamud.Bindings.ImGui;

namespace VFXHider.Windows;

internal static unsafe class ClippedRows
{
    public static void Draw(int count, Action<int> drawRow)
    {
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper());
        clipper.Begin(count, -1f);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                drawRow(i);
        }

        clipper.End();
        clipper.Destroy();
    }
}
