using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 形状搜索（`--shape` / 界面「◇ 搜形状」）的**种子**。
///
/// ★★ 为什么种子必须被申报（2026-08-25 查出）
///   <see cref="Sizer"/> 的 D8 主循环是在**种子的板厚上做增量**：
///   `d.TabThickMm[j] = Clamp(d.TabThickMm[j] + dT, tLo, ThickHiMm)`，
///   每轮至多 `ThickStepMm`，而且「连坏 3 轮就停」。它**不重新初始化板厚**。
///   ⇒ 种子是一个**会影响答案**的输入。
///
///   影响有多大要分情况，不能一概而论（这一点是实测得来的，别再写死结论）：
///    · 形状被**工艺下界主导**时（如盘Ø120，抗屈曲下界 4.71 mm），
///      板厚被顶到下界，终态与种子无关 —— 实测从 0.73 走到 4.71，走了 3.98 mm。
///    · 形状**有裕度可省**时，「省铂」是沿梯度下降 + 变坏就停，
///      停在哪里就与出发点有关了。
///   ⇒ 所以不说「种子决定答案」，只说「种子是输入，必须印出来」。
///
/// ★ 此前的毛病（本类就是为它而建）：`--shape` 里写死 `FinalDesign.W08.Clone()`，
///   只覆盖 WallMm。于是 `--wall 0.6` 会拿 **0.8 档的板厚分布**去配 0.6 的管；
///   而 <see cref="FinalDesign.ByWall"/> 明明就在那儿，一直没人接。
///   更糟的是输出里**一个字都没提种子是谁** —— 拿到那份控制台输出的人，
///   无法从中还原出「这是用什么算的」。
/// </summary>
public static class ShapeSeed
{
    /// <summary>选出来的种子，连同**必须打印**的申报。</summary>
    public sealed class Choice
    {
        /// <summary>已经 Clone 过、壁厚已覆盖的种子。可以直接改形状后交给 Sizer。</summary>
        public FinalDesign Seed = null!;
        /// <summary>种子的出处，**调用方必须原样打印**。</summary>
        public string Note = "";
        /// <summary>种子档的壁厚与本次要算的壁厚对不上（找不到同壁厚的档时才会发生）。</summary>
        public bool WallMismatch;
        /// <summary>种子来自 .3dm 图纸（而不是定案档）。</summary>
        public bool FromDrawing;
    }

    /// <summary>
    /// 按壁厚挑档；`name` 给了就按档名挑。
    ///
    /// ⚠ 三条「不静默」：
    ///  · 档名给了但找不到 ⇒ **抛**，并列出可选（照 <see cref="FinalDesign.Select"/> 的规矩）。
    ///  · 找不到同壁厚的档 ⇒ 回退到 `current`，但把 <see cref="Choice.WallMismatch"/> 立起来，
    ///    并在 Note 里写明「板厚分布来自另一档」。回退可以，**不出声不行**。
    ///  · 无论哪条路，Note 都不为空 —— 调用方没得选，只能印。
    /// </summary>
    public static Choice Choose(double wallMm, string? name, IReadOnlyList<double>? thickMm,
                                IReadOnlyList<FinalDesign> all, FinalDesign current)
    {
        if (all is null || all.Count == 0) throw new ArgumentException("没有可用的定案档");
        FinalDesign tmpl;
        bool mismatch = false;
        string how;

        if (!string.IsNullOrWhiteSpace(name))
        {
            tmpl = all.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal))
                ?? all.FirstOrDefault(d => d.Name.Contains(name!, StringComparison.Ordinal))
                ?? throw new ArgumentException(
                    "--seed 认不出这个档名：" + name + "。可选：" +
                    string.Join("／", all.Select(d => d.Name)));
            how = "--seed 指定";
            mismatch = Math.Abs(tmpl.WallMm - wallMm) > 1e-6;
        }
        else
        {
            var byWall = all.FirstOrDefault(d => Math.Abs(d.WallMm - wallMm) < 1e-6);
            if (byWall is not null) { tmpl = byWall; how = "按管壁自动挑档"; }
            else { tmpl = current; how = "按管壁挑不到档，回退到当前档"; mismatch = true; }
        }

        var seed = tmpl.Clone();
        seed.WallMm = wallMm;

        // ★★★★★ 五个**优化变量**不许来自定案档（用户 2026-08-25：
        //   「把种子这种方法彻底禁掉，定案檔是用来校正计算流程，不应当被乱用」）。
        //   一律覆盖成 StartPoint 里声明的起点；留在定案档里的只有
        //   **图纸与界面都给不出**的构型/工艺常数（压接段、舌根圆角、环宽、控温点、圆盘保温），
        //   那是铁律②「几何只有一个来源」要求的 —— 并在 Note 里申报。
        for (int j = 0; j < seed.TabInsulMm.Length; j++) seed.TabInsulMm[j] = StartPoint.TabInsulMm;
        for (int j = 0; j < seed.RingMul.Length; j++) seed.RingMul[j] = StartPoint.RingMul;
        seed.TubeInsulMm = StartPoint.TubeInsulMm;
        seed.ClampTempC = StartPoint.ClampTempC;

        // 板厚**没有**统一起点：它在解析模式由界面控件/--thick 给，在 .3dm 模式由图纸给 ——
        // 两者都是真实输入。给不出就**抛**，不许拿定案档的板厚顶上（那正是被禁的做法）。
        if (thickMm is null || thickMm.Count == 0)
            throw new ArgumentException(
                "没有板厚起点。板厚是**优化变量**，起点只能来自真实输入（用户 2026-08-25）："
              + Environment.NewLine
              + "  · --from3dm <file.3dm> [图层]  从图纸取；或"
              + Environment.NewLine
              + "  · --thick a,b,c,d             明确给四片的板厚 mm；或"
              + Environment.NewLine
              + "  · 走界面：那四个「法兰厚度」框就是它的起点。"
              + Environment.NewLine
              + "  ⚠ **不会**再回退到定案档的板厚 —— 定案档只用来校正计算流程。");
        for (int j = 0; j < seed.TabThickMm.Length; j++)
            seed.TabThickMm[j] = thickMm[System.Math.Min(j, thickMm.Count - 1)];
        seed.Invalid = "";                            // 这是新解，不继承旧档的失效告示
        seed.InvalidChecks = Array.Empty<string>();   // 声明的判据清单也要一起清

        string thick = string.Join("/", seed.TabThickMm.Select(v => v.ToString("0.00")));
        var note = "种子：" + tmpl.Name + "（" + how + "，原壁厚 " + tmpl.WallMm.ToString("0.0") + "）"
                 + Environment.NewLine
                 + "  板厚起点 " + thick + " mm"
                 + "（来自 --thick／图纸；**不是**定案档）"
                 + " —— D8 是在这个起点上**增量**走板厚的，不是重新定。";
        if (mismatch)
            note += Environment.NewLine
                  + "  ⚠ 种子档的壁厚是 " + tmpl.WallMm.ToString("0.0")
                  + "，本次要算 " + wallMm.ToString("0.0")
                  + " —— 板厚分布来自**另一档**，结果不能当作该壁厚的独立推导。";

        note += Environment.NewLine
              + "  五个**优化变量**的起点：板厚 ← 上面那一行；舌保温 "
              + StartPoint.TabInsulMm.ToString("0.0") + "（裸舌）／环倍率 "
              + StartPoint.RingMul.ToString("0.00") + "（无台阶）／管保温 "
              + StartPoint.TubeInsulMm.ToString("0.0") + "／夹持 "
              + StartPoint.ClampTempC.ToString("0") + " °C —— 全部取自 Core/StartPoint.cs，**不是定案档**。"
              + Environment.NewLine
              + "  仍取自「" + tmpl.Name + "」的只有**图纸与界面都给不出**的构型常数："
              + "压接段 " + seed.ClampLengthMm.ToString("0") + " mm／舌根圆角／环宽／控温点／圆盘保温"
              + "（铁律②：几何只有一个来源）。";
        return new Choice { Seed = seed, Note = note, WallMismatch = mismatch };
    }

    /// <summary>
    /// 用 **.3dm 图纸**当种子 —— 这是「换个起点」唯一允许的做法。
    ///
    /// ★★ 为什么合成种子被禁（用户 2026-08-25：「不能再用所谓的中性种子，此方法禁用，
    ///    是要从 UI 或是 3DM(Pt_Heater1.3dm) 输入直接算」）：
    ///
    ///    我此前加过一个 `--seedflat`，把板厚压平成 2.0 当「中性起点」。那是**自己捏的数**：
    ///     · 它不对应任何真实工况 —— 既不是工程师会填的，也不是图纸上的；
    ///     · 而且它只压平**板厚**，舌保温与环倍率仍来自定案档 ⇒ 连「中性」都名不副实；
    ///     · 于是算出来的铂重与判据**看着正常却没有归属** —— 正是这个项目最怕的那种错。
    ///    ⇒ 起点只准来自两处真实输入：**界面参数**，或 **.3dm 图纸**。本方法是后者。
    ///
    /// ⚠ 图纸给不了的那些（舌保温、环倍率、管保温、控温点、压接段）仍取自同壁厚的定案档，
    ///   **这件事必须写进 Note**：种子里有多少来自图纸、多少来自定案，读的人有权知道。
    /// </summary>
    public static Choice FromDrawing(PlateShapeAnalyzer.Shape sh,
                                     IReadOnlyList<FinalDesign> all, FinalDesign current)
    {
        var k = ShapeToAnalytic.From(sh);                 // 三条近似由它生成，原样带出去
        // 板厚来自**图纸**（真实输入）；其余优化变量由 Choose 覆盖成 StartPoint 的起点
        var c = Choose(k.WallMm, null, new[] { k.PlateThickMm }, all, current);
        c.Seed.DiscRadiusMm = k.DiscDiameterMm * 0.5;
        c.Seed.TabHalfWidthMm = k.TabHalfWidthMm;
        c.Seed.TabLengthMm = k.TabLengthMm;
        c.FromDrawing = true;
        // ★★ 2026-08-25 更正：此处原本**整段覆盖** Choose 的申报，写的是
        //   「来自定案档（图纸给不了）：舌保温／环倍率／管保温／控温点／压接段」——
        //   而 Choose 现在已把舌保温/环倍率/管保温/夹持覆盖成 StartPoint 的起点，
        //   真正还来自定案档的只剩**控温点／压接段／圆角／环宽**。
        //   ⇒ 那句话变成了**假的**，而且它把**对的**那段申报挤掉了。
        //   现在：图纸那部分**加在** Choose 的申报**前面**，不覆盖它。
        c.Note =
            "种子：**.3dm 图纸**（不是定案档）" + Environment.NewLine
          + "  来自图纸：盘Ø " + k.DiscDiameterMm.ToString("0.0")
          + "　舌长 " + k.TabLengthMm.ToString("0.0")
          + "　舌半宽 " + k.TabHalfWidthMm.ToString("0.0")
          + "　管壁 " + k.WallMm.ToString("0.00")
          + "　板厚 " + k.PlateThickMm.ToString("0.00") + " mm" + Environment.NewLine
          + k.Note + Environment.NewLine
          + c.Note;
        return c;
    }

}
