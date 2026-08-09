# Pt_Optimize 交接文档

> 铂金直接加热系统用量优化。换机接手请从本文档开始。
> 最后更新：2026-08-09

---

## 0. 一句话现状

模型已建成并通过 18 项数值验证，几何已用 Rhino 8 对 `Pt_Heater.3dm` 机器校核（11 项 0.00 %）。

**但 2026-08-09 拿到真实工况数据后，核心结论需要重做**：

- 旧结论「HC3 1250 °C 已超限」**作废** —— 1250 不是真实控温点（真实 1150/1080/1050，递减）
- 法兰此前在分段核算里**一片都没算**，而法兰占总铂 61–75 % —— 省铂主战场不在管壁
- 电流基准错了：现在用**稳态**电流反算最小壁厚，而真实尺寸由**升温（额定）**工况定，
  稳态只有额定的 1/6。这是**危险方向**，算出的壁厚偏薄
- 玻璃温降实测 20 K，模型算 35–68 K；已定位到**作废的一维法兰模型仍在被新代码调用**

**当前状态：不要引用任何含法兰或壁厚的数值结论。** 结构（片数 n+1、共用片 1.5 倍系数、
t ∝ I、几何常数）可信；数值待 §6 的 ①②③ 完成后重算。

---

## 1. 环境搭建

```bash
git clone <本仓库>
cd Pt_Optimize
dotnet restore --disable-parallel   # 见下，务必串行
dotnet build
dotnet test          # 应为 18/18 通过
dotnet run --project Pt_Optimize
```

**依赖**：NuGet 包 `MathNet.Numerics 5.0.0`、`ScottPlot.WinForms 5.1.59`、`xunit`。

**SDK 不必是 8**。目标框架虽为 `net8.0-windows`，但 SDK 9 / 10 都能编 —— 只要
`microsoft.netcore.app.ref` 与 `microsoft.windowsdesktop.app.ref` 的 8.0.x 引用包能从 NuGet 取到
（缓存里有就不走网）。**运行**则需要 8.0 的 `Microsoft.NETCore.App` +
`Microsoft.WindowsDesktop.App` 共享运行时。
2026-08-09 在只装了 SDK 9.0.315 / 10.0.100 / 10.0.102 的机器上实测：构建 0 错误、测试 18/18。
`dotnet build` 报 6 条 NU1701（ScottPlot 依赖的 OpenTK、SkiaSharp.Views.WindowsForms 是
.NET Framework 包）**属正常**，不是配置坏了。

### ⚠ 换机第一个坑：ScottPlot 还原

SkiaSharp 原生包很大（约 80 MB），网络差时**并行下载必失败**，报 `unexpected EOF`：

```bash
dotnet restore --disable-parallel
```

**首次还原会非常久，要有心理准备**：2026-08-09 实测 **48 分钟**，
期间 `MathNet.Numerics`、`Microsoft.CodeCoverage` 等包反复报
`Received an unexpected EOF or 0 bytes from the transport stream`，
但 NuGet 的重试是有效的，最终 exit 0。**中途看到 EOF 不要掐掉重来** —— 重来是从头开始，
而继续等是接着重试。建议直接挂后台，别盯着。

**不要用 `dotnet add package`** —— 它失败时会**回滚 PackageReference**，
表面上只报个错，实际什么都没留下，之后的 `dotnet restore` 在空项目上白跑十几分钟。
要加包就直接改 `.csproj` 再 restore：引用先落盘，restore 只管下载，失败重试是增量的。

### ⚠ 换机第二个坑：CLI 直跑 .exe 没有任何输出

`Pt_Optimize` 是 `WinExe`（GUI 子系统），**不挂控制台**。直接
`Pt_Optimize.exe --cli --line` 会静默退出，一个字都不打印 —— 看起来像程序挂了，其实跑完了。

正确调用是走 `dotnet` 宿主（控制台子系统）：

```bash
dotnet Pt_Optimize/bin/Debug/net8.0-windows/Pt_Optimize.dll --cli --line
```

这样输出正常且中文是 UTF-8。`dotnet run --project Pt_Optimize -- --cli --line` 也能跑，
但**输出被重定向时中文乱码**（`Console.OutputEncoding` 设不上），只适合直接看屏幕。

**PowerShell 下另有一个假失败**：给 CLI 输出接 `| Select-Object -First N` 会提前关管道，
`$LASTEXITCODE` 变成 **255**，看着像程序崩了。判断退出码请先重定向到文件再看：

```bash
dotnet .../Pt_Optimize.dll --cli --creep > out.txt 2>&1; echo $LASTEXITCODE
```

### Rhino 8 几何接入（可选）

`Pt_Heater.3dm` 是几何的唯一事实来源，但代码里那些几何常数一直是**人工抄进去**的。
`--geom` 直接读 .3dm 跟代码逐项对照：

```bash
dotnet Pt_Optimize/bin/Debug/net8.0-windows/Pt_Optimize.dll --cli --geom
```

**架构照搬 `D:\WinForm_ISO_STR_R8` 的 harness 房规**（那边的 `probe`/`objprobe` 模板）：

```
Pt_Optimize        net8.0-windows   完全不碰 RhinoCommon，不要求本机装 Rhino
     │  启子进程，收 JSON
     ▼
Pt_Optimize.Geom   net7.0-windows / x64 / Rhino.Inside   自起 headless RhinoCore，只负责「量」
```

**「量」在子进程、「判」在主程序** —— 常数在主程序里，搬到子进程就成了拿副本校副本。

为什么必须隔一个进程：**Rhino 8 引擎 = .NET 7，进程内宿主必须同代**，而主程序是
net8.0-windows。隔开后两边各自成立，主程序不必降级到已 EOL 的 net7，
且没装 Rhino 的机器照样构建、照样跑全部核算（只是 `--geom` 用不了）。

子进程的三条硬约束（改了会 `BadImageFormatException` / 加载失败）：

| 项 | 值 | 为什么 |
|---|---|---|
| TargetFramework | `net7.0-windows` | Rhino 8 引擎 = .NET 7，进程内宿主必须同代 |
| PlatformTarget | `x64` | RhinoCommon 8 仅 64-bit |
| Rhino 载入 | `Rhino.Inside` 包，**不显式引 RhinoCommon** | 编译期由其依赖带出（net48 回退，NU1701 是预期噪音），运行期 Resolver 加载安装目录的原生 net7.0 RhinoCommon |

入口三段式，顺序错任一步都启动失败（`Pt_Optimize.Geom/Program.cs`）：

```csharp
[STAThread] static int Main(string[] args) {       // ① 控制台默认 MTA → RhinoCore 抛 COMException
    RhinoInside.Resolver.Initialize();             // ② 必须早于任何 RhinoCommon 类型 JIT
    return Run(path);                              // ③ 碰 Rhino 的代码进 NoInlining 方法
}
[MethodImpl(MethodImplOptions.NoInlining)] static int Run(string path) {
    using (new RhinoCore(new[]{"/NOSPLASH"}, WindowStyle.Hidden)) { ... }
}
```

房规里对本项目也成立的几条坑：**一进程只能启一个 RhinoCore**，用完 Dispose 且
`Environment.Exit()` 收尾（前台线程挡住进程自然退出）；**容差一律从
`doc.ModelAbsoluteTolerance` 取**，不硬编码；**`VolumeMassProperties.Compute` 等
失败时静默返回 null 而不抛异常，每次判空**。

版本钉死在 `Rhino.Inside 8.0.7-beta`（房规用浮动 `8.*-*`）：浮动版本号每次 restore
都要联网解析，而本机 restore 一次 48 分钟。要升级手动改 csproj 那一行。
本机实测：该版本连同 RhinoCommon / Grasshopper 8.0.23304.9001 与 net7 引用包全部已在
NuGet 缓存里，**首次 restore 仅 5 秒，全程离线**。

### 文档生成（可选）

```bash
pandoc docs/Pt_理论模型.md -o docs/Pt_理论模型_v4.0.docx --toc --toc-depth=3 --resource-path=docs
```

需要 pandoc。注意 texmath **不支持 `\tag{}`**，公式编号要写成 `\qquad\text{(n.n)}`。

---

## 2. 代码地图

```
Pt_Optimize/Core/
  Numerics.cs          通用一维有限体积 BVP 内核 + 求根 + 样条插值表
  SegmentSolver.cs     管段金属+玻璃耦合；外层求电流与壁厚
  Insulation.cs        多层圆筒/平板热阻，含自然与强制对流
  Materials.cs         铂物性（电阻率拟合、导热、比热、空气物性、辐射）
  MaterialDb.cs        ★ 材料数据库：9 个牌号的电阻率 + 持久强度（供应商实测）
  Mechanics.cs         应力校核；沿舌片逐点扫描；反解强度最小壁厚
  PlateCurrent2D.cs    ★ 二维法兰电流场（变厚度 + σ(T)）
  PlateThermal2D.cs    ★ 二维法兰温度场
  CoupledSolver.cs     ★ 管段 ↔ 法兰 耦合迭代
  FieldMap.cs          子午面场装配（供绘图）
  Segment.cs           分段定义
  LineSolver.cs        整线分段核算（解析，即时）
  Geometry3dm.cs       ★ 驱动 Pt_Optimize.Geom 子进程 + 判定几何常数 → --geom
  FlangeRadial.cs      ⚠ 一维环形法兰模型 —— 已作废，见 §5
  FlangeOptimizer.cs   ⚠ 基于上者的扫描 —— 已作废
  FlangeThicknessDesign.cs  法兰厚度分布反设计（未完成验证）

Pt_Optimize/UI/
  MainForm.cs          主窗体（分段核算页 + 6 个图页签 + 参数 PropertyGrid）
  FieldPlots.cs        场图与剖面图（ScottPlot）
  Schematics.cs        论文示意图（图 1–5）

Pt_Optimize.Geom/       ★ 几何量测子进程（net7.0-windows / x64 / Rhino.Inside，见 §1）
  Program.cs            自起 headless RhinoCore，读 .3dm 吐 JSON —— 只量不判

Pt_Optimize.Tests/
  VerificationTests.cs 15 项：MMS、解析解、能量闭合、网格收敛等
  MaterialDbTests.cs   3 项：对照 110 个供应商实测点

docs/
  Pt_理论模型.md            ★ 论文源文件（Markdown + LaTeX）
  Pt_理论模型_v4.0.docx     ★ 当前版本，32 页
  Pt_理论模型_v1~v3.docx    ⚠ 含已作废结论，只作历史，不要引用
  fig/                      全部图表（由程序生成）

Pt_Heater.3dm             ★ 几何唯一事实来源
鉑金電氣計算.xlsx          ★ 电阻率 + 设计工况
鉑金材料蠕變應力壽命估算.xlsx  ★ 持久强度实测数据
```

---

## 3. CLI 速查

全部经 `--cli` 进入，可带一个 JSON 参数文件覆盖默认值：

| 命令 | 作用 | 耗时 |
|---|---|---|
| `--cli` | 单段求解摘要 | 秒 |
| `--cli --couple` | 管段 ↔ 二维法兰耦合 | ~1 min |
| `--cli --line` | 分段核算三方案对比 | 秒 |
| `--cli --matrix` | 材料 × 温度 → 最小壁厚矩阵 | 秒 |
| `--cli --mech` | 力学校核 + 水头敏感性 | 秒 |
| `--cli --creep` | 持久强度表（实测数据） | 秒 |
| `--cli --tabscan` | 沿舌片逐点校核 | ~1 min |
| `--cli --save` | 省铂曲线（多工况） | ~10 min |
| `--cli --curve <dir>` | 省铂曲线细扫 + 出图 | ~10 min |
| `--cli --insul` | 保温分界扫描 | ~10 min |
| `--cli --clamp` | 夹持温度扫描 | ~10 min |
| `--cli --thick` | 孔周加厚扫描 | ~2 min |
| `--cli --plate` | 二维场独立求解 | ~2 min |
| `--cli --geom [f.3dm]` | 读 .3dm 校核几何常数（起 Geom 子进程，需 Rhino 8） | ~10 s |
| `--cli --figs <dir>` | 生成论文示意图 | 秒 |
| `--cli --plots <dir>` | 导出连接区场图 | 秒 |

JSON 参数文件示例（字段名见 `DesignInputs.cs`）：

```json
{"TSetC":1200,"TGlassInC":1200,"JAllowAPerMm2":10.0,
 "DesignLifeHours":8760,"SafetyFactor":2.0,"GlassHeadM":0.5,
 "GradeName":"Pt","WallMinMm":1.0,"SizeWall":false}
```

**注意**：长任务会锁住 `.exe`，此时 `dotnet build` 会静默失败（只报 MSB3026 警告，
不报 error CS，容易误判为「构建成功」）。**后台任务与构建不要并行**。

---

## 4. 核心结论（可直接汇报）

### 4.1 约束次序

$$\text{强度} \;>\; \text{电流密度} \;>\; \text{析晶}$$

| 约束 | 纯铂 1200 °C 下的限值 |
|---|---|
| 强度 | 壁厚 ≥ **0.677 mm** ← 控制约束 |
| 电流密度 | 壁厚 ≥ 0.562 mm |
| 析晶 | 壁厚 ≥ 0.509 mm |

### 4.2 ⚠ 已作废：「HC3 超限」

旧结论：HC3 段（1250 °C）利用率 1.11 已超限，应优先处理。

**2026-08-09 作废** —— 1250 °C 不是真实控温点。用户提供的真实稳态工况：

| 项 | 值 |
|---|---|
| 控温点（每段**中点**） | HC1 **1150** / HC2 **1080** / HC3 **1050** °C，沿流向**递减** |
| 玻璃温度 | 入口 **1150** → 出口 **1130** °C（全程降 20 K） |
| 产量 | **1.5 t/day** |
| 升温要求 | 空管 **25 → 1150 °C / 3 h** |
| 法兰衔接处温差目标 | 控温点与法兰处管温之差 **≤ 10 K** |

要点：

- **1150 同时是工作温度上限**，强度/蠕变按它校核；升温目标亦为 1150
- 供料管是**受控降温**，不是升温。后两段金属（1080/1050）比玻璃（~1140/1130）冷，
  是玻璃在加热管子 —— 与旧示例三段（1150/1200/1250 递增）方向相反
- **额定功率 ≠ 稳态功率**：大功率是为升温准备的。用户工作簿额定 11.14 kW，
  而稳态三段合计仅约 2.5 kW，差 6 倍。现在的模型用**稳态**电流去顶 J 上限反算最小壁厚，
  这是**危险方向**：真实电流更大 ⇒ 真实 J 更高 ⇒ 算出的最小壁厚偏薄。
  正确做法是三分开：**额定（升温）定尺寸 / 稳态定温场 / 上限定强度**
- 1080 与 1050 **落在纯铂持久强度实测区间（1100–1400 °C）之外**，护栏返回「超范围」，
  现状方案（全线纯铂）在真实工况下**无法判定** —— 见 §6 待补数据 ③

### 4.2b 法兰是按整线计数的

**n 段 = n+1 片**，相邻两段共用接头处那一片；单段就是 2 片，与 `Pt_Heater.3dm` 的两个
法兰实体一致（`--geom` 已校核）。此前 `LineSolver` **一片都没算**。

共用片电流取自《鉑金電氣計算.xlsx》的 `T10`/`T11`：

$$I_{\text{共用}} = \frac{I_{\text{左}} + I_{\text{右}}}{2} \times 1.5$$

**既不是取大，也不是相加。** 等电流时 = 1.5·I，是最坏情况（同相纯相加 2I）的 75 %。
工作簿据此把共用片从 1.3 加厚到 2.0 mm（比值 1.538 ≈ 电流比 1.5），四片 J 都落在 6.3–6.5。
厚度折算用 t ∝ I（定尺后 J = J_allow，而 J = K/t、K ∝ I）。

计入法兰后（稳态电流口径，**数值待重算**）：省铂率从只看管壁的 70.4 % 降到 **54.7 %**，
法兰占比现状 61 % → 最省方案 75 %。**省铂的主战场已不在管壁。**

### 4.3 纯铂无省铂空间

按强度取最小壁厚后三段利用率全为 1.00，无任何工程余量，且高温段反需加厚。

### 4.4 弥散强化铂是唯一出路

| 牌号 | 1200 °C 断裂强度 | 相对纯铂 | 电阻率相对 | 金属价格相对 |
|---|---|---|---|---|
| Pt | 1.08 MPa | 1.00 | 1.00 | 1.00 |
| **FKS16/Pt** | **8.24** | **7.6×** | **0.95** | **1.00** |
| Tanaka-ZGS-Pt | 8.31 | 7.7× | 1.00 | 1.00 |
| Pt-Rh/90-10 | 2.54 | 2.35× | 1.03 | 1.39 |

**弥散强化的基体仍是纯铂，金属价格相同** —— 这是本问题中罕见的
「不付金属代价即可解除约束」的措施。加铑不划算：强度只有 2.35 倍，价格却是 1.39 倍。

### 4.5 其他可立即执行（不动铂件尺寸）

- **停止压缩空气吹风** —— 由质量方程 $m=d_{Pt}P/(\rho_e J^2)$，吹掉的每瓦都折算成铂重
- **法兰保温包到舌片末端** —— 析晶裕度 −122 → +49 K
- **铜排夹改风冷 ≤300 °C** —— 关键是「有没有夹冷」而非「夹多冷」，
  400 °C 与 80 °C 的差别仅 10 W / 18 K，**不需要水冷**（规避安全隐患）
- **保持铂表面光洁** —— 辐射占散热 82 % 且正比于 ε，最多省 40 %

### 4.6 电流密度最高点在法兰孔周而非管子

10.63 vs 7.59 A/mm²。孔周加厚可降到 9.59，但**再加厚无收益**（瓶颈转移到舌片末端角点）。
最小方案：$r\le40$ mm 加厚至 3.0 mm，增铂 125 g/对。

---

## 5. ⚠ 已作废的内容 —— 不要引用

**一维环形法兰模型**（`FlangeRadial.cs`、`FlangeOptimizer.cs`、`--opt`）
按**环形圆盘**建模，与实际的「圆盘 Ø120 + 平面梯形舌片」几何不符。以下结论**全部无效**：

- 法兰自给率 Φ = 0.037（真值 0.72–0.82）
- 「法兰 849 g → 12 g」的优化建议
- $r_{o,max}=\sqrt{C/t_{min}}$ 可制造性判据与相关联合扫描表
- 理论模型旧版 §8.9「管壁减薄补偿」（管壁 1.0 mm 不可能渐变）

**`--flare`（舌片延长/加宽）也已作废**：加宽虽能把接点从 1235 降到 338 °C，
但析晶裕度掉到 −26 K 且铂重涨到 4341 g。

**`--cli` 主输出的「法兰 Φ」「析晶裕度」两行走的是旧模型**，
一律**以 `--couple` / `--line` 为准**。

**docs/Pt_理论模型_v1~v3.docx 含已作废结论**，只看 v4.0。

---

## 6. 下一步：缺两个数即可闭合

| 待补数据 | 用途 | 影响 |
|---|---|---|
| **① 工艺可制造最小壁厚** | 决定省铂上限 | 目前程序下界 0.300 mm 给出 915 g（省 70 %）；若实际下限 0.5 mm 则为 1525 g（省 51 %）。**这个数直接决定最终结论** |
| **② FKS16 / ZGS 加工溢价** | 完成成本对比 | 金属价格已知与纯铂相同，只差加工费。程序留有 `PtGrade.FabricationPremium` 字段，填入即可算 |

其次：

- 各段实际温度与水头（UI「分段核算」页已可编辑）
- 铂表面发射率或表面状态记录（当前最大不确定源，±40 % 散热）
- 玻璃液相线 $T_{liq}$ 实测值

| **③ 1000–1100 °C 纯铂持久强度** | 判定现状方案 | **新增，最卡**。真实控温 1080/1050 落在实测区间(1100–1400 °C)之外，护栏返回「超范围」，现状全线纯铂在真实工况下**无法判定**。弥散强化铂有 1000 °C 数据故不受影响 |
| **④ 热电偶安装位置** | 定边界条件 | 1150/1080/1050 是金属本身温度，还是点焊在外壁/埋在保温里的读数？影响冷点判定基准 |
| **⑤ 保温层密度与比热** | 升温核算 | `InsulationLayer.DensityKgM3` / `CpJKgK` 现为典型值（200 / 3000 kg/m³，1050 J/kg·K），非实测。升温时间对其敏感（保温热容大于铂本身） |

### ⚠ 待完成的工作（按优先级，2026-08-09 重排）

**① `--glass` 必须改走 `CoupledSolver`（最高优先级）**

现在 `--glass` 裸调 `SegmentSolver.Solve()`，未设 `FlangeDrawOverrideW`，
于是 `SegmentSolver.cs:237` 的 `defTab` 落到 `FlangeRadial` —— **正是 §5 作废的一维环形模型**。
该模型自给率 Φ 算成 0.037（真值 0.72–0.82），法兰抽热高估约 20 倍，
冷点因此深达 200 K 以上。**当前所有 `--glass` 结果都不可信。**

改法：每段先跑耦合解拿真实抽热 D → 设 `FlangeDrawOverrideW = D` → 再解温度场与玻璃 →
串联三段。代价约 1 min/段。

**② `RampSolver`：空管升温（尚未动工）**

空管 25 → 1150 °C / 3 h。空管时**玻璃项整个不存在**，两处要改：
`SegmentSolver.cs:195` 的 β 里 `p.HGlass * π * TubeId` 项、`:261` 源项里 `hg*pi*(T-tg)`。

关键是给出模型现在**完全没有的壁厚下界**：

$$P_{max} = J_{allow}^2 \cdot A \cdot \rho_e \cdot L \;\propto\; A \;\propto\; \text{壁厚}$$

减薄壁厚同时削减可用功率。现在只把 J 当上界（越薄 J 越大，顶到 10 就停），
方向相反的下界一条都没有。

**③ 法兰衔接处温差 ≤ 10 K 并入耦合解作硬约束**

与 ① 是同一个缺陷的两个表现：法兰抽热高估 → 管温被拉低 → 玻璃向管壁多放热。
修好法兰模型两者应同时收敛。

**④ hg 标定尚未定案**

默认值已从 220 改为 **65**（层流 Nu≈3.66、玻璃熔体 k≈0.9、ID50 ⇒ hg = Nu·k/D）。
220 对应 k_eff ≈ 2.2，远高于熔体导热。但 65 是**手算的物理合理值，不是标定结果** ——
`--glass` 的反解（现给 28.7，对应 k_eff 0.39，低于物理下限）被作废的法兰模型污染了，
必须在 ① 完成后重新反解确认。

**验证记录**：玻璃全程温降 实测 20 K；模型 hg=220 时 67.6 K，hg=65 时 35.2 K。
残余 +15 K 归因于法兰抽热高估（见 ①）。**这是全模型唯一一个拿现场实测校准的点，务必守住。**

---

### 其余待完成

1. **把电流密度与析晶两条约束并入分段核算** —— 现在 `LineSolver` 只做强度（解析、即时），
   $J$ 与析晶需耦合热解（每段约 30 s）。做完后 §4 的 915 g 才是可交付设计值。
2. **法兰孔周贴体网格加密** —— 现 $J_{max}$ 有 +3.5 % 网格敏感性，是下界。
3. **强制对流按实际喷嘴标定** —— 现用平板关联式，射流冲击的真实 h 更高。
4. `FlangeThicknessDesign.cs` 的厚度分布反设计尚未验证。

---

## 7. 踩过的坑（避免重犯）

| 坑 | 现象 | 教训 |
|---|---|---|
| **多项式外推** | 纯铂持久强度外推到 900 °C 得 **8648 MPa** | 供应商拟合有有效区间，已加护栏返回「超范围」。**任何拟合都要记区间** |
| **Brent 求根遇噪声** | 壁厚跑到 2.82 mm | 残差要解非线性 BVP，带迭代噪声；逆二次插值会放大噪声跳分支。单调+带噪+昂贵的函数用**二分** |
| **搜索区间越过物理极限** | 求解发散后被数值夹断，返回看似正常的假值 | 电流上界须钉在热稳定极限 $I_{stab}=\sqrt{\beta A/(d\rho_e/dT)}$ 内，越界**抛错**而非给数 |
| **$J$ 乘了厚度因子** | 加厚区 $J$ 虚低，$J_{max}$ 位置错误外移 | 深度平均下 $K=Jt$ 守恒，$J=K/t$ **与厚度无关** |
| **逐面累加漏面** | 抽热偏小 14 % | 阶梯状圆孔边界会漏配对；改用**能量恒等式** $Q=Q_{loss}-Q_{gen}$ |
| **`{x:0.1}` 格式串** | 13.7 打成 "14.1" | .NET 自定义数字格式里只有 `0` 和 `#` 是占位符，`1` 是**字面量** |
| **切换分支丢工作树** | 已提交到临时分支的文件在切回时被删除 | 已恢复。教训：**新项目直接建独立仓库**，不要临时分支 |
| **exe 被后台任务锁住** | build 只报 MSB3026 警告，不报 error CS | 别把长任务和构建并行；判断构建成功不能只看 `error CS` |
| **收敛判据用「每轮变化量」** | 变化量 1e-7 而能量残差仍 2 % | 变化量小 ≠ 残差小。要单独算**逐格残差**做判别 |
| **自己写的新命令踩进已作废模型** | `--glass` 冷点算出 200 K+，越"修"越深 | 裸调 `SegmentSolver.Solve()` 会**默认落到作废的 `FlangeRadial`**。§5 那句「一律以 `--couple`/`--line` 为准」不只约束读结果，**更约束写新代码**：任何新命令都要显式走耦合解，或显式设 `FlangeDrawOverrideW` |
| **拿一个参数去补两个误差** | 反解 hg 得 28.7，对应 k_eff 0.39，低于玻璃熔体导热物理下限 | 反解值**越界就是有别的误差混进来了**。物理下限是判据：反解结果落在合理区间外，说明模型里不止一处偏，不能硬认这个拟合值 |
| **示例参数被当成实测** | 段温 1150/1200/1250 递增（真实是 1150/1080/1050 递减）、产量 2.0 t/day（真实 1.5） | 示例值与实测值必须**在代码里标注来源**。§4.2 那条「HC3 已超限」的作废结论就是这么来的 |

---

## 8. 验证套件说明

`dotnet test` 应为 **18/18**。其中最关键的两项：

- **MMS 制造解法**（`Bvp1D_MMS_ObservedOrderIsSecond`）：
  先设定精确解 $T_e(x)$，反代进方程得源项，再让求解器去解。
  **能证明代码解的确实是所写的方程**，可排除离散化 bug。观测阶 1.991 → 1.999。
- **蠕变对照实测**（`Creep_ReproducesSupplierRawData`）：
  对照工作簿里 110 个供应商实测点，平均偏差 0.93 %。
  **改动 `MaterialDb` 的系数后必须重跑此项。**

改代码后若这两项挂了，先怀疑改动而不是测试。

---

## 9. 联系与出处

- 几何：`Pt_Heater.3dm`（图层 `铂金管`、`法兰`）
  —— 2026-08-09 用 `--geom` 机器校核过一次（Rhino 8.33.26188.13001）：管的内外径/壁厚/段长/铂重、
  法兰的管孔径/圆盘径/厚度/舌片末端 X/平面净面积/单片铂重，**11 项全部 0.00 % 吻合**。
  其中「平面净面积 23 591.608 mm²」一项同时校核了 `FlangePlate` 的 `HalfWidth`/`Tangent`
  那套解析轮廓。**改 .3dm 或改这些常数后请重跑 `--geom`。**
- 电阻率与设计工况：`鉑金電氣計算.xlsx`
- 持久强度：`鉑金材料蠕變應力壽命估算.xlsx`（Tanaka、Umicore）
- 金属价格：Umicore PMM，2026-08-06（Pt \$1731/oz、Rh \$8500/oz，Rh/Pt = 4.91）
  —— **铑价波动极大，长期决策请用区间而非点值**
- 若为**租赁**而非买断铂金，减重收益是租金而非一次性资产，成本模型需另建
