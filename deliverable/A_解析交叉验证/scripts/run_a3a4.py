# -*- coding: utf-8 -*-
"""A3 等电流密度设计族（铂重理论下限）+ A4 反向设计草稿（保温剖面）。"""
import math, sys, io, os
import numpy as np
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import *

RHO_G = 0.02145          # g/mm³（Materials.cs:13 PtDensity 21450 kg/m³）
L_TAB = 140.0
I = {0: 1214.0, 1: 2102.0, 2: 2102.0, 3: 1214.0}     # 逐片电流（W08 求解轨迹「设计电流 … ⇒ 片 1214/2102/2102/1214 A」）
SOLVED = {0: (0.73, 2.03), 1: (1.26, 3.51), 2: (1.26, 3.51), 3: (0.73, 2.03)}   # (板厚, 舌片厚)
R_SEC = 26.6             # 求解轨迹里报出的「最紧截面 圆盘 r=26.6」

A_STRIP = L_TAB*W_TAB - math.pi*R_HOLE**2/2          # 舌带有料面积（扣掉左半个管孔）
A_HALFANN = math.pi*(R_DISC**2 - R_HOLE**2)/2        # 右半个环（x>0 那半）

def plate_mass(t_plate, t_tab):
    return (A_STRIP*t_tab + A_HALFANN*t_plate)*RHO_G

print("═══ A3　等电流密度设计族：铂重的理论下限 ═══\n")
print("── (0) 先验几何账：用闭式重算求解器那个设计的法兰铂重，与它报的 1780 g 比")
print(f"  舌带有料面积 = 140×60 − π·25.8²/2 = {A_STRIP:.1f} mm²（扣掉左半个管孔）")
print(f"  右半个环面积 = π(30²−25.8²)/2 = {A_HALFANN:.1f} mm²")
tot = sum(plate_mass(*SOLVED[j]) for j in range(4))
for j in range(4):
    tp, tt = SOLVED[j]
    print(f"  片{j}: 板厚 {tp} / 舌片厚 {tt} ⇒ {plate_mass(tp,tt):7.1f} g"
          f"（舌带 {A_STRIP*tt*RHO_G:6.1f} + 半环 {A_HALFANN*tp*RHO_G:5.1f}）")
print(f"  合计 {tot:.1f} g　vs 求解器报的法兰铂 1780 g　⇒ 差 {100*(tot/1780-1):+.2f} %")
print(f"  ★ 舌带占 {100*sum(A_STRIP*SOLVED[j][1] for j in range(4))/sum(A_STRIP*SOLVED[j][1]+A_HALFANN*SOLVED[j][0] for j in range(4)):.1f} %，"
      f"圆盘（半环）只占 {100*sum(A_HALFANN*SOLVED[j][0] for j in range(4))/sum(A_STRIP*SOLVED[j][1]+A_HALFANN*SOLVED[j][0] for j in range(4)):.1f} %")

print("\n── (1) 规则 J ≤ 11 下的等 J 族：处处 J = J_设计 ⇒ 沿电流路径 A(s) = I/J_设计")
print("  舌板：电流沿舌长不变 ⇒ 等 J = 等截面 ⇒ 等宽舌片已经是等 J 族的成员，**舌板上没有可省的**。")
print("  圆盘：电流从管孔整圈进、向舌根汇聚，环上按整圈截面 A(r) = 2πr·t ⇒ J(r) = I/(2πr·t) 随 r 降。")
for j in (0,1):
    for Jd in (10.0, 11.0):
        tt = math.ceil(I[j]/(Jd*W_TAB)*100)/100
        tp = max(0.60, math.ceil(I[j]/(Jd*2*math.pi*R_SEC)*100)/100)
        print(f"    片{j} J={Jd:4.1f}: 舌片厚 = {I[j]}/({Jd}×60) = {I[j]/(Jd*W_TAB):.4f} → 图纸格 {tt}；"
              f"板厚 = {I[j]}/({Jd}×2π×26.6) = {I[j]/(Jd*2*math.pi*R_SEC):.4f} → {tp}（下界 0.60 焊接）")
def family(Jd):
    m = 0
    det = []
    for j in range(4):
        tt = math.ceil(I[j]/(Jd*W_TAB)*100)/100
        tp = max(0.60, math.ceil(I[j]/(Jd*2*math.pi*R_SEC)*100)/100)
        m += plate_mass(tp, tt); det.append((tp, tt))
    return m, det
for Jd in (10.0, 10.5, 11.0):
    m, det = family(Jd)
    print(f"  等 J 族 J={Jd:4.1f}：法兰铂 {m:7.1f} g　（板厚/舌片厚 " +
          " ".join(f"{a}/{b}" for a,b in det) + ")")
m10, _ = family(10.0); m11, _ = family(11.0)
print(f"  ⇒ **规则允许的理论下限（J = 11，同形状同舌长）= {m11:.0f} g**；求解器在 J=10 上给的是 {tot:.0f} g；"
      f"差 {tot-m11:.0f} g（{100*(tot-m11)/tot:.1f} %）—— 这个差不是浪费，是工程师在 ① 页把设计 J 设成 10 留的余量。")

print("\n── (2) 圆盘做成「等 J 辐条 + 焊环」能省多少")
print("  等 J 要求 φ(r)·2πr·t = I/J = 常数 ⇒ 辐条**总宽**沿 r 不变（不是均匀减薄，是把料收成几条平行边的辐条）。")
for j in (0,1):
    Jd = 10.0
    tp = max(0.60, math.ceil(I[j]/(Jd*2*math.pi*R_SEC)*100)/100)
    v_solid = A_HALFANN*tp                       # 右半环（左半已在舌带里）
    w_need = I[j]/(Jd*tp)                        # 等 J 所需总宽 mm
    # 右半环里等 J 只需要总宽 w_need 的料，径向长 4.2 mm；但焊环必须整圈（焊缝要求）
    v_spoke = w_need/2*(R_DISC-R_HOLE)           # 右半边分到一半总宽
    print(f"    片{j}: 实心右半环 {v_solid:6.1f} mm³ = {v_solid*RHO_G:5.2f} g；"
          f"等 J 辐条（右半需总宽 {w_need/2:.1f} mm × 径向 4.2）{v_spoke:6.1f} mm³ = {v_spoke*RHO_G:5.2f} g；"
          f"省 {(v_solid-v_spoke)*RHO_G:+5.2f} g/片")
print("  ⇒ 整台四片合起来省不到 1 g。**圆盘不是铂重所在**（上面 (0) 已量过：只占 1.8 %）。")
print("  ⇒ 拓扑阶段 2/3 在圆盘上找形状，上限就是这个量级 —— 与拓扑线自己的结论「整线只省 1.3 %」量级一致。")

print("\n── (3) 舌长才是铂重的主项（等 J 族下铂重严格 ∝ 舌长）")
for Lt in (100, 120, 140, 170, 200, 230):
    a_strip = Lt*W_TAB - math.pi*R_HOLE**2/2
    m = sum((a_strip*SOLVED[j][1] + A_HALFANN*SOLVED[j][0])*RHO_G for j in range(4))
    print(f"  舌长 {Lt:3d} mm ⇒ 法兰铂 {m:7.1f} g（{m-tot:+7.1f} g vs 140 mm）")
print("  实测对照：探针汇总「加长还要多花铂 —— 428 g / 30 mm」⇒ 14.3 g/mm；"
      f"闭式 {sum(W_TAB*SOLVED[j][1] for j in range(4))*RHO_G:.1f} g/mm　逐位对得上。")

print("\n── (4) J=11 的那个设计，热上过不过（用 A1 模型跑一遍，不是猜）")
T_ROOT = 1150.0 - 4.922   # = 1145.078；★ 2026-09-17 修订（把关意见 ②）：热端改用探针（P 树）自己那一档，
                          #   与对拍量同一次运行、同一棵树（原先 1146.35 取自 L 树，是跨树拼数）
for lbl, tt in (("J=10 求解器解 2.03", 2.03), ("J=11 理论下限 1.84", 1.84)):
    best = None
    for ins in np.arange(3.0, 9.01, 0.05):
        r = solve_tab(-R_HOLE, -100.0, tt, I[0], float(ins), DISC_INS, T_hot=T_ROOT, N=800)
        c = cap_zone(float(ins), T_ROOT, tt, I[0])
        if best is None or abs(r['Q_hot']) < abs(best[1]['Q_hot']): best = (ins, r, c)
    ins, r, c = best
    print(f"  {lbl}: 令热端流入≈0 的舌保温 = {ins:.2f} mm；此时 焦耳 {r['gen_zone']+c[1]:.1f} W、"
          f"进夹 {r['Q_clamp']:.1f} W、Tmax {r['Tmax']:.1f} °C（管根 {T_ROOT}）")
    a1 = solve_tab(-R_HOLE, -100.0, tt, I[0], float(ins)-0.5, DISC_INS, T_hot=T_ROOT, N=800)
    a2 = solve_tab(-R_HOLE, -100.0, tt, I[0], float(ins)+0.5, DISC_INS, T_hot=T_ROOT, N=800)
    print(f"      斜率 d(热端流入)/d(舌保温) = {(a2['Q_hot']-a1['Q_hot'])/1.0:+.2f} W/mm")

print("\n\n═══ A4　反向设计草稿：保温剖面（W08 入口片，舌长 140）═══\n")
print("推不出「唯一」剖面 —— 目标只有一条（热端净流入 = 0），未知数是整条 t_ins(x)，欠定。")
print("有用的那一步是换旋钮：**不调厚度，调这一层保温从压接端往热端盖多长**。")
print("理由：厚度只能按 0.5 mm 一层给（现场包法），而**盖多长**是连续的。\n")
BASE, EXTRA = 5.0, 0.5
print(f"算例：底层 {BASE} mm 满铺，再从**热端**往回加一层 {EXTRA} mm，覆盖长度 s（mm）")
print(f"{'s mm':>7}{'热端流入W':>11}{'焦耳W':>9}{'舌散热W':>9}{'进夹W':>9}{'Tmax':>8}")
rows = []
for s in (0, 10, 20, 30, 40, 50, 60, 74.2):
    def prof(xv, s=s): return BASE + (EXTRA if xv >= -R_HOLE - s else 0.0)
    r = solve_tab(-R_HOLE, -100.0, 2.03, I[0], prof, DISC_INS, T_hot=T_ROOT, N=800)
    # 帽子区按底层 + 加层（帽子区一定在热端一侧）
    c = cap_zone(BASE+EXTRA if s > 0 else BASE, T_ROOT, 2.03, I[0])
    rows.append((s, r['Q_hot']))
    print(f"{s:>7.1f}{r['Q_hot']:>11.2f}{r['gen_zone']+c[1]:>9.1f}{r['loss_zone']+c[2]:>9.1f}"
          f"{r['Q_clamp']:>9.1f}{r['Tmax']:>8.1f}")
ss = np.array([a for a,_ in rows]); qq = np.array([b for _,b in rows])
k = np.polyfit(ss, qq, 1)[0]
print(f"\n  d(热端流入)/d(覆盖长度) = {k:+.4f} W/mm 长度")
print(f"  对照：d(热端流入)/d(均匀厚度) = −12.0 W/mm 厚度（A2 表）")
print(f"  ⇒ 同样一瓦，用「盖多长」去调，需要动 {12.0/abs(k):.0f} mm 长度；用「加多厚」去调只需 1 mm 厚度。")
print(f"  现场把覆盖长度做到 ±2 mm 是容易的 ⇒ 等效厚度分辨率 {abs(k)*2/12.0:.3f} mm，"
      f"**窄于实测窗口 0.1～0.2 mm**；而 0.5 mm 一层的厚度档一档都落不进去。")
print("  ⇒ 这不是把窗口撑宽，是**把旋钮换细**：窗口还是那么窄，但现在有旋钮能停在里面。")
