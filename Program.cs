using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SysMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest();
                return;
            }

            Application.Run(new MainForm());
        }

        /// <summary>自检模式：初始化传感器、采样一次并写出结果文件后退出（用于验证硬件读取）。</summary>
        private static void RunSelfTest()
        {
            var sb = new StringBuilder();
            try
            {
                using var sr = new SensorReader();
                sr.Refresh();
                var s = sr.Read();
                sb.AppendLine("SensorAvailable=" + s.Ok);
                sb.AppendLine("CpuLoad=" + (s.CpuLoad?.ToString("0.0") ?? "N/A"));
                sb.AppendLine("CpuClockMhz=" + (s.CpuClockMhz?.ToString("0") ?? "N/A"));
                sb.AppendLine("CpuTempC=" + (s.CpuTempC?.ToString("0.0") ?? "N/A"));
                sb.AppendLine("GpuLoad=" + (s.GpuLoad?.ToString("0.0") ?? "N/A"));
                sb.AppendLine("GpuClockMhz=" + (s.GpuClockMhz?.ToString("0") ?? "N/A"));
                sb.AppendLine("GpuTempC=" + (s.GpuTempC?.ToString("0.0") ?? "N/A"));
                sb.AppendLine("GpuFanRpm=" + (s.GpuFanRpm?.ToString("0") ?? "N/A"));
                sb.AppendLine("GpuMemUsedMb=" + (s.GpuMemUsedMb?.ToString("0") ?? "N/A"));
                sb.AppendLine("GpuMemTotalMb=" + (s.GpuMemTotalMb?.ToString("0") ?? "N/A"));
                foreach (var f in s.Fans) sb.AppendLine("Fan: " + f.Name + "=" + f.Rpm.ToString("0"));
                var ctrl = sr.GetControllableFans();
                sb.AppendLine("ControllableFans=" + ctrl.Count);
                foreach (var c in ctrl) sb.AppendLine("CtrlFan: " + c);
                var (total, used, load) = SensorReader.GetMemory();
                sb.AppendLine("MemoryTotalMB=" + (total >> 20) + " UsedMB=" + (used >> 20) + " Load=" + load);
                var (down, up) = SensorReader.GetNetworkSpeeds();
                sb.AppendLine("NetDownMBs=" + down.ToString("0.000") + " NetUpMBs=" + up.ToString("0.000"));
                sb.AppendLine("ProcessCount=" + Process.GetProcesses().Length);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var raw = ProcessScanner.Scan();
                sw.Stop();
                sb.AppendLine("ScanCount=" + raw.Count + " ScanMs=" + sw.ElapsedMilliseconds);
                // DPI / 屏幕诊断
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    sb.AppendLine("Dpi=" + g.DpiX.ToString("0.0"));
                sb.AppendLine("Screen=" + Screen.PrimaryScreen?.Bounds);
                sb.AppendLine("WorkingArea=" + Screen.PrimaryScreen?.WorkingArea);
            }
            catch (Exception ex)
            {
                sb.AppendLine("SELFTEST_ERROR=" + ex);
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), sb.ToString(), Encoding.UTF8);
        }
    }
}
