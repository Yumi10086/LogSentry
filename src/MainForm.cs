using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// C# 5 目标（微软旧版编译器，兼容 XP/Win7 自带的 .NET 2.0 运行时也能跑 CLR4 的情况）
//
// 设计要点：
//  1) 全部界面元素在代码中构建，不依赖 Designer 文件，方便单文件编译与二次维护；
//  2) 检测项完全来自 queries.txt / 内嵌默认配置，新增检测项只改配置，不改本文件；
//  3) LogParser 调用全部异步，UI 线程永不阻塞；
//  4) 输出按 GBK(936) 解码中文，避免乱码。
//
namespace LogSentry
{
    public sealed class MainForm : Form
    {
        // ---------------------------------------------------------------- 字段

        private readonly string _exeDir;
        private string _configPath;
        private ConfigLoadResult _config;
        private readonly List<CheckBox> _checkBoxes = new List<CheckBox>();
        private bool _running;
        private CancellationTokenSource _cts;
        private bool _suppressCheckEvents;

        // 控件
        private TextBox _txtLogParser;
        private Button _btnBrowse;
        private Button _btnTest;
        private TabControl _tabs;
        private TabPage _pagePreset;
        private Panel _panelItems;
        private CheckedListBox _clbItems;
        private Button _btnAll;
        private Button _btnNone;
        private Button _btnDefault;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnSaveResult;
        private Button _btnClear;
        private RichTextBox _rtbOutput;
        private TextBox _txtParam;
        private NumericUpDown _numTimeout;
        private ComboBox _cboFormat;
        private TextBox _txtExtraArgs;
        // 手动命令页
        private TabPage _pageManual;
        private TextBox _txtManual;
        private ComboBox _cboHistory;
        private Button _btnRunManual;
        private Button _btnManualSample;
        private Button _btnManualClear;
        private CheckBox _chkManualTime;
        private Label _lblManualParse;
        private ToolStripStatusLabel _lblStatus;
        private ToolStripStatusLabel _tsslPriv;
        private ToolStripStatusLabel _tsslCount;
        private StatusStrip _statusStrip;
        private SplitContainer _split;

        // 时间筛选
        private CheckBox _chkTime;
        private ComboBox _cboRange;
        private DateTimePicker _dtFrom;
        private DateTimePicker _dtTo;
        // 时 / 分直接做成 00~23、00~59 的下拉框：第一次上手也一眼看得出"这里能调时间"
        private ComboBox _cboFh, _cboFm, _cboTh, _cboTm;
        private Label _lblSelCount;

        private const string StartButtonText = "▶  开始分析（F5）";

        // 结果缓冲（避免长时间分析时 RichTextBox 卡顿）
        private StringBuilder _pending = new StringBuilder();
        private DateTime _lastFlush = DateTime.MinValue;
        private long _totalElements = -1;
        private bool _rawColumns;

        // 性能采样（--smoke 打印用，正常使用无影响）
        internal double PerfRunOneMs;
        internal double PerfAppendMs;
        internal double PerfPrepMs;
        internal int PerfAppendCalls;

        internal double PerfFontMs;
        internal double PerfTextMs;
        internal double PerfScrollMs;
        internal double PerfMaxFlushMs;
        internal int PerfMaxFlushChars;
        internal int PerfTotalChars;
        internal StringBuilder PerfFlushLog = new StringBuilder();

        // 输出缓冲：同样的样式连续攒够再一次性写进 RichTextBox（逐行写会卡死）
        private StringBuilder _buf = new StringBuilder();
        private Color _bufColor = Color.Empty;
        private bool _bufBold;
        private bool _scrollPending;
        private System.Windows.Forms.Timer _flushTimer;
        private Font _fontRegular;
        private Font _fontBold;
        private DateTime _lastAppendUtc = DateTime.UtcNow;
        private const string FilterGuard =
            "\u3000"; // 占位

        // ---------------------------------------------------------------- 构造

        public MainForm()
        {
            _exeDir = AppDomain.CurrentDomain.BaseDirectory;
            BuildUi();
            LoadConfigSafe(null);
            ResolveLogParserPath();
            UpdatePrivilegeLabel();
        }

        // ---------------------------------------------------------------- UI 构建

        private void BuildUi()
        {
            SuspendLayout();

            Text = "LogSentry · Windows 日志一把梭分析";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 760);
            MinimumSize = new Size(900, 600);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(244, 246, 248);
            DoubleBuffered = true;
            try { Icon = SystemIcons.Shield; } catch (Exception) { }

            // ---------- 顶部：LogParser 路径（TableLayout，窗口拉伸/最大化都不会重叠）
            Panel topPanel = new Panel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 68;
            topPanel.Padding = new Padding(14, 8, 14, 6);
            topPanel.BackColor = Color.White;

            TableLayoutPanel tg = new TableLayoutPanel();
            tg.Dock = DockStyle.Fill;
            tg.ColumnCount = 3;
            tg.RowCount = 2;
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tg.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            tg.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label lblLp = new Label();
            lblLp.Text = "LogParser.exe";
            lblLp.AutoSize = true;
            lblLp.Anchor = AnchorStyles.Left;
            lblLp.Margin = new Padding(0, 0, 10, 0);
            lblLp.ForeColor = Color.FromArgb(70, 78, 90);

            _txtLogParser = new TextBox();
            _txtLogParser.Dock = DockStyle.Fill;
            _txtLogParser.Margin = new Padding(0, 3, 8, 0);
            _txtLogParser.BorderStyle = BorderStyle.FixedSingle;
            _txtLogParser.Name = "txtLogParser";

            FlowLayoutPanel lpBtns = new FlowLayoutPanel();
            lpBtns.AutoSize = true;
            lpBtns.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            lpBtns.WrapContents = false;
            lpBtns.FlowDirection = FlowDirection.LeftToRight;
            lpBtns.Margin = new Padding(0);

            _btnBrowse = MkButton("浏览…", 0, 0, 86);
            _btnBrowse.Name = "btnBrowse";
            _btnBrowse.Margin = new Padding(0, 1, 6, 0);
            _btnBrowse.Click += delegate { OnBrowseLogParser(); };

            _btnTest = MkButton("测试", 0, 0, 72);
            _btnTest.Name = "btnTest";
            _btnTest.Margin = new Padding(0, 1, 0, 0);
            _btnTest.Click += delegate { OnTestLogParser(); };

            lpBtns.Controls.Add(_btnBrowse);
            lpBtns.Controls.Add(_btnTest);

            Label lblHint = new Label();
            lblHint.Text = "默认用程序同目录的 LogParser.exe；点“浏览…”可指定别处的路径。读取 Security 日志需要管理员权限。";
            lblHint.AutoSize = false;
            lblHint.Dock = DockStyle.Fill;
            lblHint.TextAlign = ContentAlignment.MiddleLeft;
            lblHint.ForeColor = Color.FromArgb(130, 138, 150);
            lblHint.Font = new Font(Font.FontFamily, 8.5F);
            lblHint.Margin = new Padding(0, 2, 0, 0);

            tg.Controls.Add(lblLp, 0, 0);
            tg.Controls.Add(_txtLogParser, 1, 0);
            tg.Controls.Add(lpBtns, 2, 0);
            tg.Controls.Add(lblHint, 0, 1);
            tg.SetColumnSpan(lblHint, 3);
            topPanel.Controls.Add(tg);

            // ---------- 状态栏
            _statusStrip = new StatusStrip();
            _statusStrip.SizingGrip = true;
            _lblStatus = new ToolStripStatusLabel();
            _lblStatus.Text = "就绪";
            _lblStatus.Spring = true;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Name = "lblStatus";
            _tsslCount = new ToolStripStatusLabel();
            _tsslCount.Text = "";
            _tsslPriv = new ToolStripStatusLabel();
            _tsslPriv.Text = "";
            _tsslPriv.ForeColor = Color.FromArgb(180, 90, 0);
            _statusStrip.Items.Add(_lblStatus);
            _statusStrip.Items.Add(_tsslCount);
            _statusStrip.Items.Add(new ToolStripSeparator());
            _statusStrip.Items.Add(_tsslPriv);

            // ---------- 时间筛选条（LogParser 的 TimeGenerated 条件）
            Panel filterBar = BuildFilterBar();

            // ---------- 底部操作条：开始分析放这里，横跨整个窗口，任何尺寸都不会被裁掉
            Panel actionBar = BuildActionBar();

            // ---------- 主体分栏
            _split = new SplitContainer();
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Vertical;
            _split.SplitterWidth = 6;
            _split.BackColor = Color.FromArgb(232, 236, 240);

            BuildLeftPanel();
            BuildRightPanel();

            // 停靠顺序：后加入的控件先占外侧边，所以 Fill 最先加、状态栏最后加。
            Controls.Add(_split);
            Controls.Add(filterBar);
            Controls.Add(topPanel);
            Controls.Add(actionBar);
            Controls.Add(_statusStrip);

            // 分栏尺寸必须在控件获得实际大小之后再设，否则 SplitterDistance 会越界抛异常。
            // 这里设一次，窗体的 Shown 事件里再按真实尺寸设一次（构造期 SplitContainer
            // 还没拿到最终宽度，值会被内部重新规划掉，实测会跑到 936 而不是 430）。
            _split.SplitterDistance = 430;

            // 窗口被拖窄时，SplitterDistance 会超过 Width - Panel2MinSize 而抛
            // InvalidOperationException；每次尺寸变化都夹回合法区间。
            SizeChanged += delegate { ClampSplitter(); };
            Shown += delegate
            {
                // 顺序很重要：先定 SplitterDistance，再设 MinSize。
                // 反过来（先设 Panel2MinSize）会在当前距离太靠右时抛
                // "SplitterDistance 必须在 Panel1MinSize 和 Width - Panel2MinSize 之间"。
                try
                {
                    _split.SplitterDistance = 430;
                    _split.Panel1MinSize = 300;
                    _split.Panel2MinSize = 260;
                }
                catch (Exception) { }
                ClampSplitter();
                SyncTabPageBounds();
            };

            ResumeLayout(false);
            PerformLayout();
        }

        private void BuildLeftPanel()
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Color.White;
            host.Padding = new Padding(10, 8, 10, 8);

            // 不许用绝对坐标：高 DPI / 窄窗口下标题和三个按钮会互相压住、文字被裁。
            // 改成自动换行的 FlowLayoutPanel，宽度不够时自然折到下一行，高度自动撑开。
            FlowLayoutPanel header = new FlowLayoutPanel();
            header.Name = "leftHeader";
            header.Dock = DockStyle.Top;
            header.AutoSize = true;
            header.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            header.WrapContents = true;
            header.FlowDirection = FlowDirection.LeftToRight;
            header.BackColor = Color.White;
            header.Padding = new Padding(0, 0, 0, 2);

            Label lblTitle = new Label();
            lblTitle.Text = "检测项";
            lblTitle.AutoSize = true;
            lblTitle.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            lblTitle.Margin = new Padding(0, 7, 12, 0);

            _btnAll = MkSmall("全选");
            _btnNone = MkSmall("全不选");
            _btnDefault = MkSmall("默认项");
            _btnAll.Click += delegate { SetAllChecked(true); };
            _btnNone.Click += delegate { SetAllChecked(false); };
            _btnDefault.Click += delegate { ApplyDefaultSelection(); };

            Label lblCfgPath = new Label();
            lblCfgPath.Text = "配置：queries.txt（可自定义扩项）";
            lblCfgPath.AutoSize = true;
            lblCfgPath.ForeColor = Color.FromArgb(140, 148, 160);
            lblCfgPath.Font = new Font(Font.FontFamily, 8F);
            lblCfgPath.Margin = new Padding(8, 8, 0, 0);

            header.Controls.Add(lblTitle);
            header.Controls.Add(_btnAll);
            header.Controls.Add(_btnNone);
            header.Controls.Add(_btnDefault);
            header.Controls.Add(lblCfgPath);

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.Font = Font;

            _pagePreset = new TabPage("按分类勾选");
            _pagePreset.BackColor = Color.White;

            _panelItems = new Panel();
            _panelItems.Dock = DockStyle.Fill;
            _panelItems.AutoScroll = true;
            _panelItems.Padding = new Padding(4, 4, 4, 4);
            _panelItems.BackColor = Color.White;

            _clbItems = new CheckedListBox();
            _clbItems.Dock = DockStyle.Fill;
            _clbItems.CheckOnClick = true;
            _clbItems.IntegralHeight = false;
            _clbItems.BorderStyle = BorderStyle.FixedSingle;
            _clbItems.Visible = false;
            _clbItems.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                if (_suppressCheckEvents) return;
                StartFlushTimer();
            };

            _pagePreset.Controls.Add(_panelItems);
            _pagePreset.Controls.Add(_clbItems);
            _tabs.TabPages.Add(_pagePreset);

            TabPage pageAdv = new TabPage("参数与超时");
            pageAdv.BackColor = Color.White;
            pageAdv.Padding = new Padding(10);

            Panel advBox = new Panel();
            advBox.Dock = DockStyle.Fill;
            advBox.AutoScroll = true;

            // 用 TableLayoutPanel 竖排，避免绝对方位在窄窗口/高 DPI 下互相压住
            TableLayoutPanel adv = new TableLayoutPanel();
            adv.Dock = DockStyle.Top;
            adv.AutoSize = true;
            adv.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            adv.ColumnCount = 1;
            adv.RowCount = 9;
            adv.Padding = new Padding(0, 4, 0, 4);
            adv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label lblParam = new Label();
            lblParam.Text = "自定义参数（替换配置中 %PARAM%）：";
            lblParam.AutoSize = true;
            lblParam.Margin = new Padding(0, 6, 0, 4);

            _txtParam = new TextBox();
            _txtParam.Dock = DockStyle.Top;
            _txtParam.Margin = new Padding(0, 0, 0, 2);
            _txtParam.BorderStyle = BorderStyle.FixedSingle;
            _txtParam.Text = "1";

            Label lblParamHint = new Label();
            lblParamHint.Text = "示例：IP 检索项写成 WHERE SourceIP = '%PARAM%'，这里填 10.0.0.5";
            lblParamHint.AutoSize = true;
            lblParamHint.Font = new Font(Font.FontFamily, 8F);
            lblParamHint.ForeColor = Color.FromArgb(140, 148, 160);
            lblParamHint.Margin = new Padding(0, 0, 0, 12);

            // 输出格式 + 超时 同一行，各占一半
            TableLayoutPanel rowFmt = new TableLayoutPanel();
            rowFmt.Dock = DockStyle.Top;
            rowFmt.AutoSize = true;
            rowFmt.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            rowFmt.ColumnCount = 2;
            rowFmt.RowCount = 2;
            rowFmt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            rowFmt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            rowFmt.Margin = new Padding(0, 0, 0, 12);

            Label lblFmt = new Label();
            lblFmt.Text = "输出格式 (-o:)";
            lblFmt.AutoSize = true;
            lblFmt.Margin = new Padding(0, 0, 6, 4);

            Label lblTimeout = new Label();
            lblTimeout.Text = "单项超时（秒）";
            lblTimeout.AutoSize = true;
            lblTimeout.Margin = new Padding(6, 0, 0, 4);

            _cboFormat = new ComboBox();
            _cboFormat.Name = "cboFormat";
            _cboFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboFormat.Dock = DockStyle.Top;
            _cboFormat.Margin = new Padding(0, 0, 6, 0);
            _cboFormat.Items.AddRange(new object[] { "NAT", "CSV", "TSV", "XML", "W3C" });
            _cboFormat.SelectedIndex = 0;

            _numTimeout = new NumericUpDown();
            _numTimeout.Dock = DockStyle.Top;
            _numTimeout.Margin = new Padding(6, 0, 0, 0);
            _numTimeout.Minimum = 10;
            _numTimeout.Maximum = 3600;
            _numTimeout.Value = 300;

            rowFmt.Controls.Add(lblFmt, 0, 0);
            rowFmt.Controls.Add(lblTimeout, 1, 0);
            rowFmt.Controls.Add(_cboFormat, 0, 1);
            rowFmt.Controls.Add(_numTimeout, 1, 1);

            Label lblExtra = new Label();
            lblExtra.Text = "附加 LogParser 参数（可选）";
            lblExtra.AutoSize = true;
            lblExtra.Margin = new Padding(0, 0, 0, 4);

            _txtExtraArgs = new TextBox();
            _txtExtraArgs.Dock = DockStyle.Top;
            _txtExtraArgs.Margin = new Padding(0, 0, 0, 2);
            _txtExtraArgs.BorderStyle = BorderStyle.FixedSingle;
            _txtExtraArgs.Text = "-rtp:-1";

            Label lblExtraHint = new Label();
            lblExtraHint.Text = "-rtp:-1 输出全部行（NAT 默认只显示 10 行）；加 -q:on 可关掉尾部统计块。"
                + "\r\n输出格式：NAT 表格 / CSV 逗号 / TSV 制表符 / XML（UTF-16）/ W3C 日志格式；"
                + "换成非 NAT 时会自动去掉只对 NAT 有效的 -rtp。";
            lblExtraHint.AutoSize = true;
            lblExtraHint.MaximumSize = new Size(460, 0);
            lblExtraHint.Font = new Font(Font.FontFamily, 8F);
            lblExtraHint.ForeColor = Color.FromArgb(140, 148, 160);
            lblExtraHint.Margin = new Padding(0, 0, 0, 14);

            CheckBox chkRaw = new CheckBox();
            chkRaw.Name = "chkRaw";
            chkRaw.Text = "原始列宽（不压缩 LogParser 输出的对齐空格）";
            chkRaw.AutoSize = true;
            chkRaw.Margin = new Padding(0, 0, 0, 10);
            chkRaw.Checked = false;
            chkRaw.CheckedChanged += delegate(object s, EventArgs e) { _rawColumns = chkRaw.Checked; };

            Label lblTimeHint = new Label();
            lblTimeHint.Text = "时间筛选在上面的筛选条里设置：勾「按时间段筛选」后，本次所有查询都会自动带上 TimeGenerated 区间。";
            lblTimeHint.AutoSize = true;
            lblTimeHint.MaximumSize = new Size(420, 0);
            lblTimeHint.Font = new Font(Font.FontFamily, 8F);
            lblTimeHint.ForeColor = Color.FromArgb(140, 148, 160);
            lblTimeHint.Margin = new Padding(0, 0, 0, 0);

            adv.Controls.Add(lblParam, 0, 0);
            adv.Controls.Add(_txtParam, 0, 1);
            adv.Controls.Add(lblParamHint, 0, 2);
            adv.Controls.Add(rowFmt, 0, 3);
            adv.Controls.Add(lblExtra, 0, 4);
            adv.Controls.Add(_txtExtraArgs, 0, 5);
            adv.Controls.Add(lblExtraHint, 0, 6);
            adv.Controls.Add(chkRaw, 0, 7);
            adv.Controls.Add(lblTimeHint, 0, 8);
            advBox.Controls.Add(adv);
            pageAdv.Controls.Add(advBox);
            _tabs.TabPages.Add(pageAdv);

            // ---------- 页三：手动命令（检测项没覆盖到的查询，直接写命令行）
            _pageManual = new TabPage("手动命令");
            _pageManual.BackColor = Color.White;
            _pageManual.Padding = new Padding(10);

            Panel manBox = new Panel();
            manBox.Dock = DockStyle.Fill;
            manBox.AutoScroll = false;

            TableLayoutPanel man = new TableLayoutPanel();
            // 必须 Fill + 固定行规则：Dock=Top 配 AutoSize 时表格会按内容撑到 888px，
            // 比左栏还宽，横向滚动条就出来了（实测过一次）。
            man.Dock = DockStyle.Fill;
            man.AutoSize = false;
            man.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            man.ColumnCount = 1;
            man.RowCount = 7;
            man.Padding = new Padding(0, 4, 0, 4);
            man.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            man.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            man.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            man.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            man.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            man.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            man.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            man.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label lblManualHint = new Label();
            lblManualHint.Text = "预设检测项没覆盖到的，直接在这里写。三种写法都认："
                + "\r\n 1) 只写 SQL：SELECT ... FROM Security WHERE EventID = 4624"
                + "\r\n 2) 整条命令行：LogParser.exe -i:EVT -o:NAT \"SELECT ... FROM ...\""
                + "\r\n 3) 参数 + SQL：-i:EVTX -o:CSV \"SELECT ... FROM ...\""
                + "\r\nCtrl+Enter 执行；结果追加到右侧结果区，可照常导出。";
            lblManualHint.AutoSize = true;
            lblManualHint.MaximumSize = new Size(450, 0);
            lblManualHint.Font = new Font(Font.FontFamily, 8F);
            lblManualHint.ForeColor = Color.FromArgb(130, 138, 150);
            lblManualHint.Margin = new Padding(0, 2, 0, 8);

            Label lblHist = new Label();
            lblHist.Text = "最近执行（点选回填）：";
            lblHist.AutoSize = true;
            lblHist.Font = new Font(Font.FontFamily, 8F);
            lblHist.ForeColor = Color.FromArgb(140, 148, 160);
            lblHist.Margin = new Padding(0, 0, 0, 3);

            _cboHistory = new ComboBox();
            _cboHistory.Name = "cboHistory";
            _cboHistory.DropDownStyle = ComboBoxStyle.DropDownList;
            // AutoSize=true 的 ComboBox 在 TableLayoutPanel 里会按最长条目撑宽（实测 826px），
            // 把左栏顶出横向滚动条；关掉 AutoSize 让它老实待在单元格里。
            _cboHistory.AutoSize = false;
            _cboHistory.Dock = DockStyle.Fill;
            _cboHistory.Margin = new Padding(0, 0, 0, 8);
            _cboHistory.DropDownWidth = 520;
            _cboHistory.SelectedIndexChanged += delegate
            {
                HistoryEntry h = _cboHistory.SelectedItem as HistoryEntry;
                if (h != null) { _txtManual.Text = h.Command; return; }
                if (_cboHistory.SelectedItem == null) return;
                string s = _cboHistory.SelectedItem.ToString();
                if (s.Length > 0) _txtManual.Text = s;
            };

            _txtManual = new TextBox();
            _txtManual.Name = "txtManual";
            _txtManual.Multiline = true;
            _txtManual.ScrollBars = ScrollBars.Vertical;
            _txtManual.AcceptsReturn = true;
            _txtManual.WordWrap = true;
            _txtManual.Dock = DockStyle.Fill;
            _txtManual.Height = 190;
            _txtManual.Font = new Font("Consolas", 9F);
            _txtManual.BorderStyle = BorderStyle.FixedSingle;
            _txtManual.Margin = new Padding(0, 0, 0, 4);
            _txtManual.Text = DefaultManualSample;
            _txtManual.TextChanged += delegate { UpdateManualPreview(); };
            _txtManual.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    OnRunManual();
                }
            };

            FlowLayoutPanel manBtns = new FlowLayoutPanel();
            manBtns.AutoSize = true;
            manBtns.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            manBtns.WrapContents = true;
            manBtns.Margin = new Padding(0, 0, 0, 6);

            _btnRunManual = MkButton("▶ 执行命令 (Ctrl+Enter)", 0, 0, 190);
            _btnRunManual.Name = "btnRunManual";
            _btnRunManual.Click += delegate { OnRunManual(); };
            _btnManualSample = MkSmall("插入示例");
            _btnManualSample.Click += delegate { _txtManual.Text = DefaultManualSample; };
            _btnManualClear = MkSmall("清空");
            _btnManualClear.Click += delegate { _txtManual.Text = ""; };

            manBtns.Controls.Add(_btnRunManual);
            manBtns.Controls.Add(_btnManualSample);
            manBtns.Controls.Add(_btnManualClear);

            _chkManualTime = new CheckBox();
            _chkManualTime.Name = "chkManualTime";
            _chkManualTime.Text = "套用上方的时间筛选（只对 TimeGenerated 字段生效）";
            _chkManualTime.AutoSize = true;
            _chkManualTime.Checked = true;
            _chkManualTime.Margin = new Padding(0, 0, 0, 4);

            _lblManualParse = new Label();
            _lblManualParse.Name = "lblManualParse";
            _lblManualParse.AutoSize = true;
            _lblManualParse.MaximumSize = new Size(450, 0);
            _lblManualParse.Font = new Font(Font.FontFamily, 8F);
            _lblManualParse.ForeColor = Color.FromArgb(140, 148, 160);

            man.Controls.Add(lblManualHint, 0, 0);
            man.Controls.Add(lblHist, 0, 1);
            man.Controls.Add(_cboHistory, 0, 2);
            man.Controls.Add(_txtManual, 0, 3);
            man.Controls.Add(manBtns, 0, 4);
            man.Controls.Add(_chkManualTime, 0, 5);
            man.Controls.Add(_lblManualParse, 0, 6);

            manBox.Controls.Add(man);
            _pageManual.Controls.Add(manBox);
            _tabs.TabPages.Add(_pageManual);
            LoadManualHistory();
            UpdateManualPreview();

            _clbItems.Dock = DockStyle.Fill;

            // .NET Framework 的老毛病：SplitContainer 先把 Panel1 撑到 936，三个 TabPage
            // 都按 908 的宽度定下来了；随后 SplitterDistance 收到 430、TabControl 自己缩到
            // 410，TabPage 却不跟着缩，于是页面比标签区宽一倍，右边的输入框直接被裁掉。
            // 这里在标签控件每次变尺寸时，把页面尺寸强行同步回显示区。
            _tabs.Resize += delegate(object s, EventArgs e) { SyncTabPageBounds(); };

            host.Controls.Add(_tabs);
            host.Controls.Add(header);
            _split.Panel1.Controls.Add(host);
        }

        private bool _syncingPages;

        /// <summary>
        /// 把每个 TabPage 的尺寸强制同步到 TabControl 的显示区。
        /// TabControl 只在切换页/部分重排时更新页面尺寸，SplitContainer 改距离后
        /// 页面会停留在旧宽度（实测 908 对 410），内容右侧被裁掉。
        /// </summary>
        private void SyncTabPageBounds()
        {
            if (_syncingPages || _tabs == null || !_tabs.IsHandleCreated) return;
            _syncingPages = true;
            try
            {
                Rectangle r = _tabs.DisplayRectangle;
                for (int i = 0; i < _tabs.TabPages.Count; i++)
                {
                    TabPage p = _tabs.TabPages[i];
                    if (p.Bounds != r) p.Bounds = r;
                }
            }
            finally { _syncingPages = false; }
        }

        private void BuildRightPanel()
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Color.White;
            host.Padding = new Padding(10, 8, 10, 8);

            // 按钮已迁移到窗口底部那条通栏操作条（BuildActionBar），这里只放结果区
            _rtbOutput = new RichTextBox();
            _rtbOutput.Name = "rtbOutput";
            _rtbOutput.Dock = DockStyle.Fill;
            _rtbOutput.ReadOnly = true;
            _rtbOutput.BackColor = Color.FromArgb(28, 30, 34);
            _rtbOutput.ForeColor = Color.FromArgb(222, 226, 230);
            _rtbOutput.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _rtbOutput.WordWrap = false;
            _rtbOutput.ScrollBars = RichTextBoxScrollBars.Both;
            _rtbOutput.DetectUrls = false;
            _rtbOutput.BorderStyle = BorderStyle.None;
            _rtbOutput.HideSelection = false;
            try { _rtbOutput.LanguageOption = RichTextBoxLanguageOptions.UIFonts; } catch (Exception) { }

            host.Controls.Add(_rtbOutput);
            _split.Panel2.Controls.Add(host);
        }

        // ---------------------------------------------------------------- 时间筛选条

        /// <summary>日期选择器：只选"年月日"，时分交给后面的两个下拉框。</summary>
        private DateTimePicker MakePicker()
        {
            DateTimePicker p = new DateTimePicker();
            p.Format = DateTimePickerFormat.Custom;
            p.CustomFormat = "yyyy-MM-dd";
            // 宽度按字体实测：写死宽度在 150% 缩放下会裁字
            p.Width = Math.Max(104, TextRenderer.MeasureText("2099-12-31", Font).Width + 36);
            p.Margin = new Padding(0, 3, 6, 0);
            return p;
        }

        /// <summary>00 ~ max 的两位数列下拉框（时 0~23，分 0~59）。</summary>
        private ComboBox MakeNumCombo(string name, int max)
        {
            ComboBox c = new ComboBox();
            c.Name = name;
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.MaxDropDownItems = 10;
            for (int i = 0; i <= max; i++) c.Items.Add(i.ToString("00"));
            c.SelectedIndex = 0;
            c.Width = Math.Max(52, TextRenderer.MeasureText("00 ▼", Font).Width + 28);
            c.Margin = new Padding(0, 3, 2, 0);
            return c;
        }

        private Panel BuildFilterBar()
        {
            Panel bar = new Panel();
            bar.Name = "filterBar";
            bar.Dock = DockStyle.Top;
            // 高度交给 AutoSize：150% 缩放时行高变大，写死 48 会把内容裁掉
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.Padding = new Padding(14, 8, 14, 6);
            bar.BackColor = Color.FromArgb(247, 249, 251);

            FlowLayoutPanel f = new FlowLayoutPanel();
            f.Name = "filterRow";
            f.Dock = DockStyle.Top;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // 允许折行：窗口窄或 150% 缩放时整行自动变成两行，而不是把尾部裁掉
            f.WrapContents = true;
            f.FlowDirection = FlowDirection.LeftToRight;
            f.AutoScroll = false;
            f.Margin = new Padding(0);

            _chkTime = new CheckBox();
            _chkTime.Name = "chkTime";
            _chkTime.Text = "按时间段筛选";
            _chkTime.AutoSize = true;
            _chkTime.Margin = new Padding(0, 4, 14, 0);
            _chkTime.CheckedChanged += delegate { ApplyTimeRangeFromUi(true); };

            Label lblQuick = MkFlowLabel("快捷：", 8, 4);
            lblQuick.Name = "lblQuick";

            _cboRange = new ComboBox();
            _cboRange.Name = "cboRange";
            _cboRange.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboRange.Items.AddRange(new object[] {
                "最近 1 小时", "最近 24 小时", "最近 3 天", "最近 7 天", "最近 30 天", "自定义…" });
            int cboW = 0;
            for (int i = 0; i < _cboRange.Items.Count; i++)
            {
                int w = TextRenderer.MeasureText(_cboRange.Items[i].ToString() + " ▼", Font).Width;
                if (w > cboW) cboW = w;
            }
            _cboRange.Width = Math.Max(110, cboW + 34);
            _cboRange.Margin = new Padding(0, 3, 16, 0);
            _cboRange.SelectedIndex = 1;
            _cboRange.SelectedIndexChanged += delegate { ApplyTimeRangeFromUi(true); };

            Label lblFrom = MkFlowLabel("从", 8, 6);
            lblFrom.Name = "lblFrom";

            _dtFrom = MakePicker();
            _cboFh = MakeNumCombo("cboFromHour", 23);
            _cboFh.Margin = new Padding(0, 3, 2, 0);
            _cboFm = MakeNumCombo("cboFromMin", 59);
            Label lblFh = MkFlowLabel("时", 8, 2);
            Label lblFm = MkFlowLabel("分", 8, 14);

            Label lblTo = MkFlowLabel("到", 8, 6);
            lblTo.Name = "lblTo";

            _dtTo = MakePicker();
            _cboTh = MakeNumCombo("cboToHour", 23);
            _cboTm = MakeNumCombo("cboToMin", 59);
            Label lblTh = MkFlowLabel("时", 8, 2);
            Label lblTm = MkFlowLabel("分", 8, 14);

            Button btnNow = MkSmall("此刻");
            btnNow.Name = "btnNow";
            btnNow.Margin = new Padding(0, 1, 14, 0);
            btnNow.Click += delegate
            {
                // 一键把上界设成"现在"、下界设成 1 小时前，方便先粗筛再手调
                if (_cboRange.SelectedIndex != 5) _cboRange.SelectedIndex = 5;
                DateTime now = DateTime.Now;
                SetDateTime(_dtTo, _cboTh, _cboTm, now);
                SetDateTime(_dtFrom, _cboFh, _cboFm, now.AddHours(-1));
            };

            Label lblTail = new Label();
            lblTail.Text = "（只影响本次执行，不改 queries.txt）";
            lblTail.AutoSize = true;
            lblTail.ForeColor = Color.FromArgb(140, 148, 160);
            lblTail.Font = new Font(Font.FontFamily, 8.5F);
            lblTail.Margin = new Padding(2, 8, 0, 0);

            f.Controls.Add(_chkTime);
            f.Controls.Add(lblQuick);
            f.Controls.Add(_cboRange);
            f.Controls.Add(lblFrom);
            f.Controls.Add(_dtFrom);
            f.Controls.Add(_cboFh);
            f.Controls.Add(lblFh);
            f.Controls.Add(_cboFm);
            f.Controls.Add(lblFm);
            f.Controls.Add(lblTo);
            f.Controls.Add(_dtTo);
            f.Controls.Add(_cboTh);
            f.Controls.Add(lblTh);
            f.Controls.Add(_cboTm);
            f.Controls.Add(lblTm);
            f.Controls.Add(btnNow);
            f.Controls.Add(lblTail);

            bar.Controls.Add(f);

            // 先给一组像样的初值（最近 24 小时），这样即使筛选未启用，
            // 用户看到的也不是一排 00:00 —— 一眼就知道这两个框是干什么的。
            DateTime now0 = DateTime.Now;
            SetDateTime(_dtTo, _cboTh, _cboTm, now0);
            SetDateTime(_dtFrom, _cboFh, _cboFm, now0.AddDays(-1));

            ApplyTimeRangeFromUi(true);
            return bar;
        }

        /// <summary>筛选条上的小标签（AutoSize + 顶端对齐，别用绝对坐标）。</summary>
        private Label MkFlowLabel(string text, int topMargin, int rightMargin)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, topMargin, rightMargin, 0);
            return l;
        }

        private static int PartOf(ComboBox c)
        {
            if (c == null || c.SelectedIndex < 0) return 0;
            return c.SelectedIndex;
        }

        private static void SetPart(ComboBox c, int v)
        {
            if (c == null) return;
            if (v < 0) v = 0;
            if (v >= c.Items.Count) v = c.Items.Count - 1;
            c.SelectedIndex = v;
        }

        private static void SetDateTime(DateTimePicker dt, ComboBox hh, ComboBox mm, DateTime v)
        {
            if (dt == null) return;
            try { dt.Value = v.Date; } catch (Exception) { }
            SetPart(hh, v.Hour);
            SetPart(mm, v.Minute);
        }

        private DateTime FromValue()
        {
            return _dtFrom.Value.Date.AddHours(PartOf(_cboFh)).AddMinutes(PartOf(_cboFm));
        }

        private DateTime ToValue()
        {
            return _dtTo.Value.Date.AddHours(PartOf(_cboTh)).AddMinutes(PartOf(_cboTm));
        }

        /// <summary>快捷范围下拉 / 勾选框变化时，刷新时间控件的值与可用状态。</summary>
        private void ApplyTimeRangeFromUi(bool refreshPickers)
        {
            if (_chkTime == null || _cboRange == null || _dtFrom == null || _dtTo == null) return;

            bool on = _chkTime.Checked;
            _cboRange.Enabled = on;

            bool editable = on && _cboRange.SelectedIndex == 5;
            _dtFrom.Enabled = editable;
            _dtTo.Enabled = editable;
            _cboFh.Enabled = editable;
            _cboFm.Enabled = editable;
            _cboTh.Enabled = editable;
            _cboTm.Enabled = editable;

            if (!on) return;

            if (editable)
            {
                if (refreshPickers && ToValue() < FromValue())
                    SetDateTime(_dtFrom, _cboFh, _cboFm, ToValue().AddHours(-1));
                return;
            }

            if (!refreshPickers) return;

            DateTime now = DateTime.Now;
            DateTime from;
            switch (_cboRange.SelectedIndex)
            {
                case 0: from = now.AddHours(-1); break;
                case 2: from = now.AddDays(-3); break;
                case 3: from = now.AddDays(-7); break;
                case 4: from = now.AddDays(-30); break;
                default: from = now.AddDays(-1); break;
            }
            SetDateTime(_dtTo, _cboTh, _cboTm, now);
            SetDateTime(_dtFrom, _cboFh, _cboFm, from);
        }

        /// <summary>自动化入口：切回“按分类勾选”页。</summary>
        internal void ShowPresetTab()
        {
            if (_tabs != null && _tabs.TabPages.Count > 0) _tabs.SelectedIndex = 0;
        }

        /// <summary>自动化入口：切到“参数与超时”页（截图用）。</summary>
        internal void ShowOptionsTab()
        {
            if (_tabs != null && _tabs.TabPages.Count > 1) _tabs.SelectedIndex = 1;
        }

        /// <summary>自动化入口：按“最近 N 小时”打开时间筛选，走的是和用户点界面完全相同的控件。</summary>
        internal void SetTimeFilter(int hours)
        {
            SetTimeFilterExact(DateTime.Now.AddHours(-hours), DateTime.Now);
        }

        /// <summary>自动化入口：把时间筛选设成指定的起止时刻（含分秒）。</summary>
        internal void SetTimeFilterExact(DateTime from, DateTime to)
        {
            if (_chkTime == null || _cboRange == null) return;
            _chkTime.Checked = true;
            _cboRange.SelectedIndex = 5;              // 自定义…
            SetDateTime(_dtTo, _cboTh, _cboTm, to);
            SetDateTime(_dtFrom, _cboFh, _cboFm, from);
            ApplyTimeRangeFromUi(true);
        }

        /// <summary>当前选中的输出格式（NAT/CSV/TSV/XML/W3C）。</summary>
        internal string OutputFormatName
        {
            get { return _cboFormat == null || _cboFormat.SelectedItem == null ? "NAT" : _cboFormat.SelectedItem.ToString(); }
        }

        /// <summary>切换输出格式（自动化用）。</summary>
        internal void SetOutputFormat(string fmt)
        {
            if (_cboFormat == null || string.IsNullOrEmpty(fmt)) return;
            for (int i = 0; i < _cboFormat.Items.Count; i++)
            {
                if (string.Equals(_cboFormat.Items[i].ToString(), fmt.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _cboFormat.SelectedIndex = i;
                    return;
                }
            }
        }

        /// <summary>把界面上的时间筛选解析成查询条件（精确到分，秒补 00）。未勾选时两个都是 null。</summary>
        private void ResolveTimeRange(out DateTime? from, out DateTime? to)
        {
            from = null;
            to = null;
            if (_chkTime == null || !_chkTime.Checked) return;
            DateTime a = FromValue();
            DateTime b = ToValue();
            if (a > b) { DateTime t = a; a = b; b = t; }
            from = a;
            // 界面上只选到"分"，所以上界补到这一分钟的最后一秒：
            // 选"到 15:45" 应该是包含 15:45:30 这类记录，而不是把它们挡在外面。
            to = b.AddSeconds(59);
        }

        // ---------------------------------------------------------------- 底部操作条

        private Panel BuildActionBar()
        {
            Panel act = new Panel();
            act.Name = "actionBar";
            act.Dock = DockStyle.Bottom;
            act.Height = 60;
            act.BackColor = Color.White;
            act.Padding = new Padding(14, 10, 14, 10);

            TableLayoutPanel g = new TableLayoutPanel();
            g.Dock = DockStyle.Fill;
            g.ColumnCount = 2;
            g.RowCount = 1;
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            FlowLayoutPanel left = new FlowLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.WrapContents = false;
            left.FlowDirection = FlowDirection.LeftToRight;
            left.AutoScroll = false;
            left.Margin = new Padding(0);

            _btnStart = MkPrimary(StartButtonText, 0, 0, 200);
            _btnStart.Name = "btnStart";
            _btnStart.Height = 38;
            _btnStart.Margin = new Padding(0, 0, 10, 0);
            _btnStart.Click += delegate { OnStart(); };

            _btnStop = MkButton("停止", 0, 0, 86);
            _btnStop.Name = "btnStop";
            _btnStop.Height = 34;
            _btnStop.Margin = new Padding(0, 2, 10, 0);
            _btnStop.Enabled = false;
            _btnStop.Click += delegate { OnStop(); };

            _btnClear = MkButton("清空结果", 0, 0, 100);
            _btnClear.Name = "btnClear";
            _btnClear.Height = 34;
            _btnClear.Margin = new Padding(0, 2, 10, 0);
            _btnClear.Click += delegate { _buf.Length = 0; _scrollPending = false; _rtbOutput.Clear(); _totalElements = -1; UpdateCountLabel(); };

            _btnSaveResult = MkButton("导出结果", 0, 0, 100);
            _btnSaveResult.Name = "btnSaveResult";
            _btnSaveResult.Height = 34;
            _btnSaveResult.Margin = new Padding(0, 2, 0, 0);
            _btnSaveResult.Click += delegate { OnSaveResult(); };

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_btnStart, "依次执行所有已勾选的检测项（F5）");
            tip.SetToolTip(_btnStop, "中止剩余查询（已输出的结果保留）");

            left.Controls.Add(_btnStart);
            left.Controls.Add(_btnStop);
            left.Controls.Add(_btnClear);
            left.Controls.Add(_btnSaveResult);

            _lblSelCount = new Label();
            _lblSelCount.Name = "lblSelCount";
            _lblSelCount.AutoSize = true;
            _lblSelCount.Anchor = AnchorStyles.Right;
            _lblSelCount.Text = "已选 0 项";
            _lblSelCount.ForeColor = Color.FromArgb(120, 128, 140);
            _lblSelCount.Margin = new Padding(10, 13, 0, 0);

            g.Controls.Add(left, 0, 0);
            g.Controls.Add(_lblSelCount, 1, 0);
            act.Controls.Add(g);
            return act;
        }

        // ---------------------------------------------------------------- UI 小工具
        private Button MkButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            // 宽度只作为下限：高 DPI 下文字更宽，AutoSize 让按钮自己长到够放为止
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(w, 30);
            b.Padding = new Padding(6, 0, 6, 0);
            b.FlatStyle = FlatStyle.System;
            b.UseVisualStyleBackColor = true;
            return b;
        }

        private Button MkPrimary(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(w, 34);
            b.Padding = new Padding(10, 0, 10, 0);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(32, 120, 224);
            b.ForeColor = Color.White;
            b.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            return b;
        }

        private Button MkSmall(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(54, 24);
            b.Margin = new Padding(0, 4, 6, 0);
            b.FlatStyle = FlatStyle.System;
            b.Font = new Font(Font.FontFamily, 8.5F);
            return b;
        }

        private static Label MkLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        // ---------------------------------------------------------------- 配置加载

        private void LoadConfigSafe(string path)
        {
            if (path == null)
            {
                string candidate = Path.Combine(_exeDir, "queries.txt");
                if (!File.Exists(candidate))
                {
                    string alt = Path.Combine(_exeDir, "LogSentry.queries.txt");
                    if (File.Exists(alt)) candidate = alt;
                }
                if (!File.Exists(candidate))
                {
                    // 首次运行：写出内嵌默认配置，方便用户直接编辑扩项
                    try { File.WriteAllText(candidate, BuiltinQueries.Text, new UTF8Encoding(true)); }
                    catch (Exception) { }
                }
                path = candidate;
            }
            _configPath = path;
            _config = ConfigLoader.Load(path);
            RebuildCheckboxes();
        }

        private void RebuildCheckboxes()
        {
            _suppressCheckEvents = true;
            try
            {
                _panelItems.Controls.Clear();
                _checkBoxes.Clear();
                _clbItems.Items.Clear();

                if (_config == null || _config.Items.Count == 0)
                {
                    Label none = new Label();
                    none.Text = "未加载到任何检测项。\r\n请检查程序目录下的 queries.txt。";
                    none.AutoSize = true;
                    none.Location = new Point(8, 8);
                    none.ForeColor = Color.Firebrick;
                    _panelItems.Controls.Add(none);
                    AppendErrorBanner();
                    return;
                }

                string lastGroup = null;
                int y = 6;
                int groupIndex = 0;
                for (int i = 0; i < _config.Items.Count; i++)
                {
                    QueryItem item = _config.Items[i];
                    if (item.Group != lastGroup)
                    {
                        if (lastGroup != null) y += 10;
                        Label gl = new Label();
                        gl.Text = "▌ " + item.Group;
                        gl.AutoSize = false;
                        gl.Height = 24;
                        gl.Width = _panelItems.Width - 24;
                        gl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                        gl.Location = new Point(6, y);
                        gl.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
                        gl.ForeColor = Color.FromArgb(45, 100, 170);
                        gl.BackColor = Color.FromArgb(238, 243, 250);
                        gl.TextAlign = ContentAlignment.MiddleLeft;
                        _panelItems.Controls.Add(gl);
                        y += 28;
                        lastGroup = item.Group;
                        groupIndex++;
                    }

                    CheckBox cb = new CheckBox();
                    cb.Text = item.Label;
                    cb.Tag = item;
                    cb.AutoSize = false;
                    cb.Height = 24;
                    cb.Width = _panelItems.Width - 40;
                    cb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                    cb.Location = new Point(24, y);
                    cb.Checked = item.DefaultOn;
                    if (!string.IsNullOrEmpty(item.Note)) cb.Text += "    " + item.Note;
                    ToolTip tp = new ToolTip();
                    tp.SetToolTip(cb, item.Query);
                    cb.CheckedChanged += delegate { StartFlushTimer(); };
                    _panelItems.Controls.Add(cb);
                    _checkBoxes.Add(cb);
                    y += 26;
                }

                AppendErrorBanner();
                UpdateCountLabel();
            }
            finally
            {
                _suppressCheckEvents = false;
            }
        }

        private void AppendErrorBanner()
        {
            if (_config == null || _config.Errors.Count == 0) return;
            SetStatus("配置加载有 " + _config.Errors.Count + " 处警告（详见导出结果首部）", false);
        }

        private void ResolveLogParserPath()
        {
            string def = Path.Combine(_exeDir, "LogParser.exe");
            if (File.Exists(def))
            {
                _txtLogParser.Text = def;
                return;
            }
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] guess =
            {
                Path.Combine(pf86, @"Log Parser 2.2\LogParser.exe"),
                Path.Combine(pf, @"Log Parser 2.2\LogParser.exe"),
                Path.Combine(_exeDir, @"Log Parser 2.2\LogParser.exe")
            };
            for (int i = 0; i < guess.Length; i++)
            {
                if (File.Exists(guess[i])) { _txtLogParser.Text = guess[i]; return; }
            }
            _txtLogParser.Text = def;
        }

        // ---------------------------------------------------------------- 勾选状态

        private void SetAllChecked(bool on)
        {
            _suppressCheckEvents = true;
            try
            {
                for (int i = 0; i < _checkBoxes.Count; i++) _checkBoxes[i].Checked = on;
                for (int i = 0; i < _clbItems.Items.Count; i++) _clbItems.SetItemChecked(i, on);
            }
            finally { _suppressCheckEvents = false; }
            UpdateCountLabel();
        }

        private void ApplyDefaultSelection()
        {
            _suppressCheckEvents = true;
            try
            {
                for (int i = 0; i < _checkBoxes.Count; i++)
                {
                    QueryItem it = _checkBoxes[i].Tag as QueryItem;
                    _checkBoxes[i].Checked = it != null && it.DefaultOn;
                }
            }
            finally { _suppressCheckEvents = false; }
            UpdateCountLabel();
        }

        /// <summary>自动化用：按 id 勾选指定检测项（找不到的 id 会被忽略）。</summary>
        internal void SelectByIds(IList<string> ids)
        {
            if (ids == null || ids.Count == 0) return;
            HashSet<string> want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ids.Count; i++) if (!string.IsNullOrEmpty(ids[i])) want.Add(ids[i].Trim());
            _suppressCheckEvents = true;
            try
            {
                for (int i = 0; i < _checkBoxes.Count; i++)
                {
                    QueryItem it = _checkBoxes[i].Tag as QueryItem;
                    _checkBoxes[i].Checked = it != null && want.Contains(it.Id);
                }
            }
            finally { _suppressCheckEvents = false; }
            UpdateCountLabel();
        }

        private List<QueryItem> GetSelectedItems()
        {
            List<QueryItem> sel = new List<QueryItem>();
            // 以自绘复选框为准：_panelItems 在所属页签未选中时会报 Visible=false，
            // 不能拿 Visible 当开关，否则刚启动时“已选 0 项”与实际勾选数不符。
            if (_checkBoxes.Count > 0)
            {
                for (int i = 0; i < _checkBoxes.Count; i++)
                {
                    if (_checkBoxes[i].Checked)
                    {
                        QueryItem it = _checkBoxes[i].Tag as QueryItem;
                        if (it != null) sel.Add(it);
                    }
                }
            }
            else
            {
                for (int i = 0; i < _clbItems.CheckedItems.Count; i++)
                {
                    QueryItem it = _clbItems.CheckedItems[i] as QueryItem;
                    if (it != null) sel.Add(it);
                }
                // 按配置顺序重排，保证“按配置顺序依次执行”
                if (_config != null)
                {
                    List<QueryItem> ordered = new List<QueryItem>();
                    for (int i = 0; i < _config.Items.Count; i++)
                        if (sel.Contains(_config.Items[i])) ordered.Add(_config.Items[i]);
                    sel = ordered;
                }
            }
            return sel;
        }

        private void UpdateCountLabel()
        {
            List<QueryItem> sel = GetSelectedItems();
            int total = _config == null ? 0 : _config.Items.Count;
            _tsslCount.Text = "已选 " + sel.Count + " / " + total;
            if (_lblSelCount != null)
                _lblSelCount.Text = "当前已选 " + sel.Count + " 个检测项";
        }

        private void StartFlushTimer()
        {
            UpdateCountLabel();
        }

        // ---------------------------------------------------------------- 状态栏

        private void SetStatus(string text, bool busy)
        {
            if (_lblStatus == null) return;
            _lblStatus.Text = text;
            _lblStatus.ForeColor = busy ? Color.FromArgb(32, 120, 224) : SystemColors.ControlText;
        }

        private void UpdatePrivilegeLabel()
        {
            bool admin;
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p =
                    new System.Security.Principal.WindowsPrincipal(id);
                admin = p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { admin = false; }
            _tsslPriv.Text = admin ? "✔ 管理员权限" : "✖ 非管理员（Security 日志可能读取失败）";
            _tsslPriv.ForeColor = admin ? Color.FromArgb(0, 130, 70) : Color.Firebrick;
        }

        // ---------------------------------------------------------------- 输出区

        private void WriteLine(string text, Color? color, bool bold)
        {
            if (_rtbOutput == null) return;
            AppendToOutput(RichTextSafe(text) + "\r\n", color, bold);
        }

        /// <summary>
        /// RichTextBox 在 AppendText 里遇到 \0 或孤立的 \r 会静默截断后面的所有内容，
        /// 而 LogParser 的 NAT 输出偶尔就带这种字符——必须先清掉，否则结果区会莫名空掉。
        /// </summary>
        private static string RichTextSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\0') < 0 && s.IndexOf('\r') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\0') continue;
                if (c == '\r')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\n') continue; // \r\n -> 由 \n 承担
                    sb.Append("\r\n");                                   // 孤立 \r -> 换行
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// 输出统一先攒进 _buf，再由节拍统一落到 RichTextBox。
        /// 逐行 AppendText + ScrollToCaret 在长结果下极慢（实测 18 次写入要 15 秒：
        /// ScrollToCaret 10.8s、SelectionColor/Font 4.0s、AppendText 3.9s），
        /// 连续同样式的内容合并成一次写入、滚动每拍最多做一次。
        ///
        /// 样式变化时把插入点挪到「本段首行的行首」再刷：RichTextBox 的样式只作用于
        /// 插入点之后的文本，从旧行末尾起刷会让整行反白。
        /// </summary>
        private void AppendToOutput(string text, Color? color, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_rtbOutput == null) return;
            Color want = color.HasValue ? color.Value : _rtbOutput.ForeColor;
            if (_buf.Length > 0 && (_bufBold != bold || _bufColor != want))
            {
                FlushOutput();
                MoveToNewLineStart();
            }
            if (_buf.Length == 0)
            {
                _bufBold = bold;
                _bufColor = want;
            }
            _buf.Append(text);
            _lastAppendUtc = DateTime.UtcNow;
            if (_buf.Length >= BufLimit) FlushOutput();
            EnsureFlushTimer();
        }

        /// <summary>
        /// 把插入点移到末行的行首（零长度选区，不会产生反白）。
        /// 这样下一个片段的字体/颜色只内嵌在新行行首，不会把上一行整段刷成新样式。
        /// </summary>
        private void MoveToNewLineStart()
        {
            try
            {
                string all = _rtbOutput.Text;
                if (all.Length == 0) return;
                int pos = all.LastIndexOf('\n');
                _rtbOutput.SelectionStart = pos < 0 ? 0 : pos + 1;
                _rtbOutput.SelectionLength = 0;
            }
            catch (Exception) { }
        }

        /// 单次落盘上限：一次写入太大反而拖慢后面的重排，256KB 对结果区足够。
        private const int BufLimit = 262144;

        /// 空闲多久才落盘。毫秒级连续输出（一项日志几千行）要合并成一次写入，
        /// 否则每轮都付一次 400ms+ 的重排代价。
        private const int IdleFlushMs = 700;

        private void EnsureFlushTimer()
        {
            if (_flushTimer == null)
            {
                _flushTimer = new System.Windows.Forms.Timer();
                _flushTimer.Interval = 120;
                _flushTimer.Tick += delegate(object s, EventArgs e)
                {
                    if (_buf.Length == 0) { _flushTimer.Stop(); return; }
                    if (_buf.Length < BufLimit &&
                        (DateTime.UtcNow - _lastAppendUtc).TotalMilliseconds < IdleFlushMs) return;
                    FlushOutput();
                    ScrollOutputToEnd();
                };
            }
            if (!_flushTimer.Enabled) _flushTimer.Start();
        }

        /// <summary>把缓存的输出一次性写进结果区（可以在任意线程安全地重复调用）。</summary>
        internal void FlushOutput()
        {
            if (_rtbOutput == null || _buf.Length == 0) return;
            string chunk = _buf.ToString();
            _buf.Length = 0;
            Stopwatch pAll = Stopwatch.StartNew();
            Stopwatch px = Stopwatch.StartNew();
            PerfAppendCalls++;
            _rtbOutput.SelectionStart = _rtbOutput.TextLength;
            _rtbOutput.SelectionLength = 0;
            _rtbOutput.SelectionColor = _bufColor;
            PerfFontMs += px.Elapsed.TotalMilliseconds;
            px = Stopwatch.StartNew();
            _rtbOutput.SelectionFont = _bufBold ? BoldOutputFont : RegularOutputFont;
            PerfFontMs += px.Elapsed.TotalMilliseconds;
            px = Stopwatch.StartNew();
            _rtbOutput.AppendText(chunk);
            PerfTextMs += px.Elapsed.TotalMilliseconds;
            PerfAppendMs += pAll.Elapsed.TotalMilliseconds;
            PerfTotalChars += chunk.Length;
            PerfFlushLog.Append(chunk.Length + "字符:" + pAll.Elapsed.TotalMilliseconds.ToString("0") + "ms ");
            if (pAll.Elapsed.TotalMilliseconds > PerfMaxFlushMs)
            {
                PerfMaxFlushMs = pAll.Elapsed.TotalMilliseconds;
                PerfMaxFlushChars = chunk.Length;
            }
            _scrollPending = true;
            // 这里不跟着滚到底：等整个「一项」写完（标题/耗时/正文都进缓冲）再滚一次，
            // 否则每 120ms 滚一次，用户看到的是内容一路往回跳。
        }

        // 字体只建一次：每次 new Font 既慢又漏 GDI 句柄
        private Font RegularOutputFont
        {
            get
            {
                if (_fontRegular == null)
                    _fontRegular = new Font(_rtbOutput.Font.FontFamily, _rtbOutput.Font.Size, FontStyle.Regular);
                return _fontRegular;
            }
        }

        private Font BoldOutputFont
        {
            get
            {
                if (_fontBold == null)
                    _fontBold = new Font(_rtbOutput.Font.FontFamily, _rtbOutput.Font.Size, FontStyle.Bold);
                return _fontBold;
            }
        }

        /// <summary>滚到底部：按节拍做，不做逐行滚动。</summary>
        private void ScrollOutputToEnd()
        {
            if (_rtbOutput == null || !_scrollPending) return;
            _scrollPending = false;
            Stopwatch ps = Stopwatch.StartNew();
            _rtbOutput.SelectionStart = _rtbOutput.TextLength;
            _rtbOutput.ScrollToCaret();
            PerfScrollMs += ps.Elapsed.TotalMilliseconds;
        }

        private void FlushPending()
        {
            if (_pending.Length == 0) return;
            string chunk = _pending.ToString();
            _pending.Length = 0;
            AppendToOutput(RichTextSafe(chunk) + "\r\n", Color.FromArgb(210, 214, 220), false);
            _lastFlush = DateTime.UtcNow;
        }
        private void WriteLineColored(string text, Color? color, bool bold)
        {
            if (_rtbOutput == null) return;
            AppendToOutput(RichTextSafe(text) + "\r\n", color, bold);
        }

        private void AppendResultText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _pending.Append(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            if (!_pending.ToString().EndsWith("\r\n")) _pending.Append("\r\n");
            if ((DateTime.UtcNow - _lastFlush).TotalMilliseconds > 120)
                FlushPending();
        }


        // ---------------------------------------------------------------- 事件

        private void OnBrowseLogParser()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择 LogParser.exe";
                dlg.Filter = "LogParser (LogParser.exe)|LogParser.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtLogParser.Text = dlg.FileName;
                    OnTestLogParser();
                }
            }
        }

        private void OnTestLogParser()
        {
            string path = _txtLogParser.Text.Trim();
            string msg;
            bool ok = LogParserRunner.TestLogParser(path, out msg);
            SetStatus((ok ? "自检通过：" : "自检失败：") + msg, false);
            MessageBox.Show(this, msg, ok ? "LogParser 自检" : "LogParser 自检失败",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private LogParserRunner.Options BuildOptions()
        {
            LogParserRunner.Options opt = new LogParserRunner.Options();
            opt.LogParserPath = _txtLogParser.Text.Trim();
            opt.OutputFormat = _cboFormat.SelectedItem == null ? "NAT" : _cboFormat.SelectedItem.ToString();
            opt.ExtraArguments = _txtExtraArgs.Text;
            opt.TimeoutSeconds = (int)_numTimeout.Value;
            opt.UserParam = _txtParam.Text.Trim();
            DateTime? tf, tt;
            ResolveTimeRange(out tf, out tt);
            opt.TimeFrom = tf;
            opt.TimeTo = tt;
            return opt;
        }

        private async void OnStart()
        {
            await RunSelectionAsync(null);
        }

        // ---------------------------------------------------------------- 手动命令

        private static readonly string DefaultManualSample =
            "-i:EVT -o:NAT -rtp:-1 \"SELECT TimeGenerated, EventID, EXTRACT_TOKEN(Strings, 0, '|') AS 服务名, "
            + "EXTRACT_TOKEN(Strings, 1, '|') AS 映像路径 FROM System WHERE EventID = 7045\"";

        /// <summary>把输入解析成「参数 + SQL」，并把结果回显在页面上，省得用户盲写。</summary>
        private bool UpdateManualPreview()
        {
            if (_lblManualParse == null || _txtManual == null) return false;
            string args, sql;
            if (_txtManual.Text.Trim().Length == 0)
            {
                _lblManualParse.Text = "解析：等待输入";
                return false;
            }
            if (!LogParserRunner.SplitCommandLine(_txtManual.Text, out args, out sql))
            {
                _lblManualParse.Text = "解析：没找到 SELECT 关键字 —— 只写 SQL 也行，但要以 SELECT 开头";
                return false;
            }
            string fmt = LogParserRunner.NormalizeFormat(
                _cboFormat == null || _cboFormat.SelectedItem == null ? "NAT" : _cboFormat.SelectedItem.ToString());
            string rest;
            string oArg = LogParserRunner.ExtractOptionValue(args, "-o:", out rest);
            if (oArg.Length > 0) fmt = LogParserRunner.NormalizeFormat(oArg);
            string iArg = LogParserRunner.ExtractOptionValue(args, "-i:", out rest);
            if (iArg.Length == 0) iArg = "EVT";
            _lblManualParse.Text = "解析：输入格式 " + iArg + " · 输出格式 " + fmt
                + " · 参数 " + (args.Length == 0 ? "(无，用默认 -rtp:-1)" : args)
                + "\r\n语句长度 " + sql.Length + " 字符";
            return true;
        }

        /// <summary>历史命令落地在 %LOCALAPPDATA%\LogSentry\manual-history.txt（便携版解压到临时目录，写在 exe 旁边会随退出丢）。</summary>
        private string ManualHistoryPath
        {
            get
            {
                string dir;
                try
                {
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogSentry");
                }
                catch (Exception) { return null; }
                return Path.Combine(dir, "manual-history.txt");
            }
        }

        private void LoadManualHistory()
        {
            if (_cboHistory == null) return;
            _cboHistory.Items.Clear();
            try
            {
                string path = ManualHistoryPath;
                if (path == null || !File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length && _cboHistory.Items.Count < 10; i++)
                {
                    string s = (lines[i] ?? "").Trim();
                    if (s.Length > 0) _cboHistory.Items.Add(new HistoryEntry(s));
                }
            }
            catch (Exception) { }
            if (_cboHistory.Items.Count == 0)
                _cboHistory.Items.Add("(还没有执行过手动命令)");
        }

        /// <summary>历史下拉里的一行：显示用的短文本 + 回填用的完整命令。</summary>
        private sealed class HistoryEntry
        {
            public readonly string Command;
            private readonly string _display;
            public HistoryEntry(string command)
            {
                Command = command;
                _display = command.Length > 58 ? command.Substring(0, 55) + "..." : command;
            }
            public override string ToString() { return _display; }
        }

        private void SaveManualHistory(string command)
        {
            try
            {
                string one = (command ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (one.Length == 0) return;
                List<string> list = new List<string>();
                string path = ManualHistoryPath;
                if (path != null && File.Exists(path))
                {
                    string[] old = File.ReadAllLines(path, Encoding.UTF8);
                    for (int i = 0; i < old.Length; i++)
                    {
                        string s = (old[i] ?? "").Trim();
                        if (s.Length > 0) list.Add(s);
                    }
                }
                for (int i = list.Count - 1; i >= 0; i--)
                    if (string.Equals(list[i], one, StringComparison.Ordinal)) list.RemoveAt(i);
                list.Insert(0, one);
                while (list.Count > 10) list.RemoveAt(list.Count - 1);
                if (path == null) return;
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(path, list.ToArray(), new UTF8Encoding(true));
                if (_cboHistory != null)
                {
                    _cboHistory.Items.Clear();
                    for (int i = 0; i < list.Count; i++) _cboHistory.Items.Add(new HistoryEntry(list[i]));
                }
            }
            catch (Exception) { }
        }

        private void OnRunManual()
        {
            RunManualAsync(null);
        }

        /// <summary>执行手动命令行。automation 非空时不弹任何对话框（供 --smoke 无人值守用）。</summary>
        internal Task RunManualAsync(AutomationRun automation)
        {
            bool auto = automation != null;
            string args, sql;
            if (!LogParserRunner.SplitCommandLine(_txtManual.Text, out args, out sql))
            {
                UpdateManualPreview();
                if (!auto)
                    MessageBox.Show(this,
                        "没能从输入里找到 SELECT。\r\n\r\n可以只写 SQL（以 SELECT 开头），"
                        + "也可以整条粘贴：LogParser.exe -i:EVT -o:NAT \"SELECT ... FROM Security WHERE EventID = 4624\"",
                        "手动命令", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    SetStatus("手动命令解析失败：没找到 SELECT", false);
                if (auto)
                {
                    automation.OkCount = 0;
                    automation.FailCount = 1;
                    automation.HitCount = 0;
                    FlushOutput();
                    automation.OutputText = _rtbOutput.Text;
                    automation.Finished = true;
                }
                return Task.FromResult(0);
            }

            LogParserRunner.Options opt = BuildOptions();
            // 命令行里带的参数优先于"参数与超时"页里的设置
            if (args.Length > 0) opt.ExtraArguments = args;
            if (!_chkManualTime.Checked) { opt.TimeFrom = null; opt.TimeTo = null; }
            string fmt = LogParserRunner.EffectiveFormat(opt);
            opt.OutputFormat = fmt;

            QueryItem item = new QueryItem();
            item.Id = "manual";
            item.Label = "手动命令";
            item.LogName = "(手动输入)";
            item.ShowEventIds = false;
            item.Query = sql;

            SaveManualHistory(_txtManual.Text);

            List<QueryItem> one = new List<QueryItem>();
            one.Add(item);
            return RunAsyncCore(automation, one, opt, true);
        }

        internal void ShowManualTab()
        {
            if (_tabs == null || _pageManual == null) return;
            _tabs.SelectedTab = _pageManual;
        }

        internal string ManualText
        {
            get { return _txtManual == null ? "" : _txtManual.Text; }
            set { if (_txtManual != null) { _txtManual.Text = value; UpdateManualPreview(); } }
        }

        internal string ManualParsePreview
        {
            get { return _lblManualParse == null ? "" : _lblManualParse.Text; }
        }

        /// <summary>给冒烟测试用：手动命令页本身，用来量控件边界有没有溢出。</summary>
        internal Control ManualPage
        {
            get { return _pageManual; }
        }

        /// <summary>
        /// 执行当前勾选的检测项。automation 非空时为自动化模式（--smoke / 自跑）：
        /// 不弹任何对话框、不等待用户输入，方便无人值守验证整条链路。
        /// </summary>
        internal Task RunSelectionAsync(AutomationRun automation)
        {
            return RunAsyncCore(automation, null, null, false);
        }

        /// <summary>
        /// 真正的执行引擎：勾选批量（givenItems 为 null）与手动命令（givenItems 为 1 条）共用同一套
        /// 渲染、状态栏、错误处理，避免两份实现各写一遍还能跑偏。
        /// </summary>
        private async Task RunAsyncCore(AutomationRun automation, List<QueryItem> givenItems,
            LogParserRunner.Options givenOpt, bool manual)
        {
            bool auto = automation != null;
            if (_running) return;
            List<QueryItem> items = givenItems != null ? givenItems : GetSelectedItems();
            if (items.Count == 0)
            {
                if (!auto)
                    MessageBox.Show(this, "请先勾选至少一个检测项。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    SetStatus("未勾选任何检测项", false);
                return;
            }
            LogParserRunner.Options opt = givenOpt != null ? givenOpt : BuildOptions();
            if (!File.Exists(opt.LogParserPath))
            {
                if (!auto)
                    MessageBox.Show(this, "找不到 LogParser.exe：\r\n" + opt.LogParserPath
                        + "\r\n\r\n请把 LogParser.exe 放到程序同目录，或点击“浏览…”指定路径。",
                        "缺少 LogParser", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    SetStatus("找不到 LogParser.exe：" + opt.LogParserPath, false);
                return;
            }

            _running = true;
            _cts = new CancellationTokenSource();
            _btnStart.Enabled = false;
            _btnStart.Text = manual ? "执行中…" : "分析中…";
            if (_btnRunManual != null) _btnRunManual.Enabled = false;
            _btnStop.Enabled = true;
            _totalElements = 0;
            _buf.Length = 0;
            _scrollPending = false;
            _rtbOutput.Clear();
            // 注意：结果区**不能**在这里藏起来。以前为了省重绘把它 Visible=false，
            // 结果右栏直接变成一片白（用户反馈"开始分析时会白屏一会"），
            // 而且末项结果要等整轮跑完才露出来：看不到进度、也提前看不到已查出的证据。
            // 现在改成——边跑边写，但样式变化时不清选区：
            // 一是在**不持有焦点**的前提下写（焦点在按钮上，RichTextBox 不画选区）；
            // 二是每段新结果开头把插入点移到本行行首（SelectionStart = 行首、
            // 零长度，不产生反白），样式切换只内嵌在行首，不再从旧行末尾起刷。
            _rtbOutput.Visible = true;
            await Task.Yield();

            // 先把「正在准备」写在结果区，用户点完按钮立刻有反馈，不用盯着空白等。
            WriteLine("(正在执行…) 正在启动 LogParser，请稍候", Color.FromArgb(150, 158, 170), false);
            FlushOutput();
            _scrollPending = true;
            ScrollOutputToEnd();
            SetStatus("正在执行：准备中…", true);
            await Task.Yield();

            WriteLine("==========================================================", Color.FromArgb(120, 170, 240), true);
            WriteLine(" LogSentry " + (manual ? "手动命令执行" : "一把梭分析")
                + "   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Color.FromArgb(120, 170, 240), true);
            WriteLine(" 程序目录 : " + _exeDir, Color.FromArgb(150, 158, 170), false);
            WriteLine(" LogParser: " + opt.LogParserPath, Color.FromArgb(150, 158, 170), false);
            WriteLine(" 配置文件 : " + _configPath, Color.FromArgb(150, 158, 170), false);
            if (_config != null && _config.Errors.Count > 0)
            {
                WriteLine(" 配置告警 : " + _config.Errors.Count + " 处", Color.FromArgb(235, 170, 60), false);
                for (int i = 0; i < Math.Min(_config.Errors.Count, 20); i++)
                    WriteLine("   ! " + _config.Errors[i], Color.FromArgb(235, 170, 60), false);
            }
            if (manual)
            {
                WriteLine(" 模式     : 手动命令（命令行里的参数优先于“参数与超时”页）", Color.FromArgb(150, 158, 170), false);
                WriteLine(" 参数     : " + (string.IsNullOrEmpty(opt.ExtraArguments) ? "(默认)" : opt.ExtraArguments),
                    Color.FromArgb(150, 158, 170), false);
                WriteLine(" 输出格式 : " + LogParserRunner.EffectiveFormat(opt), Color.FromArgb(150, 158, 170), false);
            }
            else
            {
                WriteLine(" 检测项数 : " + items.Count, Color.FromArgb(150, 158, 170), false);
            }
            if (opt.TimeFrom.HasValue || opt.TimeTo.HasValue)
                WriteLine(" 时间范围 : " + LogParserRunner.BuildTimeCondition(opt.TimeFrom, opt.TimeTo),
                    Color.FromArgb(150, 158, 170), false);
            else
                WriteLine(" 时间范围 : 全部（未启用时间筛选）", Color.FromArgb(150, 158, 170), false);
            WriteLine("==========================================================", Color.FromArgb(120, 170, 240), true);
            WriteLine("", null, false);

            int okCount = 0, failCount = 0, hitCount = 0;
            DateTime startAll = DateTime.Now;
            string fmt = LogParserRunner.EffectiveFormat(opt);

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (_cts.IsCancellationRequested) break;
                    QueryItem item = items[i];
                    SetStatus("正在执行：" + item.DisplayLabel + "（" + (i + 1) + "/" + items.Count + "）…", true);

                    Stopwatch pRun = Stopwatch.StartNew();
                    QueryResult r = await RunOne(opt, item, _cts.Token);
                    pRun.Stop();
                    PerfRunOneMs += pRun.Elapsed.TotalMilliseconds;
                    FlushPending();
                    WriteLine("", null, false);
                    WriteLine("[*] " + (i + 1) + "/" + items.Count + "  " + item.DisplayLabel,
                        Color.FromArgb(120, 170, 240), true);

                    if (r.TimedOut && r.Error == "已取消")
                    {
                        WriteLine("[-] 已跳过（用户停止）", Color.FromArgb(200, 200, 90), false);
                        break;
                    }

                    if (r.Ok)
                    {
                        okCount++;
                        string body = r.Output;
                        long processed = LogParserRunner.ParseProcessedCount(r.RawOutput);
                        if (processed > 0) _totalElements += processed;

                        if (LogParserRunner.IsEmptyResult(r.Output))
                        {
                            WriteLine("    (无命中)    耗时：" + r.Elapsed.TotalSeconds.ToString("0.00") + "s",
                                Color.FromArgb(140, 148, 160), false);
                        }
                        else
                        {
                            Stopwatch pPrep = Stopwatch.StartNew();
                            int rows = CountDataRows(r.Output, fmt);
                            hitCount++;
                            // 只有 NAT 输出是靠一长串空格对齐列的；CSV/TSV/XML/W3C 压空格反而会动数据。
                            bool nat = LogParserRunner.IsNat(fmt);
                            string pretty = (_rawColumns || !nat) ? body : LogParserRunner.CollapsePadding(body);
                            PerfPrepMs += pPrep.Elapsed.TotalMilliseconds;

                            WriteLine("    命中 " + rows + " 行    耗时：" + r.Elapsed.TotalSeconds.ToString("0.00") + "s",
                                Color.FromArgb(120, 220, 150), true);
                            WriteLine(pretty, Color.FromArgb(210, 214, 220), false);
                        }
                    }
                    else
                    {
                        failCount++;
                        WriteLine("[!] 查询失败：" + item.Label, Color.FromArgb(255, 120, 120), true);
                        WriteLine("    原因：" + r.Error, Color.FromArgb(255, 150, 150), false);
                        WriteLine("    查询：" + item.Query, Color.FromArgb(150, 158, 170), false);
                    }
                    UpdateStatusCount();
                    // 一项写完就落地 + 滚到底：用户能边跑边看已经查出来的东西
                    // （尤其是 4625 与 4624 逐项对比的人，不用等整轮结束）。
                    FlushOutput();
                    ScrollOutputToEnd();
                }
            }
            finally
            {
                FlushPending();
                DateTime endAll = DateTime.Now;
                WriteLine("", null, false);
                WriteLine("==========================================================", Color.FromArgb(120, 170, 240), true);
                WriteLine(" 完成：成功 " + okCount + " 项 / 失败 " + failCount + " 项 / 有命中 " + hitCount + " 项",
                    failCount > 0 ? Color.FromArgb(255, 170, 90) : Color.FromArgb(120, 220, 150), true);

                WriteLine(" 总耗时：" + (endAll - startAll).TotalSeconds.ToString("0.00") + "s", Color.FromArgb(150, 158, 170), false);
                WriteLine("==========================================================", Color.FromArgb(120, 170, 240), true);

                _running = false;
                _btnStart.Enabled = true;
                _btnStart.Text = StartButtonText;
                if (_btnRunManual != null) _btnRunManual.Enabled = true;
                _btnStop.Enabled = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                SetStatus(failCount > 0
                    ? "完成（有 " + failCount + " 项失败）"
                    : "完成", false);
                UpdateStatusCount();

                _rtbOutput.Visible = true;
                _scrollPending = true;
                FlushOutput();
                ScrollOutputToEnd();
                if (auto)
                {
                    automation.OkCount = okCount;
                    automation.FailCount = failCount;
                    automation.HitCount = hitCount;
                    automation.OutputText = _rtbOutput.Text;
                    automation.Finished = true;
                }
            }
        }

        private async Task<QueryResult> RunOne(LogParserRunner.Options opt, QueryItem item, CancellationToken token)
        {
            LogParserRunner runner = new LogParserRunner();
            return await runner.RunAsync(opt, item, token);
        }

        /// <summary>保证分隔条距离始终落在合法区间，避免窗口缩小时抛 InvalidOperationException。</summary>
        private void ClampSplitter()
        {
            if (_split == null) return;
            try
            {
                int total = _split.Orientation == Orientation.Vertical ? _split.Width : _split.Height;
                int min = _split.Panel1MinSize;
                int max = total - _split.Panel2MinSize - _split.SplitterWidth;
                if (max < min) return;
                int d = _split.SplitterDistance;
                if (d < min) _split.SplitterDistance = min;
                else if (d > max) _split.SplitterDistance = max;
            }
            catch (Exception) { }
        }

        /// <summary>保留旧签名，默认按 NAT 处理。</summary>
        private static int CountDataRows(string body)
        {
            return CountDataRows(body, "NAT");
        }

        /// <summary>
        /// 统计命中的数据行数。各格式的"表头"不一样：
        /// NAT 第一行是列名 + 一行 --- 分列线；CSV/TSV 第一行是列名；
        /// W3C 前面有 #Software/#Version/#Date/#Fields 若干行；XML 每个数据行是 &lt;ROW&gt;。
        /// </summary>
        private static int CountDataRows(string body, string format)
        {
            if (string.IsNullOrEmpty(body)) return 0;
            string fmt = LogParserRunner.NormalizeFormat(format);
            string[] lines = body.Replace("\r\n", "\n").Split('\n');

            if (fmt == "XML")
            {
                int n = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("<ROW", StringComparison.OrdinalIgnoreCase) && t.EndsWith(">", StringComparison.Ordinal)) n++;
                }
                return n;
            }

            if (fmt == "W3C")
            {
                int n = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    n++;
                }
                return n;
            }

            int rows = -1; // 第一行是表头
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Trim().Length == 0) continue;
                if (ln.StartsWith("---") || ln.StartsWith("===")) continue;
                rows++;
            }
            return rows > 0 ? rows : 0;
        }

        private void UpdateStatusCount()
        {
            if (_totalElements >= 0)
                _tsslCount.Text = "已选 " + GetSelectedItems().Count + " 项 · 累计处理 " + _totalElements + " 条事件";
        }

        private void OnStop()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                SetStatus("正在停止…", true);
                _btnStop.Enabled = false;
            }
        }

        /// <summary>导出：点一下弹出三种格式，按勾选项重新查询一次拿到结构化数据。</summary>
        private void OnSaveResult()
        {
            FlushOutput();
            if (_rtbOutput.TextLength == 0)
            {
                MessageBox.Show(this, "结果区还没有内容。先点「开始分析」再导出。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("文本 (*.txt) —— 与界面显示完全一致", (Image)null, delegate { ExportText(); });
            m.Items.Add("CSV (*.csv) —— 每项一个文件，Excel 直接打开", (Image)null, delegate { ExportCsv(); });
            m.Items.Add("Excel (*.xlsx) —— 一个工作簿，每项一个工作表", (Image)null, delegate { ExportXlsx(); });
            m.Show(_btnSaveResult, new Point(0, _btnSaveResult.Height));
        }

        private void ExportText()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出分析结果（文本）";
                dlg.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dlg.FileName = "LogSentry_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                dlg.InitialDirectory = _exeDir;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        FlushOutput();
                        File.WriteAllText(dlg.FileName, _rtbOutput.Text, new UTF8Encoding(true));
                        SetStatus("已导出：" + dlg.FileName, false);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExportCsv()
        {
            ExportStructuredAsync(true);
        }

        private void ExportXlsx()
        {
            ExportStructuredAsync(false);
        }

        /// <summary>UI 入口：先问目录 / 文件名，再交给 Core 干活。</summary>
        private async void ExportStructuredAsync(bool asCsv)
        {
            if (_running)
            {
                MessageBox.Show(this, "正在执行，等这一轮跑完再导出。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (GetSelectedItems().Count == 0)
            {
                MessageBox.Show(this, "请先勾选检测项。导出会按当前勾选项重新查询一次。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string targetDir = "";
            string targetFile = "";
            if (asCsv)
            {
                using (FolderBrowserDialog fb = new FolderBrowserDialog())
                {
                    fb.Description = "选择导出 CSV 的目录（每个检测项一个文件）";
                    fb.SelectedPath = _exeDir;
                    if (fb.ShowDialog(this) != DialogResult.OK) return;
                    targetDir = fb.SelectedPath;
                }
            }
            else
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Title = "导出为 Excel";
                    dlg.Filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
                    dlg.FileName = "LogSentry_" + stamp + ".xlsx";
                    dlg.InitialDirectory = _exeDir;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    targetFile = dlg.FileName;
                }
            }

            await ExportStructuredCoreAsync(asCsv, targetDir, targetFile, false);
        }

        /// <summary>自动化入口（--smoke 用）：不弹任何对话框，结果写进 _exportSummary。</summary>
        internal async Task ExportForTestAsync(bool asCsv, string target)
        {
            await ExportStructuredCoreAsync(asCsv, asCsv ? target : "", asCsv ? "" : target, true);
        }

        /// <summary>最近一次导出的结果摘要，供冒烟脚本读取。</summary>
        internal string ExportSummary = "";

        /// <summary>
        /// 导出 CSV / XLSX。为什么要重查一次：NAT 文本是按列宽用空格对齐的，
        /// 中文值会把列宽算少，按固定列位切必然切坏路径；直接让 LogParser 输出 CSV 才有准确的分列。
        /// </summary>
        private async Task ExportStructuredCoreAsync(bool asCsv, string targetDir, string targetFile, bool quiet)
        {
            ExportSummary = "";

            List<QueryItem> items = GetSelectedItems();
            if (items.Count == 0)
            {
                SetStatus("未勾选任何检测项", false);
                if (quiet) { ExportSummary = "未勾选任何检测项"; return; }
                MessageBox.Show(this, "请先勾选检测项。导出会按当前勾选项重新查询一次。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            LogParserRunner.Options opt = BuildOptions();
            if (!File.Exists(opt.LogParserPath))
            {
                SetStatus("找不到 LogParser.exe：" + opt.LogParserPath, false);
                if (quiet) { ExportSummary = "找不到 LogParser.exe"; return; }
                MessageBox.Show(this, "找不到 LogParser.exe：\r\n" + opt.LogParserPath, "缺少 LogParser",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            opt.OutputFormat = "CSV"; // 导出走 CSV，列结构最准
            // -o:CSV 不认 -rtp（报 invalid parameter "rtp"），换成导出用的参数集
            opt.ExtraArguments = LogParserRunner.BuildExportExtraArguments(opt.ExtraArguments, false);

            _running = true;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _btnStart.Enabled = false;
            _btnSaveResult.Enabled = false;
            _btnStop.Enabled = true;

            DateTime? tf, tt;
            ResolveTimeRange(out tf, out tt);
            string cond = LogParserRunner.BuildTimeCondition(tf, tt);

            List<ExportSheet> sheets = new List<ExportSheet>();
            List<string> written = new List<string>();
            int ok = 0, fail = 0;

            try
            {
                ExportSheet ov = new ExportSheet();
                ov.Name = "概述";
                ov.Rows.Add(new string[] { "项目", "值" });
                ov.Rows.Add(new string[] { "导出时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
                ov.Rows.Add(new string[] { "LogParser", opt.LogParserPath });
                ov.Rows.Add(new string[] { "配置文件", _configPath });
                ov.Rows.Add(new string[] { "时间范围", cond.Length == 0 ? "全部（未启用时间筛选）" : cond });
                ov.Rows.Add(new string[] { "附加参数", (opt.ExtraArguments ?? "").Trim() });
                ov.Rows.Add(new string[] { "导出格式", asCsv ? "CSV（每项一个文件）" : "XLSX（每项一个工作表）" });
                ov.Rows.Add(new string[] { "说明", "导出时按勾选项用 -o:CSV 重新查询，列结构以 LogParser 的 CSV 输出为准" });
                sheets.Add(ov);

                for (int i = 0; i < items.Count; i++)
                {
                    QueryItem it = items[i];
                    SetStatus("正在导出 " + (i + 1) + "/" + items.Count + "：" + it.Label + "…", true);

                    LogParserRunner runner = new LogParserRunner();
                    QueryResult r = await runner.RunAsync(opt, it, token);
                    if (token.IsCancellationRequested) break;

                    ExportSheet sh = new ExportSheet();
                    sh.Name = it.Label;

                    if (!r.Ok)
                    {
                        fail++;
                        sh.Rows.Add(new string[] { "[!] 查询失败", r.Error });
                    }
                    else
                    {
                        List<string[]> rows = Exporters.RowsFromLogParserCsv(r.RawOutput);
                        if (rows.Count == 0)
                        {
                            sh.Rows.Add(new string[] { "(该检测项在当前日志中没有匹配记录)" });
                        }
                        else
                        {
                            for (int k = 0; k < rows.Count; k++) sh.Rows.Add(rows[k]);
                        }
                        ok++;
                    }
                    sheets.Add(sh);
                    Application.DoEvents();
                }

                if (asCsv)
                {
                    // 目标目录不存在就先建出来（--smoke / 命令行调用时不会有人替你建）
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);
                    for (int i = 0; i < sheets.Count; i++)
                    {
                        ExportSheet sh = sheets[i];
                        // 空表不落盘，但"查询失败""没有匹配记录"这类单行结果要留着，
                        // 否则导出的目录里会悄悄少几项，看不出哪一项没跑成功。
                        if (sh.Rows.Count == 0) continue;
                        string name = SafeFileName((i).ToString("00") + "_" + sh.Name);
                        string path = Path.Combine(targetDir, name + ".csv");
                        Exporters.WriteCsv(path, sh);
                        written.Add(path);
                    }
                    if (written.Count == 0) throw new InvalidOperationException("没有可导出的数据行。");
                }
                else
                {
                    Exporters.WriteXlsx(targetFile, sheets);
                    written.Add(targetFile);
                }

                SetStatus("导出完成（成功 " + ok + " 项 / 失败 " + fail + " 项）", false);

                StringBuilder msg = new StringBuilder();
                msg.Append("导出完成：成功 ").Append(ok).Append(" 项，失败 ").Append(fail).Append(" 项。\r\n\r\n");
                for (int i = 0; i < Math.Min(written.Count, 6); i++) msg.Append(written[i]).Append("\r\n");
                if (written.Count > 6) msg.Append("… 共 ").Append(written.Count).Append(" 个文件\r\n");

                ExportSummary = "ok=" + ok + " fail=" + fail + " files=" + written.Count;
                if (!quiet && MessageBox.Show(this, msg.ToString() + "\r\n打开所在目录？", "导出成功",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start("explorer.exe", asCsv
                            ? "\"" + targetDir + "\""
                            : "/select,\"" + targetFile + "\"");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                SetStatus("导出失败：" + ex.Message, false);
                ExportSummary = "失败：" + ex.Message;
                if (!quiet)
                    MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _running = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                _btnStart.Enabled = true;
                _btnSaveResult.Enabled = true;
                _btnStop.Enabled = false;
            }
        }

        private static string SafeFileName(string raw)
        {
            string s = raw ?? "sheet";
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++) s = s.Replace(bad[i], '_');
            s = s.Replace(' ', '_');
            if (s.Length > 60) s = s.Substring(0, 60);
            return s;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 手动命令页的 Ctrl+Enter 由命令框自己处理，这里只管全局 F5
            if (keyData == Keys.F5 && _btnStart.Enabled) { OnStart(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
}
}