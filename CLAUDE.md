# Pt_Optimize：Claude Code 入口

铂金直接加热系统（供料管 + 法兰）用量优化：满足全部约束的前提下使铂金总质量最小（README.md）。主程序 C# WinForms，`net8.0-windows`（Pt_Optimize/Pt_Optimize.csproj）。业主只有 Windows；Linux 只是云端容器里的镜像预跑。

## 0 基本准则（业主 2026-09-26 定；日后的基本准则，高于下文各节）

1. 在第一性原理的框架下，不许任何形式造假，不许推责式反应与回答，不许以拍板式脱责回答业主的问题（除非是第一性原理的需要）。
2. 记录到 HANDOVER 不代表完成了任务，更不是日后的借口。

（原话里「筐架」「定一性原理」按「框架」「第一性原理」录，「我」写作「业主」。）

执行时（开发者据上两条写的做法）：
- 例外的界线：第一性原理要的是给定条件，即业主的要求与目标、只有业主或现场知道的事实和数；只有这些才问业主。能由给定条件和物理推出来的，自己推、自己判断、自己负责，不列成选项让业主挑。§3 的「【待决定】」「给选项＋代价」只在这条界线内用。
- 出了问题，直说是什么、自己错在哪、怎么改；不拿前一个会话、环境、工具或业主当理由。
- 完成以事情本身做成、有证据为准。「HANDOVER 里写过」不能拿来解释没做、做错或没当面说清楚。

## 1 目录地图

- `Pt_Optimize/Core/`：纯计算（75 档），不碰界面；Linux 镜像只编它加不碰界面的测试（tools/linux_mirror/README.md）。
- `Pt_Optimize/UI/`：WinForms 页面；`Pt_Optimize/Program.cs`：命令行、`--selfcheck`、大量探针（10406 行）。
- `Pt_Optimize.Tests/`：xUnit，约 277 档；慢门带 `[Trait("速度","慢")]`。`tests/UiWiring/`：界面接线走查（Windows）。
- `Pt_Optimize.Geom/`：`net7.0-windows` + Rhino.Inside 子进程，读 .3dm（csproj 头注）；只能在装了 Rhino 8 的 Windows 上跑。
- `deliverable/`：证据档（739 个文件，含子目录；2026-09-23 a660baf 后），带开跑时刻；另有执行计划、落地审计、实施记录。无引用的旧证据档、旧界面截图、旧版 docx 共 352 个已移到归档分支 `archive/deliverable-2026-09-23`，取回命令见 `deliverable/归档清单_2026-09-23.md`（HANDOVER §0.-21 维护条）。
- `docs/`：论文与说明书。`*_v7.0.md` 是最新一版，标「草稿」、未定稿（执行计划 K1 ◐）；不带版本号的 `Pt_理论模型.md`／`Pt_工程师版.md` 是 v5.0，`DocRefTests` 仍核它们（决 80 待决定）；v6.x 的 md 只作历史（v1.0～v6.1 的 docx 已归档）。
- `tools/linux_mirror/`：Linux 镜像脚本；`督导/`：督导信件（历史）。

## 2 读法（上下文很贵，照做）

1. **先读 `HANDOVER_速读.md`**（本仓库根，≤ 250 行）。
2. **`HANDOVER.md` 禁止整读**（约 8900 行、1.1 MB）。只许 grep 或按行段读：
   - 找节：`grep -n '^## 0.-' HANDOVER.md`；全部节名：`grep -n '^## ' HANDOVER.md`；小节：`grep -n '^### ' HANDOVER.md`。
   - 2026-09-23 时的大致行段（行号会漂，先 grep 再读）：§0.-21 业主决定登记 约 8～32；§0.-20 约 34～106；§0.-19 约 108～242；§0.-18 约 244～330；§0.-17 约 332～416，其 庚（W06／W08 端到端）与 辛（R 分支结论）在「合并 2026-09-18」大节里，约 628～707。
   - 读法：一次不超过约 150 行，并且每行截断：`sed -n 'a,bp' HANDOVER.md | cut -c1-2000`。登记表（HO:3568 起）与 §8 沿革（HO:8800～8850）单行上万字（最长 HO:8828，26819 字符），先用 `awk 'NR>=a&&NR<=b{print NR, length}' HANDOVER.md` 看行长再读。
   - 常驻表：最高准则与需求登记表 `grep -n '最高准则\|用户明确要求登记表' HANDOVER.md`；判据限值出处 `## 1.83`；「应为 N」那一行在 `## 8.` 之后（约 8800）。
3. **`deliverable/` 是证据档**：不要整读目录里的档；按名字片段找可以：`find deliverable -name '*片段*'`，或 `ls deliverable | grep 片段`（例如找某探针最新一跑的 `_本次开跑于`）。以下超大文本只许 grep：`R48_D6_门阈值出处清单_2026-09-23.md`（608 KB）、`R48_保温搜索_小规模_第二版_2026-09-15.txt`（630 KB）、`R48_F6_对抗审查_78条发现_2026-09-23.json`（313 KB）、`开发执行计划_2026-09-23_各组详稿.md`（275 KB）；另有 `R48_L_升温管段伸长探针_场转储_…205421.txt`（421 KB）等，`find deliverable -size +150k ! -name '*.png'` 可列全。
4. **`deliverable/开发执行计划_2026-09-23.md`（917 行）**：第 1 节现状 22～38 行，第 2 节总览表 39～122 行，第 5 节【待决定】从 383 行起；按决号 grep：`grep -n '\*\*决 98' deliverable/开发执行计划_2026-09-23.md`。
5. **超大源码按 grep 定位后分段读**：`Program.cs` 10406 行、`UI/LineDesignPage.cs` 5992 行、`Core/LineRunner.cs` 4614 行。
6. 测试会读 HANDOVER 正文：`HandoverGateCountTests`（「应为」数，另核「待办队列」「❌ 未做」「第 9 件是什么」这几串字）、`CriteriaTableTests` 读 `## 1.83`、`RequirementRegisterTests`、`CliUiParityTests`、`OptimizationModelReconcileTests`（核 §0.0.3 对帐表）、`DocRefTests`（从 HANDOVER 标题解析节号，要求 ≥ 80 个）；改 HANDOVER 不能动这些锚点。拆档是 K2，要动 8 个读它的测试，另开工单（HO:18 记 8 个，这里点名 6 个，其余未查到）。`BannedVocabularyTests` 扫仓库根全部 `*.md`，被禁词见该档 `Banned`。

## 3 铁律（原意出处见括号）

- 第一性原理，杜绝一切造假凑数的手段（HANDOVER §0.-19 引言，:110）。
- 业主原则「通用型解法」：换不同尺寸的铂金管与法兰输入都要出结果；网格、求解器、判据里不许有绑死某一尺寸的常数，要么由物理算出，要么写明制造出处；真无解时写明「无解」、卡在哪条约束、下一根可动的杠杆，拒答不静默（HO:17，高于任何单条规则）。
- 可以做错，不可以让人以为的和事实不一样；方向错可以说出来改，不许静默替换理论、公式、计算；不拿结论代替选项（会改变要求范围的事，给选项＋代价）；不给没有证据的状态陈述（HANDOVER「最高准则」:3540 起）。
- 阈值一个不挪：不为过门挪阈值；新阈值写出处（数学推出／制造出处），看过数才定的写「选定」（§0.-19 乙、§0.-21 决 29 第 5 点）。
- 需求状态只写 ✗／◐／✅，✅ 必须附证据；开发者不标「不做」；要业主取舍的写【待决定】，列选项与成本；已先做的写「已按选项 X 做，待追认」+ 回退代价（HANDOVER 登记表 :3568 起；执行计划 §7）。
- 每道门写明覆盖／不覆盖，不许只写「绿」；只印不判的门写明只印（执行计划 §7 第 1 条）。
- 重录任何数只能带写明的变因：旧值 → 新值、变因、两份证据文件名；逐位门另写「两跑逐位相同」；说不出变因不许重录（执行计划 §7）。
- 提交说明三段：跑了什么（门名、平台、耗时）／不覆盖／成本（人时、机时）（执行计划 §7；HANDOVER「四道防线」:3565）。
- 论文与记录：判据只用全名，不写代号；不带人称；不用破折号（docs/Pt_理论模型_v7.0.md:1416；决 85 另问论文版是否全禁代号）。
- Linux 数是预跑，Windows 是记录（决 03 (a)，§0.-21）；Linux 数入库都标「Linux、待 Windows 重录」。
- 业主决定一律登记在 HANDOVER §0.-21（并在执行计划第 5 节该决号后记答）。
- 查不到的写「未查到」，推出来的写「推断」，不编（执行计划开头）。

## 4 构建与测试

**Windows（记录以此为准）**：
```
dotnet restore --disable-parallel
dotnet build Pt_Optimize.sln
dotnet test --filter "速度!=慢"
git config core.hooksPath .githooks   # 提交钩子四道门（HANDOVER §8）
```

**Linux 镜像（只作预跑，tools/linux_mirror/README.md）**：
```
apt-get install -y dotnet-sdk-8.0                      # 一次性
tools/linux_mirror/mk_linux.sh . [目录名]               # 1 生成 .linux/ 镜像项目
python3 tools/linux_mirror/fix_mirror.py . [目录名]     # 2 编译，碰界面的测试档排除
cd .linux && dotnet test Tests/Tests.csproj -c Release --no-build --filter "速度!=慢"   # 3
```
- 镜像必须在仓库里（测试「往上找 HANDOVER.md」定根）；`.linux*/` 写进 `.git/info/exclude`。
- 排除名单：README 记合并树（273 个测试档）时排除 29 档；现在的 `.linux/ui_excluded.txt`（09-23 11:33 生成）列 28 档，镜像重新生成时这个数会变，以当次文件为准（现列 `DocRefTests`、`LineCaseCloneTests`、`R48CavityRadiationGateTests`、`R48J_SolverMeshAndMarkerGateTests`、`R48MStopTolGateTests`）。
- **慢门只跑点名的**：`--filter "FullyQualifiedName~类名"`，不跑全套慢门（单条慢门几十分钟到数小时，§0.-19 庚）。
- **`HandoverGateCountTests` 在镜像里恒红**：它反射数全部用例，镜像少了界面测试，数不上；「应为」数用镜像差量加到上一次 Windows 实数（README 末段）。`R48MGradeDropdownTests` 的 BuildList 在镜像里也必红（界面编辑器是空占位，§0.-18 丙）。
- **跑快套件会改写受跟踪的 deliverable 文本**（§0.-19 己 记了 8 份，§0.-18 丙 记了 9 份）：提交前 `git status`，把它们 `git checkout -- <文件>` 还原；不要顺手提交。
- 4 核机器（HO:628）；同机并跑时 load 10～14（§0.-21 决 04，HO:26）、10～15（§0.-19 庚标题，HO:220），耗时只作量级，时间闸会失真（时间闸在测试常量里，决 04 (b)）。决 98 乙 Windows 工单对 Rhino 相关慢门（`RealDrawingParityTests` 等）用 `-- xUnit.MaxParallelThreads=1`（`deliverable/R48_决98_乙_图纸曲线读孔径_Windows工单_2026-09-23.md:198`），只限那几道门，不推广到所有长跑。
- 逐位门 `R48ClampFaceGateTests.c`、`R48ClampRecipeTests.d` 只在 Windows 判，不在 Linux 重录（§0.-17 丙）。

## 5 约定

- 证据档命名 `deliverable/<名字>_本次开跑于YYYY-MM-DD_HHMMSS.txt`，经 `DeliverableOut.Stamped`（Pt_Optimize.Tests/DeliverableOut.cs:34，格式 `yyyy-MM-dd_HHmmss`）；文件头写平台、分支、提交号、是否同机并跑。`R48DeliverableWriteGuardTests` 守写法。
- 每条规则改动带「改回」参数（旧规则可对拍），并出「开 − 关」归因档（§0.-21 决 01）。
- 重录带变因（见铁律）；半角的「应为 N/N」全文只许一个数（规矩在 HO:5680、HO:435，锚点在 §8 的 HO:8800），而且要等于反射实数；历史数照现有写法用全角「／」（如 HO:1738「应为 **1273／1273**」），门的正则只认半角「/」。
- 业主决定登记处：HANDOVER §0.-21；待决定清单：执行计划第 5 节。
- HANDOVER 新节放在文件顶部，号为 §0.-N（取顶部现有号再往下一号；进行中的节草稿暂记 §0.-2x，合并时定），分 甲 起因／乙 改了什么／丙 跑了什么／丁 不覆盖／戊 成本／己 待决定；同时改三处：登记表那一行的 ✗／◐／✅ 与覆盖率、待办队列那一行、落地审计 `deliverable/落地审计_2026-09-23.md` 对应行；标 ✅ 的必须指向证据文件（执行计划 §7 第 3 条，计划:851～857）。改 HANDOVER 第 4 行「最后更新」是现行惯例，§7 里没有出处。
- 只改文档的提交不触发钩子（`.githooks/pre-commit` 的 WATCH 名单无 docs 与 HANDOVER），说明里写明另跑了哪些文档门。
- commit 说明三段（见铁律）。

## 6 当前分支与 PR（2026-09-23 核）

- 分支 `claude/tender-faraday-bvs4in`，头 a660baf，与 origin 同步；归档分支 `archive/deliverable-2026-09-23`（= 20a6ed2）。
- PR #1（github.com/stevenlu101/Pt_Optimize/pull/1）：open、**草稿**，基分支 `feat/geom-and-flange-accounting`（20a6ed2 时 39 个提交，之后又加 a660baf）。
- 进行中的工作树在会话暂存区（`git worktree list`），不在仓库内，容器销毁即失；不要在主树里碰它们。
