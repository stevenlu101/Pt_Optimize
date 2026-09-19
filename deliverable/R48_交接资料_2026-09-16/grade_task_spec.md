# M 路工单草案：牌号数据齐全度灰显 + 牌号全链路接线 + 铜/镍数据入库

草案日期 2026-09-16（Opus 5）。状态：待 r48_H 第四轮（DataCompleteness 函数）合入 r48_M 后，在 D:\WinForm\r48_Mx 工作树上实施；改 UI 必须 --uishot 抓图。

## 0. 用户口径（原话）
- 2026-09-15「还是以纯铂为设计标准，除非工程师在APP特别设定」⇒ 默认牌号 Pt，默认路径数值逐位不变；工程师选了别的牌号要全链路生效。
- 2026-09-16「铜的数据网上找，数据不全(电阻/热膨胀/蠕变应力)的铂金合金先以灰色不可选展示，只用数据全的铂金合金」「铜与镍的数据网上找」。

## 1. 下拉灰显（M1）
- 数据源：MaterialDb.DataCompleteness(grade)（r48_H 第四轮提供：OwnResistivity / OwnExpansion / OwnCreepWithPoints / IsComplete / Missing[]）。
- 齐全 ⇒ 可选：Pt、Pt-Rh/90-10、Pt-Rh/80-20。不齐全 ⇒ 灰显不可选，旁注缺什么（全名，如「热膨胀：无该牌号数据」「电阻率：借用纯铂」「持久强度：区间推定、无原始点」）。
- PropertyGrid 的 GradeNameConverter（MaterialDb.cs:101-119）现在只给 StandardValues；灰显要换成自定义编辑器或下拉控件（WinForms PropertyGrid 不支持单项禁用 ⇒ 用 UITypeEditor 下拉列表，灰项 Enabled=false；或把牌号从 PropertyGrid 挪到独立 ComboBox，DrawMode OwnerDraw 灰字）。选择被拒时不许静默改回 Pt：提示「该牌号数据不全（缺 …），按用户 2026-09-16 规则不可选」。
- 存档 finaldesigns/*.fd.json 里若已存不齐全牌号：读入时照原样显示但标「数据不全（历史存档）」，重算前必须改成齐全牌号；不许自动替换。
- 门：Pt/PtRh10/PtRh20 可选、其余灰显；灰显项旁注文本与 DataCompleteness.Missing 逐字一致（门不手抄：读同一函数）；抓图 uishot 看到灰项。

## 2. 牌号全链路接线（M2）
- 现状（记忆 pure-pt-is-design-standard）：求解链约 40 处直接调 Materials.PtResistivity / PtTcr / RhoRef·(AlphaFit+2·BetaFit·T) / PtFitMaxC / PtThermalK / PtCp / PtDensity / PtMeltC / PtAlphaExp…，只有管强度 ④ 读 GradeName。
- 目标：DesignInputs.GradeName 选定的牌号在整条链生效：
  - 电阻率 ρ(T)：MaterialDb[grade] 的 ρ0[1+α(T−T0)+β(T−T0)²]（PtResistivityData，工作簿口径）；PtFitMaxC 按牌号数据上限；
  - 热膨胀 ε(T)：PtThermalExpansion 按牌号（Direct 才可选，所以不会碰借用）；
  - 持久强度：PtCreepWorkbook 按牌号（已按牌号）；
  - 密度：MaterialDb.DensityKgM3；熔点：按牌号（Pt 1768.2；PtRh10/20 固相线要有出处——JM 1961 评论 Fig.1 引 Acken 1934，需查表；无出处前保守取纯铂熔点并标注）；
  - **热导 k(T) 与比热 cp(T)：工作簿没有 PtRh10/20 的数据**。Materials.PtThermalK/PtCp 是纯铂函数；PtRh 合金 k 明显低于纯铂（JM 2005：加溶质使 k 急降；Wiedemann-Franz 成立 ⇒ 可由 ρ(T) 用 L·T/ρ 估 k，但要标「按 Wiedemann-Franz 由电阻率推算，非实测」；cp 可按 Neumann-Kopp 从 Pt/Rh 元素 cp 按质量分数估，标「估算」）。**这是一处会出「看起来正常的错数」的地方：接线时 k/cp 必须按牌号有来源，否则该牌号不能算齐全。** 建议把 k、cp 的来源也纳入齐全度（第四类），待用户定：是「上网找 PtRh10/20 的 k(T)、cp(T)」（候选来源 Touloukian TPRC《Thermal Conductivity: Metallic Elements and Alloys》、JM Platinum Metals Rev. 1961 5(3) 评论下篇），还是允许 Wiedemann-Franz 推算并标注。
- 默认 Pt 路径逐位不变：接线用「grade == "Pt" 时走原函数」的写法不算——要同一份实现对 Pt 给出逐位相同的数（转储门：现有网格转储与 R48LineDumpTests 六个算例 SHA 不变）。
- 门：源码门扫描 Core 里不许再直接调 Materials.Pt*（白名单只留 MaterialDb/PtResistivityData 内部与纯铂常数定义处）；换牌号门：PtRh10 下管段解的 ρ(T) 逐点等于工作簿式；LineDump 六算例 SHA 不变；界面结果页显示当前牌号全名与「电/热/膨胀/持久 各按该牌号数据」一行。

## 3. 铜与镍数据入库（M3）
- 来源清单与数值：scratchpad\cu_ni_data_2026-09-16.md（NIST Hidnert 1943 铜 CTE 9 点；CDA Pub 22 铜电阻率/TCR/蠕变三行；KME Cu-OFE；Special Metals Nickel 200 Table 3；VDM Nickel 200/201 Table 3a、Table 5）。
- 铜：新 Core/CopperData.cs（或并入 BusbarSizing）：CuRho20/CuAlpha 补出处注记（CDA Pub 22 Table 2/3，IACS 定义值；>200 °C 未核）；CuK 385 改为带温度的两点（397 @20 °C CDA；说明 300 °C 值出处待补）或保留并注明；新 CuMeanAlphaE6(T)（NIST 9 点线性内插，20–900 °C，外推标注）供升温工单压接差胀用；蠕变三行进注记（不判）。
- 镍：MaterialDb「Ni」加 ρ(T) 表（Special Metals Table 3 + VDM Table 3a 交叉）、α(T) 表、k(T) 表、VDM Table 5 持久值（10⁴ h，VdTÜV 345，标「非工作簿 a/b 模型」）；Ni 仍不齐全（持久强度模型口径不同）⇒ 灰显，说明文字写清；是否放开由用户定。
- 门：每个入库数字与 cu_ni_data 文件逐位一致（读同一份 CSV/常量表，不手抄两处）；来源字符串含出版物与表号。

## 4. 待用户表态
- PtRh10/20 的 k(T)、cp(T)、熔点：上网找还是允许推算并标注。
- Ni 要不要放开可选。
- JM 1960《Thermal Expansion of Rhodium-Platinum Alloys》PDF（含纯铂与 PtRh 到 1500 °C 的膨胀表）需要下载许可（浏览器弹了保存对话框，我没有继续）。
