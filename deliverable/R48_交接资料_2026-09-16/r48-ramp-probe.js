export const meta = {
  name: 'r48-ramp-probe',
  description: '升温全程第一个结果：在新工作树 r48_L 上写慢探针，沿 8 个设定点解空管整线、按管段温场积分伸长，出表 → 独立复核',
  phases: [
    { title: '探针', detail: '建树、拷膨胀函数、写探针、跑出表' },
    { title: '复核', detail: '独立重算一点、查单位与口径' },
  ],
}
const REPO = 'D:\\WinForm\\r48_L'
const SCR = 'C:\\Users\\admin\\AppData\\Local\\Temp\\claude\\D--WinForm-Pt-Optimize\\7e065600-607b-4804-80c9-ea03a0587700\\scratchpad'
const RULES = `
## 规矩
- 今天 2026-09-16。用户原话（09-16）：「先把路走通，再挂UI」——本路只出计算结果与表，不改 UI、不接判据、不改生产求解代码。
- 工作树 ${REPO}：先用 python "${SCR}\\mkwt_M.py" r48_L 从合并树 D:\\WinForm\\r48_M 的当前状态建（脚本会写 .basetree 并核对 .cs 逐文件相同）。**只改这个目录**；D:\\WinForm 下其他目录只读；不许 git commit/push/stash/checkout/reset/merge；不许 --no-verify。
- **不许用 Monitor 或 run_in_background 等长跑**：前台 Bash（每次 timeout ≤ 540000 ms）+ until 循环等，可多次；交前逐项自查「每一步都有结果」。
- 每个数指回出处（文件 + 行 / 输出文件 + 行）；分清实测 / 推理；不许编。长跑每个设定点都要打印进度（判据值、离限值多远、耦合轮数与距离）；方向错立刻停。探针输出只写带开跑时刻的新文件名到 ${REPO}\\deliverable\\。
- 代码注释签「2026-09-16，Opus 5」。慢测试按仓库惯例加 速度=慢 特征（看现有 R48*ProbeTests 怎么标）。
- 结束前重建补丁：cd ${REPO}; git add -A -- . ':!.basetree' ':!stream.patch'; git diff --cached --binary $(cat .basetree) > stream.patch。`

const TASK = `你是 L 路探针实施者。目标：今天给用户一张「内置设计沿升温轨迹每段管的非均匀伸长」表，判读写死在跑前。
先读：${SCR}\\ramp_task_spec.md（§1 判据定义、§2 轨迹、§6 判不了名单）；记忆里的口径已在里面。再读合并树里 SegmentSolver.cs（SolveResult.X/TMetal 逐节点温度，:15-17；段=一个控温点一段，热偶在段中点 :203/:317）、LineRunner.cs（LineCase.EmptyTube、SetpointC、ClampTempC、RampTargetC :284；Run 的返回里每段 SolveResult 在哪）、DesignSpec.cs:924-979（空管时 SetpointC 被换成 RampTargetC 重复——探针要在这之后把 SetpointC 全部改成当前设定点，**不改 RampTargetC**，它喂设计电流/截面 J）。
## 做法
1. 建树 r48_L（见规矩）。把 D:\\WinForm\\r48_H\\Pt_Optimize\\Core\\PtThermalExpansion.cs 拷进 ${REPO}\\Pt_Optimize\\Core\\（只读 r48_H；记下拷入文件 SHA-256 与 r48_H 该文件当时的 git 状态；注释里写「临时拷自 r48_H，待 H 路合入后以 H 为准」）。编译不过就把它依赖的最小类一并拷（MaterialDb 若有差异只拷缺的成员，不覆盖）；dotnet build Pt_Optimize.sln 0 错误。
2. 写慢探针 Pt_Optimize.Tests/R48RampTubeElongationProbeTests.cs：
   - 算例：内置设计 0（K 路门1b 用的那个：两段 1150/1080 °C、各 300 mm、开箱网格；从 DesignSpec/内置设计表读，不手抄数值），EmptyTube = true。
   - 轨迹：SetpointC 全线依次 = 300/450/600/750/900/1000/1080/1150 °C；ClampTempC = 100 °C；≥600 °C 的点再跑一遍 ClampTempC = 450 °C；管腔 kA 默认档。每点调 LineRunner.Run（与稳态同一份口径，不加热容项），取每段 SolveResult.X、TMetal 与该段 T设定（该段控温点温度 = 该点设定值）。
   - 积分：ε 差用 PtThermalExpansion.StrainDifference("Pt", T(x), "Pt", T设定)（看它的签名与返回类型，取 Value 与 Coverage；>1000 °C 会标外推，照记）。ΔL_A = ∫(x=0→L/2) εdiff dx，ΔL_B = ∫(L/2→L)，梯形权重、端节点半格；控温点在段中点（核 SegmentSolver 是否真是中点，不是就按它的位置分）。ΔL段 = ΔL_A + ΔL_B。单位 mm。
   - 打印（每点每段一行）：设定点、夹头档、段号、T管根A/B、T最高/最低、ΔL_A、ΔL_B、ΔL段、max(|ΔL_A|,|ΔL_B|) 与 δ/2 = 0.05 mm 的距离、覆盖类别（区间内/外推）、耦合轮数与收敛距离、场是否有效（未收敛/越熔点/超散热表 ⇒ 该点判不了）、解算耗时。另把每段 X/TMetal 原样转储到同一目录的附件文件（复核用）。
   - 跑前判读（写进测试注释与输出文件头）：物理把关人预估空管终点 +0.05～0.08 mm、300–450 °C 段 −0.05～−0.08 mm（±50 %），带玻璃稳态约 −0.013 mm；若量级差 3 倍以上先怀疑探针（单位、T设定取法、节点站位、ε 符号），不怀疑物理。低温段若段间耦合欠松弛 300 轮不到容差 = 分辨率地板（放大约 170 倍），记「判不了：分辨率地板」，不是热失控；若这样，试走牛顿路（LineRunner 里 E 路已有）再跑一次并说明。
   - 加一条快门：用解析温度场（如 T(x)=T设定+ΔT·cos(πx/L)）造 SolveResult，探针积分器结果与闭式 ∫ 值一致到 1e-6 mm；再造一例 ΔL_A=+a、ΔL_B=−a 确认整段为 0 而半段非 0。
3. 跑探针（前台等），输出表文件 ${REPO}\\deliverable\\R48_L_升温管段伸长探针_本次开跑于2026-09-16_HHMMSS.txt。跑完自己按 §2 判读逐点写结论；不许改判读去迁就结果。
4. 快门 + 探针通过后重建 stream.patch。
最终回复：表（原样，全部点全部段）、判读结论（过/不过/判不了各在哪个设定点哪一段）、拷入文件 SHA、探针文件路径、快门结果、耗时、仍开放事项、你不确定的地方。
${RULES}`

phase('探针')
const probe = await agent(TASK, { label: 'probe:L', phase: '探针' })

phase('复核')
const ver = await agent(`你是 L 路探针的独立复核者（只读：不许改 ${REPO}；要算就在 scratchpad 里算）。
实施报告：
${probe || '（无）'}
查：① 从附件里的 X/TMetal 自己用 numpy 对至少两个点（一个 ≤450 °C、一个 1150 °C）重算 ΔL_A/ΔL_B/ΔL段（ε 差自己按 r48_H PtThermalExpansion 的定义算：LT/L0−1 的差，纯铂 X14 三次式，>1000 °C 外推），与表逐位或至 1e-4 mm 相同；② T设定是不是该段控温点温度、控温点是否在段中点、接头节点是否算半格；③ 单位（mm vs m）、符号（热于设定 ⇒ 伸长为正）；④ 有没有改生产代码（git diff 基线树只应有新测试、拷入的 PtThermalExpansion.cs 与 deliverable）；⑤ 判读有没有事后迁就；⑥ 报告有没有「等待中」却写成做了。输出：必须修 / 应修 / 查过没问题，附你重算的数。短。
${RULES}`, { label: 'verify:L', phase: '复核' })

return { probe, ver }
