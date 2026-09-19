export const meta = {
  name: 'r48-pt-grade-functions-fix3',
  description: 'r48_H 物性函数第三轮：堵住膨胀覆盖分档的循环门 + 复核 minor 项 → 独立注入核验（2026-09-16 续跑，接上次中断处）',
  phases: [
    { title: '修复', detail: '覆盖分档独立门 + minor 项' },
    { title: '核验', detail: '独立注入核验' },
  ],
}
const REPO = 'D:\\WinForm\\r48_H'
const SCR = 'C:\\Users\\admin\\AppData\\Local\\Temp\\claude\\D--WinForm-Pt-Optimize\\7e065600-607b-4804-80c9-ea03a0587700\\scratchpad'
const CTX = `
工作树 ${REPO}（git worktree，.basetree 基准；不许 git commit，不许动别的工作树）。三份铂工作簿 → 按牌号温度函数（Core/PtThermalExpansion.cs、PtCreepWorkbook.cs、PtResistivityData.cs；门 R48ExpansionWorkbookTests.cs、R48CreepWorkbookTests.cs、R48ResistivityWorkbookTests.cs、MaterialDbTests.cs、XlsxBook.cs、ExactPolyFit.cs；HANDOVER「0.-6」节）。前两轮修复与复核全文：${SCR}\\..\\tasks\\wagzieaw7.output（先读，含注入脚本位置 ${SCR}\\rev3\\inj.py、${SCR}\\r48fix\\inject.py）。
## 续做说明（2026-09-16，主会话）
今天是 2026-09-16。上一次实施在 2026-09-15 晚因周额度用尽中断。工作树里已有它留下的**未暂存**改动（git diff：R48CreepWorkbookTests.cs +151、R48ExpansionWorkbookTests.cs +321、PtCreepWorkbook.cs +72、PtThermalExpansion.cs +58 行；现有 stream.patch 是中断前 19:14 的旧版，**不含**这些改动）。它的注入脚本在 ${SCR}\\r48fix3\\；思路与命令记录（按时间，先读）：${SCR}\\fix3_prev_progress.txt。**接着做，不要推倒重来**；但每一项都要你自己验证过才算做了（上次编过的门要重编重跑，上次做过的注入要重做）。新写的注记签「2026-09-16，Opus 5」。
## 仍开放的阻断（两位复核独立指出，同一件事）
膨胀覆盖分档门是循环的：映射门与应变差门的期望覆盖直接调生产 ExpansionCurve.Classify（R48ExpansionWorkbookTests.cs 约 :806/:816/:845/:851）。注入 J1（ZGS 两条 hi += 100）、J2（删数据下端端部间隔）、J4（tC<100 改 <=）、R1b（Order==2 时 inRange 放宽）、R1c（端部间隔 <=/>= 改 </>）全绿。
修法（照做）：门自己按定义算期望覆盖，不调任何生产分档函数——输入只用已由其他门从 xlsx 核过的量：每条曲线的数据温度区间与阶数（图表 XML）、表格上下界 B4/B19（0–1500 °C 外无数据）、0–100 °C 取 100 °C 那点的类别（更差取更差）、数据区间外 ≤3 阶外推 / ≥4 阶无数据、瞬时 α 下降段用精确有理数导数符号判、导数类量端部 = 排序后第 2 个与倒数第 2 个数据点以外的间隔（含端点）。9 条曲线 × 导数/非导数 × 0–1500 °C 每 0.5 K 全网格比对；另把 HANDOVER 覆盖表里每条曲线的外推/端部/形状不可信/无数据段端点写进门当决定记录核（与包络门同法）。改完重跑 J1、J2、J4、R1b、R1c 五处注入，全部必须变红；再把前两轮 32 处注入全部重跑一遍，全红；每处改回后逐字节核对。
## minor（全做）
1. 瞬时 α 在 100 °C 的台阶：逐曲线量级（Pt-Rh/80-20 与 Umicore-PtRh20 +22.1 %、Pt +2.3 %、ZGS-Pt +1.2 %、90-10 +0.4 %、ZGS-PtRh10 −1.1 %，自己复算）写进 HANDOVER，并在 0–200 °C 的瞬时 α Note 里点名台阶大小。
2. 持久强度原始点时间轴端点两向覆盖翻面（3810 组里 68 组，例 Pt 1213 °C × 0.1 h）：时间轴判断加 1e-9 相对容差（与互逆容差同口径），门在端点寿命上断言两向相同。
3. 无原始点牌号（FKS16 两个、Umicore 两个）判不了时间外推却落在 InUnconfirmedRange(1) < TimeExtrapolated(2)：主会话定——把「推定区间、无原始点、判不了时间外推」排到比 TimeExtrapolated 更不可信的位置（调整枚举顺序或新增类别，名字写清），门断言「≥ 时间外推 ⇒ 判不了」口径下 FKS16/Umicore 不会漏过。
4. a(T) ≥ 0 规则无门守（J3 注入全绿）：用人造系数的 PtGrade（测试内构造，不进 MaterialDb.All）测该规则，J3 注入须变红。
5. HotLength/Elongation 在 L0 为 NaN 时 Coverage 应为 NoData（守 PtThermalExpansion.cs:107 不变式），门对全部单点函数核这条不变式。
6. PtCreepWorkbook.cs:28「1465–1500」改成与门输出、HANDOVER 一致的 1466–1500。
7. HANDOVER 覆盖表的「无违规段」注明只在 0.1–1e5 h 内成立，并写范围外例子（纯铂 0.01 h 1101–1217 °C）。
8. PtThermalExpansion.cs 换行统一为 CRLF（与其余 Core 档一致），确认数值与门不受影响。
9. HANDOVER 出处更正：「彌散鉑金」中文名出处是 CreepForm.vb:10-11 与 MainForm1.Designer.vb:600，MainForm1.vb 里只有变量 Diffusion_Pt。
10. 快速套件改写受追踪 deliverable 文件这件事记入 HANDOVER 待办（M 表新增一条，带测试名）。
## 规矩
现有数值逐位不变（重跑网格转储，干净目录重建、核 DLL SHA）；门槛写死不挪；注记签「2026-09-16，Opus 5」（上次已签 2026-09-15 的不改）；编译 + 新门 + 读 HANDOVER 的门 + 快速套件（dotnet test ${REPO}\\Pt_Optimize.sln --no-build --nologo --filter "速度!=慢"，跑前备份受追踪 deliverable/docs/finaldesigns、跑后还原核 SHA）；HandoverGateCountTests 条数同步；重建 stream.patch（git add -A -- . ':!.basetree' ':!stream.patch' ':!deliverable'；git diff --cached --binary $(cat .basetree) > stream.patch）。`

const SCHEMA = { type: 'object', properties: { verdict: { type: 'string' }, blockersRemaining: { type: 'array', items: { type: 'string' } }, notes: { type: 'array', items: { type: 'string' } } }, required: ['verdict', 'blockersRemaining', 'notes'] }

phase('修复')
const fix = await agent(`你是实施者。${CTX}\n最终回复：阻断如何关闭 + 注入表（注入点 → 红的测试名 → 改回 SHA）；minor 1–10 逐条；测试结果原样；转储核对；stream.patch SHA；仍开放事项。`, { label: 'fix3', phase: '修复' })

phase('核验')
const ver = await agent(`你是独立核验员（只读：不许改 ${REPO} 的文件；注入一律在 scratchpad 副本里做，结束后核 ${REPO} 逐文件 SHA 未变）。任务是找错。
实施报告：
${fix || '（无）'}
${CTX}
要做：① 自己另想至少 5 处与已知注入不同的「偏不安全」改错（覆盖类别变好、区间放宽、NaN 当值、端点翻面、无原始点牌号漏过），每处记 DLL SHA 证明编进去了，看门红不红；② 核 minor 1–10 是否真落地；③ 核「现有数值逐位不变」证据可信。输出：结论、剩余阻断（每条带注入证据与修法）、其余备注。`, { label: 'verify3', phase: '核验', schema: SCHEMA })

return { fix, ver }
