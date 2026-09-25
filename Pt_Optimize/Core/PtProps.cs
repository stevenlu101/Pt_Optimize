using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ R48 物性接线（2026-09-23，Opus 5.5）：**电、热物性（电阻率 ρ、dρ/dT、电阻温度系数、热导率 k、比热 cp）按牌号的唯一取值口。**
///
/// 由来：R48 把三份铂工作簿做成了按牌号的温度函数（<see cref="MaterialDb"/>／<see cref="PtResistivityData"/>），
/// 但求解链（段解、壳体电流与温度场、板件二维电流与温度场、升温两节点与准静态、热稳定）原来一律直读 <see cref="Materials"/> 的纯铂那几支 ——
/// 参数表选了别的牌号，只有持久强度与热膨胀跟着变。本类把那几处全部改成经这里取（清单与例外见 HANDOVER 本轮条目，门见下）。
///
/// 规则（逐条写死）：
///   ① **纯铂一支逐字调原函数**：牌号为 <see cref="PureGradeName"/>（默认）时 <see cref="Rho"/>／<see cref="Tcr"/>／<see cref="K"/>／<see cref="Cp"/>
///      调的就是 <see cref="Materials.PtResistivity"/>／<see cref="Materials.PtTcr"/>／<see cref="Materials.PtThermalK"/>／<see cref="Materials.PtCp"/>，
///      <see cref="DRhoDT"/> 与求解链原来内联的那一式逐字相同（同一组常量、同一运算次序）⇒ 纯铂的数**逐位不动**。
///   ② **按牌号的那一支**：只有 <see cref="MaterialDb.DataCompleteness"/> 的电阻率与热导率／比热两类都算「自有」（含同名义成分借用）的牌号才走
///      （<see cref="PtGrade.ResistivityOhmM"/>、<see cref="PtGrade.ThermalKWPerMK"/>、<see cref="PtGrade.CpJPerKgK"/>）。
///      今天的材料库里是 Pt-Rh/90-10、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10、Umicore-PtRh10（后者只因持久强度区间未确认而不齐全，电、热数据是有的）。
///   ③ **数据不全就五项一起退回纯铂，不混用**（FKS16 两个、Pt-Rh/80-20、Umicore-PtRh20、Pd、Ni、Cu）；<see cref="Note"/> 写明缺什么。
///      推算的参考热导率（材料库里只作参考、不入判定的那一条）本类**不读**。材料库里没有的名字照样由 <see cref="MaterialDb.Get"/> 抛明确的异常。
///   ④ **密度、熔点不在本类**：密度仍用 <see cref="Materials.PtDensity"/>（材料库里的合金密度没有出处）⇒ 热容 = 纯铂密度 × 所选牌号比热；
///      熔点、电阻率拟合区护栏仍按纯铂值。
///
/// 门：R48PropsWiringGateTests（纯铂逐位、访问口逐位、选牌号真的进链、数据不全一起退回、源码门、整线只有一个牌号来源）；
///     R48MGradeNoteTruthTests（参数表说明 ↔ 源码）；R48ThermalSourceTests（比热只进升温与时间常数）。
/// </summary>
public sealed class PtProps
{
    public const string PureGradeName = "Pt";

    /// <summary>纯铂一支（走 <see cref="Materials"/> 原函数）。</summary>
    public static readonly PtProps Pure = new(MaterialDb.Get(PureGradeName), PureGradeName, PureGradeName, pure: true, fallback: false, note: "");

    private static readonly ConcurrentDictionary<string, PtProps> Cache = new(StringComparer.Ordinal);

    private readonly PtGrade _g;
    private readonly bool _pure;

    private PtProps(PtGrade g, string requested, string dataGrade, bool pure, bool fallback, string note)
    {
        _g = g; _pure = pure;
        Requested = requested; DataGrade = dataGrade; IsFallback = fallback; Note = note;
    }

    /// <summary>工程师选的牌号。</summary>
    public string Requested { get; }
    /// <summary>电、热物性实际取自材料库哪个牌号的那一条（纯铂一支与退回的 = "Pt"；借用明细写在 <see cref="Note"/>）。</summary>
    public string DataGrade { get; }
    /// <summary>true ⇒ 走 <see cref="Materials"/> 的纯铂原函数（选的就是纯铂，或数据不全退回）。</summary>
    public bool IsPure => _pure;
    /// <summary>选的不是纯铂，但电阻率或热导率／比热数据不全 ⇒ 五项一起按纯铂。</summary>
    public bool IsFallback { get; }
    /// <summary>纯铂 = ""；按牌号 = 取了谁、借了谁、数据点区间；退回 = 退回一句（含缺项）。</summary>
    public string Note { get; }

    /// <summary>电阻率 Ω·m。</summary>
    public double Rho(double tC) => _pure ? Materials.PtResistivity(tC) : _g.ResistivityOhmM(tC);

    /// <summary>dρ/dT，Ω·m/K。纯铂一支与求解链原来内联的式子逐字相同。</summary>
    public double DRhoDT(double tC) => _pure ? Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * tC)
                                             : _g.Rho0MicroOhmCm * 1e-8 * (_g.Alpha + 2 * _g.Beta * (tC - _g.T0C));

    /// <summary>电阻温度系数 (1/ρ)(dρ/dT)，1/K。</summary>
    public double Tcr(double tC) => _pure ? Materials.PtTcr(tC) : DRhoDT(tC) / Rho(tC);

    /// <summary>热导率 W/(m·K)。</summary>
    public double K(double tC) => _pure ? Materials.PtThermalK(tC) : _g.ThermalKWPerMK(tC);

    /// <summary>比热 J/(kg·K)。</summary>
    public double Cp(double tC) => _pure ? Materials.PtCp(tC) : _g.CpJPerKgK(tC);

    /// <summary>
    /// 本算例温度区间 [<paramref name="tLoC"/>, <paramref name="tHiC"/>] 超出所选牌号数据点之处，逐条成句（以「；」分隔）；
    /// 纯铂一支与退回的 = ""（纯铂那几支的覆盖区间写在 <see cref="Materials"/> 各函数的说明里，本轮不改那一支的报告）。
    /// 电阻率按 <see cref="PtResistivityData.Read"/> 的覆盖类别（外推／无数据）引它自己的说明；
    /// 另外，区间与电阻率测试值行（<see cref="PtResistivityData.Row"/>，借用的按被借那一行）标的疑点段（<c>Suspect</c>）有重叠 ⇒ 逐段写出
    /// （例：Pt-Rh/90-10 那一行 100–600 °C 疑为插补）；热导率、比热按曲线的数据点区间。
    /// </summary>
    public string RangeNote(double tLoC, double tHiC)
    {
        if (_pure || double.IsNaN(tLoC) || double.IsNaN(tHiC)) return "";
        var parts = new List<string>();
        foreach (double t in tLoC == tHiC ? new[] { tLoC } : new[] { tLoC, tHiC })
        {
            var rv = PtResistivityData.Read(Requested, t);
            if (rv.Coverage is ResistivityCoverage.Extrapolated or ResistivityCoverage.NoData)
                parts.Add($"电阻率在 {t:0} °C：{rv.Note}");
        }
        string rhoFrom = string.IsNullOrEmpty(_g.ResistivityFrom) ? _g.Name : _g.ResistivityFrom;
        foreach (var sp in PtResistivityData.Row(rhoFrom).Suspect)
            if (sp.FromC <= tHiC && sp.ToC >= tLoC)
                parts.Add($"电阻率测试值（{rhoFrom} 行）在 " + (sp.FromC == sp.ToC ? $"{sp.FromC:0} °C" : $"{sp.FromC:0}–{sp.ToC:0} °C")
                          + $" 有疑点，与本算例 {tLoC:0}–{tHiC:0} °C 重叠：{sp.Why}");
        foreach (var c in new[] { _g.ThermalKTable, _g.CpTable })
        {
            if (c is null) continue;
            if (!c.Covers(tLoC) && tLoC < c.TMinC)
                parts.Add($"{c.Quantity}：本算例最低 {tLoC:0} °C 低于数据点下限 {c.TMinC:0} °C ⇒ "
                          + (c.FromTable ? $"那一段取端点值 {c.V[0]:0.###} {c.Unit}（区间外不外插）" : "那一段为拟合式外推"));
            if (!c.Covers(tHiC) && tHiC > c.TMaxC)
                parts.Add($"{c.Quantity}：本算例最高 {tHiC:0} °C 高于数据点上限 {c.TMaxC:0} °C ⇒ "
                          + (c.FromTable ? $"那一段取端点值 {c.V[^1]:0.###} {c.Unit}（区间外不外插）" : "那一段为拟合式外推"));
        }
        return string.Join("；", parts);
    }

    /// <summary>按牌号名取。"Pt" ⇒ <see cref="Pure"/>（不查表）；其余按名缓存（材料库是静态的）；材料库里没有的名字 ⇒ <see cref="MaterialDb.Get"/> 的明确异常。</summary>
    public static PtProps For(string? gradeName)
        => string.Equals(gradeName, PureGradeName, StringComparison.Ordinal) ? Pure
           : Cache.GetOrAdd(gradeName ?? "", Build);

    /// <summary>参数表的牌号（<see cref="DesignInputs.GradeName"/>）。</summary>
    public static PtProps For(DesignInputs p) => For(p.GradeName);

    /// <summary>整线算例的牌号：<see cref="LineCase.GradeName"/>，空 ⇒ 参数表（与 LineRunner.Normalize 同一口径；两者不一致时 Normalize 当场抛）。</summary>
    public static PtProps For(LineCase c) => For(string.IsNullOrEmpty(c.GradeName) ? c.Base.GradeName : c.GradeName);

    private static PtProps Build(string name)
    {
        var g = MaterialDb.Get(name);                       // 没有这个牌号 ⇒ 明确异常
        var dc = MaterialDb.DataCompleteness(name);
        if (dc.OwnResistivity && dc.OwnThermal)
        {
            if (!g.HasResistivity || !g.HasThermal)
                throw new InvalidOperationException($"牌号「{name}」齐全度说电、热数据齐，但材料库里电阻率或热导率／比热曲线为空 —— 材料库与齐全度判定不一致");
            var borrowed = dc.Borrowed.Where(b => b.StartsWith("电阻率", StringComparison.Ordinal)
                                               || b.StartsWith("热导率与比热", StringComparison.Ordinal)).ToArray();
            string note = $"电阻率、电阻温度系数、热导率、比热按牌号「{name}」取"
                        + (borrowed.Length > 0 ? "（" + string.Join("；", borrowed) + "）" : "");
            foreach (var c in new[] { g.ThermalKTable!, g.CpTable! })
                note += $"；{c.Quantity}数据点 {c.TMinC:0}–{c.TMaxC:0} °C，" + (c.FromTable ? "区间外取端点值" : "区间外为拟合式外推");
            note += "；密度、熔点按纯铂";
            return new PtProps(g, name, name, pure: false, fallback: false, note);
        }
        var missing = dc.Missing.Where(m => m.StartsWith("电阻率", StringComparison.Ordinal)
                                         || m.StartsWith("热导率与比热", StringComparison.Ordinal)).ToArray();
        // 「持久强度仍按本牌号」只在本牌号有持久强度曲线时才写（没有曲线的牌号写了就是假话）
        string fb = $"牌号「{name}」的电、热数据不全（{string.Join("；", missing)}）⇒ 电阻率、电阻温度系数、热导率、比热一起按纯铂算"
                  + (g.HasCreep ? "（持久强度仍按本牌号）" : "");
        return new PtProps(MaterialDb.Get(PureGradeName), name, PureGradeName, pure: true, fallback: true, fb);
    }
}
