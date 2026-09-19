# -*- coding: utf-8 -*-
r"""A1 对拍：舌板一维模型 vs 舌长探针（四个舌长），入口片，带玻璃稳态。

⚠ 2026-09-17 修订（把关意见 ②）：本文件四个舌长**共用一个热端 1146.35 °C**，那是错的口径 ——
   170/200/230 的管根实测比热偶读数高出 69.8/109.0/128.5 K。**四个长度各用自己的热端**那一版在
   run_a5_hotend.py。本文件保留，只作「共用热端会把差凑成什么样」的对照，结论不引它。
   （圆盘保温已随 tab1d.DISC_INS 改成 20 mm。）
"""
import math, sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import *

# ── 入口片（片 0）终值。出处：R48_L_端到端_求解再过三关_W08_本次开跑于2026-09-17_012720.txt
T_TAB, T_PLATE, T_INS = 2.03, 0.73, 5.1
I_ENTRY = 1214.0          # 设计电流（段 1214 A）；带玻璃稳态 管J 9.506 × 127.67 mm² = 1213.6 A
T_ROOT  = 1146.35         # 1150 − 3.65（第 3 轮「片0 基准 1150.0 … 冷侧 +3.65 K」）

# ── 探针实测。出处：R48_P_舌长窗口探针_汇总_本次开跑于2026-09-17_114350.txt（一律舌保温 5.1 档）
MEAS = {140: dict(gen=362.110, clamp=191.526, draw=+1.601),
        170: dict(gen=552.5,   clamp=203.5,   draw=-30.0),
        200: dict(gen=753.5,   clamp=211.0,   draw=-47.0),
        230: dict(gen=959.0,   clamp=215.1,   draw=-55.6)}
MEAS50 = dict(gen=361.511, loss=182.476, clamp=190.430, draw=2.461)   # L140 逐档表 5.0 档

print("═══ A1　舌板一维电流引线模型 vs 探针实测（入口片，舌保温 5.1 mm，夹头 450 °C 定温）═══")
print(f"舌宽 {W_TAB:.0f} mm　舌片厚 {T_TAB} mm　截面 {W_TAB*T_TAB:.1f} mm²　"
      f"J = {I_ENTRY/(W_TAB*T_TAB):.3f} A/mm²　k = PtThermalK(1150) = {pt_k(T_SET):.2f} W/(m·K)")
area_cap, gen_cap, loss_cap = cap_zone(T_INS, T_ROOT, T_TAB, I_ENTRY)
print(f"帽子区（−25.8 < x ≤ 0 且 r > 30，管孔按住）面积 {area_cap:.1f} mm²，按 T = {T_ROOT} °C："
      f"焦耳 {gen_cap:.1f} W　散热 {loss_cap:.1f} W")
print(f"域：x ∈ [−(舌长−40), −25.8]（热端 = 管孔最左点，定温 {T_ROOT} °C）\n")

hdr = (f"{'舌长':>4}{'域长':>7}{'Tmax':>8}{'x(Tmax)':>9}{'面积均温':>9} | "
       f"{'焦耳模型':>9}{'焦耳实测':>9}{'差%':>7} | {'进夹模型':>9}{'进夹实测':>9}{'差%':>7} | "
       f"{'散热模型':>9}{'热端流入':>9}{'残差':>10}")
print(hdr); print("─"*len(hdr))
rows = {}
for L in (140, 170, 200, 230):
    xc = -(L - CLAMP_L)
    r = solve_tab(-R_HOLE, xc, T_TAB, I_ENTRY, T_INS, DISC_INS, T_hot=T_ROOT, N=1500)
    gz, lz = r['gen_zone'] + gen_cap, r['loss_zone'] + loss_cap
    m = MEAS[L]; rows[L] = (r, gz, lz)
    print(f"{L:>4}{xc:>7.0f}{r['Tmax']:>8.1f}{r['xTmax']:>9.1f}{r['Tmean_area']:>9.1f} | "
          f"{gz:>9.1f}{m['gen']:>9.1f}{100*(gz/m['gen']-1):>+7.1f} | "
          f"{r['Q_clamp']:>9.1f}{m['clamp']:>9.1f}{100*(r['Q_clamp']/m['clamp']-1):>+7.1f} | "
          f"{lz:>9.1f}{r['Q_hot']:>9.2f}{r['residual']:>10.2e}")

print(f"\nL140 舌板表面散热（r>30 分区）：模型 {rows[140][2]:.1f} W vs 实测 5.0 档 {MEAS50['loss']:.1f} W")
print("（实测的 5.1 档逐档表只给焦耳/进夹/净流入三列，散热列只有 0.5 整档，故拿 5.0 档比）")

print("\n── 等效夹头带热能力（模型自算，不是拟合）")
for L in (140, 170, 200, 230):
    r = rows[L][0]; dT = r['Tmean_area'] - T_CLAMP
    kAoverL = pt_k(T_SET)*1e-3*W_TAB*T_TAB/(L-CLAMP_L-R_HOLE)
    print(f"  L{L}: Q夹/(面积均温−450) = {r['Q_clamp']:.1f}/{dT:.1f} = {r['Q_clamp']/dT:.3f} W/K；"
          f"kA/域长 = {kAoverL:.4f} W/K；kA = {pt_k(T_SET)*1e-3*W_TAB*T_TAB:.4f} W·mm/K")

print("\n── L140 沿程 T(x)（模型）")
r = rows[140][0]; N = len(r['x'])-1; st = N//12
print("  x mm:", " ".join(f"{r['x'][i]:7.1f}" for i in range(0, N+1, st)))
print("  T °C:", " ".join(f"{r['T'][i]:7.1f}" for i in range(0, N+1, st)))
print(f"  J = {r['J']:.3f} A/mm² 沿程不变（等宽等厚，与 APP 同口径）")

# ── 热端 Dirichlet 的敏感度（T_hot 上下各 10 K）
print("\n── 热端定温 T_hot 的敏感度（L140）")
for th in (T_ROOT-10, T_ROOT, T_ROOT+10):
    r = solve_tab(-R_HOLE, -100.0, T_TAB, I_ENTRY, T_INS, DISC_INS, T_hot=th, N=1500)
    print(f"  T_hot {th:7.2f} °C → 焦耳(分区+帽) {r['gen_zone']+gen_cap:7.1f} W　进夹 {r['Q_clamp']:7.1f} W　"
          f"热端流入 {r['Q_hot']:+7.2f} W　Tmax {r['Tmax']:.1f}")

# ── 网格无关（L140）
print("\n── 网格无关（L140，模型自身）")
for n in (400, 800, 1500, 3000):
    r = solve_tab(-R_HOLE, -100.0, T_TAB, I_ENTRY, T_INS, DISC_INS, T_hot=T_ROOT, N=n)
    print(f"  N={n:5d}  焦耳 {r['gen_zone']+gen_cap:8.3f}  进夹 {r['Q_clamp']:8.3f}  热端 {r['Q_hot']:+7.3f}  Tmax {r['Tmax']:8.3f}")

# ── k 取局部 k(T) 而非 k(TSet)
print("\n── k 的口径（L140）")
for km in ("tset", "local"):
    r = solve_tab(-R_HOLE, -100.0, T_TAB, I_ENTRY, T_INS, DISC_INS, T_hot=T_ROOT, N=1500, kmode=km)
    print(f"  k={km:5s}  焦耳 {r['gen_zone']+gen_cap:8.2f}  进夹 {r['Q_clamp']:8.2f}  热端 {r['Q_hot']:+7.2f}  Tmax {r['Tmax']:8.2f}")
