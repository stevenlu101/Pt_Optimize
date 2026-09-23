# P1 跳点分析（只读扫描 TSV，不改数）。用法：python3 分析_P1跳点.py <证据目录>
import sys, os, glob, math, statistics, datetime, re
d = sys.argv[1]
stamp = datetime.datetime.now().strftime('%Y-%m-%d_%H%M%S')
out_path = os.path.join(d, f'分析_跳点表_本次开跑于{stamp}.txt')
out = []
def W(s=''): out.append(s)
W(f'P1 跳点分析 开跑 {stamp.replace("_"," ")}；输入 = 本目录各支最新一份 扫描_*.tsv；判跳：|ΔQ| > 5 × 该支该例 median|ΔQ|（光滑段增量取中位数）')
def latest(v):
    fs = sorted(glob.glob(os.path.join(d, f'扫描_{v}_本次开跑于*.tsv')))
    return fs[-1] if fs else None
variants = ['V0_生产hF2','V1_生产hF1','V2_x平移0p5','V6_xz平移0p5','V3_规则A关','V4_规则B关','V5_AB都关','V7_自检With','V8_hF1规则A关']
summary = {}
for v in variants:
    f = latest(v)
    if not f: W(f'== {v}：未跑（无扫描档）'); continue
    lines = open(f, encoding='utf8').read().splitlines()
    W(f'== {v}：{os.path.basename(f)}')
    for l in lines:
        if l.startswith('# 耗时'): W('   ' + l)
    for case in (0, 1):
        xs = zs = None; rows = []
        for l in lines:
            if l.startswith(f'# 例 {case} x节点'): xs = [float(t) for t in l.split(' ')[-1].split(',')]
            elif l.startswith(f'# 例 {case} z节点'): zs = [float(t) for t in l.split(' ')[-1].split(',')]
            elif l.startswith(f'{case}\t'):
                p = l.split('\t')
                if p[-1] != 'ok': W(f'   例{case} r {p[1]} 异常：{p[-1]}'); continue
                rows.append(dict(r=float(p[1]), Q=float(p[2]), G=float(p[3]), cells=int(p[4]), A=int(p[5]), B=int(p[6]), arc=int(p[7]), hlen=float(p[8]),
                                 band=int(p[9]), bfrac=float(p[10]), barea=float(p[11]), marea=float(p[12]), cov=float(p[13]), Tm=float(p[14]), Tx=float(p[15]), Tz=float(p[16]), Ti=int(p[17])))
        if not rows: continue
        name = '盘Ø56片1' if case == 0 else 'Builtin[0]片0'
        ax = sorted(set(abs(x) for x in xs)); az = sorted(set(abs(z) for z in zs))
        noder = sorted(set(round(math.hypot(x, z), 9) for x in ax for z in az if 25.4 <= math.hypot(x, z) <= 26.6))
        nodeof = {}
        for x in ax:
            for z in az:
                rr = round(math.hypot(x, z), 9)
                if 25.4 <= rr <= 26.6: nodeof.setdefault(rr, []).append((x, z))
        dq = [rows[i+1]['Q'] - rows[i]['Q'] for i in range(len(rows)-1)]
        med = statistics.median(abs(t) for t in dq)
        thr = 5 * med
        W(f'  -- 例{case} {name}：{len(rows)} 点；median|ΔQ| {med:.5f} W，门槛 5× = {thr:.5f} W；光滑段斜率（median ΔQ / 0.005）{statistics.median(dq)/0.005:+.3f} W/mm')
        W(f'     孔附近格线 |x|∈[25.4,26.6]：{[x for x in ax if 25.4<=x<=26.6]}；|z|∈[25.4,26.6]：{[z for z in az if 25.4<=z<=26.6]}')
        W(f'     节点半径 ∈ [25.4, 26.6]（第一象限节点 (|x|,|z|)）：' + '；'.join(f'{rr:.6f} {nodeof[rr]}' for rr in noder))
        jumps = []
        for i, t in enumerate(dq):
            a, b = rows[i], rows[i+1]
            ra, rb = a['r'], b['r']
            lines_in = [c for c in set(ax) | set(az) if ra - 1e-6 <= c <= rb + 1e-6]
            nodes_in = [rr for rr in noder if ra - 1e-6 < rr <= rb + 1e-6]
            onl = [c for c in set(ax) | set(az) if abs(c - ra) < 1e-6 or abs(c - rb) < 1e-6]
            isj = abs(t) > thr
            ev = []
            if nodes_in: ev.append('节点过圆 ' + ','.join(f'{rr:.4f}{nodeof[rr][:2]}' for rr in nodes_in))
            if lines_in: ev.append('格线 ' + ','.join(f'{c:g}' for c in sorted(lines_in)) + ('（端点在格线上 <1e-6）' if onl else '（格线在区间内）'))
            if b['A'] != a['A']: ev.append(f'并格A {a["A"]}→{b["A"]}')
            if b['B'] != a['B']: ev.append(f'并格B {a["B"]}→{b["B"]}')
            if b['arc'] != a['arc']: ev.append(f'弧面 {a["arc"]}→{b["arc"]}')
            if b['cells'] != a['cells']: ev.append(f'格 {a["cells"]}→{b["cells"]}')
            if b['band'] != a['band']: ev.append(f'带格 {a["band"]}→{b["band"]}')
            rec = dict(ra=ra, rb=rb, dq=t, ev=ev, a=a, b=b, isj=isj, nodes=nodes_in, lines=lines_in)
            if isj or ev: jumps.append(rec)
        W(f'     跳点（|ΔQ| > 门槛）与结构事件（任何结构量变）并列；★ = 跳点')
        W('     区间 | ΔQ W | 事件 | 孔面总长 前→后 | 带格最小面积 前→后 mm² | 最热格 T@(x,z) 前→后')
        for j in jumps:
            a, b = j['a'], j['b']
            W(f"     {'★' if j['isj'] else ' '} {j['ra']:.3f}→{j['rb']:.3f} | {j['dq']:+.4f} | {'；'.join(j['ev']) or '无结构变化'} | {a['hlen']:.4f}→{b['hlen']:.4f} | {a['barea']:.5f}→{b['barea']:.5f} | {a['Tm']:.2f}@({a['Tx']:.1f},{a['Tz']:.1f})→{b['Tm']:.2f}@({b['Tx']:.1f},{b['Tz']:.1f})")
        # 反查：每个节点过圆事件是否伴随跳点
        W('     反查：节点半径 → 所在区间 ΔQ、是否跳')
        for rr in noder:
            if not (25.5 < rr <= 26.5): continue
            i = next((k for k in range(len(rows)-1) if rows[k]['r'] - 1e-6 < rr <= rows[k+1]['r'] + 1e-6), None)
            if i is None: continue
            W(f"       {rr:.6f} {nodeof[rr]}：区间 {rows[i]['r']:.3f}→{rows[i+1]['r']:.3f} ΔQ {dq[i]:+.4f} {'跳' if abs(dq[i])>thr else '不跳'}；并格A {rows[i]['A']}→{rows[i+1]['A']} 弧面 {rows[i]['arc']}→{rows[i+1]['arc']}")
        summary[(v, case)] = dict(thr=thr, med=med, jumps=[(j['ra'], j['rb'], j['dq'], j['ev']) for j in jumps if j['isj']],
                                   A=(min(r['A'] for r in rows), max(r['A'] for r in rows)), B=(min(r['B'] for r in rows), max(r['B'] for r in rows)))
W('')
W('== 汇总：各支各例跳点数、并格数范围')
for (v, case), s in summary.items():
    W(f'  {v} 例{case}：跳点 {len(s["jumps"])} 个（门槛 {s["thr"]:.5f} W）；并格A {s["A"][0]}～{s["A"][1]}；并格B {s["B"][0]}～{s["B"][1]}；跳点 ' + '；'.join(f'{a:.3f}→{b:.3f} {q:+.4f}' for a, b, q, e in s['jumps']))
open(out_path, 'w', encoding='utf8').write('\n'.join(out) + '\n')
print(out_path)
