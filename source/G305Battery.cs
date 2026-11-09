// G305 Battery Tray - Windows tray battery monitor for Logitech G305 (AA cell).
// Derived from gpw-battery-tray (MIT, Copyright (c) 2026 xrfc); see NOTICE.md.
// Direct HID++ 2.0 over Windows HID. No G HUB, no driver install, no network.
// Build with the .NET Framework C# compiler; see build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace G305Battery {
    internal static class Native {
        internal const uint ReadAccess = 0x80000000, WriteAccess = 0x40000000, OverlappedFlag = 0x40000000;
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData {
            internal int Size; internal Guid ClassGuid; internal int Flags; internal IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Attributes {
            internal int Size; internal ushort Vendor, Product, Version;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps {
            internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort LinkNodes, InputButtons, InputValues, InputIndices;
            internal ushort OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Overlapped {
            internal UIntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event;
        }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] internal static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes a);
        [DllImport("hid.dll", SetLastError = true)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr info, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint size, out uint done, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint size, out uint done, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint done, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateEventW(IntPtr security, bool manual, bool initial, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential)] internal struct IconIdentifier {
            internal uint Size; internal IntPtr Window; internal uint Id; internal Guid Guid;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [DllImport("shell32.dll")] internal static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out Rect rect);
        [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll")] internal static extern IntPtr GetProcessWindowStation();
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder data, uint size, out uint required);
        internal static Exception Error(string operation) { return new IOException(operation + " (Win32 " + Marshal.GetLastWin32Error() + ")"); }
        internal static bool IsLightTheme() {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    object value = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    if (value == null) return true;
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                }
            } catch (Exception) { return true; }
        }
    }

    // A single HID++ vendor collection (short 7-byte or long 20-byte report).
    internal sealed class HidInterface {
        internal string Path; internal ushort Pid, Usage; internal int InputLength, OutputLength;
        internal string Product { get { return "046D:" + Pid.ToString("X4", CultureInfo.InvariantCulture); } }
        internal string Transport {
            get {
                switch (Pid) {
                    case 0xc53f: return "G305 LIGHTSPEED 接收器";
                    case 0xc539: case 0xc53a: case 0xc53d: case 0xc541: case 0xc545: case 0xc547: return "LIGHTSPEED 接收器";
                    case 0xc548: return "Logi Bolt 接收器";
                    case 0xc52b: case 0xc52f: return "Unifying 接收器";
                    default: return "罗技 HID++ 接口";
                }
            }
        }
        internal int Priority {
            get {
                int p;
                switch (Pid) {
                    case 0xc53f: p = 100; break;
                    case 0xc539: case 0xc53a: case 0xc53d: case 0xc541: case 0xc545: case 0xc547: p = 90; break;
                    case 0xc548: p = 60; break;
                    case 0xc52b: case 0xc52f: p = 50; break;
                    default: p = 10; break;
                }
                return p + (OutputLength == 20 ? 5 : 0);
            }
        }
        internal static List<HidInterface> Enumerate() {
            Guid guid; Native.HidD_GetHidGuid(out guid);
            IntPtr info = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (info == new IntPtr(-1)) throw Native.Error("枚举 HID 设备失败");
            var result = new List<HidInterface>();
            try {
                for (uint i = 0; ; i++) {
                    var data = new Native.InterfaceData(); data.Size = Marshal.SizeOf(typeof(Native.InterfaceData));
                    if (!Native.SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, i, ref data)) {
                        if (Marshal.GetLastWin32Error() == 259) break;
                        throw Native.Error("枚举 HID 接口失败");
                    }
                    uint required;
                    Native.SetupDiGetDeviceInterfaceDetailW(info, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 8) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(info, ref data, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path)) continue;
                        // Only Logitech HID++ vendor collections; never touch keyboards, audio, etc.
                        if (path.IndexOf("vid_046d", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (var handle = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                            if (handle.IsInvalid) continue;
                            var attr = new Native.Attributes(); attr.Size = Marshal.SizeOf(typeof(Native.Attributes));
                            if (!Native.HidD_GetAttributes(handle, ref attr) || attr.Vendor != 0x046d) continue;
                            IntPtr pp;
                            if (!Native.HidD_GetPreparsedData(handle, out pp)) continue;
                            try {
                                Native.Caps caps;
                                if (Native.HidP_GetCaps(pp, out caps) != 0x110000) continue;
                                if (caps.UsagePage != 0xff00) continue;
                                if ((caps.OutputLength != 7 && caps.OutputLength != 20) || caps.InputLength < 7) continue;
                                result.Add(new HidInterface { Path = path, Pid = attr.Product, Usage = caps.Usage,
                                    InputLength = caps.InputLength, OutputLength = caps.OutputLength });
                            } finally { Native.HidD_FreePreparsedData(pp); }
                        }
                    } finally { Marshal.FreeHGlobal(detail); }
                }
            } finally { Native.SetupDiDestroyDeviceInfoList(info); }
            // De-duplicate identical paths, then prefer G305 receivers and long reports.
            return result.GroupBy(x => x.Path).Select(g => g.First())
                .OrderByDescending(x => x.Priority).ThenByDescending(x => x.OutputLength).ToList();
        }
    }

    internal sealed class HidSession : IDisposable {
        readonly SafeFileHandle reader, writer;
        readonly HidInterface device;
        int softwareId = 9;
        internal HidSession(HidInterface d) {
            device = d;
            reader = Native.CreateFileW(d.Path, Native.ReadAccess, 3, IntPtr.Zero, 3, Native.OverlappedFlag, IntPtr.Zero);
            if (reader.IsInvalid) { reader.Dispose(); throw Native.Error("打开 HID 读取接口失败"); }
            writer = Native.CreateFileW(d.Path, Native.WriteAccess, 3, IntPtr.Zero, 3, Native.OverlappedFlag, IntPtr.Zero);
            if (writer.IsInvalid) { writer.Dispose(); reader.Dispose(); throw Native.Error("打开 HID 查询接口失败"); }
        }
        byte[] Transfer(bool writing, byte[] input, int length, int timeout) {
            SafeFileHandle h = writing ? writer : reader;
            IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) throw Native.Error("创建 HID 事件失败");
            IntPtr buffer = Marshal.AllocHGlobal(length), ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlapped)));
            bool pending = false;
            try {
                Marshal.StructureToPtr(new Native.Overlapped { Event = ev }, ov, false);
                if (writing) Marshal.Copy(input, 0, buffer, length);
                uint done;
                bool ok = writing ? Native.WriteFile(h, buffer, (uint)length, out done, ov)
                                  : Native.ReadFile(h, buffer, (uint)length, out done, ov);
                if (!ok) {
                    if (Marshal.GetLastWin32Error() != 997) throw Native.Error(writing ? "HID 查询发送失败" : "HID 读取失败");
                    pending = true;
                    uint wait = Native.WaitForSingleObject(ev, (uint)Math.Max(1, timeout));
                    if (wait != 0) {
                        Native.CancelIoEx(h, ov);
                        Native.GetOverlappedResult(h, ov, out done, true);
                        pending = false;
                        if (wait == 258) throw new TimeoutException("鼠标没有回复，可能休眠或已断开");
                        throw Native.Error("等待 HID 回复失败");
                    }
                    ok = Native.GetOverlappedResult(h, ov, out done, false);
                    pending = false;
                    if (!ok) throw Native.Error("HID 操作失败");
                }
                if (writing && done != length) throw new IOException("HID 查询未完整发送");
                var data = new byte[done];
                if (!writing && done > 0) Marshal.Copy(buffer, data, 0, (int)done);
                return data;
            } finally {
                if (pending) { uint done; Native.CancelIoEx(h, ov); Native.GetOverlappedResult(h, ov, out done, true); }
                Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(buffer); Native.CloseHandle(ev);
            }
        }
        internal static byte[] Decode(byte[] r, int slot, int feature, int command) {
            if (r.Length < 4 || (r[0] != 0x10 && r[0] != 0x11) || r[1] != slot) return null;
            if (r.Length >= 6 && r[2] == 0xff && r[3] == feature && r[4] == command)
                throw new IOException("HID++ 错误 0x" + r[5].ToString("x2"));
            if (r.Length >= 7 && r[2] == 0x8f && r[3] == feature && r[4] == command)
                throw new IOException("HID++ 1.0 错误 0x" + r[5].ToString("x2"));
            if (r[2] != feature || r[3] != command) return null;
            return r.Skip(4).ToArray();
        }
        internal byte[] Call(int slot, int feature, int function, params byte[] args) {
            return CallWithTimeout(slot, feature, function, 850, args);
        }
        internal byte[] CallWithTimeout(int slot, int feature, int function, int timeoutMs, params byte[] args) {
            softwareId = softwareId % 15 + 1;
            int command = (function << 4) | softwareId;
            byte[] request = new byte[device.OutputLength];
            request[0] = (byte)(request.Length == 20 ? 0x11 : 0x10); request[1] = (byte)slot;
            request[2] = (byte)feature; request[3] = (byte)command;
            Array.Copy(args, 0, request, 4, Math.Min(args.Length, request.Length - 4));
            Transfer(true, request, request.Length, Math.Min(800, timeoutMs));
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs) {
                byte[] response = Transfer(false, null, device.InputLength, (int)Math.Max(1, timeoutMs - watch.ElapsedMilliseconds));
                byte[] payload = Decode(response, slot, feature, command);
                if (payload != null) return payload;
            }
            throw new TimeoutException("鼠标回复超时，请动一下鼠标后重试");
        }
        internal int Feature(int slot, int id) { return Feature(slot, id, 850); }
        internal int Feature(int slot, int id, int timeoutMs) {
            byte[] response = CallWithTimeout(slot, 0, 0, timeoutMs, (byte)(id >> 8), (byte)id);
            if (response.Length < 1 || response[0] == 0) return 0;
            return response[0];
        }
        internal int TryFeature(int slot, int id, int timeoutMs) {
            try { return Feature(slot, id, timeoutMs); } catch (Exception) { return 0; }
        }
        internal string Name(int slot) { return Name(slot, 400); }
        internal string Name(int slot, int timeoutMs) {
            int feature = Feature(slot, 5, timeoutMs);
            if (feature == 0) throw new IOException("设备不支持 DEVICE NAME (0x0005)");
            byte[] length = CallWithTimeout(slot, feature, 0, timeoutMs);
            if (length.Length < 1 || length[0] == 0 || length[0] > 100) throw new IOException("设备名称回复无效");
            var name = new List<byte>();
            while (name.Count < length[0]) {
                byte[] part = CallWithTimeout(slot, feature, 1, timeoutMs, (byte)name.Count);
                int n = Math.Min(part.Length, length[0] - name.Count);
                if (n == 0) throw new IOException("设备名称回复不完整");
                name.AddRange(part.Take(n));
            }
            return Encoding.UTF8.GetString(name.ToArray()).TrimEnd('\0', ' ', '\r', '\n');
        }
        public void Dispose() { writer.Dispose(); reader.Dispose(); }
    }

    // Voltage fallback: Solaar's 13-point Li-Po curve plus a coarse AA curve.
    internal static class Curves {
        static readonly double[][] Lipo = new double[][] {
            new double[]{3500,0}, new double[]{3579,2}, new double[]{3646,5}, new double[]{3671,10},
            new double[]{3717,20}, new double[]{3751,30}, new double[]{3778,40}, new double[]{3811,50},
            new double[]{3859,60}, new double[]{3922,70}, new double[]{3989,80}, new double[]{4067,90},
            new double[]{4186,100}
        };
        // G305 uses one AA cell. Alkaline/NiMH curves differ; this is only a rough fallback.
        static readonly double[][] Aa = new double[][] {
            new double[]{950,0}, new double[]{1100,10}, new double[]{1180,25}, new double[]{1260,45},
            new double[]{1340,65}, new double[]{1430,85}, new double[]{1580,100}
        };
        internal static int Estimate(int mv, bool aa) {
            double[][] points = aa ? Aa : Lipo;
            if (mv <= points[0][0]) return (int)Math.Round(points[0][1]);
            if (mv >= points[points.Length - 1][0]) return 100;
            for (int i = 1; i < points.Length; i++) if (mv <= points[i][0]) {
                double[] low = points[i - 1], high = points[i];
                return (int)Math.Round(low[1] + (high[1] - low[1]) * (mv - low[0]) / (high[0] - low[0]), MidpointRounding.AwayFromZero);
            }
            return 0;
        }
    }

    internal enum BatterySource { UnifiedBattery, BatteryStatus, BatteryVoltage }

    internal sealed class Reading {
        internal bool Ok, Charging, Full, IsEstimate, Critical;
        internal int? Percentage;
        internal int StatusCode, Millivolts, NextLevel, LevelBucket, Flags, Slot;
        internal BatterySource Source;
        internal string StatusText, Error, Name, Transport, Product, Raw;
        internal DateTime Time = DateTime.Now;

        internal static Reading Fail(string message) { return new Reading { Error = message, StatusText = "离线" }; }
        internal static string Hex(byte[] data) {
            if (data == null) return "";
            // Long HID++ reports pad the payload with zeros; keep the meaningful bytes.
            int n = data.Length;
            while (n > 3 && data[n - 1] == 0) n--;
            var sb = new StringBuilder();
            foreach (byte b in data.Take(Math.Min(n, 16))) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        internal static string StatusName(int code) {
            switch (code) {
                case 0: return "放电中";
                case 1: return "充电中";
                case 2: return "即将充满";
                case 3: return "已充满";
                case 4: return "慢速充电";
                case 5: return "电池类型无效";
                case 6: return "温度异常";
                case 7: return "充电错误";
                default: return "未知状态";
            }
        }
        internal static string StatusId(int code) {
            switch (code) {
                case 0: return "discharging";
                case 1: return "recharging";
                case 2: return "almost_full";
                case 3: return "full";
                case 4: return "slow_recharge";
                case 5: return "invalid_battery";
                case 6: return "thermal_error";
                case 7: return "charging_error";
                default: return "unknown";
            }
        }
        static Reading Common(byte[] payload, BatterySource source, int status) {
            var r = new Reading { Ok = true, Source = source, StatusCode = status, Raw = Hex(payload),
                StatusText = StatusName(status) };
            r.Charging = status == 1 || status == 2 || status == 4;
            r.Full = status == 3;
            r.Critical = status == 5 || status == 6 || status == 7;
            return r;
        }
        // 0x1000 batteryStatus, fn 0: discharge_level, next_level, status.
        internal static Reading ParseLegacy(byte[] p) {
            if (p.Length < 3) throw new IOException("0x1000 回复长度不足");
            int level = p[0], next = p[1], status = p[2];
            if (status > 7) throw new IOException("0x1000 状态字节无效 0x" + status.ToString("X2"));
            Reading r = Common(p, BatterySource.BatteryStatus, status);
            r.NextLevel = next;
            r.Percentage = Clamp(status == 3 && level == 0 ? 100 : level);
            if (!r.Percentage.HasValue && r.Full) r.Percentage = 100;
            return r;
        }
        // 0x1004 unifiedBattery, fn 0 capabilities + fn 1 status.
        internal static Reading ParseUnified(byte[] caps, byte[] p) {
            if (p.Length < 3) throw new IOException("0x1004 回复长度不足");
            int percent = p[0], bucket = p[1], status = p[2];
            if (status > 7) throw new IOException("0x1004 状态字节无效 0x" + status.ToString("X2"));
            bool gauge = caps != null && caps.Length >= 2 && (caps[1] & 2) != 0;
            Reading r = Common(p, BatterySource.UnifiedBattery, status);
            r.LevelBucket = bucket;
            if (percent > 0) { r.Percentage = Clamp(percent); if (!gauge) r.IsEstimate = true; }
            else {
                r.IsEstimate = true;
                switch (bucket) {
                    case 8: r.Percentage = 100; break;
                    case 4: r.Percentage = 60; break;
                    case 2: r.Percentage = 20; break;
                    case 1: r.Percentage = 5; break;
                    default: r.Percentage = 0; break;
                }
            }
            if (r.Full && !r.Percentage.HasValue) r.Percentage = 100;
            return r;
        }
        // 0x1001 batteryVoltage, fn 0: big-endian mV + flags.
        internal static Reading ParseVoltage(byte[] p) {
            if (p.Length < 3) throw new IOException("0x1001 回复长度不足");
            int mv = (p[0] << 8) | p[1], flags = p[2];
            bool aa = mv >= 900 && mv < 2700;
            if (!aa && (mv < 2700 || mv > 4400)) throw new IOException("设备返回了无效电压 " + mv + " mV");
            bool powered = (flags & 128) != 0;
            int chargeStatus = flags & 0x03;
            if (powered && chargeStatus >= 3) throw new IOException("鼠标报告充电异常 (flags 0x" + flags.ToString("X2") + ")");
            int status = powered ? (chargeStatus == 1 ? 3 : chargeStatus == 2 ? 0 : 1) : 0;
            Reading r = Common(p, BatterySource.BatteryVoltage, status);
            r.Millivolts = mv; r.Flags = flags; r.IsEstimate = true;
            r.Percentage = r.Full ? 100 : Curves.Estimate(mv, aa);
            return r;
        }
        static int? Clamp(int value) { return value < 0 ? 0 : value > 100 ? 100 : (int?)value; }
        internal string SourceName {
            get {
                switch (Source) {
                    case BatterySource.UnifiedBattery: return "0x1004 unifiedBattery";
                    case BatterySource.BatteryStatus: return "0x1000 batteryStatus";
                    default: return "0x1001 batteryVoltage";
                }
            }
        }
        internal string LevelText {
            get { return Percentage.HasValue ? (IsEstimate ? "约 " : "") + Percentage.Value + "%" : "--"; }
        }
        internal string Json() {
            return "{\"ok\":" + (Ok ? "true" : "false") +
                ",\"device\":\"" + Escape(Name ?? "") + "\"" +
                ",\"receiver\":\"" + Escape(Product ?? "") + "\"" +
                ",\"transport\":\"" + Escape(Transport ?? "") + "\"" +
                ",\"slot\":" + Slot +
                ",\"source\":\"" + (Ok ? SourceName : "") + "\"" +
                ",\"percentage\":" + (Ok && Percentage.HasValue ? Percentage.Value.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"estimated\":" + (Ok && IsEstimate ? "true" : "false") +
                ",\"status\":\"" + (Ok ? StatusId(StatusCode) : "offline") + "\"" +
                ",\"status_text\":\"" + Escape(Ok ? StatusText : "离线") + "\"" +
                ",\"charging\":" + (Ok ? (Charging ? "true" : "false") : "null") +
                ",\"full\":" + (Ok ? (Full ? "true" : "false") : "null") +
                ",\"voltage_mv\":" + (Ok && Millivolts > 0 ? Millivolts.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"next_level\":" + (Ok ? NextLevel.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"raw\":\"" + (Ok ? Raw : "") + "\"" +
                ",\"captured_at\":\"" + Time.ToString("o", CultureInfo.InvariantCulture) + "\"" +
                ",\"error\":\"" + Escape(Error ?? "") + "\"}";
        }
        internal static string Escape(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal static class Battery {
        // G305 is a single-device LIGHTSPEED mouse: slot 1. Other slots cover
        // wired (0xFF) and multi-device Bolt/Unifying receivers.
        static readonly int[] ExtraSlots = new int[] { 0xff, 2, 3, 4, 5, 6 };
        internal static Reading Read() {
            var errors = new List<string>();
            try {
                List<HidInterface> interfaces = HidInterface.Enumerate();
                if (interfaces.Count == 0)
                    return Reading.Fail("未找到罗技 HID++ 接收器。请插入 G305 原装 LIGHTSPEED 接收器，或运行 --scan 查看本机接口。");
                // Pass 1: slot 1 on every interface. Remember which interfaces
                // actually answered so pass 2 does not time out on dead slots.
                var alive = new List<HidInterface>();
                foreach (HidInterface d in interfaces) {
                    bool responded;
                    Reading r = Probe(d, 1, errors, out responded);
                    if (r != null) return r;
                    if (responded) alive.Add(d);
                }
                if (alive.Count == 0)
                    return Reading.Fail("接收器已插入但鼠标没有应答，可能处于休眠或关机状态。动一下鼠标后刷新。");
                foreach (int slot in ExtraSlots) foreach (HidInterface d in alive) {
                    bool responded;
                    Reading r = Probe(d, slot, errors, out responded);
                    if (r != null) return r;
                }
                string detail = string.Join(" | ", errors.Distinct().Take(3).ToArray());
                return Reading.Fail("未能读到 G305 电量。请确认鼠标已开机并配对。" + (detail.Length > 0 ? "\n" + detail : ""));
            } catch (Exception ex) { return Reading.Fail(ex.Message); }
        }
        static Reading Probe(HidInterface d, int slot, List<string> errors, out bool responded) {
            responded = false;
            try {
                using (var hid = new HidSession(d)) {
                    int f1004 = 0, f1000 = 0, f1001 = 0;
                    // A genuine reply (including "feature absent" or an HID++
                    // error frame) means something is alive on this slot.
                    try { f1004 = hid.Feature(slot, 0x1004, 350); responded = true; }
                    catch (TimeoutException) {
                        // A sleeping LIGHTSPEED mouse often answers the second
                        // probe; retry once before giving up on this slot.
                        Thread.Sleep(120);
                        try { f1004 = hid.Feature(slot, 0x1004, 550); responded = true; }
                        catch (TimeoutException) { return null; }
                        catch (Exception) { responded = true; }
                    }
                    catch (Exception) { responded = true; }
                    if (f1004 == 0) {
                        try { f1000 = hid.Feature(slot, 0x1000, 350); responded = true; }
                        catch (TimeoutException) { }
                        catch (Exception) { responded = true; }
                    }
                    if (f1004 == 0 && f1000 == 0) {
                        try { f1001 = hid.Feature(slot, 0x1001, 350); responded = true; }
                        catch (TimeoutException) { }
                        catch (Exception) { responded = true; }
                    }
                    if (f1004 == 0 && f1000 == 0 && f1001 == 0) return null;
                    Reading r;
                    if (f1004 != 0) {
                        byte[] caps = null;
                        try { caps = hid.CallWithTimeout(slot, f1004, 0, 500); } catch (Exception) { }
                        r = Reading.ParseUnified(caps, hid.Call(slot, f1004, 1));
                    } else if (f1000 != 0) {
                        r = Reading.ParseLegacy(hid.Call(slot, f1000, 0));
                    } else {
                        r = Reading.ParseVoltage(hid.Call(slot, f1001, 0));
                    }
                    string name = "";
                    try { name = hid.Name(slot); } catch (Exception) { }
                    r.Name = name;
                    // A named non-G305 Logitech device is not our target. Unknown
                    // names (sleeping mouse / no 0x0005) are accepted but labelled.
                    if (name.Length > 0 && name.IndexOf("G305", StringComparison.OrdinalIgnoreCase) < 0) {
                        errors.Add("跳过非 G305 设备：" + name);
                        return null;
                    }
                    if (name.Length == 0) r.Name = "G305（名称未读到，按 HID++ 电量特性识别）";
                    r.Slot = slot; r.Product = d.Product; r.Transport = d.Transport;
                    return r;
                }
            } catch (TimeoutException ex) {
                if (errors.Count < 12) errors.Add(d.Product + " slot " + slot + "：" + ex.Message);
            } catch (Exception ex) {
                if (errors.Count < 12) errors.Add(d.Product + " slot " + slot + "：" + ex.Message);
            }
            return null;
        }
        internal static string Scan() {
            var sb = new StringBuilder();
            try {
                List<HidInterface> list = HidInterface.Enumerate();
                if (list.Count == 0) return "未发现罗技 HID++ 厂商接口 (VID 046D / UsagePage FF00)。";
                foreach (HidInterface d in list)
                    sb.AppendLine(d.Product + "  " + d.Transport + "  usage=0x" + d.Usage.ToString("X4") +
                        "  out=" + d.OutputLength + "  in=" + d.InputLength + "  priority=" + d.Priority + "\n  " + d.Path);
            } catch (Exception ex) { sb.AppendLine("扫描失败：" + ex.Message); }
            return sb.ToString();
        }
    }

    internal sealed class Settings {
        internal string Theme = "auto";       // auto | light | dark
        internal string Style = "capsule";    // capsule | number
        internal bool LowAlert = true;
        internal int Interval = 60;          // seconds
        // Poll periods offered in the tray menu. A G305 runs on one AA cell and
        // typically changes a whole battery step every few weeks, so 12h/24h are
        // perfectly usable and avoid pointless HID++ traffic.
        internal static readonly int[] Intervals = { 30, 60, 300, 3600, 43200, 86400 };
        internal static bool IsValidInterval(int seconds) {
            foreach (int n in Intervals) if (n == seconds) return true;
            return false;
        }
        internal static string IntervalLabel(int seconds) {
            switch (seconds) {
                case 30: return "30 秒";
                case 60: return "1 分钟";
                case 300: return "5 分钟";
                case 3600: return "1 小时";
                case 43200: return "12 小时";
                case 86400: return "24 小时";
                default: return seconds.ToString(CultureInfo.InvariantCulture) + " 秒";
            }
        }
        static string FilePath {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "G305Battery", "settings.txt"); }
        }
        internal static Settings Load() {
            var s = new Settings();
            try {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8)) {
                    int p = line.IndexOf('='); if (p <= 0) continue;
                    string k = line.Substring(0, p).Trim(), v = line.Substring(p + 1).Trim();
                    switch (k) {
                        case "theme": s.Theme = v; break;
                        case "style": s.Style = v; break;
                        case "low_alert": s.LowAlert = v == "1"; break;
                        // Accept any sane value (old builds allowed 10..3600), not just menu presets.
                        case "interval": int n; if (int.TryParse(v, out n) && n >= 10 && n <= 86400) s.Interval = n; break;
                    }
                }
            } catch (Exception) { }
            if (s.Theme != "light" && s.Theme != "dark") s.Theme = "auto";
            // Old builds stored "battery"; migrate them to the new capsule icon.
            if (s.Style != "number") s.Style = "capsule";
            return s;
        }
        internal void Save() {
            try {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, "theme=" + Theme + "\nstyle=" + Style + "\nlow_alert=" +
                    (LowAlert ? "1" : "0") + "\ninterval=" + Interval.ToString(CultureInfo.InvariantCulture) + "\n", new UTF8Encoding(false));
            } catch (Exception) { }
        }
        internal bool IsLight {
            get {
                if (Theme == "light") return true;
                if (Theme == "dark") return false;
                return Native.IsLightTheme();
            }
        }
    }

    // The executable embeds source/app.ico via csc /win32icon. Reuse it for the
    // tray menu and the About dialog; fall back to a battery icon if absent.
    internal static class Branding {
        static Icon cached;
        internal static Icon AppIcon {
            get {
                if (cached == null) {
                    try { cached = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch (Exception) { }
                    if (cached == null) cached = IconPainter.Make(90, false, false, false, true, false);
                }
                return (Icon)cached.Clone();
            }
        }
        internal static Image Logo(int px) {
            using (Icon icon = AppIcon)
            using (Icon sized = new Icon(icon, px, px)) return sized.ToBitmap();
        }
    }

    internal sealed class AboutForm : Form {
        readonly PictureBox picture = new PictureBox();
        internal AboutForm(string intervalText, string version) {
            Text = "关于 G305 Battery Tray";
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.White;
            ClientSize = new Size(470, 320);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;
            using (Icon app = Branding.AppIcon) Icon = (Icon)app.Clone();

            picture.Image = Branding.Logo(72);
            picture.Size = new Size(72, 72);
            picture.Location = new Point(24, 24);
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            Controls.Add(picture);

            Label title = MakeLabel("G305 Battery Tray", new Point(112, 22), new Size(330, 26), 15f, FontStyle.Bold, Color.FromArgb(18, 32, 46));
            Label sub = MakeLabel("罗技 G305 电量托盘  ·  " + version + "  ·  非官方小工具", new Point(112, 52), new Size(330, 20), 9f, FontStyle.Regular, Color.FromArgb(96, 110, 124));
            Label body = MakeLabel(
                "通过 HID++ 直接读取电量，免 G HUB、免驱动、免联网。\n" +
                "支持 0x1004 / 0x1000 / 0x1001 电量特性。\n" +
                "当前刷新间隔：" + intervalText + "；单击托盘图标可立即刷新。",
                new Point(112, 84), new Size(330, 100), 9f, FontStyle.Regular, Color.FromArgb(38, 50, 62));
            Controls.Add(title); Controls.Add(sub); Controls.Add(body);

            var rule = new Label { Location = new Point(24, 196), Size = new Size(422, 1), BackColor = Color.FromArgb(226, 231, 236), Text = "" };
            Controls.Add(rule);
            Controls.Add(MakeLabel("基于 gpw-battery-tray (MIT, xrfc) 的 HID 传输层改写；协议参考 OpenLogi 与 Solaar。",
                new Point(24, 202), new Size(422, 34), 8.5f, FontStyle.Regular, Color.FromArgb(88, 100, 112)));
            Controls.Add(MakeLabel("Logitech / logi / G305 为罗技公司商标；本程序图标为自制风格化标识，非罗技官方提供。",
                new Point(24, 236), new Size(422, 34), 8.5f, FontStyle.Regular, Color.FromArgb(120, 130, 140)));

            var ok = new Button { Text = "确定", Location = new Point(370, 284), Size = new Size(76, 26), DialogResult = DialogResult.OK };
            Controls.Add(ok); AcceptButton = ok; CancelButton = ok;
            ok.Click += delegate { Close(); };
        }
        static Label MakeLabel(string text, Point at, Size size, float em, FontStyle style, Color color) {
            return new Label { Text = text, Location = at, Size = size, AutoSize = false,
                Font = new Font("Microsoft YaHei UI", em, style, GraphicsUnit.Point), ForeColor = color };
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (picture.Image != null) picture.Image.Dispose();
                if (Icon != null) Icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class IconPainter {
        // Stage colours: the capsule itself changes colour as the AA cell drains.
        sealed class Stage {
            internal Color Fill, Text, Glow;
            internal int GlowAlpha;
            internal Stage(Color fill, Color text, Color glow, int glowAlpha) {
                Fill = fill; Text = text; Glow = glow; GlowAlpha = glowAlpha;
            }
        }
        static Stage StageFor(int? percent, bool charging, bool full, bool critical) {
            if (!percent.HasValue) return new Stage(Color.FromArgb(138, 144, 153), Color.White, Color.FromArgb(138, 144, 153), 12);
            if (critical) return new Stage(Color.FromArgb(199, 80, 80), Color.White, Color.FromArgb(226, 82, 82), 34);
            if (charging) return new Stage(Color.FromArgb(76, 159, 245), Color.White, Color.FromArgb(76, 159, 245), 34);
            if (full) return new Stage(Color.FromArgb(126, 212, 156), Color.FromArgb(22, 51, 34), Color.FromArgb(126, 212, 156), 28);
            int p = percent.Value;
            if (p >= 60) return new Stage(Color.FromArgb(237, 239, 242), Color.FromArgb(31, 34, 40), Color.FromArgb(237, 239, 242), 12);
            if (p >= 35) return new Stage(Color.FromArgb(244, 206, 104), Color.FromArgb(43, 33, 16), Color.FromArgb(244, 206, 104), 24);
            if (p >= 15) return new Stage(Color.FromArgb(240, 152, 74), Color.FromArgb(43, 24, 8), Color.FromArgb(240, 152, 74), 28);
            return new Stage(Color.FromArgb(226, 82, 82), Color.White, Color.FromArgb(226, 82, 82), 34);
        }
        internal static Color StageFill(int? percent, bool charging, bool full, bool critical) {
            return StageFor(percent, charging, full, critical).Fill;
        }
        static Color Mix(Color c, Color target, float amount) {
            return Color.FromArgb(c.A,
                (int)Math.Round(c.R + (target.R - c.R) * amount),
                (int)Math.Round(c.G + (target.G - c.G) * amount),
                (int)Math.Round(c.B + (target.B - c.B) * amount));
        }
        internal static Icon Make(int? percent, bool charging, bool full, bool critical, bool light, bool bigNumber) {
            using (var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb)) {
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    string digits = percent.HasValue ? percent.Value.ToString(CultureInfo.InvariantCulture) : "--";
                    if (bigNumber) {
                        // Optional high-legibility style: plain large digits on a solid tile.
                        // Transparent tile too: the ink follows the taskbar theme.
                        Color fore = light ? Color.FromArgb(24, 26, 30) : Color.FromArgb(243, 243, 243);
                        Color accent = critical ? Color.FromArgb(226, 82, 82)
                            : charging ? Color.FromArgb(76, 159, 245)
                            : !percent.HasValue ? (light ? Color.FromArgb(110, 116, 124) : Color.FromArgb(158, 164, 172))
                            : percent.Value <= 20 ? (light ? Color.FromArgb(200, 96, 24) : Color.FromArgb(255, 178, 84)) : fore;
                        DrawNumber(g, digits, accent, 0, 0, 27, 32, 26, 13);
                        if (percent.HasValue) DrawNumber(g, "%", accent, 24, 14, 8, 16, 11, 7);
                    } else {
                        Stage stage = StageFor(percent, charging, full, critical);
                        DrawCapsule(g, digits, stage, light);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { Native.DestroyIcon(handle); }
            }
        }
        // Horizontal Windows-style battery: rounded capsule, terminal nub,
        // gradient fill, glass highlight, soft shadow and a faint stage glow.
        static void DrawCapsule(Graphics g, string digits, Stage stage, bool light) {
            float x = 1.0f, y = 5.0f, w = 27.0f, h = 22.0f, r = 7.4f;
            Color top = Mix(stage.Fill, Color.White, 0.10f);
            Color bottom = Mix(stage.Fill, Color.Black, 0.08f);
            // 1. Faint outer glow, stronger for low / charging / error stages.
            for (int i = 3; i >= 1; i--) {
                int alpha = stage.GlowAlpha * (4 - i) / 6;
                using (GraphicsPath halo = RoundRect(x - i, y - i, w + 2 * i, h + 2 * i, r + i))
                using (var brush = new SolidBrush(Color.FromArgb(alpha, stage.Glow)))
                    g.FillPath(brush, halo);
            }
            // 2. Terminal nub, slightly darker than the capsule.
            float tx = x + w + 0.8f, ty = y + h * 0.30f, tw = 2.8f, th = h * 0.40f;
            using (GraphicsPath nub = RoundRect(tx, ty, tw, th, 1.3f))
            using (var brush = new SolidBrush(Mix(stage.Fill, Color.Black, 0.05f)))
                g.FillPath(brush, nub);
            // 3. Soft drop shadow.
            using (GraphicsPath shadow = RoundRect(x + 0.5f, y + 1.3f, w, h, r))
            using (var brush = new SolidBrush(Color.FromArgb(light ? 42 : 30, 0, 0, 0)))
                g.FillPath(brush, shadow);
            // 4. Capsule body with a subtle vertical gradient.
            using (GraphicsPath body = RoundRect(x, y, w, h, r))
            using (var brush = new LinearGradientBrush(new RectangleF(x, y, w, h), top, bottom, LinearGradientMode.Vertical))
                g.FillPath(brush, body);
            // 5. Glass highlight clipped to the capsule.
            using (GraphicsPath body = RoundRect(x, y, w, h, r)) {
                g.SetClip(body);
                using (var brush = new LinearGradientBrush(new RectangleF(x, y, w, h * 0.48f),
                    Color.FromArgb(58, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(brush, x, y, w, h * 0.48f);
                g.ResetClip();
                // 6. Hairline outline keeps the capsule readable on both tray themes.
                // A pale capsule needs a firmer outline on a light taskbar;
                // on a dark taskbar a faint light rim is enough.
                Color edge = light ? Color.FromArgb(56, 18, 20, 24) : Color.FromArgb(42, 255, 255, 255);
                using (var pen = new Pen(edge, 0.7f)) g.DrawPath(pen, body);
            }
            // 7. Digits: dark on pale stages, white on saturated ones.
            DrawNumber(g, digits, stage.Text, x + 1.0f, y, w - 2.0f, h, 22, 11);
        }
        static GraphicsPath RoundRect(float x, float y, float w, float h, float r) {
            float d = r * 2f;
            var path = new GraphicsPath();
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
        // Draw by point instead of a clipping rectangle: a 32px icon is scaled
        // down to 16px in the tray, so every glyph must survive intact.
        static void DrawNumber(Graphics g, string text, Color color, float x, float y, float w, float h, float maxSize, float minSize) {
            float size = maxSize;
            Font font = null; SizeF measured = SizeF.Empty;
            try {
                StringFormat format = StringFormat.GenericTypographic;
                while (size >= minSize) {
                    Font candidate = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
                    SizeF m = g.MeasureString(text, candidate, new PointF(0, 0), format);
                    if (m.Width <= w - 0.5f && m.Height <= h) { font = candidate; measured = m; break; }
                    candidate.Dispose(); size -= 0.5f;
                }
                if (font == null) {
                    font = new Font("Segoe UI", minSize, FontStyle.Bold, GraphicsUnit.Pixel);
                    measured = g.MeasureString(text, font, new PointF(0, 0), format);
                }
                using (var brush = new SolidBrush(color))
                    g.DrawString(text, font, brush, x + (w - measured.Width) / 2f,
                        y + (h - measured.Height) / 2f, format);
            } finally { if (font != null) font.Dispose(); }
        }
    }

    // Watches for USB device-tree changes so re-plugging the LIGHTSPEED receiver
    // refreshes the reading immediately even with a 12/24 hour poll interval.
    internal sealed class DeviceWatcher : NativeWindow {
        internal event Action Changed;
        internal DeviceWatcher() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m) {
            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVICEARRIVAL = 0x8000;
            const int DBT_DEVNODES_CHANGED = 0x0007;
            if (m.Msg == WM_DEVICECHANGE) {
                int code = m.WParam.ToInt32();
                if (code == DBT_DEVICEARRIVAL || code == DBT_DEVNODES_CHANGED) {
                    Action action = Changed; if (action != null) action();
                }
            }
            base.WndProc(ref m);
        }
    }

    internal sealed class Tray : ApplicationContext {
        internal const string Version = "1.2";
        readonly NotifyIcon icon = new NotifyIcon();
        readonly Control dispatcher = new Control();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem status = new ToolStripMenuItem("正在读取 G305…");
        readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("开机启动");
        readonly ToolStripMenuItem alertItem = new ToolStripMenuItem("低电量提醒 (≤20%)");
        readonly DeviceWatcher watcher = new DeviceWatcher();
        readonly Settings settings;
        Reading current, lastGood;
        Icon picture; bool busy, closed, wasLow;
        DateTime lastSuccess, lastEventRefresh;
        readonly string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "G305 电量.lnk");

        internal Tray(Settings s, int intervalSeconds) {
            settings = s;
            IntPtr handle = dispatcher.Handle;
            var menu = new ContextMenuStrip();
            menu.ImageScalingSize = new Size(16, 16);
            using (Image logo = Branding.Logo(16)) status.Image = (Image)logo.Clone();
            status.Enabled = false; menu.Items.Add(status);
            menu.Items.Add("立即刷新", null, delegate { Refresh(); });
            menu.Items.Add("查看读数", null, delegate { Details(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(SubMenu("图标样式",
                RadioItem("胶囊电池 + 数字", "capsule", StyleClicked), RadioItem("大号数字", "number", StyleClicked)));
            menu.Items.Add(SubMenu("图标配色",
                RadioItem("跟随系统", "auto", ThemeClicked), RadioItem("浅色", "light", ThemeClicked), RadioItem("深色", "dark", ThemeClicked)));
            menu.Items.Add(SubMenu("刷新间隔",
                RadioItem("30 秒", "30", IntervalClicked), RadioItem("1 分钟", "60", IntervalClicked),
                RadioItem("5 分钟", "300", IntervalClicked), new ToolStripSeparator(),
                RadioItem("1 小时", "3600", IntervalClicked), RadioItem("12 小时", "43200", IntervalClicked),
                RadioItem("24 小时", "86400", IntervalClicked)));
            alertItem.CheckOnClick = true; alertItem.Checked = settings.LowAlert;
            alertItem.Click += delegate { settings.LowAlert = alertItem.Checked; settings.Save(); };
            menu.Items.Add(alertItem);
            startupItem.Checked = File.Exists(shortcut);
            startupItem.Click += delegate { SetStartup(); };
            menu.Items.Add(startupItem);
            menu.Opening += delegate { startupItem.Checked = File.Exists(shortcut); UpdateChecks(menu); };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于", null, delegate { About(); });
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            icon.ContextMenuStrip = menu;
            icon.Text = "G305 电量：正在读取";
            SetPicture(null, false, false, false);
            icon.Visible = true;
            icon.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Refresh(); };
            icon.DoubleClick += delegate { Details(); };
            timer.Interval = (intervalSeconds > 0 ? intervalSeconds : settings.Interval) * 1000;
            timer.Tick += delegate { Refresh(); }; timer.Start();
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
            SystemEvents.SessionSwitch += SessionSwitched;
            watcher.Changed += delegate { RequestRefresh(); };
            UpdateChecks(menu);
            Refresh();
        }
        ToolStripMenuItem RadioItem(string label, string value, EventHandler handler) {
            var item = new ToolStripMenuItem(label); item.Tag = value; item.Click += handler; return item;
        }
        static ToolStripMenuItem SubMenu(string label, params ToolStripItem[] items) {
            var parent = new ToolStripMenuItem(label);
            parent.DropDownItems.AddRange(items);
            return parent;
        }
        // Long poll intervals must not leave the icon stale for hours after the
        // user comes back or re-plugs the receiver. Throttle the burst of
        // WM_DEVICECHANGE messages that a single USB insertion produces.
        void RequestRefresh() {
            if (closed || (DateTime.UtcNow - lastEventRefresh).TotalSeconds < 3) return;
            lastEventRefresh = DateTime.UtcNow;
            try { dispatcher.BeginInvoke((Action)delegate { if (!closed) Refresh(); }); }
            catch (InvalidOperationException) { }
        }
        void SessionSwitched(object sender, SessionSwitchEventArgs e) {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon) RequestRefresh();
        }
        void UpdateChecks(ContextMenuStrip menu) {
            foreach (ToolStripItem item in menu.Items) {
                var parent = item as ToolStripMenuItem;
                if (parent == null || !parent.HasDropDownItems) continue;
                foreach (ToolStripItem child in parent.DropDownItems) {
                    var childItem = child as ToolStripMenuItem;
                    if (childItem == null || childItem.Tag == null) continue;
                    string v = childItem.Tag.ToString();
                    // Timer.Interval is milliseconds, menu tags are seconds.
                    childItem.Checked = (parent.Text == "图标样式" && v == settings.Style) ||
                        (parent.Text == "图标配色" && v == settings.Theme) ||
                        (parent.Text == "刷新间隔" && v == (timer.Interval / 1000).ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        void StyleClicked(object sender, EventArgs e) {
            settings.Style = ((ToolStripMenuItem)sender).Tag.ToString(); settings.Save(); Apply(current, false);
        }
        void ThemeClicked(object sender, EventArgs e) {
            settings.Theme = ((ToolStripMenuItem)sender).Tag.ToString(); settings.Save(); Apply(current, false);
        }
        void IntervalClicked(object sender, EventArgs e) {
            int n; if (!int.TryParse(((ToolStripMenuItem)sender).Tag.ToString(), out n)) return;
            if (!Settings.IsValidInterval(n)) return;
            timer.Stop(); timer.Interval = n * 1000; timer.Start();  // restart the countdown
            settings.Interval = n; settings.Save(); Apply(current, false);
        }
        void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
            if (closed || settings.Theme != "auto") return;
            try { dispatcher.BeginInvoke((Action)delegate { if (!closed) Apply(current, false); }); }
            catch (InvalidOperationException) { }
        }
        void PowerChanged(object sender, PowerModeChangedEventArgs e) {
            if (e.Mode == PowerModes.Resume && !closed) {
                try { dispatcher.BeginInvoke((Action)delegate { if (!closed) Refresh(); }); }
                catch (InvalidOperationException) { }
            }
        }
        void Refresh() {
            if (busy || closed) return; busy = true;
            Task.Run(delegate {
                Reading next = Battery.Read();
                if (closed) return;
                try { dispatcher.BeginInvoke((Action)delegate { if (!closed) { busy = false; Apply(next, true); } }); }
                catch (InvalidOperationException) { }
            });
        }
        void Apply(Reading next, bool notify) {
            if (next == null) return;
            current = next;
            bool critical = next.Ok && next.Critical;
            if (next.Ok) {
                lastSuccess = next.Time; lastGood = next;
                status.Text = "G305：" + next.LevelText + " · " + next.StatusText;
                icon.Text = "G305 " + next.LevelText + " · " + next.StatusText + " | " + next.Time.ToString("HH:mm:ss");
                SetPicture(next.Percentage, next.Charging, next.Full, critical);
                bool low = next.Percentage.HasValue && next.Percentage.Value <= 20;
                if (notify && settings.LowAlert && low && !wasLow) {
                    try { icon.ShowBalloonTip(5000, "G305 电量偏低", "剩余约 " + next.Percentage.Value + "%，建议更换 AA 电池。", ToolTipIcon.Warning); }
                    catch (Exception) { }
                }
                wasLow = low;
            } else {
                status.Text = "G305：休眠 / 离线";
                icon.Text = "G305 休眠 / 离线" + (lastSuccess == default(DateTime) ? "" : " | 上次成功 " + lastSuccess.ToString("HH:mm:ss"));
                SetPicture(null, false, false, false);
            }
        }
        void SetPicture(int? percent, bool charging, bool full, bool critical) {
            Icon next = IconPainter.Make(percent, charging, full, critical, settings.IsLight, settings.Style == "number");
            Icon old = picture; picture = next; icon.Icon = next;
            if (old != null) old.Dispose();
        }
        void Details() {
            string text;
            if (current == null) text = "正在读取，请稍候。";
            else if (!current.Ok) text = current.Error +
                (lastGood == null ? "" : "\n\n上次成功（不是当前电量）：" + lastGood.LevelText + "，" + lastSuccess.ToString("yyyy-MM-dd HH:mm:ss"));
            else text = "罗技 G305 电量\n\n设备：" + current.Name +
                "\n电量：" + current.LevelText + (current.IsEstimate ? "（估算）" : "（固件上报）") +
                "\n状态：" + current.StatusText + " (0x" + current.StatusCode.ToString("X2") + ")" +
                "\n协议：" + current.SourceName +
                "\n接收器：" + current.Product + " · " + current.Transport + " · slot " + current.Slot +
                "\n原始回复：0x" + current.Raw +
                (current.Millivolts > 0 ? "\n电压：" + current.Millivolts + " mV" : "") +
                "\n更新时间：" + current.Time.ToString("yyyy-MM-dd HH:mm:ss") +
                "\n\n直接通过 HID++ 读取，不依赖 G HUB 或第三方驱动。\nG305 使用一节 AA 电池；0x1000 上报的是固件百分比，\n0x1004 / 0x1001 为兼容其它电量特性的回退路径。\n每 " + Settings.IntervalLabel(timer.Interval / 1000) + "自动刷新；\n单击图标、解锁屏幕或重新插入接收器会立即刷新一次。";
            MessageBox.Show(text, "G305 电量", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        void About() {
            using (var form = new AboutForm(Settings.IntervalLabel(timer.Interval / 1000), "v" + Version)) form.ShowDialog();
        }
        void SetStartup() {
            try {
                if (File.Exists(shortcut)) File.Delete(shortcut);
                else {
                    object shell = null, link = null;
                    try {
                        Type type = Type.GetTypeFromProgID("WScript.Shell"); shell = Activator.CreateInstance(type);
                        link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcut });
                        Type lt = link.GetType();
                        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
                        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { AppDomain.CurrentDomain.BaseDirectory });
                        lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "G305 电量托盘" });
                        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                    } finally {
                        if (link != null) Marshal.FinalReleaseComObject(link);
                        if (shell != null) Marshal.FinalReleaseComObject(shell);
                    }
                }
                startupItem.Checked = File.Exists(shortcut);
            } catch (Exception ex) { MessageBox.Show("修改开机启动失败：" + ex.Message, "G305 电量"); }
        }
        internal void Diagnose(string path) {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var nativeWindow = (NativeWindow)typeof(NotifyIcon).GetField("window", flags).GetValue(icon);
            var id = new Native.IconIdentifier { Size = (uint)Marshal.SizeOf(typeof(Native.IconIdentifier)),
                Window = nativeWindow.Handle, Id = (uint)(int)typeof(NotifyIcon).GetField("id", flags).GetValue(icon) };
            Native.Rect rect; int result = Native.Shell_NotifyIconGetRect(ref id, out rect);
            var desk = new StringBuilder(256); var station = new StringBuilder(256); uint needed;
            Native.GetUserObjectInformationW(Native.GetThreadDesktop(Native.GetCurrentThreadId()), 2, desk, 512, out needed);
            Native.GetUserObjectInformationW(Native.GetProcessWindowStation(), 2, station, 512, out needed);
            string added = typeof(NotifyIcon).GetField("added", flags).GetValue(icon).ToString();
            File.WriteAllText(path, "{\"added\":\"" + added + "\",\"desktop\":\"" + desk + "\",\"station\":\"" + station +
                "\",\"rect_result\":" + result + ",\"left\":" + rect.Left + ",\"top\":" + rect.Top + ",\"right\":" + rect.Right +
                ",\"bottom\":" + rect.Bottom + ",\"interval_seconds\":" + (timer.Interval / 1000) +
                ",\"interval_label\":\"" + Settings.IntervalLabel(timer.Interval / 1000) + "\"" +
                ",\"reading\":" + (current == null ? "null" : current.Json()) + "}", new UTF8Encoding(false));
        }
        protected override void ExitThreadCore() {
            if (closed) return; closed = true;
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.UserPreferenceChanged -= PreferenceChanged;
            SystemEvents.SessionSwitch -= SessionSwitched;
            watcher.DestroyHandle();
            timer.Stop(); timer.Dispose();
            icon.Visible = false; icon.Dispose();
            if (status.Image != null) status.Image.Dispose();
            if (picture != null) picture.Dispose();
            dispatcher.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class Program {
        static void Require(bool value, string text) { if (!value) throw new Exception("TEST FAILED: " + text); }
        static int SelfTest() {
            Require(Marshal.SizeOf(typeof(Native.Caps)) == 64, "HID caps layout");
            byte[] r = HidSession.Decode(new byte[] { 0x11, 1, 6, 0x5d, 90, 80, 0 }, 1, 6, 0x5d);
            Require(r != null && r.Length == 3, "HID++ reply matching");
            Require(HidSession.Decode(new byte[] { 0x11, 1, 6, 0x5e, 90, 80, 0 }, 1, 6, 0x5d) == null, "software ID matching");
            Require(HidSession.Decode(new byte[] { 0x11, 2, 6, 0x5d, 90, 80, 0 }, 1, 6, 0x5d) == null, "device index matching");
            bool threw = false;
            try { HidSession.Decode(new byte[] { 0x10, 1, 0xff, 6, 0x5d, 9 }, 1, 6, 0x5d); } catch (IOException) { threw = true; }
            Require(threw, "HID++ error frame");
            Reading legacy = Reading.ParseLegacy(new byte[] { 90, 80, 0 });
            Require(legacy.Ok && legacy.Percentage == 90 && legacy.NextLevel == 80 && !legacy.Charging, "0x1000 parse (G305 90%)");
            Require(Reading.ParseLegacy(new byte[] { 0, 0, 3 }).Percentage == 100, "0x1000 full status");
            Require(Reading.ParseLegacy(new byte[] { 15, 10, 0 }).Percentage == 15, "0x1000 low level");
            Require(Reading.ParseLegacy(new byte[] { 0, 0, 1 }).Charging, "0x1000 charging status");
            Require(Reading.StatusName(6) == "温度异常", "status mapping");
            Reading unified = Reading.ParseUnified(new byte[] { 0x0f, 0x02 }, new byte[] { 87, 4, 0 });
            Require(unified.Percentage == 87 && unified.Source == BatterySource.UnifiedBattery, "0x1004 parse");
            Require(Reading.ParseUnified(new byte[] { 0, 0 }, new byte[] { 0, 4, 0 }).Percentage == 60, "0x1004 level bucket fallback");
            Require(Reading.ParseUnified(new byte[] { 0, 0 }, new byte[] { 0, 1, 0 }).Percentage == 5, "0x1004 critical bucket");
            Reading voltage = Reading.ParseVoltage(new byte[] { 0x10, 0x02, 0x00 });
            Require(voltage.Millivolts == 4098 && voltage.Percentage == 93 && voltage.IsEstimate, "0x1001 Li-Po curve");
            Reading aa = Reading.ParseVoltage(new byte[] { 0x05, 0xAA, 0x00 });
            Require(aa.Millivolts == 1450 && aa.Percentage >= 85 && aa.Percentage <= 90, "0x1001 AA fallback curve");
            threw = false; try { Reading.ParseVoltage(new byte[] { 0, 0, 0 }); } catch (IOException) { threw = true; }
            Require(threw, "invalid voltage rejection");
            Require(Curves.Estimate(3400, false) == 0 && Curves.Estimate(4300, false) == 100, "Li-Po curve bounds");
            Require(Reading.ParseLegacy(new byte[] { 90, 80, 0 }).Json().Contains("\"percentage\":90"), "JSON output");
            Color high = IconPainter.StageFill(90, false, false, false);
            Require(high != IconPainter.StageFill(15, false, false, false), "stage colour: high vs low");
            Require(high != IconPainter.StageFill(90, true, false, false), "stage colour: charging");
            Require(high != IconPainter.StageFill(90, false, true, false), "stage colour: full");
            Require(high != IconPainter.StageFill(null, false, false, false), "stage colour: offline");
            using (var light = IconPainter.Make(90, false, false, false, true, false))
            using (var dark = IconPainter.Make(15, false, false, false, false, false))
            using (var big = IconPainter.Make(null, false, false, false, true, true)) {
                Require(light.Width == 32 && dark.Width == 32 && big.Width == 32, "tray icon lifecycle");
                using (Bitmap bmp = light.ToBitmap()) Require(bmp.GetPixel(0, 0).A == 0, "transparent tray icon corners");
                using (Bitmap bmp = big.ToBitmap()) Require(bmp.GetPixel(0, 0).A == 0, "transparent big-number corners");
            }
            Require(Settings.IsValidInterval(43200) && Settings.IsValidInterval(86400), "12h / 24h poll intervals");
            Require(!Settings.IsValidInterval(86401) && !Settings.IsValidInterval(9), "poll interval bounds");
            Require(Settings.IntervalLabel(86400) == "24 小时" && Settings.IntervalLabel(43200) == "12 小时", "interval labels");
            using (var longTimer = new System.Windows.Forms.Timer()) {
                longTimer.Interval = 86400 * 1000;
                Require(longTimer.Interval == 86400000 && longTimer.Interval / 1000 == 86400, "WinForms timer accepts 24h");
            }
            using (Icon brand = Branding.AppIcon) Require(brand != null && brand.Width >= 16, "embedded application logo");
            using (var about = new AboutForm("24 小时", "v1.2")) Require(about.Controls.Count >= 6, "about dialog with logo");
            Console.WriteLine("PASS: 31 checks (HID++ frames, 0x1000/0x1004/0x1001, curves, status, JSON, stages, transparency, icons, intervals, branding, about)");
            return 0;
        }
        static string Value(string[] args, string prefix, string fallback) {
            foreach (string a in args) if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length);
            return fallback;
        }
        static int LogoPreview(string[] args) {
            string path = "g305-logo-preview.png";
            foreach (string a in args) if (!a.StartsWith("--", StringComparison.Ordinal) && !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) path = a;
            using (Icon icon = Branding.AppIcon)
            using (Bitmap bmp = icon.ToBitmap()) {
                bmp.Save(path, ImageFormat.Png);
                Console.WriteLine("embedded exe icon: " + icon.Width + "x" + icon.Height + " -> " + Path.GetFullPath(path));
            }
            return 0;
        }
        static int IconPreview(string[] args) {
            string path = "g305-icon-preview.png";
            foreach (string a in args) if (!a.StartsWith("--", StringComparison.Ordinal) && !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) path = a;
            int pct;
            if (!int.TryParse(Value(args, "--pct=", "90"), out pct)) pct = -1;
            bool light = Value(args, "--theme=", "light") != "dark";
            bool big = Value(args, "--style=", "capsule") == "number";
            bool charging = args.Contains("--charging"), full = args.Contains("--full"), critical = args.Contains("--critical");
            using (Icon icon = IconPainter.Make(pct < 0 ? (int?)null : pct, charging, full, critical, light, big))
            using (Bitmap bmp = icon.ToBitmap()) {
                bmp.Save(path, ImageFormat.Png);
                Console.WriteLine("icon preview: " + Path.GetFullPath(path));
            }
            return 0;
        }
        [STAThread] static int Main(string[] args) {
            if (args.Contains("--self-test")) return SelfTest();
            if (args.Contains("--once")) { Reading r = Battery.Read(); Console.OutputEncoding = new UTF8Encoding(false); Console.WriteLine(r.Json()); return r.Ok ? 0 : 1; }
            if (args.Contains("--scan")) { Console.OutputEncoding = new UTF8Encoding(false); Console.Write(Battery.Scan()); return 0; }
            if (args.Contains("--icon-preview")) return IconPreview(args);
            if (args.Contains("--logo-preview")) return LogoPreview(args);
            if (args.Contains("--quit")) {
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\G305Battery.Quit")) signal.Set();
                return 0;
            }
            int interval = 0; int.TryParse(Value(args, "--interval=", "0"), out interval);
            if (interval < 10 || interval > 86400) interval = 0;   // guard against n*1000 overflow
            // Debug-only: allow a second short-lived instance while the real tray is running.
            bool allowSecond = args.Contains("--no-single-instance");
            bool created;
            using (var mutex = new Mutex(true, "Local\\G305Battery.Instance", out created)) {
                if (!created && !allowSecond) return 0;
                try {
                    Native.SetProcessDPIAware(); Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Settings settings = Settings.Load();
                    using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\G305Battery.Quit"))
                    using (var context = new Tray(settings, interval)) {
                        var poll = new System.Windows.Forms.Timer(); poll.Interval = 300;
                        poll.Tick += delegate { if (quit.WaitOne(0)) context.ExitThread(); }; poll.Start();
                        System.Windows.Forms.Timer diagnostic = null;
                        if (args.Contains("--diagnose")) {
                            string diagnosticPath = args[args.Length - 1];
                            diagnostic = new System.Windows.Forms.Timer(); diagnostic.Interval = 2000;
                            diagnostic.Tick += delegate { diagnostic.Stop(); context.Diagnose(diagnosticPath); }; diagnostic.Start();
                        }
                        try { Application.Run(context); } finally { poll.Dispose(); if (diagnostic != null) diagnostic.Dispose(); }
                    }
                } catch (Exception ex) { MessageBox.Show(ex.Message, "G305 电量", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                finally { if (created) mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }
}
