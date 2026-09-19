# impl:K 实施者最终回复（2026-09-15 首跑，原样；agent a2462231ecb21f6f1）

K 路做完了，没有提交。
- 快速套件 1027/1027 全过（`D:\WinForm\r48_K\...\scratchpad\fast_suite_K.out`）。
- 8 个注入实验每个都能让门变红。
- 补丁已重建：`D:\WinForm\r48_K\stream.patch`，改动 16 个文件。
- 有两处和字面原话可能不一致，需要你定（见文末「需要主会话定」第 1、2 条）。

## 逐条

**K1 LineRunner 判据分工况**
- **分工况表只有一张**：`LineResult.RequiredByState`（LineRunner.cs:951），每条判据带「带玻璃稳态」「空管到温稳态」两列 Kind。
  - 本工况的必备名单用 `RequiredFor(emptyTube)`（:970）取；单条判据的 Kind 用 `StateKindOf`（:978）查。
  - 原来的静态 `Required` 已删，调用处都要写明是哪个工况。
- **Kind 在 Judge 末尾盖**：Judge 调 `ApplyStateCriteria(c.EmptyTube, checks)`（:3838）。
  - 降为参考的判据，说明最前面加一句「空管到温稳态：用户 2026-09-15 定只卡法兰截面电流密度（「只要 J<11 即可」）……带玻璃稳态下本条仍是硬判据」。
  - 值、Ok、判不了照常计算和打印。
  - 带玻璃稳态下这一步不改任何一条的 Kind。
- **工况位写进结果**：新增 `LineResult.EmptyTube`，RunOnce（:1622）和管侧单解（:2016）写入。`MissingChecks`、`HardOk` 按它取名单。
- **场的有效性两态都保留**：新增 `FieldUndeterminedReasons`（:1013）。
  - 读的位：Ok 为假（越过熔点、段解失败）、逐片 FieldsConverged（温度场或电位场没收敛、超散热表上限）、保温分界判不了、压接盖孔、压接进盘、逐段管表超界。
  - 只要有一条，`AllOk`（:1070）和 `HardOk`（:1050）就为假，`Failed` 里点名「本工况的场判不了」。
  - 带玻璃稳态下这一项不会改变任何 AllOk：这几种情形原本都会把管孔净流入标成判不了，或者 Ok 为假、没收敛。
- **P3-4**：`LineCase.EmptyTube` 注释里「全部判据都要过」改成了落地后的说法。
- **静态断言加工况维**：CriteriaTableTests、ThermocoupleBasisTests:234、RequiredChecksTests、CriteriaGlossaryTests、FineResolve、FlowDeadEnd、UiWiring 都改了。

**K2 InsulationSearch（保温搜索）**
- **每项对到整线判据**：`Criterion.LineKey` 记对应哪条整线判据；`Kind`（:163）只查 `StateKindOf`；`PlateTerms`（:481）是名字与整线判据的对应表，不含工况。
- **参考项不进选择**：`Feasible`、`MinNormMargin`、`Worst`、NaN 判不了、`FeasibleByState` 都只看卡交付的项（:183 起）。
  - 空管态这一片的项全是参考项时，进 `ReferenceOnlyStates`，报告印「不逐格点卡（场判得了 N 格）」，不再印成 0 个窗口。
- **γ 要求**：Round 只在本工况有卡交付项时要求 γ>0（:1492）；空管态 γ 量不出只会让参考值不闭合。空管态闭合不成立时，按冻结管根报参考值（:1541）。
- **逐格点有效性照旧**：任一态单片场判不了，整个格点判不了（按口径保留）。
- **工况位由框架盖**：新增 `EvaluateView`（:1578），评估函数返回的每项都由它按 `v.EmptyTube` 盖工况位；`v.EmptyTube` 取自解出本态的整线算例。
- **P1-3 整线判定**：
  - 整线两态的场有效性读 `FieldUndeterminedReasons`（:1268）。
  - 新增 `FinalLineVerdict`（:1211），读终点选择上整线两态的 `AllOk` 和 `Failed`。
  - `LayerResult.Feasible` = 逐片可行 且 终点整线两态全过（:796）；★ 建议层只从这里取。
- **报告头**：「⚠ 分工况判据尚未落地」换成按分工况表逐态生成的实际口径。
- **P2-7**：默认评估函数的门改用非缺省限值 3.7／4.3 K，另加 LineKey、Kind 断言。

**K3 MeshVerify（加密复算）**
- `TolTemplate(emptyTube)`（:115）用分工况表过滤容差表 `MeshTolerances`（:124）。带玻璃稳态仍是原来那三条，次序和容差不变。
- 空管态三条全是参考 ⇒ `RefuseForState`（:139）拒答，判词写「不能据此说这个设计过了」。它接在 :404，`TolTemplate(lc.EmptyTube)` 在 :468。
- 现在界面和命令行都不造空管算例做复核。
- Program.cs 的 `--meshadapt` 改成 `TolTemplate(lcA.EmptyTube)`。

**K4 界面**
- 使用说明「判据全表」硬安全线那张表加了一列「空管到温稳态」（卡交付／只作参考，取自同一张表），表下加一段两种工况的说明，不带人称。
- §1.83 判据表加了同名一列，CriteriaTableTests 两列都核。
- **抓图**：
  - `--uishot` 出了 25 张（`uishot_K\`）。WinForms 页面这一路没改。
  - 使用说明页是 WebView2，截出来 `05_使用说明.png` 里网页区域是空白，看不到这张表。
  - 所以另用 `--cli --manual` 导出 HTML，再用无头 Edge 渲染成 `uishot_K\manual\判据全表_空管到温稳态列.png`。
  - 第一次渲染时新列被挤成一字一行，已加 nowrap 修好，重新抓图确认。

**K6 HANDOVER**
- 测试数改成应为 1158/1158（+24，逐项列了）。
- 合并注记里「判据口径待落地」改成落地记录，签「2026-09-15，Opus 5（K 路）」。热侧降参考只写用户原话；冷侧和净流入写明是「主会话按原话『只要J<11即可』的解读，已向用户复述、未遭否定」。
- §1.83 表头说明也加了同样的记录。

**没动 J 路的区域**：Solver、FlangeAutoSizer、LineDesignPage、LineRunner 2850-2868、MeshVerify :380／388-392／463 都没碰。MeshVerify 我改的是 :404 附近加的拒答和 :468 那一行，离 J 的行很近，合并时可能冲突。

## 新测试（StateCriteriaGateTests，9 个用例）与注入实验

| 门 | 验什么 |
|---|---|
| 门1 ×2 | 热侧用生产的 `ThermocoupleChecks` 在合成接头数据上造出 7/5 K：空管态 AllOk 为真，带玻璃态为假 |
| 门1b | 真解（两段三片、开箱网格，约 1 分钟）：空管结果三条标参考、工况位接上；把真结果热侧改成超限，只让带玻璃的 AllOk 变假 |
| 门2 | 源码门：`GlassKind`／`EmptyTubeKind` 只出现在 LineRunner.cs；消费者必须调 `StateKindOf`，不许按工况或态号自己选 Kind |
| 门2b | 保温搜索、MeshVerify、Criteria 三个消费者跟着同一张表走 |
| 门3 ×2 | 8 种场判不了的情形：AllOk、HardOk 为假，Failed 点名；并自证空管态只看判据表会报全过 |
| 门4 | 管 J 超限、逐片三项都过的整线结果 ⇒ 终点判定不可行、层不可行 |
| 门5 | 保温搜索逐格点：参考项不进可行集和排序；评估函数写漏工况位时由 EvaluateView 盖上 |

注入实验：每次把一处改回旧写法，编译、跑门、原字节还原。日志在 `...\scratchpad\kinj\E*_*.log`。

| 注入 | 结果 |
|---|---|
| E1 分工况表退回两态同一份 | 红 11（门1、1b、2b、3、5，Glossary，Thermocouple，CriteriaTable 4 个） |
| E2 AllOk 不读场有效性 | 红 3（门2、门3、门4） |
| E3 逐格点可行取全部项 | 红 1（门5） |
| E4 层可行不读终点整线判定 | 红 1（门4） |
| E5 终点整线判定只读三条 | 红 1（门4） |
| E6 EvaluateView 不盖工况位 | 红 1（门5） |
| E7 Judge 不盖 Kind | 红 2（门2、门1b） |
| E8 两态都要求 γ | 红 1（门2，只有源码门拦得住） |

## 测试命令与结果（原样）

- `dotnet build Pt_Optimize.sln` 和 `tests/UiWiring`：`0 个错误`
- `dotnet test Pt_Optimize.sln --no-build --nologo --filter "速度!=慢"`（19:30:09–19:36:28）：`已通过! - 失败: 0，通过: 1027，已跳过: 0，总计: 1027，持续时间: 6 m 17 s`
- 抓图后改了 nowrap 和一句措辞，重跑相关 81 条（CriteriaGlossary、NoCriterionCodeInUi、DocRef、ThermocoupleBasis、CliUiParity、CriteriaTable、HandoverGateCount、状态门）：全过。
- 快速套件改写了 8 份 deliverable 文件：跑前整目录备份，跑后已还原，与备份比 0 差异。

## 实测数据：门1b 真解

算例：内置设计 0，两段 1150/1080 °C、各 300 mm，开箱网格。出处是本次 `dotnet test --logger console;verbosity=detailed` 的屏幕输出，没有落文件。这是门的算例，不是设计结论（截面 J 27.7 是这个算例本身不过）。

| 判据 | 带玻璃稳态（硬） | 空管到温稳态（参考） |
|---|---|---|
| 管孔净流入 W | −21.131/0 超 | −38.345/0 超 |
| 最热铂高出热偶读数 K | 138.667/5 超 | 199.297/5 超 |
| 管根低于热偶读数 K | −17.414/5 | −27.741/5 |
| AllOk 不过的项 | 热侧、净流入、截面 J 27.7/11 | 只剩截面 J 27.7/11 |

## 需要主会话定
1. **管 J 在空管态仍按硬判据**：它读本工况的段电流。原话「只要 J<11」是否连它也降，我没改，改表一行即可。升温、舌片自由段、圆盘盖得住管孔、截面 J 与工况无关，两态逐位相同，不受影响。
2. **带玻璃稳态的 Failed 会多一行**：场判不了时多出「本工况的场判不了」这一行。AllOk 不变，但界面文字会变长。

## 仍开放
1. 「升温」一条还在「吃法兰场」名单里（LineRunner.cs:2368 附近）。现在守门不靠它，名单没动。
2. 逐格点单片解没有单独查「超散热表上限」。生产配方下上限就是铂熔点（ShellThermal.cs:194、347），与已查的越过熔点是同一个阈值；只供测试的散热表参数下不等价。
3. 慢探针 R48InsulationSearchProbeTests 打印的「可行」仍读逐片可行（StartsOutcome.Feasible），不含终点整线判定。已有的保温搜索证据文件都是旧口径下跑的，没有重跑。
4. 空管态降为参考的三条，说明里仍带原来的操作建议（前面已加「只作参考」一句）。
5. HANDOVER 测试数那一行是超长单行，合并时和其他路大概率冲突。

## 不确定的地方
- 空管态闭合不成立时，我按冻结管根报参考值、不把格点打成判不了。理由是它不是场的有效性问题；这是我的判断，没有人确认过。
- 门1b 让快速套件多了约 1 分钟。

## 改动文件
- `D:\WinForm\r48_K\Pt_Optimize\Core\LineRunner.cs`
- `D:\WinForm\r48_K\Pt_Optimize\Core\InsulationSearch.cs`
- `D:\WinForm\r48_K\Pt_Optimize\Core\MeshVerify.cs`
- `D:\WinForm\r48_K\Pt_Optimize\Core\Criteria.cs`
- `D:\WinForm\r48_K\Pt_Optimize\Program.cs`
- `D:\WinForm\r48_K\HANDOVER.md`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\StateCriteriaGateTests.cs`（新）
- `D:\WinForm\r48_K\Pt_Optimize.Tests\RequiredChecksTests.cs`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\CriteriaTableTests.cs`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\CriteriaGlossaryTests.cs`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\ThermocoupleBasisTests.cs`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\MeshVerifyTests.cs`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\R48InsulationSearchEngineTests.cs`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\FineResolveAfterVerifyTests.cs`
- `D:\WinForm\r48_K\Pt_Optimize.Tests\FlowDeadEndTests.cs`
- `D:\WinForm\r48_K\tests\UiWiring\Program.cs`

补丁：`D:\WinForm\r48_K\stream.patch`