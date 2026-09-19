export const meta = {
  name: 'r48-streams-IJK-cont',
  description: '三路续跑（2026-09-16）：I、K 实施已完成（报告存档为文件）直接进审查→修复；J 接着上次中断处实施→审查→修复',
  phases: [
    { title: '实施', detail: 'J 接着上次做（I、K 已完成，不重跑）' },
    { title: '审查', detail: '每路一名独立对抗审查' },
    { title: '修复', detail: '按审查修，重建 stream.patch' },
  ],
}

const SCR = 'C:\\Users\\admin\\AppData\\Local\\Temp\\claude\\D--WinForm-Pt-Optimize\\7e065600-607b-4804-80c9-ea03a0587700\\scratchpad'
const GATE = SCR + '\\gate_merge_list.md'

const RULES = (repo) => `
## 通用规矩（每条照做）
- 你的工作树：${repo}（git worktree，分离 HEAD；.basetree 是起点树 = 合并树 D:\\WinForm\\r48_M 的快照）。**只改这个目录**；不许 git commit/push/stash/checkout/reset/merge；不许 --no-verify；其他 D:\\WinForm\\r48_* 与 Pt_Optimize*、D:\\PtDesign_V2 只读。Bash 每条命令先 cd 到你的工作树。
- 项目：Pt_Optimize（C#/net8 WinForms，中文界面），铂金直接加热系统法兰优化 APP。今天 2026-09-16；三路工作在 2026-09-15 开跑、当晚因周额度用尽中断，现在续跑。
- 合并把关待办全文（必读，你这一路的条目在里面）：${GATE}。合并三阶段报告：${SCR}\\..\\tasks\\wkxhmmxh4.output。
- 每个数指回出处（文件 + 行 / 输出文件 + 行）；分清实测 / 推理；不许编；「判不了」不许当「过」；门槛事先写死，不许为了变绿事后挪；门不许手抄生产配方（够不着就提成公开函数）；新状态位要确认下游真读到（造门在下游）。
- 代码注释与 HANDOVER 注记签「2026-09-16，Opus 5」（2026-09-15 已签的不改）。界面不许出现判据代号（②′／②″／③ 等）与命令行开关名，一律写全名。**改 UI 必须自己抓图**：dotnet run --project Pt_Optimize -- --cli --uishot <你的工作树>\\uishot_<路名>，逐页看 PNG 确认布局（不是源码写了就算）。
- 长跑要放探针：每轮打印判据值与离限值多远；方向错立刻停。慢探针输出**只写带开跑时刻的新文件名**（不覆盖 deliverable 里已有证据）。
- 编译：dotnet build Pt_Optimize.sln 与 tests/UiWiring。快速套件：dotnet test Pt_Optimize.sln --no-build --nologo --filter "速度!=慢"（约 6 min；它会改写几个受追踪的 deliverable 文件：跑前备份、跑后还原，报告里说明）。HANDOVER 的测试总数行由 HandoverGateCountTests 按反射核，改了测试数要同步。失败原样贴。
- 结束前重建补丁：cd ${repo}; git add -A -- . ':!.basetree' ':!stream.patch' ':!uishot_*'; git diff --cached --binary $(cat .basetree) > stream.patch（不含测试运行产生的 deliverable 改写）。
- 最终回复（给主会话）：逐条（按待办编号）做了什么 / 没做与原因；改动文件；新测试及其「改回旧写法会红」的注入实验；测试命令与结果原样；实测数据表（带输出文件路径）；仍开放事项；你不确定的地方。`

const I_TASK = `你是 I 路（证据与数值核验）实施者。以**测量与核验**为主，生产代码只在确证有错时最小改动。
待办条目（详见 ${GATE}）：
I1 P0-1 G2 两条带玻璃慢探针在合并树上变红的归因。G3 带玻璃按构造为 0（主会话已读码核实 SegmentSolver.CavityRadKAAt 非空管返回 0）⇒ 从嫌疑里删掉 G3。做**正向重放**：新建工作树 D:\\WinForm\\r48_I_G2F（git worktree add --detach 自 D:\\WinForm\\r48_M 的 HEAD；用 git read-tree -um 把工作区设成 r48_G2 的 .basetree 树，再 git apply --3way r48_G2/stream.patch；以上只读 r48_G2），先复现 r48_G2 记录值（R48G2RampClampChannelTests 的 RecB2、RecBudget 逐位），再打 r48_F/stream.patch，重跑两条；若与合并树 15:36 输出一致 ⇒ 差全部归 F；否则依次再加 E、G1、合并修改，直到对上。结果写进带时刻的新证据文件；归因清楚后在 r48_I 里按规矩重记探针记录（旧值→新值、依据：哪一路的哪项有意改动），并改正 HANDOVER 与探针注释里「G3 嫌疑」「算术没动的旁证」两说并存的问题（探针判词 R48G2RampClampChannelTests.cs:296 的触发条件要读懂后写清哪句成立）。
I2 P0-2 「E 纯搬移 / 带玻璃生产数逐位不变」补证据：恢复 E 的整线转储测试（参考 r48_E/deliverable/R48_E_LineRunner搬移逐位对拍_2026-09-15.txt 与 r48_E 的测试源码），全字段按 R 格式（往返精度）输出，含 WarmStart、BaselineRootC；算例覆盖 B2 带玻璃、管腔系数置 0 的空管、UseMeasuredCurrent、SigmaOfTCoupling、一个图纸路径算例。对照树：在另一个临时工作树里用合并前备份（${SCR}\\emerge\\ 下 LineRunner/Solver/InsulationSearch 的 _prev 版本，即 E 第一轮合入后的合并树）替换这三份文件（InsulationSearch 相关测试可排除出该临时树的编译），两树转储比 SHA-256；逐位不同的字段逐个解释是否属于有意改动。转储测试做成慢测试入库，输出带时刻。
I3 P0-3 段间耦合放大：同一算例（B2 带玻璃、B2 空管）管段 Nodes 取 201/401/801，各调 LineRunner.NeighbourJacobian，打印 Jacobi 谱半径/收缩比与 1/(1−g)，对照闭式 1/(1+Δx/ℓt) 与 LineRunner.cs:1433、1439 写死的 ×25。只测不改停机规则（改法等数值把关人定），结果写证据文件。
I4 P0-4 保温搜索板厚只抬不降：核 0.73/1.26 是否就是 J 下角（按 DesignSpec/ Solver.ApplySectionFloor 与 DiscFloorMm 实算三层管保温 7.5/8.0/8.5 的下角）；若不是，最小修：保温搜索先把板厚置到约束盒下角再抬（注意「优化不许有起点概念」），加门。
I5 P1-15 在同一次 Judge 里比 segs[i].CurrentA 与 dcr.SegPeakA[i]（带玻璃、空管各一），给出空管稳态电流是否超设计电流、超多少；改正 DesignCurrent.cs:21「上界」措辞（若确证不是上界）。
I6 P2-1/P2-2/P2-3/P2-11/P2-12：① 核合并树 deliverable 里被 Core/测试/HANDOVER 引用的证据文件是否齐（主会话已补拷两批，清单 deliverable/R48_合并拷入证据清单_2026-09-15.txt），还缺的列出（不许用合并树新跑结果冒充）；改正仍说「合并树里没有」的注释（ShellMesh.cs:816；LineRunner.cs:2056、2105 等）；② E 两道慢门遇文件已存在就抛：改成写带时刻的新文件名；③ 全部慢探针 File.WriteAllText 目标与 deliverable 现有文件求交集，凡会覆盖被引证据的，改成带时刻文件名，并加一道快门（源码扫描：测试里写 deliverable 的调用必须带时刻或经 EvidenceFile）；④ F 1536 证据头指纹对不上、E 一次性实测数两处注释矛盾：照实改注释（引对拍文件实数）。`

const J_TASK = `你是 J 路（求解器与界面安全线）实施者。每条先**做实验确证**（给算例与输出），确证才修，修完加「改回旧写法会红」的门。
待办条目（详见 ${GATE}）：
J1 P1-2 Solver.Finish 终局复核网格：主会话已读码核实 Solver.cs:1405 只有 if (lastOpt.FineMm > 0) RefineWholeMesh，lastOpt=navOpt（FineMm=0、FineRadiusMm>0）时终局复核用 BuildCase 默认半径 50 而求根用 59（:712-715 记录过净流入 +3.267 对 −6.533 W 符号相反）。修：Finish 改调 Solver.ApplyCaseMesh(lcF, lastOpt)；门：导航遍即终局时 lcF 网格参数与最后一遍求根逐项相同；改 :1469「唯一一份」措辞使其属实。
J2 P1-1 图纸路径「法兰截面 J」限值恒 11 不跟设定 J 走：LineDesignPage.cs:2697-2706 造算例不给 JDesignAPerMm2；FlangeAutoSizer.CloneCase（:1375-1404）漏拷 JDesignAPerMm2、ClampTempC、Ramp*、FreeTabMinMm、Couple*、Anderson*、FlangePlates、RefusedWhy；注释 :1341-1346 说有 LineCaseCloneTests 但测试不存在。修：图纸路径传 J；CloneCase 改成反射式全字段拷贝或逐字段 + 真的 LineCaseCloneTests（反射枚举 LineCase 全部字段，任何字段漏拷即红）。实验：图纸模式 J 设 8 核算，看判据表截面 J 限值。
J3 P1-4 Solver 的 Gate 只接 Ok/Converged，绕过五道「判不了」后置标记（Solver.cs:1648-1697；LineRunner.cs:1868-1872；PlateSlack :1717、Holds :1084、rest 滤名 :582-583；InstallReport.cs:147-155 同样）：让 Gate/PlateSlack/Holds/InstallReport 读同一份判不了标记（提成公开函数，一处实现），没收敛的场不许用来抬旋钮；停机病因如实。实验：压小 CG 迭代上限使一片温度场不收敛，看 StopWhy。
J4 P1-5 MeshVerify「加密复算：网格无关」可盖在没收敛/判不了的数上并打已验戳（MeshVerify.cs:380、388-392、463；LineDesignPage.cs:4175）：没收敛或判不了一律不打「网格无关」，判词如实。
J5 P1-6 搜形状不看 fin.Feasible 就印「最轻的全过形状」并写回控件（LineDesignPage.cs:3470、3486、3509）：不可行不许说全过、不许写回控件（或写回但明示不可行，择一并说明）；抓图验。
J6 P1-7 整片热稳定与升温两节点只评发热最大的一片、发热 NaN 的片被跳过（LineRunner.cs:2850-2857、2865-2868）：逐片评，取裕度最小；NaN 片 ⇒ 判不了。实验先证：逐片保温不等的设计上四片各调一次 StabilityCheck。
J7 P1-8 FlangeAutoSizer 达标/压住了/熔点闸漏 NaN（FlangeAutoSizer.cs:371、1262、951、1066、1122、1145）：NaN ⇒ 判不了，不许当达标。
J8 P0-5 导航遍抬过头退不回（Solver.cs:1067-1072 回收名单只有舌保温、t₁、t₂；圆盘槽、舌孔孔径、舌孔拉长、r₁、r₂、板厚退不回；形状族冻结在导航场；FlangeAutoSizer 粗网格二分倍数成永久下界）：**先做实验**找一个实例（报告里「导航网格高估冷侧 9.02 对 0.57」无出处，要自己找算例）；确证后最小修（回收名单扩到可退的旋钮、在细网格上复核倍数），不确证就只记录实验与结论。
J9 P2-5 整面接触两种定义（ShellMesh.cs:122 vs 配方至少一格为真；:1264 ClampCell 全 false 也赋值）：查生产是否可达；统一定义与注释，加门。
J10 P2-7/P2-8/P2-9：默认评估函数限值门挡不住写死 5（R48InsulationSearchEngineTests.cs:262-276、:326-327）——改门使限值取非 5 的算例也验；保温搜索细区半径传递加行为门；LineResult.RecipeDeviations 与 PlateStateView.GammaHotKPerW/GammaColdKPerW/ClosureNote 赋值无人读——接到报告/界面（抓图）或删去，择一说明。
J11 P2-14 RampScreen 解前解后门禁判的不是同一物理量、界面文字过时、TargetC 写死 1150：TargetC 改读 RampTargetC，文字照实；不改判定口径（升温判据由别路重做）。
J12 P3 表里属于 J 文件的措辞项（6、8、9、10、11）顺手改。
注意：K 路同时在改 LineRunner.Judge 判据清单、InsulationSearch.DefaultCriteria 与可行性、MeshVerify.cs:112-116 的「三条」、CriteriaTableTests/ThermocoupleBasisTests——**这些区域你不要动**，避免冲突。
## 续做说明（2026-09-16，主会话）
上一次实施在 2026-09-15 20:27 因周额度用尽中断。工作树 D:\\WinForm\\r48_J 里已有它留下的**未提交、未暂存**改动，**不要推倒重来，接着做**：
- 已改的受追踪文件（git diff $(cat .basetree) --stat）：FlangeAutoSizer.cs、LineRunner.cs、Solver.cs、MeshVerify.cs、RampScreen.cs、ShellMesh.cs、ShellCurrent.cs、ShellThermal.cs、ShapeSearchPlan.cs、DesignScreen.cs、InstallReport.cs、LineDesignPage.cs、AnalysisPage.cs、R48RecipeFingerprintTests.cs（14 个，+448/−76 行）。
- 新文件（未跟踪）：Pt_Optimize.Tests/LineCaseCloneTests.cs、R48J_ClampFullFaceDefinitionGateTests.cs、R48J_ExperimentProbeTests.cs、R48J_InsulationSearchWiringGateTests.cs、R48J_SolverMeshAndMarkerGateTests.cs；deliverable/J路_J1…J10 十份带时刻的实验证据文件（J1、J2、J3、J4、J6×2、J6乙、J7、J8、J9、J10乙）。
- 中断时的状态：dotnet build 0 错误；正在跑 R48J_SolverMeshAndMarkerGateTests 并修其中一条断言（InstallReport 行的匹配）。注入实验、快速套件、UI 抓图、HANDOVER 注记（含测试数同步）、stream.patch 都还没做。
- 上一次的全部思路与命令记录（按时间，先读）：${SCR}\\j_prev_progress.txt。
- 没有 stream.patch。凡「做了」都要你自己重新验证过才算数；上一次写的证据文件可以引用但要核对内容与出处。`

const K_TASK = `你是 K 路（分工况判据）实施者。
用户原话（2026-09-15）：「比如空管时铂过热那条也不该按 5 ℃ 卡，只要J<11即可」「希望设计法兰能升温，升温后减少散热的用料最少法兰」「管内有热玻璃时电流小，法兰成为散热片」。主会话口径：
- 带玻璃稳态：管孔净流入 > 0、冷侧（热偶读数基准 − 较冷管根 ≤ 5 K）、热侧（最热铂 − 基准 ≤ 5 K）为硬判据，其余硬判据照旧（管 J、截面 J、①、熔点、局部热稳定、⑤⑥ 等现有硬项不变）。
- 空管到温稳态：设计判据只卡截面 J < 11（按设定 J+1，与现有一致）；热侧、冷侧、净流入降为参考量（照常计算与打印）。
- **场的有效性不是判据、两态都保留**：场没收敛、越铂熔点、散热表超界、电位场未收敛 ⇒ 该工况「判不了」，判不了不许当过（空管态也一样；这是为了守住 P1-9：P-a 后空管态不能没有读场的守门）。
待办条目（详见 ${GATE} 的 P1-3、P1-9、P3-4 及 P2-7 的判据部分）：
K1 LineRunner.Judge 按 LineCase.EmptyTube 一份开关分工况：判据清单（必备名单 LineRunner.cs:937-949 静态 ⇒ 加工况维）、CheckKind（Hard/Reference）、说明文字（全名、不带代号，写明「空管到温稳态：用户 2026-09-15 定只卡截面电流密度」）。AllOk/HardOk 口径随之。CriteriaTableTests.cs:50、ThermocoupleBasisTests.cs:234 等静态断言加工况维。判据表（HANDOVER 判据表 + CriteriaTableTests 核的那份）同步。
K2 InsulationSearch：DefaultCriteria 与可行性按同一开关（**调 LineRunner 的同一份判定，不许各判各的**）；P1-3：可行/★建议必须读整线结果的全部硬判据（AllOk/HardOk 与判不了标记：压接盖孔、压接进盘、管表超界等），不只三条；P1-9：参考项不进可行性、不进排序键（:101-106 两态最小裕度要剔参考项）；Round 要求两态 γ>0（:1354-1359）、空管判不了则整格点判不了（:1396-1399）、Feasible/MinNormMargin 取全部项（:159-160）、FeasibleByState 空管恒 0 印「空管单态没有窗口」（:240）——逐条按新口径改：空管态只作有效性过滤（逐格点，判不了 ⇒ 该格点判不了）+ 截面 J；报告头「分工况判据尚未落地」改为落地后的实际口径。
K3 MeshVerify.cs:112-116 第三处写死「三条」：按同一份分工况清单。
K4 界面：判据表/说明里出现工况的地方照实（空管态三项标参考），**抓图验**（--uishot）。
K5 门：①「空管态热侧超 5 K 不影响 AllOk，带玻璃态超 5 K 影响」（两态各造一个算例或用可注入的结果对象）；② 源码门：判据分工况只有一处实现（InsulationSearch/MeshVerify 不许自带清单）；③ 有效性守门：空管场判不了 ⇒ AllOk 为假且判词点名；④ 保温搜索可行性读了全部硬判据（造一个管 J 超限但三条都过的结果 ⇒ 不可行）。注入实验：把分工况开关改回旧写法 ⇒ 门红。
K6 HANDOVER：判据表与合并注记里「分工况判据待落地」改成落地记录（签名），记用户原话与口径；空管三项降参考的依据只写用户原话，冷侧/净流入的归属写明是「主会话按原话『只要J<11即可』的解读，已向用户复述未遭否定」。
注意：J 路同时在改 Solver（Finish/Gate/回收）、FlangeAutoSizer、LineDesignPage 搜形状与图纸路径、MeshVerify 的判词与已验戳（:380、388-392、463）、LineRunner 热稳定逐片（:2850-2868）——**这些区域你不要动**。`

// 主会话对各路实施报告里「需要主会话定」与「开放事项」的裁定（2026-09-16），审查者与修复者都要知道，不要把已裁定的事再当阻断。
const DECISIONS = {
  I: `
## 主会话裁定（2026-09-16，针对 I 路实施报告的开放事项）
1. 快速套件按原名覆盖的 12 份回归产物：本轮保留例外名单（写死在门里），不搬、不改；HANDOVER 待办里记一条即可。
2. ×25 停机规则本路不改（数值把关人已定口径：收缩比 ρ = 1/(1+Δx/ℓt)、放大倍数 1+ℓt/Δx；改法另派）。I3 只要测量与证据文件可信即可。
3. ApplySectionFloor 一步闭式对共用片偏高 1～2 格：本路不修，HANDOVER 待办记一条（带实测数与算例出处）。
4. 临时工作树 D:\\WinForm\\r48_I_G2F、r48_I_Eprev 本轮保留，供审查者复核；谁都不许删。
5. docs/Pt_理论模型_v6.0.md、v6.1.md 里同一句「上界」：修复阶段顺手改，与 DesignCurrent.cs 注释一致。
6. 板厚口径改后保温搜索没重跑：本路不重跑（另派），但 HANDOVER 里要写明旧记录的板厚是继承值、不能与改后混读。`,
  J: `
## 主会话裁定（2026-09-16）
- J 路是续做（见任务书末「续做说明」）；审查者要把上一次留下的改动与这次的一起审，不许因为「上次的」就跳过。`,
  K: `
## 主会话裁定（2026-09-16，针对 K 路实施报告「需要主会话定」两条）
1. 管 J 在空管到温稳态**保持硬判据**（不降）：用户原话「只要J<11即可」指的是电流密度类判据整体，管 J 与截面 J 都是 J<11。K 路现状即正确，不改。
2. 带玻璃稳态的 Failed 多一行「本工况的场判不了」：接受，不改。
3. 「空管态闭合不成立时按冻结管根报参考值、不打成判不了」：接受这个判断，但 HANDOVER 注记要写明这是实施者的判断、依据是什么（不是场有效性问题）。
4. 用户 2026-09-15 又定了工况地位：升温全程是第一步（先过），带玻璃稳态决定法兰设计成功与否，空管到温只卡 J<11 与场有效性。升温判据不在 K 路范围（另派）；K 路只要保证判据表/说明里的工况措辞与这个地位不矛盾。`,
}

// I、K 的实施在 2026-09-15 已完成（快套件分别 1008/1008、1027/1027；stream.patch 已在各自工作树里）；报告原样存档为文件，审查者先读全文。
const IMPL_DONE = (key) => `（${key} 路实施已于 2026-09-15 完成，实施者最终回复原样存档在 ${SCR}\\impl${key}_report.md ——先读全文；工作树里就是它做完的状态。）`

const STREAMS = [
  { key: 'I', repo: 'D:\\WinForm\\r48_I', task: I_TASK },
  { key: 'K', repo: 'D:\\WinForm\\r48_K', task: K_TASK },
  { key: 'J', repo: 'D:\\WinForm\\r48_J', task: J_TASK },
]

const results = await pipeline(
  STREAMS,
  (s) => s.key === 'J'
    ? agent(`${s.task}\n${RULES(s.repo)}`, { label: `impl:${s.key}`, phase: '实施' })
    : IMPL_DONE(s.key),
  (impl, s) => agent(`你是 ${s.key} 路的独立对抗审查者（只读：不许改文件、不许提交；可编译、可跑测试、可临时注入改动验门红绿但必须改回并逐字节核对）。任务是**找错**，攻不下来就说攻不下来。
该路任务书：
${s.task}
实施者报告：
${impl || '（实施者未返回）'}
${DECISIONS[s.key]}
改动：${s.repo}\\stream.patch（相对 .basetree）。
逐条查：A 每条待办是否真落地（不是只改注释/只加断言字符串）；B 新门若把生产代码改回旧写法会不会红（亲自注入至少三处）；C 新逻辑正确性（NaN、判不了传播、边界、工况开关读点全不全——grep 全部调用点）；D 同一件事有无第二份实现；E 行为变了却没有门；F 注释/HANDOVER 与代码数字一致否；G UI 改动是否抓图验过（看 PNG 路径是否存在、布局是否正常）；H 实测数据可不可比（同树同配方同网格？）；I 与另两路（I/J/K）的改动区域是否越界冲突。
输出：必须修（带证据与最小修法）/ 应修 / 查过没问题的类别。短。
${RULES(s.repo)}`, { label: `review:${s.key}`, phase: '审查' }),
  (rev, s) => agent(`你是 ${s.key} 路实施者，按审查意见修。逐条核实审查意见（不成立的写明理由与证据，不许无视），成立的修掉；重跑受影响的测试与快速套件；重建 stream.patch。
该路任务书：
${s.task}
${s.key === 'J' ? '' : '实施者报告：' + IMPL_DONE(s.key)}
审查意见：
${rev || '（审查未返回）'}
${DECISIONS[s.key]}
${RULES(s.repo)}
最终回复：逐条审查意见的处理结果；最终测试结果原样；stream.patch 是否已重建；仍开放事项。`, { label: `fix:${s.key}`, phase: '修复' }),
)

return { I: results[0], K: results[1], J: results[2] }
