export const meta = {
  name: 'r48-pt-grade-functions-fix4',
  description: 'r48_H 物性函数第四轮：收尾第三轮未完的 4 步 + 关掉核验员新发现的阻断（持久强度区间边界无门守）+ 数据齐全度函数 → 独立注入核验',
  phases: [
    { title: '修复', detail: '收尾 + 区间边界门 + 齐全度函数' },
    { title: '核验', detail: '独立注入核验' },
  ],
}
const REPO = 'D:\\WinForm\\r48_H'
const SCR = 'C:\\Users\\admin\\AppData\\Local\\Temp\\claude\\D--WinForm-Pt-Optimize\\7e065600-607b-4804-80c9-ea03a0587700\\scratchpad'
const CTX = `
今天 2026-09-16。工作树 ${REPO}（git worktree，.basetree 基准；不许 git commit，不许动别的工作树；D:\\WinForm 下其他目录只读）。三份铂工作簿 → 按牌号温度函数（Core/PtThermalExpansion.cs、PtCreepWorkbook.cs、PtResistivityData.cs、MaterialDb.cs；门 R48ExpansionWorkbookTests.cs、R48CreepWorkbookTests.cs、R48ResistivityWorkbookTests.cs、MaterialDbTests.cs、XlsxBook.cs、ExactPolyFit.cs；HANDOVER「0.-6」节）。
先读：第三轮核验报告全文 ${SCR}\\verify3_report.md（含阻断的注入证据与修法、15 处新注入结果、未完成清单、小备注）；第三轮实施记录 ${SCR}\\fix3_prev_progress.txt 与 ${SCR}\\r48fix3\\（注入脚本、r4_results.json、restore_deliv.py、fastsuite_v4.txt）；核验员的注入脚本与副本 ${SCR}\\verify4\\（inject_v.py、inject_known.py、v_results.json、v2_results.json、k_results.json、xdump\\Program.cs）。
## 现状（主会话核过）
- 第三轮实施方在快速套件没跑完时就交了差：工作树里改动都在（git status：HANDOVER MM、四个 Core/测试档已改），但 ① 快速套件结果没有汇总行（fastsuite_v4.txt 只有 DesignSpecStoreTests.DiscInsulMm 那条基准树自带的失败）；② 受追踪的 deliverable/R47_第三轮N1_倒角实验_2026-09-13.txt 被套件追加了 2 行，还没还原（restore_deliv.py 没跑）；③ stream.patch 还是 2026-09-15 19:14 的旧版；④ HANDOVER 措辞微调没做完。现在 r48_H 没有测试进程在跑。
- 核验员复核：主阻断（覆盖分档门循环）已关，39 处已知注入全红，minor 1–10 全落地，数值逐位不变可信。
## 本轮要做（全做，每条要有证据）
A. **收尾第三轮的 4 步**：跑完快速套件（dotnet test ${REPO}\\Pt_Optimize.sln --no-build --nologo --filter "速度!=慢"，前台等到出「已通过/失败」汇总行，Bash 超时最长 600000 ms，超时就按测试类分批跑并把每批汇总行都存档；跑前备份受追踪 deliverable/docs/finaldesigns，跑后还原并核 SHA；**不许在套件没出结果时结束回合**）；跑 restore_deliv.py 还原并核；HANDOVER 措辞改完重跑文档门（第三轮同一 docgates.filter）与 HandoverGateCountTests；重建 stream.patch。
B. **关阻断：持久强度拟合区间边界无门守**（核验员 V12/V12b/V12c：MaterialDb.cs:59-60 InCreepRange 上端 +5 K／+0.5 K、下端 −5 K 放宽后 45 条门全绿；后果 RuptureStress("Pt",1403,1000) 给值且标 InConfirmedRange）。修法照核验员：在 R48CreepWorkbookTests「原始持久强度点逐格等于代码_只读G到L列_牌号标题有映射」（已从 xlsx 读出 ts.Min/ts.Max）里，对每个有原始点的牌号断言 RuptureStress 与 RuptureLife 在 tMax+δ、tMin−δ（δ = 1e-6、0.5、5）为 NoData 且 Value NaN，在 tMax、tMin 本身有值；钉死容差 InCreepRange(tMax+1e-10) 为真、InCreepRange(tMax+1e-6) 为假（两端同做）；推定区间的四个牌号用 CreepTMinC/CreepTMaxC 同法；包络门网格两端各加半 K 外探针（期望 NoData）。改后重跑 V12／V12b／V12c 必须全红，再把 39 处已知注入全部重跑（用核验员的 inject_known.py 口径，在 scratchpad 副本或原树上做但每处改回逐字节核）。
C. **核验员小项**：① V7 专门门「推定区间无原始点…不漏过」改成 Assert.Equal(UnconfirmedRangeTimeUnjudgeable)，V7 注入后该门必须红；② V6 时间轴容差硬编码 1e-3 只有一条 2e-9 探针抓到——加一道专门门（端点 ±1e-9 相对容差内外各一点，两向覆盖类别断言），V6 注入须至少红 2；③ HotLength/Elongation 在 L0 非有限（+∞/−∞）时返回 +∞ 且 HasValue = true：定义为「L0 非有限 ⇒ NoData」，PtThermalExpansion.cs:107 不变式改写为「Value 非有限 ⇔ Coverage == NoData」，门覆盖 ±∞；④ Pt_Optimize/Core/PtResistivityData.cs 换行改 CRLF（测试侧 XlsxBook.cs、ExactPolyFit.cs 也改），HANDOVER minor 8 措辞改准确，重跑相关门确认数值不受影响；⑤ HANDOVER M 表 M11 挪到 M10 之后；⑥ 「现有数值逐位不变」：把核验员的膨胀转储（${SCR}\\verify4\\xdump\\Program.cs：9 曲线 × 值/导数 + 12 牌号 × 3 借用 × −10–1510 °C 每 0.5 K 七个函数 + 持久强度）收进不变性仪器（入库为慢测试或 tests 工具，输出带时刻），记下本轮 SHA；PtThermalExpansion.cs:42 与 HANDOVER 里「网格转储逐字节同第三轮」改成属实的说法（原转储不含膨胀函数）。
D. **数据齐全度函数**（用户 2026-09-16 原话：「数据不全(电阻/热膨胀/蠕变应力)的铂金合金先以灰色不可选展示，只用数据全的铂金合金」；灰显本身由界面路做，本路只提供判定）：MaterialDb 加公开函数 DataCompleteness(gradeName) → 记录 (OwnResistivity, OwnExpansion, OwnCreepWithPoints, IsComplete, Missing: string[])，定义写死：OwnResistivity = ResistivityFrom == 自己；OwnExpansion = PtThermalExpansion 映射 Link == Direct；OwnCreepWithPoints = HasCreep 且 RangeConfirmed（有原始点）；IsComplete = 三者皆真。门：Pt、Pt-Rh/90-10、Pt-Rh/80-20 齐全；Tanaka-ZGS-Pt/ZGS-PtRh10（电阻率借用）、Umicore-PtRh10/20（电阻率与膨胀借用、区间推定）、FKS16/Pt、FKS16/PtRh-9010（无膨胀）、Pd、Ni、Cu 不齐全且 Missing 逐项点名；注入（把借用当自己的）须红。HANDOVER 0.-6 节记用户原话与定义，注明「借用≠自己的数据」是主会话解读、未向用户确认。
## 规矩
现有数值逐位不变（重跑网格转储 + 膨胀转储，干净目录重建、核 DLL SHA）；门槛写死不挪；注记签「2026-09-16，Opus 5」（已签 09-15 的不改）；编译 + 新门 + 文档门 + 快速套件；HandoverGateCountTests 条数同步；重建 stream.patch（git add -A -- . ':!.basetree' ':!stream.patch' ':!deliverable'；git diff --cached --binary $(cat .basetree) > stream.patch）；最终回复前自查「有没有哪一步没跑完就说做了」。`

const SCHEMA = { type: 'object', properties: { verdict: { type: 'string' }, blockersRemaining: { type: 'array', items: { type: 'string' } }, notes: { type: 'array', items: { type: 'string' } } }, required: ['verdict', 'blockersRemaining', 'notes'] }

phase('修复')
const fix = await agent(`你是实施者。${CTX}\n最终回复：A 四步各自的证据（套件汇总行原样、还原核 SHA、文档门结果、stream.patch SHA 与文件数）；B 阻断如何关闭 + 注入表（V12/V12b/V12c 与 39 处 → 红的测试名 → 改回 SHA）；C ①–⑥ 逐条；D 函数与门、注入结果；转储核对；仍开放事项；你不确定的地方。`, { label: 'fix4', phase: '修复' })

phase('核验')
const ver = await agent(`你是独立核验员（只读：不许改 ${REPO} 的文件；注入一律在 scratchpad 副本里做，开始与结束各对 ${REPO} 全部文件 SHA 逐一比对）。任务是找错。
实施报告：
${fix || '（无）'}
${CTX}
要做：① 核 A 四步是否真做完（套件汇总行、deliverable 还原、stream.patch 含本轮全部改动且相对 .basetree）；② 重跑 V12/V12b/V12c、V6、V7 与 39 处已知注入，全红才算；③ 自己另想至少 5 处与已知不同的「偏不安全」改错（区间边界、容差、齐全度把借用当自己的、NoData 当值、端点翻面），每处记 DLL SHA；④ 核 D 齐全度定义与门；⑤ 核「现有数值逐位不变」两份转储可信。输出：结论、剩余阻断（每条带注入证据与修法）、其余备注。`, { label: 'verify4', phase: '核验', schema: SCHEMA })

return { fix, ver }
