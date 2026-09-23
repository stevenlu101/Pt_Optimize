# -*- coding: utf-8 -*-
"""A2 补：舌板的平衡温度 T_eq（局部焦耳热 = 局部表面散热）与热边界层长度。"""
import math, sys, io, os
import numpy as np
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import *

def t_eq(t_tab, I_A, ins, lo=200.0, hi=2500.0):
    """gen'(T) = loss'(T) 的根：ρ(T)·J²·t·w = 2·q''(T)·w"""
    J = I_A/(W_TAB*t_tab)
    f = lambda T: pt_rho(T)*1e3*J*J*t_tab*W_TAB - 2*plate_flux_w_per_m2(T, ins)*1e-6*W_TAB
    for _ in range(200):
        m = 0.5*(lo+hi)
        if f(m) > 0: lo = m
        else: hi = m
    return 0.5*(lo+hi)

print("═══ A2 补　舌板的平衡温度 T_eq：局部焦耳热 = 局部表面散热 ═══")
print("  闭式：ρ(T)·J²·t·w = 2·q''(T,t_ins)·w  ⇒  ρ(T)·J² ·t = 2·q''(T,t_ins)")
print("  意义：舌板足够长时，中段既不靠导热也不靠端头，就停在这个温度上。")
print("        ⇒ **进铜排夹的热与舌长无关**（只由压接端那一段边界层决定），而焦耳热 ∝ 舌长。")
print("        ⇒ 多出来的焦耳热没有出口，只能从热端灌回管子。这就是加长关窗口的闭式理由。\n")
print(f"{'舌片厚':>7}{'J A/mm²':>9}{'舌保温mm':>10}{'T_eq °C':>10}{'相对热偶 1150':>14}")
for tt, Ia in ((2.03, 1214.0),):
    for ins in (2.0, 3.0, 4.0, 5.0, 5.1, 6.0, 7.0, 8.0):
        T = t_eq(tt, Ia, ins)
        print(f"{tt:>7.2f}{Ia/(W_TAB*tt):>9.3f}{ins:>10.1f}{T:>10.1f}{T-1150:>+14.1f}")
print("\n  舌片厚按 J=11 的理论下限 1.84 mm：")
for ins in (3.0, 3.75, 5.0):
    T = t_eq(1.84, 1214.0, ins)
    print(f"{1.84:>7.2f}{1214.0/(W_TAB*1.84):>9.3f}{ins:>10.2f}{T:>10.1f}{T-1150:>+14.1f}")

# 让 T_eq = 1155（热偶 + 5）的舌保温
lo, hi = 0.5, 12.0
for _ in range(80):
    m = 0.5*(lo+hi)
    if t_eq(2.03, 1214.0, m) < 1155.0: lo = m
    else: hi = m
print(f"\n  ⇒ 令 T_eq = 1155 °C（热偶 1150 + 限值 5 K）的舌保温 = {0.5*(lo+hi):.2f} mm（舌片厚 2.03）")
lo, hi = 0.5, 12.0
for _ in range(80):
    m = 0.5*(lo+hi)
    if t_eq(1.84, 1214.0, m) < 1155.0: lo = m
    else: hi = m
print(f"     同一条件下舌片厚 1.84（J=11 下限）⇒ 舌保温 = {0.5*(lo+hi):.2f} mm")
print("     **这是一条与舌长无关的硬上界**：舌保温超过它，只要舌板够长，最热铂一定超 5 K。")
print(f"     求解器解出来的入口片舌保温是 5.1 mm，已经在这条线的上方 ⇒ 140 mm 能过，是因为**舌板还不够长**、没走到 T_eq。")

print("\n── 热边界层长度（模型自量：从压接端到 T 升到 T_eq 的 90 % 处）")
for L in (140, 170, 200, 230, 300):
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), 2.03, 1214.0, 5.1, DISC_INS, T_hot=1146.35, N=1200)
    Te = t_eq(2.03, 1214.0, 5.1)
    tgt = 450 + 0.9*(Te-450)
    i = int(np.argmax(r['T'] >= tgt)) if (r['T'] >= tgt).any() else -1
    d = (r['x'][i] - r['x'][0]) if i >= 0 else float('nan')
    print(f"  L={L:3d}  T_eq={Te:7.1f}  Tmax={r['Tmax']:7.1f}  进夹={r['Q_clamp']:6.1f} W  "
          f"边界层长≈{d:6.1f} mm" + ("" if i >= 0 else "（还没走到 0.9·T_eq）"))
