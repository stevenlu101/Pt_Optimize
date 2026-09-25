export const meta = {
  name: 'r48-KJ-cont',
  description: 'K 路修复续做（上一位在慢探针跑到一半时结束回合）+ J 路实施续做（额度中断）；各自做完 → 独立复核',
  phases: [
    { title: '续做', detail: 'K 修复收尾、J 实施接着做' },
    { title: '复核', detail: '各一名独立复核' },
  ],
}
const SCR = 'C:\\Users\\admin\\AppData\\Local\\Temp\\claude\\D--WinForm-Pt-Optimize\\7e065600-607b-4804-80c9-ea03a0587700\\scratchpad'
const GATE = SCR + '\\gate_merge_list.md'

const RULES = (repo) => `
## 规矩
- 今天 2026-09-16。用户 09-16 原话：「先把路走通，再挂UI」——本轮不新做 UI，只把已开的口子收完。
- 工作树 ${repo}（git worktree，.basetree 是起点树 = 合并树 D:\\WinForm\\r48_M 的快照）。**只改这个目录**；D:\\WinForm 下其他目录只读；不许 git commit/push/stash/checkout/reset/merge；不许 --no-verify。Bash 每条命令先 cd 到工作树。
- **不许用 Monitor 或 run_in_background 等长跑**（上一位就是这样把「等结果中」交成了最终回复）：长跑用前台 Bash（每次 timeout ≤ 540000 ms）+ until 循环等，可多次调用；交前逐项自查「每一步都有结果」，没结果不许交。
- 每个数指回出处；分清实测/推理；不许编；「判不了」不许当「过」；门槛事先写死；门不许手抄生产配方（够不着提成公开函数）；新状态位要在下游造门。
- 注记签「2026-09-16，Opus 5」（已签 09-15 的不改）。界面不许出现判据代号与命令行开关名。慢探针输出只写带开跑时刻的新文件名。
- 编译 dotnet build Pt_Optimize.sln 与 tests/UiWiring；快速套件 dotnet test Pt_Optimize.sln --no-build --nologo --filter "速度!=慢"（约 6 min，会改写受追踪 deliverable：跑前备份、跑后还原核 md5）；HandoverGateCountTests 同步测试数；失败原样贴。
- 结束前重建补丁：cd ${repo}; git add -A -- . ':!.basetree' ':!stream.patch' ':!uishot_*'; git diff --cached --binary $(cat .basetree) > stream.patch；核 git diff --stat 为空、无注入残留。`

const K_TASK = `你是 K 路（分工况判据）实施者，**续做修复阶段**。
先读：审查意见全文 ${SCR}\\review_K_report.md（必须修 M1、M2；应修 S1–S8）；K 路实施报告 ${SCR}\\implK_report.md；上一位修复者的中断记录 ${SCR}\\fixK_prev_progress.txt（它已改了不少，最后卡在等保温搜索慢探针 deliverable/R48_保温搜索_分工况口径_管7点5_本次开跑于2026-09-16_1951.txt 跑完）。
## 主会话裁定（2026-09-16）
1. 管 J 在空管到温稳态**保持硬判据**（用户「只要J<11即可」指电流密度类整体，管 J 与截面 J 都是 J<11）——所以审查 S1 的措辞要改成「只卡电流密度（管 J 与法兰截面 J）与场的有效性」，S2 的「待主会话定」改成已定。
2. 带玻璃稳态 Failed 多一行「本工况的场判不了」：接受。
3. 「空管态闭合不成立按冻结管根报参考值」：接受，但 HANDOVER 注记要写明是实施者判断、依据是什么，并按审查 S3 写明「这条判断无门」（或补门）。
4. 工况地位（用户 09-15）：升温全程第一步 → 带玻璃稳态定成败 → 空管只卡 J<11 与场有效性；判据表/说明措辞不许与此矛盾。
## 要做
A. 先查现状：git status、git diff $(cat .basetree) --stat、有无 testhost 还在跑（PowerShell Get-CimInstance 看 CommandLine 含 r48_K），有就前台等它退出；上一位改到哪一步逐条核（HANDOVER 已改的、措辞已改的别重复改）。
B. **必须修 M1**：InsulationSearch.cs 里判不了条件的那一行，树里是 \`else if (r.Flanges.Any(f => !f.FieldsConverged))\`，stream.patch 里是 \`else if (bad.Length > 0)\`（上一轮审查注入后没还原，行尾也从 CRLF 变 LF）。以补丁写法为准改回并恢复 CRLF；核 diff 只此一处变化。
C. **必须修 M2**：把该处 whys 逻辑提成公开纯函数（如 LineStateWhy(LineResult, string state)），造行为门：Ok=true、Converged=true、FieldsConverged=true、但 ClampCoversHole=true 的结果 ⇒ 判不了非空；注入（改回只看 FieldsConverged）须红。
D. 应修 S1–S8 逐条（S7 的保温搜索实测：把上一位跑的那份探针跑完或重跑，出带时刻文件；S4/S5 若只能记「无行为门」就写进 HANDOVER 并说明为什么）。
E. 快速套件全过（贴汇总行）；HANDOVER 注记与测试数同步；重建 stream.patch。
最终回复：A 现状；B–E 逐条证据（注入红的测试名、改回 SHA、探针文件路径与关键行）；套件汇总行；stream.patch SHA 与大小；仍开放事项；不确定的地方。
${RULES('D:\\WinForm\\r48_K')}`

const J_TASK = `你是 J 路（求解器与界面安全线）实施者，**第三次续做**（09-15 一次、09-16 一次，都因额度中断）。
先读：J 路任务书（J1–J12 全文）在 ${SCR}\\r48-streams-ijk-cont.js 的 J_TASK 常量里，逐条照做；合并把关待办全文 ${GATE}；上两次的记录 ${SCR}\\j_prev_progress.txt（09-15）与 ${SCR}\\implJ2_prev_progress.txt（09-16，最后在等 J8 慢探针）。
## 现状（主会话核过）
- 工作树 D:\\WinForm\\r48_J 里已有两次留下的未提交改动（14+ 个受追踪文件、5 个新测试、十余份 J路_J* 证据文件）；stream.patch 是 09-16 19:33 的版本，**不一定含最新改动**，交前必须重建。
- 上一次卡在 J8 的慢探针（deliverable/J路_J8_*R45*）：轨迹已跑到「片2 舌保温抬到上界 20.000 仍不过『管根低于热偶读数』⇒ 这根旋钮到顶了；求解器手上只有法兰侧九根旋钮」。这条是真发现，要写进 J8 结论（连同「盘径与舌半宽不在旋钮里」这个边界）。探针没跑完就前台等它跑完，或按已有输出下结论并说明未跑完的部分。
- 现在没有 testhost 在 r48_J 上跑（先自己核一遍）。
## 要做
逐条核对 J1–J12 哪些已落地、哪些没有（不许因为「上次做过」就跳过，自己验），没做完的做完；每条都要有「改回旧写法会红」的门与注入证据；J8 按上述发现写结论；最后快速套件全过、HANDOVER 注记与测试数同步、重建 stream.patch。
注意：K 路同时在改 LineRunner.Judge 判据清单、InsulationSearch、MeshVerify:112-116、CriteriaTableTests/ThermocoupleBasisTests——这些区域不要动。
最终回复：J1–J12 逐条（做了什么/没做与原因）；改动文件；注入实验表；测试命令与结果原样；实测数据表（带输出文件路径）；stream.patch SHA 与大小；仍开放事项；不确定的地方。
${RULES('D:\\WinForm\\r48_J')}`

const STREAMS = [
  { key: 'K', repo: 'D:\\WinForm\\r48_K', task: K_TASK },
  { key: 'J', repo: 'D:\\WinForm\\r48_J', task: J_TASK },
]

const results = await pipeline(
  STREAMS,
  (s) => agent(s.task, { label: `cont:${s.key}`, phase: '续做' }),
  (impl, s) => agent(`你是 ${s.key} 路的独立复核者（只读：不许改 ${s.repo}；注入在 scratchpad 副本里做，开始与结束各核 ${s.repo} 全文件 SHA 相同）。任务是找错。
实施报告：
${impl || '（未返回）'}
该路任务书：
${s.task}
查：① 每条是否真落地（不是只改注释/只加字符串断言）；② 亲自注入至少三处把生产代码改回旧写法，看门红不红；③ NaN/判不了传播、工况开关读点全不全（grep 全部调用点）；④ 有没有第二份实现；⑤ 行为变了却没门；⑥ 注释/HANDOVER 与代码数字一致否；⑦ 实测数据可不可比（同树同配方同网格）；⑧ 与另两路（I/J/K）改动区域有无越界冲突；⑨ **报告里有没有「等待中／未跑完」却写成做了的地方**。
输出：必须修（带证据与最小修法）/ 应修 / 查过没问题。短。
${RULES(s.repo)}`, { label: `check:${s.key}`, phase: '复核' }),
)

return { K: results[0], J: results[1] }
