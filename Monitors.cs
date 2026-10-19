using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Rect32 = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace TeamScreen;

/// <param name="Id">EDID manufacturer+product code, e.g. AUS27A6. Stable across reboots and port swaps.</param>
public sealed record MonitorInfo(string DeviceName, string Id, string Name, Rect32 Bounds, bool Primary, bool Mirrored)
{
    public string Label => $"{Name} — {Bounds.Width}×{Bounds.Height}{(Primary ? " · main screen" : "")}{(Mirrored ? " · MIRRORED" : "")}";
}

public static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var names = FriendlyNames();
        var list = new List<MonitorInfo>();
        for (uint i = 0; ; i++)
        {
            var adapter = new Native.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevices(null, i, ref adapter, 0)) break;
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == adapter.DeviceName);
            if (screen == null) continue;   // adapter output not part of the desktop

            var ids = new List<string>();
            for (uint j = 0; ; j++)
            {
                var mon = new Native.DISPLAY_DEVICE { cb = adapter.cb };
                if (!Native.EnumDisplayDevices(adapter.DeviceName, j, ref mon, 1)) break;
                var parts = mon.DeviceID.Split('#');   // \\?\DISPLAY#AUS27A6#5&...#{guid}
                if (parts.Length > 1) ids.Add(parts[1]);
            }
            foreach (var id in ids)
                list.Add(new MonitorInfo(adapter.DeviceName, id, names.GetValueOrDefault(id, id), screen.Bounds, screen.Primary, ids.Count > 1));
        }
        return list;
    }

    public static MonitorInfo? Find(string id) => All().FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    static Dictionary<string, string> FriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID");
            foreach (ManagementObject mo in q.Get())
            {
                var instance = (string)mo["InstanceName"];          // DISPLAY\AUS27A6\5&...
                var id = instance.Split('\\').ElementAtOrDefault(1);
                var raw = mo["UserFriendlyName"] as ushort[];
                if (id == null || raw == null) continue;
                var name = new string(raw.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                if (name.Length > 0) map[id] = name;
            }
        }
        catch { /* WMI unavailable: fall back to EDID ids */ }
        return map;
    }
}
