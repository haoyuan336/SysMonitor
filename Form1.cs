using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SysMonitor
{
    /// <summary>
    /// 常驻桌面的实时系统监控面板。
    /// 屏幕边缘吸附：鼠标移开自动收起为边缘小图标，悬停小图标展开完整窗口。
    /// </summary>
    public class MainForm : Form
    {
        private enum DockEdge { Left, Right, Top, Bottom }

        private readonly SensorReader _sensors = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 200 };
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<int, (long cpuTicks, long wallTicks)> _prevCpu = new();

        private Label _cpuVal = null!, _gpuVal = null!, _ramVal = null!, _netVal = null!, _fanVal = null!;
        private Label _status = null!;
        private FlatBar _memBar = null!;
        private TrackBar _fanTrack = null!;
        private Label _fanPct = null!;
        private bool _fanSupported = true;
        private DataGridView _grid = null!;
        private NotifyIcon _tray = null!;
        private Bitmap? _trayBitmap;
        private int _procCount;
        private string _filter = "";
        private List<ProcInfo> _lastScan = new();
        private const int KillCol = 4;
        private FlatBtn _btnKillAll = null!;
        private FlatBtn _btnSortCpu = null!, _btnSortMem = null!;
        private bool _killingAll;
        private string _sortKey = "cpu";   // "cpu" | "mem"
        private bool _sortDesc = true;     // 降序（占用高的在前）

        // ---- 边缘折叠相关 ----
        private Size _fullSize = new(470, 650);   // 展开时完整尺寸（96 DPI 设计值）
        private float _scale = 1f;                // 当前 DPI 缩放因子（DpiX/96）
        private DockEdge _edge = DockEdge.Right;
        private bool _expanded = true;
        private bool _pinned;                       // 固定窗口：不自动收起
        private bool _dragging;
        private Point _dragStart, _winStart;
        private DateTime _hideRequestAt;
        private DateTime _lastCollapseAt;
        private const int HideDelayMs = 2500;       // 鼠标离开后多少毫秒才收起
        private const int LeaveMargin = 40;         // 窗口外多少像素内不算离开（DPI 设计值）

        /// <summary>按 DPI 缩放的设计尺寸。</summary>
        private int S(float v) => (int)Math.Round(v * _scale);
        /// <summary>按 DPI 缩放的字体。</summary>
        private Font F(float pt) => new(Font.FontFamily, Math.Max(6f, pt * _scale));

        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        public MainForm()
        {
            Text = "系统监控";
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            using (var g = CreateGraphics())
            {
                _scale = g.DpiX / 96f;
                if (_scale < 0.5f || _scale > 3f) _scale = 1f;
            }
            _fullSize = new Size(S(470), S(650));
            Size = _fullSize;
            BackColor = Color.FromArgb(20, 21, 26);
            Font = new Font("Microsoft YaHei UI", 9f);
            DoubleBuffered = true;

            BuildUi();
            RestorePosition();
            SetupTray();

            // 拖动（标题栏与收起条都可拖，空白处也可拖）
            MouseDown += OnDragStart;
            MouseMove += OnDragMove;
            MouseUp += OnDragEnd;
            var header = (Panel)Controls[0];
            header.MouseDown += OnDragStart;
            title.MouseDown += OnDragStart;

            _timer.Tick += (_, _) => UpdateAll();
            _timer.Start();
            _hoverTimer.Tick += HoverTick;
            _hoverTimer.Start();
            StartProcessScanLoop();
        }

        private Label title = null!;

        // ================= 实时数据刷新（1s） =================

        private void UpdateAll()
        {
            try
            {
                _sensors.Refresh();
                var s = _sensors.Read();

                // CPU：占用 | 频率 | 温度（读不到显示 —）
                var cpuText = s.CpuLoad is float c ? $"{c:0}%" : "—";
                cpuText += $"  |  频率 {(s.CpuClockMhz is float clk && clk > 0 ? $"{clk / 1000f:0.00} GHz" : "—")}";
                cpuText += $"  |  温度 {(s.CpuTempC is float ct && ct > 0 ? $"{ct:0}°C" : "—")}";
                _cpuVal.Text = cpuText;

                // GPU：占用 | 频率 | 温度 | 显存 | 风扇
                var gpuText = s.GpuLoad is float gl ? $"{gl:0}%" : "—";
                if (s.GpuClockMhz is float gc && gc > 0) gpuText += $"  |  {gc:0} MHz";
                if (s.GpuTempC is float gt && gt > 0) gpuText += $"  |  {gt:0}°C";
                if (s.GpuMemUsedMb is float gu && s.GpuMemTotalMb is float gm && gm > 0)
                    gpuText += $"  |  显存 {gu / 1024f:0.0}/{gm / 1024f:0.0} GB";
                if (s.GpuFanRpm is float gf && gf > 0) gpuText += $"  |  {gf:0} RPM";
                _gpuVal.Text = gpuText;

                // 内存
                var (total, used, load) = SensorReader.GetMemory();
                _ramVal.Text = total > 0
                    ? $"{used / 1073741824.0:0.0} / {total / 1073741824.0:0.0} GB  ({load}%)"
                    : "—";
                _memBar.Value = (int)load;
                _memBar.Invalidate();

                // 网络
                var (down, up) = SensorReader.GetNetworkSpeeds();
                _netVal.Text = $"↓ {down:0.00} MB/s   ↑ {up:0.00} MB/s";

                // 风扇（CPU 风扇字段始终显示）
                var fans = new List<string>();
                var cpuFan = s.Fans.FirstOrDefault(f => f.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
                if (cpuFan.Name != null)
                    fans.Add($"CPU {cpuFan.Rpm:0}");
                else
                    fans.Add("CPU —");
                if (s.GpuFanRpm is float gfr && gfr > 0) fans.Add($"显卡 {gfr:0}");
                foreach (var f in s.Fans.Where(x => !x.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)))
                {
                    var nm = f.Name.Replace("Fan", "").Replace("#", "").Replace("Speed", "").Trim();
                    if (nm.Length == 0) nm = "风扇";
                    if (!fans.Any(x => x.StartsWith(nm))) fans.Add($"{nm} {f.Rpm:0}");
                }
                _fanVal.Text = string.Join("  |  ", fans) + " RPM";

                _status.Text = $"进程数: {_procCount}   更新时间: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                _status.Text = "刷新出错: " + ex.Message;
            }
        }

        // ================= 进程扫描（后台线程） =================

        private void StartProcessScanLoop()
        {
            Task.Run(async () =>
            {
                var running = true;
                while (running)
                {
                    List<ProcInfo>? list = null;
                    try { list = await Task.Run(ScanAndCompute); }
                    catch { }
                    if (list != null && !_cts.IsCancellationRequested)
                    {
                        try { BeginInvoke(new Action(() => ApplyProcessGrid(list))); }
                        catch { running = false; }
                    }
                    try { await Task.Delay(2000, _cts.Token); }
                    catch { running = false; }
                }
            }, _cts.Token);
        }

        private List<ProcInfo> ScanAndCompute()
        {
            var now = DateTime.UtcNow.Ticks;
            var raw = ProcessScanner.Scan();
            var result = new List<ProcInfo>(raw.Count);
            foreach (var r in raw)
            {
                double cpu = 0;
                if (_prevCpu.TryGetValue(r.Pid, out var prev))
                {
                    var dWall = now - prev.wallTicks;
                    if (dWall > 0)
                    {
                        var dCpu = r.CpuTicks - prev.cpuTicks;
                        if (dCpu > 0) cpu = dCpu * 100.0 / dWall / Environment.ProcessorCount;
                    }
                }
                _prevCpu[r.Pid] = (r.CpuTicks, now);
                result.Add(new ProcInfo
                {
                    Name = r.Name,
                    Pid = r.Pid,
                    Cpu = Math.Min(999.0, cpu),
                    MemMb = r.MemBytes >> 20
                });
            }

            var live = raw.Select(x => x.Pid).ToHashSet();
            foreach (var k in _prevCpu.Keys.Where(k => !live.Contains(k)).ToList()) _prevCpu.Remove(k);
            _procCount = raw.Count;
            return result;
        }

        private void ApplyProcessGrid(List<ProcInfo> top)
        {
            _lastScan = top;

            List<ProcInfo> view;
            IEnumerable<ProcInfo> baseSeq = string.IsNullOrEmpty(_filter)
                ? top
                : top.Where(x => x.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));
            IOrderedEnumerable<ProcInfo> sorted = _sortKey == "mem"
                ? (_sortDesc ? baseSeq.OrderByDescending(x => x.MemMb) : baseSeq.OrderBy(x => x.MemMb))
                : (_sortDesc ? baseSeq.OrderByDescending(x => x.Cpu) : baseSeq.OrderBy(x => x.Cpu));
            view = sorted.Take(100).ToList();

            // 列头排序箭头（防御：不影响列表刷新）
            try
            {
                _grid.Columns[2].HeaderCell.SortGlyphDirection =
                    _sortKey == "cpu" ? (_sortDesc ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None;
                _grid.Columns[3].HeaderCell.SortGlyphDirection =
                    _sortKey == "mem" ? (_sortDesc ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None;
            }
            catch { }
            var target = view.Select(x => x.Pid).ToHashSet();

            var current = new Dictionary<int, int>();
            for (int i = 0; i < _grid.Rows.Count; i++)
                if (_grid.Rows[i].Cells[1].Value is int pid) current[pid] = i;

            _grid.SuspendLayout();
            foreach (var pid in current.Keys.Where(k => !target.Contains(k))
                         .OrderByDescending(k => current[k]).ToList())
                _grid.Rows.RemoveAt(current[pid]);

            var after = new Dictionary<int, int>();
            for (int i = 0; i < _grid.Rows.Count; i++)
                if (_grid.Rows[i].Cells[1].Value is int pid) after[pid] = i;

            foreach (var t in view)
            {
                if (after.TryGetValue(t.Pid, out var idx))
                {
                    _grid.Rows[idx].Cells[0].Value = t.Name;
                    _grid.Rows[idx].Cells[2].Value = Math.Round(t.Cpu, 1);
                    _grid.Rows[idx].Cells[3].Value = t.MemMb;
                }
                else
                {
                    _grid.Rows.Add(t.Name, t.Pid, Math.Round(t.Cpu, 1), t.MemMb, "");
                }
            }

            // 按当前排序键重排行（view 顺序已正确，这里确保 grid 行序与排序一致）
            _grid.Sort(_sortKey == "mem" ? _grid.Columns[3] : _grid.Columns[2],
                       _sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending);
            _grid.ResumeLayout();
        }

        /// <summary>高亮当前排序按钮（蓝底=选中）。</summary>
        private void UpdateSortButtons()
        {
            if (_btnSortCpu == null || _btnSortMem == null) return;
            if (_sortKey == "cpu")
            {
                _btnSortCpu.BackColor = Color.FromArgb(86, 140, 220);
                _btnSortCpu.ForeColor = Color.White;
                _btnSortMem.BackColor = Color.FromArgb(48, 52, 68);
                _btnSortMem.ForeColor = Color.FromArgb(200, 205, 220);
            }
            else
            {
                _btnSortMem.BackColor = Color.FromArgb(86, 140, 220);
                _btnSortMem.ForeColor = Color.White;
                _btnSortCpu.BackColor = Color.FromArgb(48, 52, 68);
                _btnSortCpu.ForeColor = Color.FromArgb(200, 205, 220);
            }
        }

        private void KillProcessAt(int rowIndex)
        {
            var row = _grid.Rows[rowIndex];
            if (row.Cells[1].Value is not int pid) return;
            var name = row.Cells[0].Value?.ToString() ?? "";
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                _status.Text = $"已结束进程: {name} (PID {pid})";
                _hideRequestAt = default; // 点完按钮后给足时间继续操作
            }
            catch (Exception ex)
            {
                _status.Text = $"结束失败: {name} — {ex.Message}";
            }
        }

        private void KillTreeAt(int rowIndex)
        {
            var row = _grid.Rows[rowIndex];
            if (row.Cells[1].Value is not int pid) return;
            var name = row.Cells[0].Value?.ToString() ?? "";
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(true);
                _status.Text = $"已结束进程树: {name} (PID {pid})";
                _hideRequestAt = default;
            }
            catch (Exception ex)
            {
                _status.Text = $"结束失败: {name} — {ex.Message}";
            }
        }

        /// <summary>一键结束所有匹配当前筛选词的进程（批量直杀）。</summary>
        private void KillAllFiltered()
        {
            if (string.IsNullOrEmpty(_filter))
            {
                _status.Text = "请先在筛选框输入进程名，再点「结束全部」";
                return;
            }
            if (_killingAll) return;
            var targets = _lastScan
                .Where(p => p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Pid).Distinct()
                .Where(pid => pid != Environment.ProcessId)
                .ToList();
            if (targets.Count == 0)
            {
                _status.Text = $"没有匹配「{_filter}」的进程";
                return;
            }
            if (MessageBox.Show(this,
                    $"确定结束全部 {targets.Count} 个「{_filter}」进程？\n（若进程反复复活，说明有守护进程/服务在拉起它，请先停用对应服务）",
                    "结束全部进程", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _killingAll = true;
            _btnKillAll.Enabled = false;
            _status.Text = $"正在结束 {targets.Count} 个「{_filter}」进程...";
            Task.Run(() =>
            {
                int ok = 0, fail = 0;
                foreach (var pid in targets)
                {
                    if (TryTerminateProcess(pid)) ok++; else fail++;
                }
                BeginInvoke(new Action(() =>
                {
                    _killingAll = false;
                    _btnKillAll.Enabled = true;
                    _hideRequestAt = default;
                    _status.Text = $"已结束 {ok} 个「{_filter}」进程（失败 {fail}）";
                    _ = Task.Run(ScanAndCompute).ContinueWith(t =>
                    {
                        if (!t.IsCompletedSuccessfully) return;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                var remaining = t.Result.Count(p => p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));
                                _status.Text = remaining > 0
                                    ? $"已结束 {ok} 个；仍剩 {remaining} 个（多为已退出的僵尸进程，重启系统可清除）"
                                    : $"已结束全部 {ok} 个「{_filter}」进程";
                                ApplyProcessGrid(t.Result);
                            }));
                        }
                        catch { }
                    });
                }));
            });
        }

        private static bool TryTerminateProcess(int pid)
        {
            IntPtr h = OpenProcess(0x0001 /* PROCESS_TERMINATE */, false, pid);
            if (h == IntPtr.Zero) return false;
            bool ok = TerminateProcess(h, 1);
            CloseHandle(h);
            return ok;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        // ================= 边缘折叠 =================

        private void HoverTick(object? s, EventArgs e)
        {
            if (_dragging || _pinned)
            {
                _hideRequestAt = default;
                return;
            }
            var mp = Cursor.Position;
            if (_expanded)
            {
                var relaxed = Bounds;
                relaxed.Inflate(S(LeaveMargin), S(LeaveMargin));
                if (relaxed.Contains(mp))
                {
                    _hideRequestAt = default;
                }
                else
                {
                    if (_hideRequestAt == default) _hideRequestAt = DateTime.Now;
                    else if ((DateTime.Now - _hideRequestAt).TotalMilliseconds >= HideDelayMs) Collapse();
                }
            }
            else
            {
                var tab = TabBounds();
                tab.Inflate(S(32), S(32));
                if (tab.Contains(mp) && (DateTime.Now - _lastCollapseAt).TotalMilliseconds > 1200)
                    Expand();
            }
        }

        private void Collapse()
        {
            if (!_expanded) return;
            _expanded = false;
            _hideRequestAt = default;
            _lastCollapseAt = DateTime.Now;
            HideContent();
            var tab = TabBounds();
            SetBounds(tab.X, tab.Y, tab.Width, tab.Height);
            Refresh();
        }

        private void Expand()
        {
            if (_expanded) return;
            _expanded = true;
            _hideRequestAt = default;
            ShowContent();
            PlaceExpanded();
        }

        private void HideContent()
        {
            foreach (Control c in Controls) c.Visible = false;
        }

        private void ShowContent()
        {
            foreach (Control c in Controls) c.Visible = true;
        }

        /// <summary>展开状态下的窗口位置（贴当前停靠边缘，按窗口中心对齐）。</summary>
        private void PlaceExpanded()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int cx = Bounds.Left + Bounds.Width / 2;
            int cy = Bounds.Top + Bounds.Height / 2;
            switch (_edge)
            {
                case DockEdge.Left:
                    SetBounds(wa.Left, ClampTo(cy - _fullSize.Height / 2, wa.Top, wa.Bottom - _fullSize.Height),
                        _fullSize.Width, _fullSize.Height);
                    break;
                case DockEdge.Right:
                    SetBounds(wa.Right - _fullSize.Width, ClampTo(cy - _fullSize.Height / 2, wa.Top, wa.Bottom - _fullSize.Height),
                        _fullSize.Width, _fullSize.Height);
                    break;
                case DockEdge.Top:
                    SetBounds(ClampTo(cx - _fullSize.Width / 2, wa.Left, wa.Right - _fullSize.Width), wa.Top,
                        _fullSize.Width, _fullSize.Height);
                    break;
                case DockEdge.Bottom:
                    SetBounds(ClampTo(cx - _fullSize.Width / 2, wa.Left, wa.Right - _fullSize.Width), wa.Bottom - _fullSize.Height,
                        _fullSize.Width, _fullSize.Height);
                    break;
            }
        }

        /// <summary>收起条的屏幕位置。</summary>
        private Rectangle TabBounds()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int cx = Bounds.Left + Bounds.Width / 2;
            int cy = Bounds.Top + Bounds.Height / 2;
            if (_edge == DockEdge.Left || _edge == DockEdge.Right)
            {
                int x = _edge == DockEdge.Left ? wa.Left : wa.Right - S(20);
                int y = ClampTo(cy - S(130) / 2, wa.Top, wa.Bottom - S(130));
                return new Rectangle(x, y, S(20), S(130));
            }
            else
            {
                int y = _edge == DockEdge.Top ? wa.Top : wa.Bottom - S(20);
                int x = ClampTo(cx - S(130) / 2, wa.Left, wa.Right - S(130));
                return new Rectangle(x, y, S(130), S(20));
            }
        }

        private static int ClampTo(int v, int min, int max)
        {
            if (max < min) max = min;
            return Math.Min(Math.Max(v, min), max);
        }

        /// <summary>拖动结束：吸附到最近的屏幕边缘。</summary>
        private void SnapToEdge()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int cx = Bounds.Left + Bounds.Width / 2;
            int cy = Bounds.Top + Bounds.Height / 2;
            int dl = cx - wa.Left, dr = wa.Right - cx, dt = cy - wa.Top, db = wa.Bottom - cy;
            int m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            if (m == dl) _edge = DockEdge.Left;
            else if (m == dr) _edge = DockEdge.Right;
            else if (m == dt) _edge = DockEdge.Top;
            else _edge = DockEdge.Bottom;

            _expanded = true;
            ShowContent();
            PlaceExpanded();
        }

        // ---- 拖动 ----

        private void OnDragStart(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _dragging) return;
            _dragging = true;
            _dragStart = Cursor.Position;
            _winStart = Location;
            _hideRequestAt = default;
            Capture = true;
        }

        private void OnDragMove(object? s, MouseEventArgs e)
        {
            if (!_dragging) return;
            var dx = Cursor.Position.X - _dragStart.X;
            var dy = Cursor.Position.Y - _dragStart.Y;
            Location = new Point(_winStart.X + dx, _winStart.Y + dy);
        }

        private void OnDragEnd(object? s, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            SnapToEdge();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_expanded) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var border = new Pen(Color.FromArgb(45, 50, 65)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            int d = S(10);
            int x = Width / 2 - d / 2;
            int y = Height / 2 - d / 2;
            using (var b = new SolidBrush(Color.FromArgb(74, 222, 128)))
                g.FillEllipse(b, x, y, d, d);
            using (var ring = new Pen(Color.FromArgb(120, 130, 150)))
                g.DrawEllipse(ring, x - S(3), y - S(3), d + S(6), d + S(6));
        }

        // ================= UI 构建 =================

        private void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Color.FromArgb(28, 30, 38) };
            title = new Label
            {
                Text = "⚡ 系统监控",
                ForeColor = Color.White,
                Font = F(10.5f).WithBold(),
                AutoSize = true,
                Location = new Point(S(14), S(9))
            };
            header.Controls.Add(title);

            var btnMin = new FlatBtn("—", 10f * _scale) { Bounds = new Rectangle(Width - S(80), S(8), S(34), S(24)) };
            var btnClose = new FlatBtn("✕", 10f * _scale) { Bounds = new Rectangle(Width - S(42), S(8), S(34), S(24)) };
            var btnPin = new FlatBtn("📌", 9f * _scale) { Bounds = new Rectangle(Width - S(118), S(8), S(34), S(24)) };
            header.Controls.Add(btnMin);
            header.Controls.Add(btnClose);
            header.Controls.Add(btnPin);
            btnMin.Click += (_, _) => { _dragging = false; Hide(); _tray.Visible = true; };
            btnClose.Click += (_, _) => Application.Exit();
            btnPin.Click += (_, _) => TogglePin(btnPin);
            Controls.Add(header);

            var stats = new Panel { Dock = DockStyle.Top, Height = S(190), BackColor = Color.FromArgb(20, 21, 26) };
            int y = S(8);
            (_cpuVal, _) = AddStatRow(stats, "CPU", Color.FromArgb(86, 200, 138), ref y);
            (_gpuVal, _) = AddStatRow(stats, "GPU", Color.FromArgb(86, 160, 240), ref y);
            (_ramVal, _) = AddStatRow(stats, "内存", Color.FromArgb(240, 180, 86), ref y);

            // 内存占用进度条（行内底部细条，颜色随负载变化）
            _memBar = new FlatBar
            {
                Location = new Point(S(14), S(8) + 2 * S(30) + S(24)),
                Size = new Size(stats.Width - S(28), S(5))
            };
            _memBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            stats.Controls.Add(_memBar);

            (_netVal, _) = AddStatRow(stats, "网络", Color.FromArgb(160, 140, 240), ref y);
            (_fanVal, _) = AddStatRow(stats, "风扇", Color.FromArgb(220, 120, 120), ref y);

            // 风扇调速行（第 6 行）
            var fanCap = new Label
            {
                Text = "调速",
                ForeColor = Color.FromArgb(220, 120, 120),
                Font = F(9.5f).WithBold(),
                AutoSize = true,
                Location = new Point(S(14), S(162))
            };
            _fanTrack = new TrackBar
            {
                Minimum = 20, Maximum = 100, Value = 100,
                TickStyle = TickStyle.None,
                Size = new Size(S(150), S(28)),
                Location = new Point(S(56), S(154)),
                BackColor = Color.FromArgb(20, 21, 26)
            };
            _fanPct = new Label
            {
                Text = "100%",
                ForeColor = Color.White,
                Font = F(9.5f),
                AutoSize = true,
                Location = new Point(S(210), S(162))
            };
            var fanState = new Label
            {
                Text = "",
                ForeColor = Color.FromArgb(150, 155, 170),
                Font = F(8.5f),
                AutoSize = true,
                Location = new Point(S(262), S(163))
            };
            _fanTrack.Scroll += (_, _) =>
            {
                _sensors.SetFanPercent(_fanTrack.Value);
                _fanPct.Text = _fanTrack.Value + "%";
            };
            // 探测硬件是否支持调速（不支持则禁用滑块）
            try
            {
                var ctrl = _sensors.GetControllableFans();
                _fanSupported = ctrl.Count > 0;
                if (!_fanSupported)
                {
                    _fanTrack.Enabled = false;
                    fanState.Text = "本机不支持";
                }
            }
            catch { _fanSupported = false; _fanTrack.Enabled = false; fanState.Text = "本机不支持"; }

            stats.Controls.Add(fanCap);
            stats.Controls.Add(_fanTrack);
            stats.Controls.Add(_fanPct);
            stats.Controls.Add(fanState);
            Controls.Add(stats);

            var filterPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(36),
                BackColor = Color.FromArgb(20, 21, 26),
                Padding = new Padding(S(14), S(7), S(14), 0)
            };
            var filterLabel = new Label
            {
                Text = "筛选",
                ForeColor = Color.FromArgb(160, 165, 180),
                Font = F(9f),
                AutoSize = true,
                Location = new Point(S(14), S(11))
            };
            var filterBox = new TextBox
            {
                ForeColor = Color.White,
                BackColor = Color.FromArgb(30, 32, 40),
                BorderStyle = BorderStyle.FixedSingle,
                Font = F(9f),
                Location = new Point(S(56), S(7)),
                Width = Width - S(70) - S(74) - S(100)
            };
            filterBox.TextChanged += (_, _) =>
            {
                _filter = filterBox.Text.Trim();
                ApplyProcessGrid(_lastScan);
            };
            // 排序切换按钮（按CPU / 按内存）——固定坐标，不做 Anchor（收起/展开动画会破坏右缘距离）
            _btnSortMem = new FlatBtn("按内存", 9f * _scale)
            {
                Location = new Point(Width - S(70) - S(50), S(7)),
                Size = new Size(S(44), S(24)),
                BackColor = Color.FromArgb(48, 52, 68),
                ForeColor = Color.FromArgb(200, 205, 220),
            };
            _btnSortCpu = new FlatBtn("按CPU", 9f * _scale)
            {
                Location = new Point(Width - S(70) - S(50) - S(50), S(7)),
                Size = new Size(S(44), S(24)),
                BackColor = Color.FromArgb(86, 140, 220),
                ForeColor = Color.White,
            };
            _btnSortCpu.Click += (_, _) => { _sortKey = "cpu"; _sortDesc = true; UpdateSortButtons(); ApplyProcessGrid(_lastScan); };
            _btnSortMem.Click += (_, _) => { _sortKey = "mem"; _sortDesc = true; UpdateSortButtons(); ApplyProcessGrid(_lastScan); };
            UpdateSortButtons();
            _btnKillAll = new FlatBtn("结束全部", 9f * _scale)
            {
                Location = new Point(Width - S(70), S(7)),
                Size = new Size(S(62), S(24)),
                BackColor = Color.FromArgb(150, 52, 52),
                ForeColor = Color.White,
            };
            _btnKillAll.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 62, 62);
            _btnKillAll.FlatAppearance.MouseDownBackColor = Color.FromArgb(120, 40, 40);
            _btnKillAll.Click += (_, _) => KillAllFiltered();
            filterPanel.Controls.Add(filterLabel);
            filterPanel.Controls.Add(filterBox);
            filterPanel.Controls.Add(_btnSortCpu);
            filterPanel.Controls.Add(_btnSortMem);
            filterPanel.Controls.Add(_btnKillAll);
            Controls.Add(filterPanel);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(24, 25, 31),
                BorderStyle = BorderStyle.None,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = S(28),
                RowTemplate = { Height = S(30) },
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EditMode = DataGridViewEditMode.EditProgrammatically,
                AutoGenerateColumns = false,
                ScrollBars = ScrollBars.Vertical,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(38, 40, 50),
            };
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 32, 40),
                ForeColor = Color.FromArgb(180, 185, 200),
                Font = F(9f).WithBold(),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                SelectionBackColor = Color.FromArgb(30, 32, 40),
                SelectionForeColor = Color.FromArgb(180, 185, 200)
            };
            _grid.RowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(24, 25, 31),
                ForeColor = Color.White,
                Font = F(9f),
                SelectionBackColor = Color.FromArgb(45, 60, 90),
                SelectionForeColor = Color.White
            };
            _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(27, 28, 35),
                ForeColor = Color.White,
                Font = F(9f),
                SelectionBackColor = Color.FromArgb(45, 60, 90),
                SelectionForeColor = Color.White
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colName", HeaderText = "进程", Width = S(160),
                SortMode = DataGridViewColumnSortMode.Programmatic
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colPid", HeaderText = "PID", Width = S(60),
                SortMode = DataGridViewColumnSortMode.Programmatic,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colCpu", HeaderText = "CPU%", Width = S(65),
                SortMode = DataGridViewColumnSortMode.Programmatic,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "0.0"
                }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colMem", HeaderText = "内存MB", Width = S(80),
                SortMode = DataGridViewColumnSortMode.Programmatic,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N0"
                }
            });
            _grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "colKill", HeaderText = "", Text = "结束",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = S(56),
                UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(190, 60, 60),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(190, 60, 60),
                    SelectionForeColor = Color.White
                }
            });
            _grid.CellClick += (_, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == KillCol) KillProcessAt(e.RowIndex);
            };
            // 列头点击排序：CPU% / 内存MB 列切换排序字段，再点切换升降序
            _grid.ColumnHeaderMouseClick += (_, e) =>
            {
                if (e.ColumnIndex == 2)  // CPU%
                {
                    if (_sortKey == "cpu") _sortDesc = !_sortDesc;
                    else { _sortKey = "cpu"; _sortDesc = true; }
                }
                else if (e.ColumnIndex == 3)  // 内存MB
                {
                    if (_sortKey == "mem") _sortDesc = !_sortDesc;
                    else { _sortKey = "mem"; _sortDesc = true; }
                }
                else return;
                UpdateSortButtons();
                ApplyProcessGrid(_lastScan);
            };
            var ctx = new ContextMenuStrip();
            ctx.Items.Add("结束进程", null, (_, _) =>
            {
                if (_grid.CurrentRow != null) KillProcessAt(_grid.CurrentRow.Index);
            });
            ctx.Items.Add("结束进程树", null, (_, _) =>
            {
                if (_grid.CurrentRow != null) KillTreeAt(_grid.CurrentRow.Index);
            });
            _grid.ContextMenuStrip = ctx;
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                .SetValue(_grid, true);
            Controls.Add(_grid);

            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = S(26),
                ForeColor = Color.FromArgb(160, 165, 180),
                BackColor = Color.FromArgb(28, 30, 38),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(10), 0, 0, 0)
            };
            Controls.Add(_status);
        }

        private (Label value, Label name) AddStatRow(Panel parent, string caption, Color color, ref int y)
        {
            var name = new Label
            {
                Text = caption,
                ForeColor = color,
                Font = F(9.5f).WithBold(),
                AutoSize = true,
                Location = new Point(S(14), y)
            };
            var value = new Label
            {
                Text = "—",
                ForeColor = Color.White,
                Font = F(10.5f),
                AutoSize = true,
                Location = new Point(S(80), y)
            };
            parent.Controls.Add(name);
            parent.Controls.Add(value);
            y += S(30);
            return (value, name);
        }

        // ================= 托盘 / 配置 =================

        private void SetupTray()
        {
            _trayBitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(_trayBitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var bg = new SolidBrush(Color.FromArgb(30, 120, 220));
                g.FillEllipse(bg, 1, 1, 14, 14);
                using var bolt = new SolidBrush(Color.White);
                g.FillPolygon(bolt, new[]
                {
                    new PointF(10, 3), new PointF(5, 9), new PointF(8, 9),
                    new PointF(6, 13), new PointF(11, 7), new PointF(8, 7)
                });
            }
            _tray = new NotifyIcon
            {
                Icon = Icon.FromHandle(_trayBitmap.GetHicon()),
                Text = "系统监控（鼠标移到屏幕边缘小圆点展开）",
                Visible = false,
                ContextMenuStrip = new ContextMenuStrip()
            };
            _tray.ContextMenuStrip.Items.Add("显示主界面", null, (_, _) => ShowMain());
            _tray.ContextMenuStrip.Items.Add("固定窗口 / 取消固定", null, (_, _) => TogglePin(null));
            _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => Application.Exit());
            _tray.DoubleClick += (_, _) => ShowMain();
        }

        /// <summary>切换固定状态：固定后窗口不自动收起。</summary>
        private void TogglePin(FlatBtn? btn)
        {
            _pinned = !_pinned;
            _hideRequestAt = default;
            if (btn != null)
            {
                btn.BackColor = _pinned ? Color.FromArgb(80, 100, 60) : Color.Transparent;
                btn.ForeColor = _pinned ? Color.FromArgb(210, 230, 150) : Color.White;
            }
            if (_pinned)
            {
                if (!_expanded)
                {
                    _expanded = true;
                    ShowContent();
                    PlaceExpanded();
                }
            }
            else
            {
                ShowMain();
            }
            if (_status != null)
                _status.Text = _pinned ? "已固定窗口（不会自动收起），再点 📌 取消固定" : _status.Text;
        }

        private void ShowMain()
        {
            Show();
            WindowState = FormWindowState.Normal;
            _tray.Visible = false;
            if (!_expanded)
            {
                _expanded = true;
                ShowContent();
                PlaceExpanded();
            }
            Activate();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 保证窗口完整落在工作区内（内容用 Dock 布局，自动适应）
            var wa = Screen.FromControl(this).WorkingArea;
            _fullSize = new Size(Math.Min(_fullSize.Width, wa.Width), Math.Min(_fullSize.Height, wa.Height));
            PlaceExpanded();
        }

        private void RestorePosition()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var j = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement;
                if (Enum.TryParse<DockEdge>(j.GetProperty("edge").GetString(), true, out var e))
                    _edge = e;
                int x = j.GetProperty("x").GetInt32();
                int y = j.GetProperty("y").GetInt32();
                Location = new Point(x, y);
            }
            catch { }
        }

        private void SavePosition()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigPath, $"{{\"edge\":\"{_edge}\",\"x\":{Location.X},\"y\":{Location.Y}}}");
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            _hoverTimer.Stop();
            _cts.Cancel();
            SavePosition();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.OnFormClosing(e);
        }
    }

    internal static class FontEx
    {
        public static Font WithBold(this Font f) => new(f, FontStyle.Bold);
    }

    internal sealed class FlatBtn : Button
    {
        public FlatBtn(string text, float fontSizePt = 10f)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", fontSizePt);
            BackColor = Color.Transparent;
            Cursor = Cursors.Default;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 64, 80);
            FlatAppearance.MouseDownBackColor = Color.FromArgb(45, 48, 62);
        }
    }

    /// <summary>扁平进度条（内存占用等百分比指示）。</summary>
    internal sealed class FlatBar : Control
    {
        public int Value;
        public FlatBar() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using (var bg = new SolidBrush(Color.FromArgb(42, 44, 54)))
                g.FillRectangle(bg, 0, 0, Width, Height);
            int w = (int)(Width * Math.Clamp(Value / 100f, 0f, 1f));
            if (w <= 0) return;
            var c = Value >= 85 ? Color.FromArgb(232, 82, 82) : Value >= 60 ? Color.FromArgb(240, 180, 86) : Color.FromArgb(86, 200, 138);
            using (var fb = new SolidBrush(c)) g.FillRectangle(fb, 0, 0, w, Height);
        }
    }
}
