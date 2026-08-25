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
        /// <summary>板厚被压平成均匀值（`--seedflat`）。</summary>
        public bool Flattened;
    }

    /// <summary>
    /// 按壁厚挑档；`name` 给了就按档名挑；`flatMm` 给了就把板厚压平成均匀值。
    ///
    /// ⚠ 三条「不静默」：
    ///  · 档名给了但找不到 ⇒ **抛**，并列出可选（照 <see cref="FinalDesign.Select"/> 的规矩）。
    ///  · 找不到同壁厚的档 ⇒ 回退到 `current`，但把 <see cref="Choice.WallMismatch"/> 立起来，
    ///    并在 Note 里写明「板厚分布来自另一档」。回退可以，**不出声不行**。
    ///  · 无论哪条路，Note 都不为空 —— 调用方没得选，只能印。
    /// </summary>
    public static Choice Choose(double wallMm, string? name, double? flatMm,
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
        seed.Invalid = "";                            // 这是新解，不继承旧档的失效告示
        seed.InvalidChecks = Array.Empty<string>();   // 声明的判据清单也要一起清

        bool flattened = false;
        if (flatMm is double t)
        {
            if (t <= 0) throw new ArgumentException("--seedflat 要正数，收到 " + t);
            for (int j = 0; j < seed.TabThickMm.Length; j++) seed.TabThickMm[j] = t;
            flattened = true;
        }

        string thick = string.Join("/", seed.TabThickMm.Select(v => v.ToString("0.00")));
        var note = "种子：" + tmpl.Name + "（" + how + "，原壁厚 " + tmpl.WallMm.ToString("0.0") + "）"
                 + Environment.NewLine
                 + "  板厚起点 " + thick + " mm"
                 + (flattened ? "（--seedflat 压平）" : "（来自该档）")
                 + " —— D8 是在这个起点上**增量**走板厚的，不是重新定。";
        if (mismatch)
            note += Environment.NewLine
                  + "  ⚠ 种子档的壁厚是 " + tmpl.WallMm.ToString("0.0")
                  + "，本次要算 " + wallMm.ToString("0.0")
                  + " —— 板厚分布来自**另一档**，结果不能当作该壁厚的独立推导。";

        return new Choice { Seed = seed, Note = note, WallMismatch = mismatch, Flattened = flattened };
    }
}
