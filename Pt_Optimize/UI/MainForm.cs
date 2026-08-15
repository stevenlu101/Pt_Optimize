using System.Text;
using System.Text.Json;
using PtOptimize.Core;

namespace PtOptimize.UI;

public sealed class MainForm : Form
{
    private readonly PropertyGrid _grid = new();
    private readonly RichTextBox _out = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ScottPlot.WinForms.FormsPlot _pAxial = FieldPlots.NewPlot();
    private readonly DataGridView _segGrid = new();
    private readonly BindingSource _segBind = new();
    private readonly List<Segment> _segs = new()
    {
        new Segment { Name = "HC1", TSetC = 1150, TGlassInC = 1150, GlassHeadM = 0.3, LengthMm = 300 },
        new Segment { Name = "HC2", TSetC = 1080, TGlassInC = 1140, GlassHeadM = 0.6, LengthMm = 300 },
        new Segment { Name = "HC3", TSetC = 1050, TGlassInC = 1130, GlassHeadM = 1.0, LengthMm = 300 },
    };
    private readonly RichTextBox _segOut = new();
    private DesignInputs _in = new();
    private SolveResult? _res;

    // 法兰定尺是分钟级的耦合解，必须给进度且可取消
    private readonly ToolStripProgressBar _segProg = new() { Visible = false, Maximum = 1000 };
    private readonly ToolStripLabel _segStatus = new("") { Visible = false };
    private ToolStripButton? _flangeBtn;
    private CancellationTokenSource? _flangeCts;

    public MainForm()
    {
        Text = "Pt_Optimize — 铂金直接加热 整线设计与用量优化";
        Width = 1400; Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(1, 1) };
        tool.Items.Add(Btn("计算 (F5)", (_, _) => Run()));
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(Btn("扫描：保温厚度", (_, _) => Sweep("insul", 0, 50, 11, "内层保温厚度 [mm]")));
        tool.Items.Add(Btn("扫描：法兰厚度", (_, _) => Sweep("flangeTf", 0.4, 5, 11, "法兰厚度 tf [mm]")));
        tool.Items.Add(Btn("扫描：铂发射率", (_, _) => Sweep("eps", 0.10, 0.30, 9, "铂表面发射率 ε")));
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(Btn("保存", (_, _) => Save()));
        tool.Items.Add(Btn("读取", (_, _) => LoadCase()));
        tool.Items.Add(Btn("导出 CSV", (_, _) => ExportCsv()));

        _grid.SelectedObject = _in;
        _grid.PropertySort = PropertySort.Categorized;
        _grid.HelpVisible = true;
        _grid.Dock = DockStyle.Fill;

        _out.Dock = DockStyle.Fill;
        _out.Font = new Font("Consolas", 9.5f);
        _out.ReadOnly = true;
        _out.WordWrap = false;
        _out.BackColor = Color.FromArgb(252, 252, 250);

        // ★ 2026-08-12：删掉「温度场/电流密度场/体积发热场」（走 FieldMap 的子午面图，
        //   其法兰部分是已作废的一维环形模型）与「法兰温度剖面/厚度·自给率」（同源）。
        //   法兰的真实二维场改看「整线设计」页，那里直接画壳解的 T/J。
        foreach (var (title, ctrl) in new (string, Control)[]
        {
            ("轴向剖面", _pAxial),
        })
        {
            var page = new TabPage(title) { Padding = new Padding(2) };
            page.Controls.Add(ctrl);
            _tabs.TabPages.Add(page);
        }

        // ── 分段输入页：每段独立的温度、水头、牌号、几何
        _segGrid.Dock = DockStyle.Fill;
        _segGrid.AutoGenerateColumns = true;
        _segGrid.AllowUserToAddRows = true;
        _segGrid.AllowUserToDeleteRows = true;
        _segGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _segBind.DataSource = _segs;
        _segGrid.DataSource = _segBind;
        _segGrid.DataError += (_, e) => e.ThrowException = false;

        _segOut.Dock = DockStyle.Fill;
        _segOut.Font = new Font("Consolas", 9.5f);
        _segOut.ReadOnly = true; _segOut.WordWrap = false;
        _segOut.BackColor = Color.FromArgb(252, 252, 250);

        var segTool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        segTool.Items.Add(Btn("核算全线", (_, _) => RunLine()));
        segTool.Items.Add(Btn("为各段选最省牌号", (_, _) => AutoGrade()));
        segTool.Items.Add(Btn("按强度取最小壁厚", (_, _) => MinWalls()));
        segTool.Items.Add(new ToolStripSeparator());
        _flangeBtn = Btn("核算法兰（分钟级）", (_, _) => _ = RunFlangesAsync());
        segTool.Items.Add(_flangeBtn);
        _segProg.Size = new Size(180, 16);
        segTool.Items.Add(_segProg);
        segTool.Items.Add(_segStatus);

        var segSplit = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        segSplit.Panel1.Controls.Add(_segGrid);
        segSplit.Panel2.Controls.Add(_segOut);
        var segPage = new TabPage("分段核算") { Padding = new Padding(2) };
        segPage.Controls.Add(segSplit);
        segPage.Controls.Add(segTool);
        _tabs.TabPages.Insert(0, segPage);

        // ★ 整线设计页：工程师的主工作面，放在最前
        _tabs.TabPages.Insert(0, new AnalysisPage(_in));
        _tabs.TabPages.Insert(0, new LineDesignPage(_in));
        _tabs.TabPages.Add(new ManualPage());   // 使用说明（图文，按定案档实时生成）

        var right = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        right.Panel1.Controls.Add(_out);
        right.Panel2.Controls.Add(_tabs);

        var main = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(_grid);
        main.Panel2.Controls.Add(right);

        Controls.Add(main);
        Controls.Add(tool);

        Load += (_, _) =>
        {
            main.SplitterDistance = 430;
            right.SplitterDistance = (int)(right.Height * 0.56);
            Run();
            RunLine();
        };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) Run();
            // F1 = 帮助：跳到「使用说明」页（图文，按定案档实时生成）
            else if (e.KeyCode == Keys.F1) ShowHelp();
        };
    }

    /// <summary>F1／「帮助」：切到使用说明页。说明书不做成单独的导出命令，
    /// 就放在程序里 —— 图是按当前定案档实时画的，导出来的静态副本会和定案值漂开。</summary>
    private void ShowHelp()
    {
        foreach (TabPage t in _tabs.TabPages)
            if (t is ManualPage) { _tabs.SelectedTab = t; return; }
    }

    private void RunLine()
    {
        _segBind.EndEdit();
        var rs = LineSolver.Solve(_segs, _in);
        var (mass, cost, bad) = LineSolver.Totals(rs);
        var sb = new StringBuilder();
        sb.AppendLine($"材料数据：用户实测工作簿   寿命 {_in.DesignLifeHours:0} h   安全系数 {_in.SafetyFactor:0.0}");
        sb.AppendLine($"金属价格比：Rh/Pt = 4.91（Umicore PMM 2026-08-06，Pt $1731/oz、Rh $8500/oz）");
        sb.AppendLine();
        sb.AppendLine($"{"段",6}{"温度",7}{"水头",7}{"牌号",20}{"壁厚",8}{"强度最小",9}" +
                      $"{"σ_vm",8}{"许用",8}{"利用率",8}{"铂重",9}{"相对成本",9}  判定");
        sb.AppendLine($"{"",6}{"°C",7}{"m",7}{"",20}{"mm",8}{"mm",9}{"MPa",8}{"MPa",8}{"",8}{"g",9}{"",9}");
        sb.AppendLine(new string('-', 108));
        foreach (var r in rs)
            sb.AppendLine($"{r.Seg.Name,6}{r.Seg.TSetC,7:0}{r.Seg.GlassHeadM,7:0.0}{r.Seg.GradeName,20}" +
                $"{r.Seg.WallMm,8:0.000}{(double.IsNaN(r.MinWallStrengthMm) ? "不可行" : r.MinWallStrengthMm.ToString("0.000")),9}" +
                $"{r.VonMisesMPa,8:0.000}{r.AllowMPa,8:0.000}{r.Utilization,8:0.00}" +
                $"{r.MassG,9:0}{r.CostRelative,9:0}  {(r.Feasible ? "✓" : "✗ " + r.Binding)}");
        sb.AppendLine();
        sb.AppendLine($"合计铂重 {mass:0} g    相对成本 {cost:0}（= Σ 质量×牌号成本倍数，纯铂同质量为基准）");
        if (bad > 0) sb.AppendLine($"★ {bad} 段强度超限 —— 加厚或换牌号");
        sb.AppendLine();
        sb.AppendLine($"★ 以上只是**管壁**。整线还有 {LineSolver.FlangeCount(_segs.Count)} 片法兰"
                    + $"（{_segs.Count} 段 → n+1 片，段间共用），厚度由电流密度定，");
        sb.AppendLine("  不随管壁减薄同比例变薄 —— 点「核算法兰」把它算进来，那才是可交付的总铂。");
        sb.AppendLine();
        sb.AppendLine("注：本页强度与质量核算是解析的（即时）。电流密度与析晶需耦合热解，");
        sb.AppendLine("    请用 --cli --matrix / --save 等批处理模式。");
        _segOut.Text = sb.ToString();
    }

    /// <summary>
    /// 法兰定尺：每段一次耦合解，分钟级。放后台线程 + 进度 + 可取消，
    /// 否则界面在整个过程里假死（Windows 会显示「无响应」，用户以为崩了）。
    /// </summary>
    private async Task RunFlangesAsync()
    {
        if (_flangeCts is not null) { _flangeCts.Cancel(); return; }   // 再点一次 = 取消

        _segBind.EndEdit();
        var segs = _segs.Select(s => new Segment
        {
            Name = s.Name, TSetC = s.TSetC, TGlassInC = s.TGlassInC,
            GlassHeadM = s.GlassHeadM, LengthMm = s.LengthMm,
            TubeIdMm = s.TubeIdMm, WallMm = s.WallMm,
            GradeName = s.GradeName, TLiquidusC = s.TLiquidusC
        }).ToList();
        if (segs.Count == 0) return;

        double tubeG = LineSolver.Totals(LineSolver.Solve(segs, _in)).massG;
        var plate = new FlangePlate();

        _flangeCts = new CancellationTokenSource();
        var ct = _flangeCts.Token;
        _segProg.Visible = _segStatus.Visible = true;
        _segProg.Value = 0;
        _segStatus.Text = "启动…";
        if (_flangeBtn is not null) _flangeBtn.Text = "取消";

        // Progress<T> 在构造处捕获 UI 同步上下文，回调自动回到 UI 线程
        var prog = new Progress<LineSolver.FlangeProgress>(fp =>
        {
            _segProg.Value = Math.Clamp((int)(fp.Fraction * 1000), 0, _segProg.Maximum);
            _segStatus.Text = fp.ToString();
        });

        try
        {
            var fs = await Task.Run(
                () => LineSolver.SizeFlanges(segs, _in, plate, prog, ct), ct);

            var sb = new StringBuilder(_segOut.Text);
            sb.AppendLine();
            sb.AppendLine($"=== 法兰（{segs.Count} 段 → {fs.Count} 片，段间共用）===");
            sb.AppendLine($"{"接头",14}{"共用",6}{"电流 A",10}{"厚度 mm",10}{"铂重 g",10}  定尺依据");
            sb.AppendLine(new string('-', 66));
            foreach (var f in fs)
                sb.AppendLine($"{f.Joint,14}{(f.Shared ? "是" : "—"),6}{f.CurrentA,10:0}" +
                              $"{f.ThicknessMm,10:0.000}{f.MassG,10:0}  {f.SizedBy}");
            double fg = fs.Sum(x => x.MassG);
            sb.AppendLine();
            sb.AppendLine($"法兰合计 {fg:0} g   管 {tubeG:0} g   全线 {tubeG + fg:0} g   " +
                          $"法兰占 {fg / (tubeG + fg) * 100:0}%");
            sb.AppendLine("共用片电流 = (I_左+I_右)/2 × 1.5（《鉑金電氣計算.xlsx》口径）；厚度 t ∝ I。");
            _segOut.Text = sb.ToString();
            _segStatus.Text = "完成";
        }
        catch (OperationCanceledException) { _segStatus.Text = "已取消"; }
        catch (Exception ex)
        {
            _segStatus.Text = "失败";
            MessageBox.Show(this, ex.Message, "法兰定尺失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _flangeCts.Dispose();
            _flangeCts = null;
            _segProg.Visible = false;
            if (_flangeBtn is not null) _flangeBtn.Text = "核算法兰（分钟级）";
        }
    }

    private void AutoGrade()
    {
        _segBind.EndEdit();
        var cands = new[] { "Pt", "Pt-Rh/90-10", "Tanaka-ZGS-Pt", "FKS16/Pt",
                            "Tanaka-ZGS-PtRh10", "FKS16/PtRh-9010" };
        foreach (var s in _segs)
        {
            var b = LineSolver.BestGrade(s, _in, cands);
            if (!string.IsNullOrEmpty(b)) s.GradeName = b;
        }
        _segBind.ResetBindings(false);
        RunLine();
    }

    private void MinWalls()
    {
        _segBind.EndEdit();
        var rs = LineSolver.Solve(_segs, _in);
        for (int i = 0; i < _segs.Count && i < rs.Count; i++)
            if (!double.IsNaN(rs[i].MinWallStrengthMm))
                _segs[i].WallMm = Math.Round(Math.Max(rs[i].MinWallStrengthMm, 0.3), 3);
        _segBind.ResetBindings(false);
        RunLine();
    }

    private static ToolStripButton Btn(string text, EventHandler h)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += h;
        return b;
    }

    private void Run()
    {
        _res = SegmentSolver.Solve(_in);
        _out.Text = _res.Ok ? Report(_in, _res) : "求解失败: " + _res.Message;
        if (!_res.Ok) return;

        // 视野取 5 倍热衰减长度，覆盖法兰冷效应的全部影响范围
        double xView = Math.Min(_in.TubeLengthMm * 0.5, Math.Max(30.0, 6.0 * _res.DecayLengthMm));
        FieldPlots.DrawAxialProfile(_pAxial, _res, _in);
    }

    private static string Report(DesignInputs p, SolveResult r)
    {
        var s = new StringBuilder();
        void H(string t) { s.AppendLine(); s.AppendLine("── " + t + " " + new string('─', Math.Max(2, 58 - t.Length * 2))); }
        void L(string k, string v) => s.AppendLine($"  {k,-26}{v}");

        H("铂用量  ← 目标函数");
        L("设计壁厚", $"{r.WallDesignMm:0.000} mm" +
            (r.WallLimitedByMinimum ? $"   ← 受最小壁厚限制 (电学仅需 {r.WallElecMm:0.000})"
                                    : "   ← 受电流密度限制"));
        L("供料管铂重", $"{r.MassTubeKg:0.000} kg   ({r.MassTubeKg / p.TubeLength:0.000} kg/m)");
        L("合计", $"{r.MassTotalKg:0.000} kg");
        L("比功率", $"{r.PowerDensityWPerKg:0} W/kg   (m = P / 该值)");

        H("热平衡");
        L("单位长度热损失", $"{r.LossPerMeterWPerM / 1000:0.00} kW/m");
        L("段总功率", $"{r.PowerTotalW / 1000:0.00} kW");
        L("保温外径 / 外表温度", $"Ø{r.InsulationOuterDiaMm:0.0} mm  /  {r.OuterSurfaceTempC:0} °C");
        L("热衰减长度 ℓt", $"{r.DecayLengthMm:0.0} mm");
        L("时间常数 金属/含玻璃", $"{r.TauMetalS:0} s  /  {r.TauWithGlassS / 60:0.0} min");

        H("电气");
        L("电流 I", $"{r.CurrentA:0} A");
        L("段电压 U", $"{r.VoltageV:0.00} V");
        L("段电阻 R", $"{r.ResistanceOhm * 1000:0.000} mΩ");
        L("实际电流密度 J", $"{r.JActualAPerMm2:0.00} A/mm²   (许用 {p.JAllowAPerMm2:0.0}，余量 {r.JMarginPct:0.0} %)");
        L("TCR @T_set", $"{r.TcrPerK * 1e4:0.00}e-4 /K");
        L("热稳定裕度", $"{r.StabilityRatio:0.0} ×   " + (r.StabilityRatio > 3 ? "稳定" : "⚠ 裕度不足"));

        H("温度分布 / 析晶");
        L("中段 (控温点)", $"{p.TSetC:0.0} °C");
        L("最高 / 最低", $"{r.TMaxC:0.0} / {r.TMinC:0.0} °C");
        L("法兰 A / B 端", $"{r.TFlangeAC:0.0} / {r.TFlangeBC:0.0} °C");
        L("玻璃出口", $"{r.TGlassOutC:0.0} °C");
        L("最小析晶裕度", $"{r.DevitMarginMinK:+0.0;-0.0} K   (要求 ≥ {p.DevitMarginK:0} K)");
        s.AppendLine(r.DevitRisk
            ? "  ★ 析晶风险：最冷点低于 T_liq + 裕度，位置在法兰"
            : "  ✓ 全程高于析晶安全线");

        // ★ 2026-08-12：原「法兰自给率 Φ」一段走的是已作废的一维环形模型（§5），
        //   连同 FlangeRadial 一并删除。法兰的 Φ / 抽热 / 局部最高温请看「整线设计」页，
        //   那里是二维壳解的真值。

        H("流动");
        L("流速 / 停留时间", $"{r.VelocityMmPerS:0.0} mm/s  /  {r.ResidenceS:0} s");
        L("压降", $"{r.PressureDropKPa:0.0} kPa  = {r.GlassHeadM:0.00} m 玻璃柱");

        if (r.ILeakA > 1e-9)
        {
            H("⚠ 直流分量电解（法拉第上限估计）");
            L("玻璃柱电阻", $"{r.RGlassOhm:0.0} Ω");
            L("漏电流(直流)", $"{r.ILeakA * 1000:0.00} mA");
            L("铂阳极溶解", $"{r.PtDissolvedGPerH:0.000} g/h  = {r.PtDissolvedKgPerYear:0.000} kg/年");
            L("Na⁺ 迁移", $"{r.NaFluxGPerH:0.000} g/h  → 阳极区贫碱，T_liq 局部升高");
            L("阳极析氧", $"{r.O2NlPerH:0.000} NL/h");
            s.AppendLine("  注：忽略界面极化阻抗，为上限。交流下应为 0。");
        }
        else if (p.Supply != SupplyMode.Dc)
        {
            H("电解");
            s.AppendLine("  交流供电，无净法拉第迁移。");
            s.AppendLine("  建议用钳表直流档实测二次电流直流分量，填入「直流偏置」核算。");
        }

        return s.ToString();
    }

    private void Sweep(string what, double from, double to, int steps, string label)
    {
        var rows = SegmentSolver.Sweep(_in, what, from, to, steps);
        var s = new StringBuilder();
        s.AppendLine($"扫描：{label}");
        s.AppendLine();
        s.AppendLine($"{label,-22}{"损失kW/m",10}{"壁厚mm",10}{"总铂kg",10}{"最冷°C",10}{"裕度K",9}{"Φ",8}{"I(A)",9}");
        s.AppendLine(new string('─', 88));
        double? baseMass = null;
        foreach (var x in rows)
        {
            baseMass ??= x.MassKg;
            s.AppendLine($"{x.Value,-22:0.00}{x.LossPerM / 1000,10:0.00}{x.WallMm,10:0.000}" +
                         $"{x.MassKg,10:0.000}{x.TMin,10:0.0}{x.Margin,9:0.0}{x.Phi,8:0.00}{x.IA,9:0}");
        }
        if (rows.Count > 1)
        {
            double m0 = rows[0].MassKg, m1 = rows[^1].MassKg;
            s.AppendLine();
            s.AppendLine($"铂用量: {m0:0.000} → {m1:0.000} kg   ({(m1 - m0) / m0 * 100:+0.0;-0.0} %)");
        }
        _out.Text = s.ToString();
    }

    private void Save()
    {
        using var d = new SaveFileDialog { Filter = "Pt_Optimize 方案 (*.json)|*.json", FileName = "case.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(d.FileName,
            JsonSerializer.Serialize(_in, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadCase()
    {
        using var d = new OpenFileDialog { Filter = "Pt_Optimize 方案 (*.json)|*.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        var x = JsonSerializer.Deserialize<DesignInputs>(File.ReadAllText(d.FileName));
        if (x is null) return;
        _in = x; _grid.SelectedObject = _in; Run();
    }

    private void ExportCsv()
    {
        if (_res is null || !_res.Ok) return;
        using var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "profile.csv" };
        if (d.ShowDialog() != DialogResult.OK) return;
        var s = new StringBuilder("x_mm,T_metal_C,T_glass_C\n");
        for (int i = 0; i < _res.X.Length; i++)
            s.AppendLine($"{_res.X[i]:0.###},{_res.TMetal[i]:0.###},{_res.TGlass[i]:0.###}");
        File.WriteAllText(d.FileName, s.ToString(), Encoding.UTF8);
    }
}
