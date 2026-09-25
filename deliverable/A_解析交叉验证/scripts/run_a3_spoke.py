# -*- coding: utf-8 -*-
"""A3 补正：圆盘上「等 J 辐条」到底还剩多少料可去（等 J 要求落在**整圈**上，不是半圈）。"""
import math, sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import R_DISC, R_HOLE, W_TAB

I = {0: 1214.0, 1: 2102.0}
T_PLATE = {0: 0.73, 1: 1.26}
print("═══ A3 补正　圆盘：等 J 辐条还剩多少可去 ═══")
print("等 J 判据（与 APP 的截面规则同口径：A(r) = 有料周向宽 × 板厚）：")
print("  需要的周向总宽 w_need(r) = I/(J_设计·t)  —— 与 r 无关（所以「等 J 辐条」是平行边的，不是渐缩的）")
print("  可用的周向总宽 = 2πr（实心环）\n")
print(f"{'片':>3}{'板厚mm':>8}{'I A':>7}{'w_need mm':>11}{'2πr@25.8':>11}{'2πr@26.6':>11}{'2πr@30':>10}{'最紧处利用率':>13}")
for j in (0, 1):
    t = T_PLATE[j]
    w = I[j]/(10.0*t)
    print(f"{j:>3}{t:>8.2f}{I[j]:>7.0f}{w:>11.1f}{2*math.pi*25.8:>11.1f}{2*math.pi*26.6:>11.1f}"
          f"{2*math.pi*30:>10.1f}{100*w/(2*math.pi*26.6):>12.1f}%")
print("\n⇒ 最紧半径（求解轨迹报的 r = 26.6）上，等 J 需要的周向总宽已经占满整圈的 99.5–99.8 %。")
print("  **实心环本身就是这个板厚下的等 J 形状** —— 辐条、减重槽、开孔一律无处下刀。")
print("  这从一条完全独立的路径复现了求解器轨迹里那句「圆盘背侧减重槽 开不出槽（周向留不出桥）」。")
print("\n另一支：把板厚按 t(r) = I/(J·2πr) 逐半径减（等 J 的另一种实现，环仍整圈）")
for j in (0, 1):
    t26 = I[j]/(10.0*2*math.pi*R_HOLE)
    v_const = math.pi*(R_DISC**2 - R_HOLE**2)*T_PLATE[j]           # 整环等厚
    # ∫ 2πr · I/(J·2πr) dr = (I/J)·(r2−r1)
    v_eqj = (I[j]/10.0)*(R_DISC - R_HOLE)
    print(f"  片{j}: 整环等厚 {v_const:7.1f} mm³ = {v_const*0.02145:5.2f} g；"
          f"等 J 变厚 t(r) = {t26:.3f}→{I[j]/(10.0*2*math.pi*R_DISC):.3f} mm ⇒ {v_eqj:7.1f} mm³ = {v_eqj*0.02145:5.2f} g；"
          f"省 {(v_const-v_eqj)*0.02145:+5.2f} g/片")
print("  四片合计省 "
      f"{sum(2*(math.pi*(R_DISC**2-R_HOLE**2)*T_PLATE[j] - (I[j]/10.0)*(R_DISC-R_HOLE))*0.02145 for j in (0,1)):+.2f} g"
      "（法兰 1779 g 的 0.1 %）—— 且要求板厚沿半径连续变化，工艺上换不来。")
