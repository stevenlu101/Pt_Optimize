# -*- coding: utf-8 -*-
r"""
A7（2026-09-17 新增）：把关意见 ③⑤ 与 §4.2 零点的精算。
  ③ 首版叫「管孔净流入差 11.3 W」的那个量**不是** APP 的判据量 QFromTubeW，
    而是「Q夹 − 舌板焦耳 + 舌板表面散热」= **越过 r = 30 那一圈进舌片区的净热**（分区账的余项）。
    APP 判据量 QFromTubeW（管孔边界净流入）在 L140 的 5.0／5.1 档是 2.461／1.601 W（探针逐档表）。
  ⑤ 「r = 25.8 上 J = 10.26」降级：SectionSizing.cs:223 的截面扫描起始半径是**明写规则**
    rIn = 孔半径 + 焊脚（DesignSpec.cs:497 焊脚 = max(板厚, 壁厚) = max(0.73, 0.80) = 0.80 ⇒ rIn = 26.6），
    而 25.8–26.6 那圈焊脚环带另由「舌盘交界」截面（SectionSizing.cs:190-218：边条 + 焊弧 × 孔边厚）覆盖。
  §4.2 令热端流入 = 0 的舌保温，用二分精算（首版是 0.05 步扫描）。
"""
import math, sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import *

I0, TC0 = 1214.0, 1150.0
T_ROOT = TC0 - 4.922      # 1145.08：探针 L140 5.1 档那一列给的片0 管根下界

print("═══ A7　把关意见 ③⑤ 与 §4.2 零点精算 ═══")
print(f"圆盘保温 {DISC_INS} mm（DesignSpec.cs:181/205/949）　热端 {T_ROOT:.2f} °C　夹头 450 °C 定温\n")

print("── (③) 两个量必须分清：判据量 QFromTubeW ≠ 分区账余项")
print("  甲 APP 判据量「管孔净流入」= FlangeOut.QFromTubeW（Core/LineRunner.cs:1743/1756/1760），")
print("     探针 L140 逐档表：5.0 档 2.461 W、5.1 档 1.601 W。整线那一列 5.0/5.1 = 0.648/0.657 W。")
print("  乙 首版拿去比的那个 11.39 W = 进铜排夹 − 舌板焦耳 + 舌板表面散热")
print("     = **越过 r = 30 圈进舌片区的净热**（分区账的余项，不是管孔边界）。逐档实测：")
MEAS = {3.0:(347.3,215.522,164.065,23.42), 3.5:(351.433,206.067,171.632,17.368),
        4.0:(355.147,197.493,178.488,11.917), 4.5:(358.508,189.669,184.735,6.966),
        5.0:(361.511,182.476,190.430,2.461),  5.5:(364.300,175.870,195.688,-1.676),
        6.0:(366.812,169.745,200.519,-5.478), 7.0:(371.289,158.783,209.167,-12.254),
        8.0:(375.134,149.233,216.677,-18.119)}
print(f"  {'舌保温':>7}{'焦耳':>9}{'散热':>9}{'进夹':>9} | {'乙 分区余项':>12}{'甲 QFromTubeW':>15}{'甲−乙':>9}")
for t in sorted(MEAS):
    g,l,c,q = MEAS[t]
    print(f"  {t:>7.1f}{g:>9.3f}{l:>9.3f}{c:>9.3f} | {c-g+l:>12.2f}{q:>15.3f}{q-(c-g+l):>9.2f}")
print("  ⇒ 两者差 21 W 上下、且随保温走 —— **它们本来就不是同一个量**，不许互相当证据。")
print("\n  本模型的两个对应量（同一次解、同一本账）：")
print(f"  {'舌保温':>7}{'焦耳':>9}{'散热':>9}{'进夹':>9} | {'分区余项':>10}{'Q_hot(x=−25.8)':>16}")
for t in (3.0,4.0,5.0,5.1,6.0,8.0):
    a,gc,lc = cap_zone(t, T_ROOT, 2.03, I0)
    r = solve_tab(-R_HOLE, -100.0, 2.03, I0, t, T_hot=T_ROOT, N=1500)
    g, l, c = r['gen_zone']+gc, r['loss_zone']+lc, r['Q_clamp']
    print(f"  {t:>7.1f}{g:>9.1f}{l:>9.1f}{c:>9.1f} | {c-g+l:>10.2f}{r['Q_hot']:>16.2f}")
print("  ⇒ 模型的「分区余项」与「Q_hot」逐位不同（面积项差一个帽子区的进出），两者都不是 QFromTubeW。")
print("  ⇒ 结论改写：本线**没有**算过 APP 那条判据量；说「差 11.3 W」是把两个不同的量摆在一起。")

print("\n── (⑤) 最紧截面半径：25.8 还是 26.6")
leg = max(0.73, 0.80)
print(f"  规则（SectionSizing.cs:223）：rIn = 孔半径 + 焊脚 = 25.8 + {leg:.2f} = {25.8+leg:.2f} mm —— **明写的**，不是遗漏。")
print(f"  焊脚（DesignSpec.cs:497）= max(板厚, 壁厚) = max(0.73, 0.80) = {leg:.2f} mm。")
print(f"  25.8–26.6 那圈焊脚环带另由「舌盘交界」截面覆盖（SectionSizing.cs:190-218：边条 + 焊弧 × 孔边厚）。")
print(f"  {'r mm':>7}{'片0 t=0.73':>12}{'片1 t=1.26':>12}  （J = I/(2πr·t)，APP 圆盘截面规则 SectionSizing.cs:240 arc = 2πr）")
for r_ in (25.8, 26.6, 30.0):
    print(f"  {r_:>7.1f}{I0/(2*math.pi*r_*0.73):>12.3f}{2102.0/(2*math.pi*r_*1.26):>12.3f}")
print("  ⇒ 首版那句「r=25.8 上 J=10.26 ⇒ 设定 J 在那 0.8 mm 环带上被突破」**降级为一问**：")
print("     只剩「焊脚堆料算不算载流截面」这一个问题 —— 若算，那一圈的截面比 2πr·t 还大、J 更低；")
print("     若不算，那一圈根本没有铂母材可用，起始半径取 26.6 正确。两种读法都不支持「判据被突破」。")

print("\n── (§4.2) 令热端流入 = 0 的舌保温（二分精算，不是 0.05 步扫描）")
def zero_ins(tt, Ia, lo=2.0, hi=12.0, Th=T_ROOT):
    for _ in range(60):
        m = 0.5*(lo+hi)
        q = solve_tab(-R_HOLE, -100.0, tt, Ia, m, T_hot=Th, N=1200)['Q_hot']
        if q > 0: lo = m
        else: hi = m
    return 0.5*(lo+hi)
for lbl, tt in (("J=10 求解器解 舌片厚 2.03", 2.03), ("J=11 理论下限 舌片厚 1.84", 1.84)):
    z = zero_ins(tt, I0)
    a,gc,lc = cap_zone(z, T_ROOT, tt, I0)
    r = solve_tab(-R_HOLE, -100.0, tt, I0, z, T_hot=T_ROOT, N=1500)
    a1 = solve_tab(-R_HOLE, -100.0, tt, I0, z-0.5, T_hot=T_ROOT, N=1200)
    a2 = solve_tab(-R_HOLE, -100.0, tt, I0, z+0.5, T_hot=T_ROOT, N=1200)
    print(f"  {lbl}: 零点舌保温 = **{z:.3f} mm**　焦耳 {r['gen_zone']+gc:.1f} W　进夹 {r['Q_clamp']:.1f} W　"
          f"斜率 {(a2['Q_hot']-a1['Q_hot']):+.2f} W/mm")
print(f"  对照：求解器对「管根低于热偶读数」二分求出 5.0134 mm，向上对齐图纸格 ⇒ 5.1")
print(f"       （出处 R48_L_端到端_求解再过三关_W08_本次开跑于2026-09-17_012720.txt 第 1 轮「片0 舌保温 → 5.100」那一行）")
print("  ⚠ 两者不是同一条方程（模型解「热端净导热 = 0」，求解器解「管根低于热偶读数 = 5 K」），")
print("     数接近是好迹象，不是逐位对拍。")

print("\n── (圆盘保温口径本身值多少) 2.5 vs 20 mm，其余一律不动")
for ins_d in (2.5, 20.0):
    a,gc,lc = cap_zone(5.1, T_ROOT, 2.03, I0)
    r = solve_tab(-R_HOLE, -100.0, 2.03, I0, 5.1, ins_d, T_hot=T_ROOT, N=1500)
    z = None
    lo, hi = 2.0, 12.0
    for _ in range(50):
        m = 0.5*(lo+hi)
        if solve_tab(-R_HOLE, -100.0, 2.03, I0, m, ins_d, T_hot=T_ROOT, N=1000)['Q_hot'] > 0: lo = m
        else: hi = m
    z = 0.5*(lo+hi)
    print(f"  圆盘保温 {ins_d:>5.1f} mm ⇒ 焦耳 {r['gen_zone']+gc:7.1f} W　进夹 {r['Q_clamp']:7.1f} W　"
          f"热端流入 {r['Q_hot']:+6.2f} W　零点舌保温 {z:.3f} mm")
