# -*- coding: utf-8 -*-
"""A2 窗口的闭式：净流入对舌保温的导数、窗口宽度的标度律、为什么加长把窗口关掉。"""
import math, sys, io, os
import numpy as np
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tab1d import *

T_TAB, I_ENTRY = 2.03, 1214.0
# ★ 2026-09-17 修订（把关意见 ②）：热端**逐长度**取自己的 —— 片0 基准 1150 减探针那一档的
#   「管根低于热偶读数」（= 片0 管根的下界，推导见 run_a5_hotend.py 文件头）。
COLD = {140:+4.922, 170:-69.808, 200:-108.957, 230:-128.527}   # 探针逐档表 5.1 那一行
def T_hot_of(L):
    # 有探针实测的长度用自己的；没有实测的（下面第 (5) 节扫临界长度）钉在 L140 那一档，
    # 因为那一节问的正是「管根按住不动时，多长会冒出内部峰」—— 是个定温的思想实验，不是对拍。
    return 1150.0 - COLD[L] if L in COLD else 1150.0 - COLD[140]
T_ROOT = T_hot_of(140)          # 1145.078 —— L140 单独一档时用它

def run(L, ins, N=1200):
    return solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, ins, DISC_INS, T_hot=T_hot_of(L), N=N)

print("═══ A2　窗口的闭式 ═══\n")
print("── (1) 净流入对舌保温的导数（模型），L = 140 mm")
print(f"{'舌保温mm':>8}{'热端流入W':>11}{'焦耳W':>9}{'舌散热W':>9}{'进夹W':>9}{'Tmax':>8}{'x(Tmax)':>9}")
ins_list = [3.0,3.5,4.0,4.5,5.0,5.1,5.2,5.5,6.0,6.5,7.0,7.5,8.0]
qs = []
for ins in ins_list:
    r = run(140, ins); a,g,l = cap_zone(ins, T_ROOT, T_TAB, I_ENTRY)
    qs.append((ins, r['Q_hot'], r['gen_zone']+g, r['loss_zone']+l, r['Q_clamp'], r['Tmax'], r['xTmax']))
    print(f"{ins:>8.1f}{r['Q_hot']:>11.2f}{r['gen_zone']+g:>9.1f}{r['loss_zone']+l:>9.1f}"
          f"{r['Q_clamp']:>9.1f}{r['Tmax']:>8.1f}{r['xTmax']:>9.1f}")
ins_a = np.array([q[0] for q in qs]); qh = np.array([q[1] for q in qs])
sl = np.polyfit(ins_a, qh, 1)[0]
print(f"\n模型最小二乘斜率 d(热端流入)/d(舌保温) = {sl:+.3f} W/mm")
print("实测（探针 L140 逐档表，51 档最小二乘）d(管孔净流入)/d(舌保温) = −8.159 W/mm")
print("实测（同表，由 Q夹 − 焦耳 + 散热 反算的热端流入）:")
mm = {3.0:(347.3,215.522,164.065),3.5:(351.433,206.067,171.632),4.0:(355.147,197.493,178.488),
      4.5:(358.508,189.669,184.735),5.0:(361.511,182.476,190.430),5.5:(364.3,175.87,195.688),
      6.0:(366.812,169.745,200.519),6.5:(369.144,164.069,205.004),7.0:(371.289,158.783,209.167),
      7.5:(373.268,153.846,213.042),8.0:(375.134,149.233,216.677)}
mk = sorted(mm); mq = [mm[k][2]-mm[k][0]+mm[k][1] for k in mk]
print("  " + "  ".join(f"{k}:{v:+.2f}" for k,v in zip(mk,mq)))
print(f"  实测最小二乘斜率 = {np.polyfit(np.array(mk), np.array(mq),1)[0]:+.3f} W/mm")

print("\n── (2) 标度律：斜率随舌长怎么走（模型 vs 实测）")
print(f"{'舌长':>5}{'模型 dQ/dt':>12}{'实测 dQ/dt':>12}{'模型 dQ夹/dt':>13}{'模型 d散热/dt':>14}{'模型 Tmax@5.1':>14}")
meas_slope = {140:-8.159, 170:-11.6, 200:-15.2, 230:-18.3}   # 汇总文件：140 −8.2 → 230 −18.3
for L in (140,170,200,230):
    a1 = run(L, 4.6); a2 = run(L, 5.6)
    c1 = cap_zone(4.6, T_hot_of(L), T_TAB, I_ENTRY); c2 = cap_zone(5.6, T_hot_of(L), T_TAB, I_ENTRY)
    dq  = (a2['Q_hot']-a1['Q_hot'])/1.0
    dqc = (a2['Q_clamp']-a1['Q_clamp'])/1.0
    dl  = ((a2['loss_zone']+c2[2])-(a1['loss_zone']+c1[2]))/1.0
    r51 = run(L, 5.1)
    print(f"{L:>5}{dq:>12.2f}{meas_slope[L]:>12.2f}{dqc:>13.2f}{dl:>14.2f}{r51['Tmax']:>14.1f}")
print("（实测 170/200/230 的斜率取自汇总文件那一句「−8.2（140）→ −18.3 W/mm（230）」；"
      "170/200 是我按那句话的两端线性插的，标为**推定**，不是实测逐档拟合）")

print("\n── (3) 窗口宽度的闭式（用实测的两条判据斜率 + 模型的物理解释）")
print("窗口 = {t : 管根低于热偶读数 f(t) ≤ 5 K  且  最热铂高出热偶读数 g(t) ≤ 5 K}")
print("  宽度 Δt = (5 − f(t*))/|f'| + (5 − g(t*))/g'   （在任一可行点 t* 上展开）")
f = {5.0:5.692,5.1:4.922,5.2:4.528}; g = {5.0:2.157,5.1:4.529,5.2:6.978}
fp = (f[5.1]-f[5.0])/0.1; gp = (g[5.2]-g[5.1])/0.1
print(f"  实测 f' = ({f[5.1]}−{f[5.0]})/0.1 = {fp:+.1f} K/mm；g' = ({g[5.2]}−{g[5.1]})/0.1 = {gp:+.1f} K/mm")
print(f"  在 t* = 5.1：Δt = (5−{f[5.1]})/{abs(fp):.1f} + (5−{g[5.1]})/{gp:.1f} "
      f"= {(5-f[5.1])/abs(fp):.4f} + {(5-g[5.1])/gp:.4f} = {(5-f[5.1])/abs(fp)+(5-g[5.1])/gp:.4f} mm")
print("  实测窗口（0.1 步进）[5.1, 5.1] 宽 0.1 mm，真实宽度 ≤ 0.2；拓扑线基线 0.14 mm —— 三者同一量级")

print("\n── (4) 为什么加长把窗口关掉（模型的账）")
print(f"{'舌长':>5}{'焦耳W':>9}{'进夹W':>9}{'焦耳−进夹':>11}{'舌散热W':>10}{'热端流入W':>11}{'kA/域长 W/K':>13}")
for L in (140,170,200,230):
    r = run(L, 5.1); a,gc,lc = cap_zone(5.1, T_hot_of(L), T_TAB, I_ENTRY)
    dom = L-CLAMP_L-R_HOLE
    print(f"{L:>5}{r['gen_zone']+gc:>9.1f}{r['Q_clamp']:>9.1f}{r['gen_zone']+gc-r['Q_clamp']:>11.1f}"
          f"{r['loss_zone']+lc:>10.1f}{r['Q_hot']:>11.2f}{pt_k(T_SET)*1e-3*W_TAB*T_TAB/dom:>13.4f}")
print("闭式：截面 A = I/J 被规则钉死、与长度无关 ⇒ 焦耳热 ∝ L（模型与实测都逐位对得上）；")
print("      而夹头带热能力 ≈ kA/ℓ_热（ℓ_热 = 峰到压接段的距离，被表面散热钉在一个有限值上，不随 L 长）")
print("      ⇒ 多出来的 I²ρL/A 只能靠抬温去散掉，温度升，热端反灌。")

print("\n── (5) 舌板出现内部温度峰的临界长度（模型；**这一节热端一律钉在 L140 那一档 1145.08 °C**，")
print("        因为问的是「管根按住不动时，加长到多少会冒出内部峰」—— 定温的思想实验，不是对拍）")
for L in (140,145,150,155,160,170,180):
    r = solve_tab(-R_HOLE, -(L-CLAMP_L), T_TAB, I_ENTRY, 5.1, DISC_INS, T_hot=T_ROOT, N=1200)
    print(f"  L={L}: Tmax {r['Tmax']:8.1f} °C 在 x = {r['xTmax']:7.1f} mm"
          + ("（峰在热端 = 没有内部峰）" if abs(r['xTmax']+R_HOLE) < 0.2 else "（**内部峰**）"))
