using System;
using System.Collections.Generic;

namespace VFXHider.VfxFilter;

internal sealed class ErrorThrottle
{
    private const long IntervalMs = 60_000;

    private readonly Dictionary<string, (long LastLogged, int Suppressed)> seen = [];

    public void Error(Exception ex, string message)
    {
        var now = Environment.TickCount64;
        int suppressed;

        lock (seen)
        {
            if (seen.TryGetValue(message, out var state) && now - state.LastLogged < IntervalMs)
            {
                seen[message] = (state.LastLogged, state.Suppressed + 1);
                return;
            }

            suppressed = state.Suppressed;
            seen[message] = (now, 0);
        }

        if (suppressed > 0)
            Plugin.Log.Error(ex, $"{message} ({suppressed} more since the last report)");
        else
            Plugin.Log.Error(ex, message);
    }
}
