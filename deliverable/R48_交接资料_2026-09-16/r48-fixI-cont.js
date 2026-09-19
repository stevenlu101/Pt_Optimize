export const meta = {
  name: 'r48-fixI-cont',
  description: 'I 路修复续做：上一位实施者在慢测试跑到一半时结束了回合；接着按审查意见修完、重建 stream.patch → 改后复核',
  phases: [
    { title: '修复', detail: 'I 路按审查意见修完（续做）' },
    { title: '复核', detail: '改后复核' },
  ],
}
const REPO = 'D:\\WinForm\\r48_I'
const SCR = 'C:\\Users\\admin\\AppData\\Local\\Temp\\claude\\D--WinForm-Pt-Optimize\\7e065600-607b-4804-80c9-ea03a0587700\\scratchpad'
const RULES = `
## 通用规矩（每条照做）
- 你的工作树：${REPO}（git worktree，分离 HEAD；.basetree 是起点树 = 合并树 D:\\WinForm\\r48_M 的快照）。**只改这个目录**；不许 git commit/push/stash/checkout/reset/merge；不许 --no-verify；其他 D:\\WinForm\\r48_* 与 Pt_Optimize*、D:\\PtDesign_V2 只读（D:\\WinForm\\r48_I_G2F、r48_I_Eprev 是 I 路的临时对照树，只读、不许删）。Bash 每条命令先 cd 到你的工作树。
- 项目：Pt_Optimize（C#/net8 WinForms，中文界面），铂金直接加热系统法兰优化 APP。今天 2026-09-16。
- **不许用 Monitor 或 run_in_background 等长跑**：长跑用前台 Bash（每次 timeout ≤ 540000 ms）并用 until 循环等待，可多次调用；结束回合前逐项自查「每一步都有结果」，没结果不许交。上一位实施者就是在慢测试跑到一半时用后台监视等待并结束了回合，工作流把那句话当成了最终回复。
- 每个数指回出处（文件 + 行 / 输出文件 + 行）；分清实测 / 推理；不许编；「判不了」不许当「过」；门槛事先写死；门不许手抄生产配方；新状态位要确认下游真读到。
- 代码注释与 HANDOVER 注记签「2026-09-16，Opus 5」（已签 09-15 的不改）。慢探针输出只写带开跑时刻的新文件名。
- 编译：dotnet build Pt_Optimize.sln 与 tests/UiWiring。快速套件：dotnet test Pt_Optimize.sln --no-build --nologo --filter "速度!=慢"（约 6 min；会改写受追踪 deliverable 文件：跑前备份、跑后还原核 md5）。HandoverGateCountTests 按反射核测试总数。失败原样贴。
- 结束前重建补丁：cd ${REPO}; git add -A -- . ':!.basetree' ':!stream.patch' ':!uishot_*'; git diff --cached --binary $(cat .basetree) > stream.patch；核 git diff --stat 为空（工作区=暂存区）、无注入残留。`

const TASK = `你是 I 路（证据与数值核验）实施者，**续做**修复阶段。
先读：I 路任务书与实施报告 ${SCR}\\implI_report.md；审查意见全文 ${SCR}\\review_I_report.md（必须修：无；应修 1–6）；上一位修复者的中断记录 ${SCR}\\fixI_prev_progress.txt（它在跑 R48LineDumpTests 慢门重记 SHA 时结束了回合）。
## 主会话裁定（2026-09-16）
1. 快速套件按原名覆盖的 12 份回归产物：本轮保留例外名单，不搬；HANDOVER 待办记一条，并按审查应修 6 写明其中 5 份被 Core 注释引作出处、是「活证据」。
2. ×25 停机规则本路不改。3. ApplySectionFloor 偏高 1～2 格本路不修，HANDOVER 待办记一条。4. 临时树保留。5. docs/Pt_理论模型_v6.0.md:161、v6.1.md:214 的「上界、确定、闭式」改成与 DesignCurrent.cs:21 一致（审查应修 3）。6. 板厚口径改后保温搜索没重跑：HANDOVER 写明旧记录板厚是继承值。
## 要做
A. 先查现状：git status；R48LineDumpTests 的输出（deliverable 下带时刻的新文件）跑到哪；有没有 testhost/dotnet 进程还在 ${REPO} 上跑（PowerShell Get-CimInstance Win32_Process 看 CommandLine 含 r48_I），有就前台 until 等它退出；上一位改到一半的文件逐个看 diff，决定接着用还是改回。
B. 按审查应修 1–6 逐条修：① PlateFloor 门 a 加与函数无关的断言（lo ≥ DiscFloorMm − 1e-12、lo − DiscFloorMm < QuantThickMm、W08 写死 0.60），注入 D（Ceiling→Floor）须红；② RunLayer 走 LayerDesign 的行为门：在可跑的快门里造（如 LayerDesign 结果与 RunLayer 单层输出的 PlateThickMm 逐位相同，若必须慢测试则写进慢探针并说明未实跑）；③ 裁定 5 的 docs 两处；④ WriteGuard 注释改引本树里存在的出处；⑤ LineDump 的「去文字」SHA 不写文字长度，重记六个记录值（慢门要真跑完：先跑一遍得新值，写进记录，再跑一遍确认逐位相同；两次输出都带时刻）；⑥ HANDOVER 待办措辞。
C. 快速套件全过（贴汇总行）；HandoverGateCountTests 同步；HANDOVER 加 I 路修复注记；重建 stream.patch。
最终回复：A 现状；B ①–⑥ 各自证据（注入红的测试名、改回 md5）；LineDump 两次运行的文件名与六个 SHA；套件汇总行；stream.patch SHA 与大小；仍开放事项；你不确定的地方。
${RULES}`

phase('修复')
const fix = await agent(TASK, { label: 'fix:I-续', phase: '修复' })

phase('复核')
const ver = await agent(`你是 I 路改后复核者（只读：不许改 ${REPO} 的文件；注入在 scratchpad 副本里做并核 ${REPO} 全文件 SHA 前后相同）。
修复报告：
${fix || '（无）'}
审查意见原文：${SCR}\\review_I_report.md。
查：① 应修 1–6 是否真落地（不是只改文字）；② 注入 D（ThickLowerCornerMm 的 Ceiling→Floor）与 RunLayer 改回旧写法各一次，看门红不红；③ LineDump 六个 SHA 是否两次运行一致、文字长度是否已不进 SHA；④ stream.patch 是否等于 git diff --cached --binary 基线树、含全部改动；⑤ 报告里有没有「等待中／未跑完」却写成做了的地方。输出：必须修 / 应修 / 查过没问题。短。
${RULES}`, { label: 'verify:I-续', phase: '复核' })

return { fix, ver }
