# -*- coding: utf-8 -*-
r"""
A5（2026-09-17 修订，把关意见 ②）：**四个舌长各用自己的热端管根温度**重跑 A1 对拍。

首版的错：四个舌长共用一个热端 1146.35 °C（= 1150 − 3.65，L 树第 3 轮片0 的冷侧），
而探针实测里 170／200／230 的管根**比热偶读数高出几十到一百多开尔文**，根本不是 1146。
那一版「四个舌长齐平（−0.1～+3.9 %）」是拿错的边界凑出来的。

热端温度怎么来（每一步指得回出处）：
  · 探针逐档表的「管根低于热偶读数 K」列 = LineRunner.ThermocoupleChecks 的 cold.Actual
    （Core/LineRunner.cs:3038-3053，wc = tcJoints.OrderByDescending(t => t.ColdK).First()）
    ⇒ 它是**整线四片里最坏的那一片**的 (该片热偶基准 − 该片管根较冷端)，逐档表**不印是哪一片、也不印基准**。
  · 逐片热偶基准（同一个 W08 解，L 树第 3 轮）：片0 1150.0／片1 1114.7／片2 1064.9／片3 1050.0 °C。
  · 本模型算的是**入口片（片0）**。由 max 的定义：对每一片都有 (基准 − 管根) ≤ 该列值，
    ⇒ 片0 管根 ≥ 1150 − 该列值。**所以下面用的热端温度是片0管根的下界，不是它本身。**
    （140 那一档若最坏片真是 HC1|HC2，片0 的管根只会比 1145.1 更高。）
"""
import math, sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import *

T_TAB, T_INS, I_ENTRY = 2.03, 5.1, 1214.0

# 探针 r48_P（合并树 r48_M 的代码）L140/L170/L200/L230 逐档表，舌保温 5.1 那一行
# 列：管根低于热偶读数 K、入口片舌板焦耳热 W、入口片进铜排夹 W、入口片管孔净流入 W（= FlangeOut.QFromTubeW）
PROBE = {140: dict(cold=+4.922,   gen=362.110, clamp=191.526, qtube=+1.601),
         170: dict(cold=-69.808,  gen=552.516, clamp=203.500, qtube=-30.016),
         200: dict(cold=-108.957, gen=753.466, clamp=211.004, qtube=-47.049),
         230: dict(cold=-128.527, gen=958.987, clamp=215.114, qtube=-55.617)}
TC0 = 1150.0          # 片0 热偶基准（L 树第 3 轮「片0 基准 1150.0」）
T_OLD = 1146.35       # 首版四个长度共用的那一个（= 1150 − 3.65，L 树片0 冷侧）

print("═══ A5　热端边界逐长度取自己的（把关意见 ②）═══")
print(f"圆盘保温 = {DISC_INS} mm（DesignSpec.cs:181/205/949）；舌保温 {T_INS} mm；夹头 450 °C 定温\n")
print("热端管根温度（片0 下界）= 1150 − 探针那一列：")
for L in (140,170,200,230):
    print(f"  L{L}: 管根低于热偶读数 {PROBE[L]['cold']:+8.3f} K ⇒ 片0 管根 ≥ {TC0-PROBE[L]['cold']:8.2f} °C")

hdr = (f"\n{'舌长':>5}{'热端°C':>9} | {'焦耳模型':>9}{'焦耳实测':>9}{'差%':>7} | "
       f"{'进夹模型':>9}{'进夹实测':>9}{'差%':>7} | {'散热模型':>9}{'热端流入':>10}{'Tmax':>9}{'x(Tmax)':>9}")
print("── (1) 各用自己的热端（本次修订的口径）")
print(hdr); print("─"*116)
new = {}
for L in (140,170,200,230):
    Th = TC0 - PROBE[L]['cold']
    a,gc,lc = cap_zone(T_INS, Th, T_TAB, I_ENTRY)
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, T_INS, T_hot=Th, N=1500)
    gz, lz = r['gen_zone']+gc, r['loss_zone']+lc
    m = PROBE[L]; new[L]=(gz, r['Q_clamp'], lz, r['Q_hot'], r['Tmax'], r['xTmax'], Th)
    print(f"{L:>5}{Th:>9.2f} | {gz:>9.1f}{m['gen']:>9.1f}{100*(gz/m['gen']-1):>+7.1f} | "
          f"{r['Q_clamp']:>9.1f}{m['clamp']:>9.1f}{100*(r['Q_clamp']/m['clamp']-1):>+7.1f} | "
          f"{lz:>9.1f}{r['Q_hot']:>10.2f}{r['Tmax']:>9.1f}{r['xTmax']:>9.1f}")

print("\n── (2) 首版的口径（四个长度共用 1146.35 °C）—— 供对照，看差是怎么被凑齐的")
print(hdr); print("─"*116)
old = {}
for L in (140,170,200,230):
    a,gc,lc = cap_zone(T_INS, T_OLD, T_TAB, I_ENTRY)
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, T_INS, T_hot=T_OLD, N=1500)
    gz, lz = r['gen_zone']+gc, r['loss_zone']+lc
    m = PROBE[L]; old[L]=(gz, r['Q_clamp'])
    print(f"{L:>5}{T_OLD:>9.2f} | {gz:>9.1f}{m['gen']:>9.1f}{100*(gz/m['gen']-1):>+7.1f} | "
          f"{r['Q_clamp']:>9.1f}{m['clamp']:>9.1f}{100*(r['Q_clamp']/m['clamp']-1):>+7.1f} | "
          f"{lz:>9.1f}{r['Q_hot']:>10.2f}{r['Tmax']:>9.1f}{r['xTmax']:>9.1f}")

print("\n── (3) 两种口径的偏差对照（焦耳热）")
print(f"{'舌长':>5}{'各用自己 差%':>14}{'共用1146 差%':>14}")
for L in (140,170,200,230):
    m = PROBE[L]['gen']
    print(f"{L:>5}{100*(new[L][0]/m-1):>+14.1f}{100*(old[L][0]/m-1):>+14.1f}")

print("\n── (4) 舌板均温差反算（TCR = d(lnρ)/dT，**在该长度自己的面积均温上取**，不是在 1150 上取）")
def tcr_at(T): return (ALPHA_FIT + 2*BETA_FIT*T)/(1.0 + ALPHA_FIT*T + BETA_FIT*T*T)
print(f"  参考：d(lnρ)/dT @900 °C = {tcr_at(900.0):.3e}　@1150 °C = {tcr_at(1150.0):.3e} /K")
for L in (140,170,200,230):
    d = new[L][0]/PROBE[L]['gen'] - 1.0
    Th = new[L][6]
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, T_INS, T_hot=Th, N=1500)
    tc = tcr_at(r['Tmean_area'])
    print(f"  L{L}: 面积均温 {r['Tmean_area']:7.1f} °C　TCR {tc:.3e} /K　焦耳差 {100*d:+6.1f} % "
          f"⇒ 模型均温比 APP 高约 {d/tc:+6.1f} K")
