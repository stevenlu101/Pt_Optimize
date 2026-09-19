K 路对抗审查结论（2026-09-16，只读；树已按原字节还原，`git diff 基线树` 仍只有 K 的 16 个文件，deliverable 无差异）

## 必须修

**M1 工作树 ≠ stream.patch，且树里的是弱写法（上一轮被中断的审查注入没还原）**
- 证据：`git diff $(cat .basetree)` 与 `stream.patch` 只差一行 —— `Pt_Optimize/Core/InsulationSearch.cs:1271`
  树：`else if (r.Flanges.Any(f => !f.FieldsConverged)) whys[s] = $"…场判不了：" + string.Join("；", bad);`
  补丁（:1263）：`else if (bad.Length > 0) whys[s] = …`
- 出处链：InsulationSearch.cs mtime 09-15 20:27 > stream.patch 19:50；`scratchpad/kaudit_bak/InsulationSearch.cs`（09-15 20:13，上一轮审查的注入前备份）`diff --strip-trailing-cr` 与树只差这一行、内容 = 补丁写法 ⇒ 上一轮审查改回旧条件后没还原（行尾也从 CRLF 变成了 LF）。
- 后果：整线解里压接盖孔／压接进盘／管表超界／保温分界判不了 **不会** 让该轮判不了（`bad` 只进文字，条件仍只看 FieldsConverged），只剩终点 `FinalLineVerdict` 读 AllOk 兜底。实施者报告与注释（:1268「读唯一一份 FieldUndeterminedReasons」）说的是补丁写法。
- 最小修法：合并以 `stream.patch` 为准（或把 kaudit_bak 那份拷回、恢复 CRLF）；并补 M2 的门。

**M2 这一处没有行为门（树原样全绿）**
- 证据：树原样跑 K 相关 8 组门全绿（`scratchpad/kaudit2_gates_asis_185449.log`，EXIT=0）；注入 X7 把它改成补丁写法也全绿（120/120）。门2 只断言字符串 `var bad = r.FieldUndeterminedReasons;`（StateCriteriaGateTests:633），不验条件。
- 最小修法：把 whys 那段提成公开纯函数（如 `LineStateWhy(LineResult r, string state)`），门造一个 `Ok=true、Converged=true、Flanges[0].ClampCoversHole=true、FieldsConverged=true` 的结果 ⇒ 非空。

## 应修

- **S1 界面文字与代码自相矛盾（也与 09-16 裁定 1 矛盾）**：`LineRunner.cs:991 StateDowngradeNote`（进判据 Note）、`InsulationSearch.cs:935` 报告头、`MeshVerify.cs:145` 拒答判词都写「只卡法兰截面电流密度」，而分工况表里管 J 在空管态是硬判据、说明书同页由表生成的段落列了「管 J」「法兰截面 J」。改成「只卡电流密度（管 J 与法兰截面 J）与场的有效性」；门1 用 StartsWith(StateDowngradeNote) 不会红。注释 Criteria.cs:56、MeshVerify.cs:113 同改。
- **S2 「待主会话定」已过期**：`LineRunner.cs:948`、`HANDOVER.md:2840` 第五列「K 路 2026-09-15 Opus 5 未改、待主会话定」→ 按裁定 1 记「管 J 空管态保持硬判据」，签 2026-09-16。
- **S3 裁定 3 的注记缺**：HANDOVER 里 grep「冻结管根」0 命中；只在 InsulationSearch.cs:1499-1500、:1548 注释里。要写明是实施者判断、依据「闭合不成立是 γ 线性化问题、不是场有效性」。另：注入 X3（EvaluatePoint 里 `if (judged)` 拿掉、闭合不成立一律判不了）120/120 全绿 ⇒ 这条判断没有门，注记里也要写「无门」或补门。
- **S4 MeshVerify 拒答只有源码门**：注入 X1（保留 `if (RefuseForState…)` 原句，只删 `return res;`）27/27 全绿；MeshVerifyLineCaseTests 无空管算例。补一条：空管 caseFactory ⇒ Verdict 含拒答句且工厂不解（仿 MeshVerifyLineCaseTests:79 写法）。
- **S5 层可行接线只有源码门**：注入 X8（`lr.FinalLineAllOk = ok` 改成恒 true）120/120 全绿。至少在 HANDOVER 记「无行为门」；有合成整线搜索的门再补。
- **S6 门2 源码串有歧义**：`"EmptyTube = c.EmptyTube }"` 在 LineRunner.cs:1622 与 :2016 各一处；注入 X2 只删 :1622，门2 绿、靠门1b（58 s 真解）才红。改成核 :1622 整行或计数 == 2。
- **S7 新口径下没有任何一次保温搜索实测**（实施者已列开放项 3；P1-3 建议的实验「管 J 贴限的层看建议层 AllOk」没跑）。现有 deliverable/R48_保温搜索_*_2026-09-15.txt 都是旧口径；R48InsulationSearchProbeTests 类注释 K1「某一单态可行 = 0 ⇒ 该态单独没有窗口」按新口径已不成立。引用「可行／★建议」前须重跑（甲 22.7 min）。
- **S8 文档小错**：HANDOVER.md:5737 引「合并注记里『判据口径已落地』一句」，该句实际写法是「★ 2026-09-15 Opus 5（K 路）：已落地…」；Criteria.Html 两工况段落的有效性清单漏「保温分界判不了」；说明书截图（uishot_K/manual/判据全表_空管到温稳态列.png）只到参考量表，新段落未入图（HTML 里有）。

## 查过没问题

- **A 落地**：K1–K6 都是真改动：分工况表单一来源 `RequiredByState`（LineRunner.cs:951）、`RequiredFor/StateKindOf`、Judge 末尾 `ApplyStateCriteria`（:3838）、工况位写进两处 `new LineResult`（生产里只有 :1622、:2016 两处）、`FieldUndeterminedReasons` 读位不读文字、MeshVerify `TolTemplate(bool)/RefuseForState`、说明书加列+段落、HANDOVER 表加列、测试数 1158（HandoverGateCountTests 绿）。P3-4 注释已改（LineRunner.cs:452-456）。
- **C 正确性**：Judge 里没有任何参考量判据的名字以 8 个前缀开头（grep :3200-3830）⇒ 带玻璃态 Kind 逐位不变；前缀无碰撞；五种场有效性情形在带玻璃态本就会把 NetFlux 标判不了（dependsOnFlangeFields :2440、管表超界那遍标全部非豁免项）⇒ 带玻璃 AllOk 不翻（推理）；HardOk 在 LineRunner 外无消费者；`--meshadapt` 的 lcA 只造带玻璃（Program.cs:5073）故 `tolA[0..2]` 安全；三条判据的其他消费者（Solver/Sizer/FlangeAutoSizer/ShapeReview/InstallReport/LineDesignPage/ManualPage）都在带玻璃链路。
- **D 第二份实现**：无。Criteria/InsulationSearch/MeshVerify 都查表；ManualPage.cs:882-883 那张「硬判据」表是设计记录表（无工况维），不算分工况实现。
- **B 注入**（`scratchpad/kaudit2/inject2_190057.log` 与 `inject2_stdout2.txt`，每次 sha256 还原核对「相同」）：X4 参考项 NaN 打判不了 → 门5 红；X5 漏读管表超界 → 门3 两态红；X6 说明书空管列不查表 → Glossary + 门2b 红；X2 → 门1b 红。实施者 E1–E8 日志在 `scratchpad/kinj/`（09-15 19:24–19:28）已核对存在。
- **H 实测可比**：门1b 在本树重跑，数逐位同实施者表（带玻璃 −21.131 W／138.667 K／−17.414 K；空管 −38.345 W／199.297 K／−27.741 K；截面 J 27.7/11）—— 同树同配方同网格（`kaudit2_gates_asis_185449.log`）。
- **G 抓图**：uishot_K 25 张 PNG（09-15 19:37）+ 说明书表 PNG（19:45）存在，新列排版正常（已看图）。
- **I 越界**：未动 Solver/FlangeAutoSizer/LineDesignPage/LineRunner 2850-2868；MeshVerify K 的插入在基线 :371-376、改 :432，与 J 的 :380/388-392/463 相邻不重叠，合并时留意。
- **编译/测试**：`dotnet build Pt_Optimize.sln` 0 错误；`tests/UiWiring` 0 错误；快速套件树原样 `已通过! 失败 0，通过 1027，总计 1027，6 m 13 s`（`scratchpad/kaudit2/fast_suite_asis_190546.log`）。它改写了 8 份 deliverable 文件，我的 tar 备份命令失败，已按基线树 blob 逐份还原（CRLF 同库内约定），`git diff 基线树 -- deliverable` 为空。

## 不确定
- M1 的成因是推断（备份时刻与内容吻合），不是看到还原失败的日志。
- 带玻璃 AllOk 不受 `FieldUndeterminedReasons` 影响是推理，没有逐情形真解。