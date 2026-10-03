using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysMonitor
{
    /// <summary>进程快照原始数据。</summary>
    public struct RawProc
    {
        public string Name;
        public int Pid;
        public long CpuTicks;
        public long MemBytes;
    }

    /// <summary>用于界面展示的进程数据。</summary>
    public struct ProcInfo
    {
        public string Name;
        public int Pid;
        public double Cpu;
        public long MemMb;
    }

    /// <summary>
    /// 用 NtQuerySystemInformation 一次性枚举全部进程（一次系统调用，毫秒级），
    /// 避免 Process.GetProcesses() 在进程数极多时的高昂开销；失败时回退到托管 API。
    /// </summary>
    public static class ProcessScanner
    {
        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        // Win10 1903+ 的 SYSTEM_PROCESS_INFORMATION x64 布局
        [StructLayout(LayoutKind.Sequential)]
        private struct SpiEntry
        {
            public uint NextEntryOffset;
            public uint NumberOfThreads;
            public long WorkingSetPrivate;
            public uint HardFaultCount;
            public uint NumberOfThreadsHighWatermark;
            public ulong CycleTime;
            public long CreateTime;
            public long UserTime;
            public long KernelTime;
            public ushort ImageNameLength;
            public ushort ImageNameMaxLength;
            public IntPtr ImageNameBuffer;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
            public uint HandleCount;
            public uint SessionId;
            public IntPtr UniqueProcessKey;
            public IntPtr PeakVirtualSize;
            public IntPtr VirtualSize;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
        }

        public static List<RawProc> Scan()
        {
            var list = new List<RawProc>(512);
            if (!TryNtQuery(list)) Fallback(list);
            return list;
        }

        private static bool TryNtQuery(List<RawProc> list)
        {
            int retLen = 0;
            int status = NtQuerySystemInformation(SystemProcessInformation, IntPtr.Zero, 0, out retLen);
            if (status != StatusInfoLengthMismatch || retLen <= 0) return false;

            int size = retLen + 65536; // 预留新进程增长空间
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                status = NtQuerySystemInformation(SystemProcessInformation, buf, size, out retLen);
                if (status != 0) return false;

                IntPtr p = buf;
                while (true)
                {
                    var e = Marshal.PtrToStructure<SpiEntry>(p);
                    long pid = e.UniqueProcessId.ToInt64();
                    if (pid > 0 && pid <= int.MaxValue)
                    {
                        string name = "";
                        if (e.ImageNameBuffer != IntPtr.Zero && e.ImageNameLength > 0)
                        {
                            try { name = Marshal.PtrToStringUni(e.ImageNameBuffer, e.ImageNameLength / 2) ?? ""; }
                            catch { name = ""; }
                        }
                        if (string.IsNullOrEmpty(name)) name = "(System)";
                        list.Add(new RawProc
                        {
                            Name = name,
                            Pid = (int)pid,
                            CpuTicks = e.UserTime + e.KernelTime,
                            MemBytes = e.WorkingSetSize.ToInt64()
                        });
                    }
                    if (e.NextEntryOffset == 0) break;
                    p = IntPtr.Add(p, (int)e.NextEntryOffset);
                }
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static void Fallback(List<RawProc> list)
        {
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return; }
            try
            {
                foreach (var p in procs)
                {
                    long cpu = 0, mem = 0;
                    try { cpu = p.TotalProcessorTime.Ticks; mem = p.WorkingSet64; } catch { }
                    list.Add(new RawProc { Name = p.ProcessName, Pid = p.Id, CpuTicks = cpu, MemBytes = mem });
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
    }
}
