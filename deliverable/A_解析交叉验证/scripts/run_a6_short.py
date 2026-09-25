# -*- coding: utf-8 -*-
r"""
A6（2026-09-17 新增，把关意见 ⑦）：**短舌长方向 —— 一维模型对冷侧有没有裁决权？**

首版 §4.3 写「缩舌长在热上是安全方向（焦耳热少、离 T_eq 更远）」。
而短舌长探针（r48_P，同树同网格同配方）实测：
  L125 管根低于热偶读数 77.5 K／L110 170.5 K／L80 428.8 K（限值 5 K）—— 冷侧整条崩掉，无可行点。
本脚本回答两件事：
  甲 一维模型能不能算出「抽多少热」？—— 能，那是 Q_hot（热端流进本域的净导热）。
  乙 一维模型能不能算出「管根被抽冷多少 K」？—— **不能**。热端是 Dirichlet（定温），
     管子自己的热阻在模型之外；定温边界的物理含义就是「热端要多少给多少、温度不动」。
     ⇒ 冷侧温降这一问，本模型**没有裁决权**；能给的只有驱动量与方向。

出处：
  · 探针短方向 R48_P_短舌长窗口探针_L80/L110/L125_本次开跑于2026-09-17_130026.txt（舌保温 5.1 那一行与逐档表）
  · 探针长方向 R48_P_舌长窗口探针_L140/L170/L200/L230_本次开跑于2026-09-17_114350/115429/114438/115431.txt
  · 汇总 R48_P_舌长窗口探针_汇总_短方向与四片同扫_本次开跑于2026-09-17_130026.txt「一张表」
  · 几何下限：自由段 = 舌长 − 切点 − 压接段 ≥ 100 mm（LineCase.FreeTabMinMm = GeometryScreen.FreeTabMinDefaultMm）
    ⇒ 最小合法舌长 140.0 mm（切点 x=0，压接段 40）。100 / 110 / 125 全部违规。
"""
import math, sys, io, os
import numpy as np
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import *

T_TAB, T_INS, I_ENTRY = 2.03, 5.1, 1214.0
TC0 = 1150.0

# 探针实测（舌保温 5.1 那一行）：入口片焦耳/进夹/管孔净流入 QFromTubeW、整线最坏片管根低于热偶读数
PROBE = {80 : dict(gen= 49.935, clamp=196.442, qtube=150.991, cold=428.774, slope=-0.397),
         110: dict(gen=191.798, clamp=180.648, qtube= 55.692, cold=170.485, slope=-3.330),
         125: dict(gen=273.738, clamp=185.256, qtube= 25.313, cold= 77.489, slope=-5.614),
         140: dict(gen=362.110, clamp=191.526, qtube=  1.601, cold=  4.922, slope=-8.159)}

print("═══ A6　短舌长方向：一维模型算得出什么、算不出什么（把关意见 ⑦）═══")
print(f"圆盘保温 {DISC_INS} mm　舌保温 {T_INS} mm　舌片厚 {T_TAB} mm　I = {I_ENTRY} A　夹头 450 °C 定温\n")

print("── (0) 热端温度怎么取")
print("  探针那一列是整线最坏片的 (基准 − 管根)；片0 基准 1150 ⇒ 片0 管根 ≥ 1150 − 该列值。")
print("  短舌长这三档该下界分别是 1150−428.8 = 721.2／1150−170.5 = 979.5／1150−77.5 = 1072.5 °C —— **管根被抽冷**。")
print("  本模型两种跑法都做：甲 热端定在 1145.08（= L140 那一档的下界，『管根没被抽冷』的假设）；")
print("                     乙 热端定在探针实测的那个（冷的）管根下界 —— 看模型的 Q_hot 会怎么变。\n")

print("── (1) 甲：热端一律 1145.08 °C（= 首版 §4.3 那句话背后的假设：管根不动）")
hdr=(f"{'舌长':>5}{'自由段':>7}{'域长':>7} | {'焦耳模型':>9}{'焦耳实测':>9}{'差%':>7} | "
     f"{'进夹模型':>9}{'进夹实测':>9}{'差%':>7} | {'散热模型':>9}{'Q_hot模型':>11}{'QFromTube实测':>14}{'Tmax':>8}")
print(hdr); print("─"*len(hdr))
TH_A = TC0 - PROBE[140]['cold']
mdl_a = {}
for L in (80, 100, 110, 125, 140):
    a,gc,lc = cap_zone(T_INS, TH_A, T_TAB, I_ENTRY)
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, T_INS, T_hot=TH_A, N=1500)
    gz, lz = r['gen_zone']+gc, r['loss_zone']+lc
    mdl_a[L] = (gz, r['Q_clamp'], lz, r['Q_hot'], r['Tmax'])
    m = PROBE.get(L)
    g_s = f"{m['gen']:>9.1f}{100*(gz/m['gen']-1):>+7.1f}" if m else f"{'—':>9}{'—':>7}"
    c_s = f"{m['clamp']:>9.1f}{100*(r['Q_clamp']/m['clamp']-1):>+7.1f}" if m else f"{'—':>9}{'—':>7}"
    q_s = f"{m['qtube']:>14.2f}" if m else f"{'—':>14}"
    print(f"{L:>5}{L-CLAMP_L:>7.0f}{-(L-CLAMP_L)+R_HOLE:>7.1f} | {gz:>9.1f}{g_s} | "
          f"{r['Q_clamp']:>9.1f}{c_s} | {lz:>9.1f}{r['Q_hot']:>11.2f}{q_s}{r['Tmax']:>8.1f}")

print("\n  ⇒ **模型的 Q_hot 与实测 QFromTubeW 同号、同向、同量级地随舌长变短暴涨。**")
print("     它们不是同一个断面（Q_hot 是 x=−25.8 这条竖线上的导热；QFromTubeW 是管孔边界的净流入），")
print("     所以不做逐位对拍，只对**方向与量级**。")

print("\n── (2) 乙：热端取探针实测的（被抽冷的）管根下界")
print(f"{'舌长':>5}{'热端°C':>9}{'焦耳模型':>10}{'焦耳实测':>10}{'差%':>8}{'进夹模型':>10}{'进夹实测':>10}{'Q_hot模型':>11}")
for L in (80, 110, 125, 140):
    Th = TC0 - PROBE[L]['cold']
    a,gc,lc = cap_zone(T_INS, Th, T_TAB, I_ENTRY)
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, T_INS, T_hot=Th, N=1500)
    gz = r['gen_zone']+gc; m = PROBE[L]
    print(f"{L:>5}{Th:>9.1f}{gz:>10.1f}{m['gen']:>10.1f}{100*(gz/m['gen']-1):>+8.1f}"
          f"{r['Q_clamp']:>10.1f}{m['clamp']:>10.1f}{r['Q_hot']:>11.2f}")
print("  ⇒ 把实测的冷管根喂回去，焦耳热与进夹都能跟上实测；**但这是拿实测当输入，不是模型自己算出来的冷侧。**")

print("\n── (3) 保温这根旋钮在短舌长上还剩多少权力（模型 vs 实测，热端一律 1145.08）")
print(f"{'舌长':>5}{'模型 dQ_hot/dt_ins':>20}{'实测 dQFromTube/dt_ins':>24}{'模型 舌散热/进夹':>18}{'实测 舌散热/进夹':>18}")
MEAS_LOSS = {80:(16.325,196.292), 110:(77.935,180.080), 125:(124.505,184.443), 140:(182.476,190.430)}  # 5.0 档
for L in (80, 100, 110, 125, 140):
    a1 = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, 4.6, T_hot=TH_A, N=1200)
    a2 = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, 5.6, T_hot=TH_A, N=1200)
    c1 = cap_zone(4.6, TH_A, T_TAB, I_ENTRY); c2 = cap_zone(5.6, TH_A, T_TAB, I_ENTRY)
    dq = (a2['Q_hot']-a1['Q_hot'])/1.0
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, T_INS, T_hot=TH_A, N=1200)
    c = cap_zone(T_INS, TH_A, T_TAB, I_ENTRY)
    ratio = (r['loss_zone']+c[2])/r['Q_clamp']
    ms = f"{PROBE[L]['slope']:>24.3f}" if L in PROBE else f"{'—':>24}"
    mr = f"{MEAS_LOSS[L][0]/MEAS_LOSS[L][1]:>18.2f}" if L in MEAS_LOSS else f"{'—':>18}"
    print(f"{L:>5}{dq:>20.2f}{ms}{ratio:>18.2f}{mr}")
print("  ⇒ 两条线都指同一件事：舌板越短，**表面散热这条路相对导热塌掉**，保温这根旋钮跟着失效。")
print("     这是短方向出事的第一性原理，模型独立复现了它。")

print("\n── (4) 冷侧温降：模型给不出，能给的只有一个按实测系数的外推（**标成外推，不是模型结果**）")
print("  实测的换算系数（探针 L140 逐档表 3.0→5.0 档，同一次运行、同一张网格）：")
print("    Δ管根低于热偶读数 55.217 − 5.692 = 49.525 K ÷ Δ入口片管孔净流入 23.42 − 2.461 = 20.959 W = 2.363 K/W")
print("  拿它乘模型算出来的 Q_hot 增量（相对 L140）：")
for L in (80, 100, 110, 125):
    dQ = mdl_a[L][3] - mdl_a[140][3]
    est = PROBE[140]['cold'] + 2.363*dQ
    act = PROBE[L]['cold'] if L in PROBE else float('nan')
    print(f"    L{L}: 模型 ΔQ_hot {dQ:+7.1f} W ⇒ 外推管根低于热偶读数 ≈ {est:7.1f} K　"
          + (f"（实测 {act:.1f} K）" if act == act else "（没有实测）"))
print("  ⇒ 外推与实测同一量级、同一方向，但**不是证据**：系数是从实测借的，模型只出了 ΔQ。")
print("     严格的话只有一句：**一维模型对冷侧（管根温降）无裁决权**，因为它的热端是定温。")

print("\n── (5) 铂重那一栏（首版 §4.3 说缩到 100 mm 省 570 g）")
RHO_G = 0.02145
SOLVED = {0:(0.73,2.03),1:(1.26,3.51),2:(1.26,3.51),3:(0.73,2.03)}
A_HALFANN = math.pi*(R_DISC**2-R_HOLE**2)/2
def mass(Lt):
    a = Lt*W_TAB - math.pi*R_HOLE**2/2
    return sum((a*SOLVED[j][1] + A_HALFANN*SOLVED[j][0])*RHO_G for j in range(4))
for Lt in (100, 110, 125, 140):
    free = Lt - CLAMP_L
    ok = "合法" if free >= 100 else f"**违规**（自由段 {free:.0f} < 100）"
    print(f"  舌长 {Lt:3d}：法兰铂 {mass(Lt):7.1f} g（{mass(Lt)-mass(140):+7.1f} g）　自由段 {free:3.0f} mm　{ok}")
print("  ⇒ 110／125 在**装配**上已经违规；探针又证明它们在**热学**上也不成立（管根低于热偶读数 170.5／77.5 K，限值 5）。")
print("     ⇒ 首版「限制它的是自由段这条规则、不是热」**不成立**：两条闸门都关着。")
