# -*- coding: utf-8 -*-
r"""
A 线：解析交叉验证 —— 舌板一维电流引线模型（闭式/一维，独立于 APP 的二维壳求解器）
2026-09-17  Opus 5

物性与散热配方**逐字**照 D:\WinForm\r48_M\Pt_Optimize\Core 下的生产代码：
  Materials.cs         行 18-20 (RhoRef/AlphaFit/BetaFit)、行 33-34 PtResistivity、行 45 PtThermalK
                       行 69-82 空气物性、行 103-114 HConvVertical、行 138-143 HRad
  Insulation.cs        行 173-213 PlateFlux（单层 + 外表面）、行 215-223 FlatOuterFlux、行 36 KAt
  DesignScreen.cs      行 169-182 PlateFluxWPerM2（法兰表面热流唯一配方）、行 155-159 FlangeFaceInsulated
  DesignInputs.cs      行 122 TAmbC=25、273 PtEmissivity=.18、277 OuterEmissivity=.45、289 LossScale=1
                       296-297 Layer1(k0=.04,k1=3e-4)、336 风速 0、351 ConvCharLenM=.05
  DesignSpec.cs        行 181 FlangeInsulMm = 20.0（**算例里真正用的圆盘保温**）、行 205 WholeLineDiscInsulMm、
                       行 949 BuildCase: p.FlangeInsulThickMm = WholeLineDiscInsulMm
                       ⚠ 2026-09-17 修订（把关意见 ①）：首版照抄了 DesignInputs.cs:332 的属性默认值 2.5 mm，
                          **那一行在 BuildCase 里被 DesignSpec 覆写**（DesignInputs.cs:332 的说明自己写着「本项被③页控件接管」）。
                          W08 算例的圆盘保温是 **20.0 mm**。本文件的 DISC_INS 即此值。
  ShellThermal.cs      行 512-564 保温边界按半径 r ≤ 盘半径、行 1042-1043 分区账 r ≤ 盘半径
                       行 981-992 QToClampW（定温舌端）、行 360-363 ClampBoundaryOf（定温 450 °C）
  PlateThermal2D.cs    行 78-80 k 取 PtThermalK(TSet) 常数、行 9 控制方程
"""
import math
import numpy as np

# ───────────────────────── 物性（Materials.cs 逐字） ─────────────────────────
RHO_REF   = 9.83e-8                    # Materials.cs:18
ALPHA_FIT = 0.0039678411655333         # Materials.cs:19
BETA_FIT  = -5.849309909955442e-07     # Materials.cs:20
SIGMA     = 5.670374e-8                # Materials.cs:82
AIR_PR    = 0.71                       # Materials.cs:80
PT_DENSITY_G_MM3 = 21450.0 * 1e-6      # Materials.cs:13 → g/mm³ = 0.02145
PT_FIT_MAX_C = 1500.0                  # Materials.cs:31

def pt_rho(t):                         # Ω·m   Materials.cs:33-34
    return RHO_REF * (1.0 + ALPHA_FIT * t + BETA_FIT * t * t)

def pt_k(t):                           # W/(m·K)  Materials.cs:45
    return 71.6 + 0.0106 * t

def air_k(tK):  return 1.5207e-11*tK**3 - 4.8574e-8*tK**2 + 1.0184e-4*tK - 3.9333e-4
def air_mu(tK): return 1.458e-6 * tK**1.5 / (tK + 110.4)
def air_rho(tK):return 101325.0 / (287.05 * tK)
def air_nu(tK): return air_mu(tK) / air_rho(tK)

def h_conv_vertical(tS, tA, l):        # Materials.cs:103-114（Churchill–Chu 竖板）
    dT = tS - tA
    if dT <= 0.01 or l <= 0: return 0.0
    tf = (tS + tA) * 0.5 + 273.15
    nu = air_nu(tf); k = air_k(tf); alpha = nu / AIR_PR
    ra = 9.81 * (1.0/tf) * dT * l**3 / (nu * alpha)
    if ra < 1e-3: return 0.0
    den = (1.0 + (0.492/AIR_PR)**(9.0/16.0))**(8.0/27.0)
    s = 0.825 + 0.387 * ra**(1.0/6.0) / den
    return s * s * k / l

def h_rad(eps, tS, tA):                # Materials.cs:138-143
    dT = tS - tA
    if abs(dT) < 1e-6: return 0.0
    return eps * SIGMA * ((tS+273.15)**4 - (tA+273.15)**4) / dT

# ───────────────────── 表面热流配方（Insulation / DesignScreen） ─────────────────────
T_AMB, EPS_PT, EPS_OUT, LOSS_SCALE = 25.0, 0.18, 0.45, 1.0
LAY_K0, LAY_K1, CHAR_LEN, INSUL_MIN = 0.04, 3.0e-4, 0.05, 0.05

def _outer_flux(tSurf, eps):           # Insulation.cs:215-223（风速 0 ⇒ HConvMixed = h_nat）
    return max(1e-6, LOSS_SCALE) * (h_rad(eps, tSurf, T_AMB)
                                    + h_conv_vertical(tSurf, T_AMB, max(0.02, CHAR_LEN))) * (tSurf - T_AMB)

def plate_flux_w_per_m2(tC, insul_mm):
    """DesignScreen.PlateFluxWPerM2（DesignScreen.cs:169-182），单面 W/m²"""
    if tC <= T_AMB: return 0.0
    if insul_mm != insul_mm or insul_mm < INSUL_MIN:      # NaN 或 < 0.05 ⇒ 裸铂
        return _outer_flux(tC, EPS_PT)
    lo, hi = T_AMB, tC
    for _ in range(80):                                   # Insulation.cs:202 同样 80 次二分
        tOut = 0.5*(lo+hi)
        kIns = max(1e-4, LAY_K0 + LAY_K1*(tC + (tOut-tC)*0.5))   # frac = 0.5（单层）
        sumR = (insul_mm*1e-3)/(kIns*LOSS_SCALE)
        if (tC-tOut)/sumR > _outer_flux(tOut, EPS_OUT): lo = tOut
        else: hi = tOut
    tOut = 0.5*(lo+hi)
    kIns = max(1e-4, LAY_K0 + LAY_K1*(tC + (tOut-tC)*0.5))
    return max(0.0, (tC-tOut)/((insul_mm*1e-3)/(kIns*LOSS_SCALE)))

class FluxTab:
    """q''(T) 插值表 —— 生产里是 LossTable（ShellThermal.LossTableHiC / LossTableNodes），此处等价"""
    def __init__(self, insul_mm, tHi=1900.0, n=750):
        self.t0, self.t1, self.n = T_AMB, tHi, n
        self.h = (tHi - T_AMB)/n
        self.T = np.array([T_AMB + i*self.h for i in range(n+1)])
        self.v = np.array([plate_flux_w_per_m2(t, insul_mm) for t in self.T])
        self.d = np.diff(self.v)/self.h
    def val(self, t):
        u = np.clip((np.asarray(t)-self.t0)/self.h, 0, self.n-1e-9)
        i = u.astype(int); f = u - i
        return self.v[i]*(1-f) + self.v[i+1]*f
    def slope(self, t):
        u = np.clip((np.asarray(t)-self.t0)/self.h, 0, self.n-1e-9)
        return self.d[u.astype(int)]

# ───────────────────────── 几何（DesignSpec.W08） ─────────────────────────
R_DISC, R_HOLE, W_TAB, CLAMP_L = 30.0, 25.8, 60.0, 40.0
T_CLAMP, T_SET = 450.0, 1150.0

def w_outside_disc(x):
    """舌带上 r > 盘半径 的宽度（mm）—— 分区账与保温都按这个圆分（ShellThermal.cs:535,1088）"""
    ax = np.abs(x)
    return np.where(ax >= R_DISC, W_TAB, W_TAB - 2.0*np.sqrt(np.maximum(0.0, R_DISC**2 - ax**2)))

_TABCACHE = {}
def _tab(ins):
    if ins not in _TABCACHE: _TABCACHE[ins] = FluxTab(ins)
    return _TABCACHE[ins]

# ───────────────────────── 一维 BVP（牛顿 + 三对角） ─────────────────────────
DISC_INS = 20.0   # 圆盘（法兰）保温 mm —— DesignSpec.cs:181 FlangeInsulMm=20.0 → :205 → :949 写进 DesignInputs.FlangeInsulThickMm

def solve_tab(x_hot, x_clamp, t_tab, I_A, ins_tab, ins_disc=DISC_INS,
              T_hot=1146.35, T_clamp=T_CLAMP, N=1200, kmode="tset"):
    """
    域 x ∈ [x_clamp, x_hot]（mm，x_clamp < x_hot ≤ 0；x=0 是舌带与圆盘的切点，管轴在原点）
        d/dx(kA dT/dx) + ρ(T)·J²·t·w_mat − 2[q''_舌保温(T)·w_out + q''_法兰保温(T)·w_in] = 0
    边界：T(x_clamp) = T_clamp（压接段定温，ShellThermal 定温舌端）
          T(x_hot)   = T_hot  （管孔把这一截按住）
    """
    L = x_hot - x_clamp
    h = L/N
    x = x_clamp + h*np.arange(N+1)
    A = W_TAB * t_tab                       # mm²，等宽等厚 ⇒ J 沿程不变（与 APP 逐位同口径）
    J = I_A/A
    kPt = pt_k(T_SET) if kmode == "tset" else None
    fd = _tab(ins_disc)
    # ins_tab 可以是常数，也可以是 x 的函数（保温剖面）—— 逐节点取值，按取到的几种厚度各建一张表
    if callable(ins_tab):
        ins_arr = np.array([ins_tab(v) for v in x])
        uniq = sorted(set(np.round(ins_arr, 6)))
        tabs = {u: _tab(float(u)) for u in uniq}
        idx = np.round(ins_arr, 6)
        def ft_val(T, sl):
            out = np.empty_like(np.asarray(T, dtype=float))
            for u in uniq:
                m = (idx[1:-1] == u) if T.size == x.size-2 else (idx == u)
                if m.any(): out[m] = (tabs[u].slope(T[m]) if sl else tabs[u].val(T[m]))
            return out
        class _P:
            def val(s, T): return ft_val(T, False)
            def slope(s, T): return ft_val(T, True)
        ft = _P()
    else:
        ft = _tab(ins_tab)
    wo = w_outside_disc(x); wi = W_TAB - wo
    # 有料宽度：|x| < R_HOLE 时中间是管孔
    wmat = np.where(np.abs(x) >= R_HOLE, W_TAB,
                    W_TAB - 2.0*np.sqrt(np.maximum(0.0, R_HOLE**2 - x**2)))
    T = T_clamp + (T_hot - T_clamp)*(x - x_clamp)/L

    # 牛顿 + 三对角直解（Jacobi 在细网格上收敛太慢，实测 400 步只走 0.2 K）
    from scipy.linalg import solve_banded
    kc = (kPt if kPt is not None else pt_k(T_SET))*1e-3*A       # W·mm/K（k 常数口径）
    for _ in range(200):
        Tin = T[1:-1]
        c = kc if kPt is not None else (pt_k(Tin)*1e-3*A)
        q0 = 2.0*(ft.val(Tin)*1e-6*wo[1:-1] + fd.val(Tin)*1e-6*wi[1:-1])
        qp = 2.0*(ft.slope(Tin)*1e-6*wo[1:-1] + fd.slope(Tin)*1e-6*wi[1:-1])
        qg = pt_rho(Tin)*1e3 * J*J * t_tab * wmat[1:-1]
        qgp = RHO_REF*(ALPHA_FIT + 2*BETA_FIT*Tin)*1e3 * J*J * t_tab * wmat[1:-1]
        R = c/(h*h)*(T[:-2] - 2*Tin + T[2:]) + qg - q0
        lo_d = np.full(Tin.size, 1.0)*(c/(h*h))
        di_d = -2*(c/(h*h)) + qgp - qp
        up_d = np.full(Tin.size, 1.0)*(c/(h*h))
        ab = np.zeros((3, Tin.size))
        ab[0, 1:] = np.atleast_1d(up_d)[:-1] if np.ndim(up_d) else up_d
        ab[1, :]  = di_d
        ab[2, :-1] = np.atleast_1d(lo_d)[1:] if np.ndim(lo_d) else lo_d
        delta = solve_banded((1, 1), ab, -R)
        step = np.clip(delta, -200.0, 200.0)
        T[1:-1] = np.clip(Tin + step, T_AMB, 3000.0)
        if np.max(np.abs(step)) < 1e-9: break

    wq = np.full(N+1, h); wq[0] = wq[-1] = h*0.5              # 梯形
    rho_mm = pt_rho(T)*1e3
    g_lin  = rho_mm*J*J*t_tab*wmat                            # W/mm 全断面
    lo_lin = 2.0*ft.val(T)*1e-6*wo                            # W/mm 舌保温面（r>30）
    li_lin = 2.0*fd.val(T)*1e-6*wi                            # W/mm 法兰保温面（r≤30）
    # 分区账：r > 盘半径 的份额（有料宽度里落在 r>30 的部分 = wo，因为 r>30 处必然有料）
    g_zone = rho_mm*J*J*t_tab*wo
    kA0 = (kPt if kPt is not None else pt_k(T[0]))*1e-3*A
    kAN = (kPt if kPt is not None else pt_k(T[-1]))*1e-3*A
    Q_clamp = kA0*(-3*T[0] + 4*T[1] - T[2])/(2*h)             # >0 = 流进压接段
    Q_hot   = kAN*( 3*T[-1] - 4*T[-2] + T[-3])/(2*h)          # >0 = 从热端流进本域
    i = int(np.argmax(T))
    return dict(x=x, T=T, J=J, A=A, h=h,
                gen_all=float((g_lin*wq).sum()), loss_all=float(((lo_lin+li_lin)*wq).sum()),
                gen_zone=float((g_zone*wq).sum()), loss_zone=float((lo_lin*wq).sum()),
                Q_clamp=float(Q_clamp), Q_hot=float(Q_hot),
                Tmax=float(T[i]), xTmax=float(x[i]),
                Tmean=float(T[1:-1].mean()), Tmean_area=float((T*wmat*wq).sum()/(wmat*wq).sum()),
                residual=float((g_lin*wq).sum() + Q_hot - ((lo_lin+li_lin)*wq).sum() - Q_clamp))

def cap_zone(t_ins, T_root, t_tab, I_A):
    """x ∈ (−R_HOLE, 0] 里 r > 盘半径 的那一片（管孔把它按在 T_root 附近）：面积、焦耳、散热"""
    a, R = R_HOLE, R_DISC
    area = 60*a - 2*(a*math.sqrt(R*R-a*a)/2 + (R*R/2)*math.asin(a/R))
    J = I_A/(W_TAB*t_tab)
    return (area,
            pt_rho(T_root)*1e3 * J*J * t_tab * area,
            2*plate_flux_w_per_m2(T_root, t_ins)*1e-6 * area)
