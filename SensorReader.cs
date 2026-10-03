using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SysMonitor
{
    /// <summary>一次采样得到的传感器读数。</summary>
    public class SensorReading
    {
        public bool Ok;
        public float? CpuLoad;
        public float? CpuClockMhz;
        public float? CpuTempC;
        public float? GpuLoad;
        public float? GpuClockMhz;
        public float? GpuTempC;
        public float? GpuFanRpm;
        public float? GpuMemUsedMb;   // 单位 MB
        public float? GpuMemTotalMb;  // 单位 MB
        public List<(string Name, double Rpm)> Fans = new();
    }

    /// <summary>
    /// 通过 LibreHardwareMonitorLib 读取 CPU/GPU 温度、频率、风扇转速等硬件传感器。
    /// 需要管理员权限（Ring0 驱动）才能读到主板 SuperIO 与 CPU MSR 传感器。
    /// </summary>
    public class SensorReader : IDisposable
    {
        private readonly Computer _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsBatteryEnabled = false,
            IsPsuEnabled = false
        };
        private bool _opened;

        public SensorReader()
        {
            try
            {
                _computer.Open();
                _opened = true;
            }
            catch
            {
                _opened = false;
            }
        }

        public void Refresh()
        {
            if (!_opened) return;
            foreach (var hw in _computer.Hardware)
            {
                try { hw.Update(); } catch { }
                foreach (var sub in hw.SubHardware)
                {
                    try { sub.Update(); } catch { }
                }
            }
        }

        public SensorReading Read()
        {
            var r = new SensorReading();
            if (!_opened) return r;

            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            r.CpuLoad = Find(hw, SensorType.Load, "Total", "Core")?.Value;
                            r.CpuClockMhz = Find(hw, SensorType.Clock, "Package", "Core", "Frequency")?.Value;
                            r.CpuTempC = Find(hw, SensorType.Temperature, "Tctl", "Package", "Core")?.Value;
                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                        case HardwareType.GpuIntel:
                            r.GpuLoad ??= Find(hw, SensorType.Load, "Core", "GPU")?.Value;
                            r.GpuClockMhz ??= Find(hw, SensorType.Clock, "Core", "GPU")?.Value;
                            r.GpuTempC ??= Find(hw, SensorType.Temperature, "Core", "GPU")?.Value;
                            r.GpuFanRpm ??= Find(hw, SensorType.Fan, "Fan")?.Value;
                            if (r.GpuMemUsedMb == null && Find(hw, SensorType.SmallData, "Memory Used") is { Value: float used })
                                r.GpuMemUsedMb = used / 1048576f;
                            if (r.GpuMemTotalMb == null && Find(hw, SensorType.SmallData, "Memory Total") is { Value: float total })
                                r.GpuMemTotalMb = total / 1048576f;
                            break;

                        case HardwareType.Motherboard:
                        case HardwareType.SuperIO:
                        case HardwareType.EmbeddedController:
                            foreach (var s in hw.Sensors)
                            {
                                if (s.SensorType == SensorType.Fan && s.Value is float fv && fv > 0)
                                    r.Fans.Add((s.Name, fv));
                            }
                            break;
                    }
                }
                catch { }
            }

            // 去重同名风扇
            r.Fans = r.Fans.GroupBy(f => f.Name).Select(g => g.First()).ToList();

            // CPU 温度/频率兜底：LHM 读不到时尝试 WMI（ACPI 热区 / 处理器信息）
            if (r.CpuTempC == null || r.CpuTempC <= 0)
                r.CpuTempC = WmiCpuTempC();
            if (r.CpuClockMhz == null || r.CpuClockMhz <= 0)
                r.CpuClockMhz = WmiCpuClockMhz();

            r.Ok = r.CpuLoad != null || r.CpuClockMhz != null || r.CpuTempC != null
                || r.GpuLoad != null || r.GpuTempC != null || r.GpuClockMhz != null;
            return r;
        }

        /// <summary>WMI 兜底：读 ACPI 热区温度（10 分之 K，转摄氏）。</summary>
        private static float? WmiCpuTempC()
        {
            try
            {
                using var mos = new System.Management.ManagementObjectSearcher(
                    "root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (var o in mos.Get())
                {
                    var raw = (uint)o["CurrentTemperature"];
                    var c = (float)raw / 10f - 273.15f;
                    if (c > 0 && c < 120) return (float)Math.Round(c, 1);
                }
            }
            catch { }
            return null;
        }

        /// <summary>WMI 兜底：读处理器当前频率（MHz）。</summary>
        private static float? WmiCpuClockMhz()
        {
            try
            {
                using var mos = new System.Management.ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT CurrentClockSpeed FROM Win32_Processor");
                foreach (var o in mos.Get())
                {
                    if (o["CurrentClockSpeed"] is uint mhz && mhz > 100) return mhz;
                }
            }
            catch { }
            return null;
        }

        private static ISensor? Find(IHardware hw, SensorType type, params string[] preferred)
        {
            var list = hw.Sensors.Where(s => s.SensorType == type).ToList();
            if (list.Count == 0) return null;
            foreach (var p in preferred)
            {
                var hit = list.FirstOrDefault(s => s.Name != null && s.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit;
            }
            // 频率传感器避免误取到外频 Bus Speed
            var first = list.FirstOrDefault(s => s.Name != null && s.Name.IndexOf("Bus", StringComparison.OrdinalIgnoreCase) < 0);
            return first ?? list[0];
        }

        // ---------------- 风扇调速（依赖主板 SuperIO/EC 芯片支持） ----------------

        /// <summary>返回可软件调速的风扇列表（空 = 本机硬件不支持调速）。</summary>
        public List<string> GetControllableFans()
        {
            var names = new List<string>();
            if (!_opened) return names;
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Fan || s.Control == null) continue;
                        names.Add($"{hw.Name} / {s.Name}");
                    }
                }
                catch { }
            }
            return names;
        }

        /// <summary>把所有可软件调速的风扇设为指定百分比（0~100）。</summary>
        public void SetFanPercent(float pct)
        {
            if (!_opened) return;
            pct = Math.Clamp(pct, 0, 100);
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Fan || s.Control == null) continue;
                        try { s.Control.SetSoftware(pct / 100f); } catch { }
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_opened)
            {
                try { _computer.Close(); } catch { }
                _opened = false;
            }
        }

        // ---------------- 系统内存 ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MemoryStatusEx
        {
            public uint dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        public static (ulong total, ulong used, uint load) GetMemory()
        {
            var m = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(m) && m.ullTotalPhys > 0)
                return (m.ullTotalPhys, m.ullTotalPhys - m.ullAvailPhys, m.dwMemoryLoad);
            return (0, 0, 0);
        }

        // ---------------- 网络速度（整机上下行） ----------------

        private static long _prevRx = -1, _prevTx = -1;
        private static DateTime _prevNetTime = DateTime.UtcNow;

        public static (double downMBps, double upMBps) GetNetworkSpeeds()
        {
            long rx = 0, tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var t = ni.NetworkInterfaceType;
                if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var st = ni.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
                catch { }
            }
            var now = DateTime.UtcNow;
            var dt = (now - _prevNetTime).TotalSeconds;
            double down = 0, up = 0;
            if (dt >= 0.5 && _prevRx >= 0)
            {
                down = Math.Max(0, rx - _prevRx) / dt / 1048576.0;
                up = Math.Max(0, tx - _prevTx) / dt / 1048576.0;
            }
            _prevRx = rx;
            _prevTx = tx;
            _prevNetTime = now;
            return (down, up);
        }
    }
}
