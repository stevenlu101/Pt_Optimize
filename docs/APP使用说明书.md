# Pt_Optimize 说明（仓库侧）

> 最后更新 2026-08-16。

---

## 操作说明在程序里，不在这里

启动 `Pt_Optimize.exe`，按 **F1** 或点最后一个页签「使用说明」。

那是一份**图文说明书**：界面地图、每个按钮做什么、判据表怎么读、限值出处、
已知坑、现场还需确认的数，都在里面。图不是截图，是**按当前设计记录实时画的 SVG** ——
换一档，图跟着变。

**为什么不在这份 md 里也写一份：** 那就成了同一份内容存两处。
本项目栽在「两处然后悄悄漂开」上的次数比栽在任何算法问题上都多
（HANDOVER §1.8）。说明书离开它所说明的那个程序，是最容易漂开的一种。

下面只留**程序里没法讲的事**。

---

## 1. 怎么跑起来

图形界面：

```bash
dotnet Pt_Optimize/bin/Release/net8.0-windows/Pt_Optimize.dll
```

命令行（同一个可执行文件，加 `--cli`）：

```bash
dotnet Pt_Optimize/bin/Release/net8.0-windows/Pt_Optimize.dll --cli --help
```

`--help` 列出全部命令，并按 `DesignSpec.All` 实时列出设计记录 ——
**那份清单不在本文件里维护**，改了代码它自己就变了。

---

## 2. 构建

```bash
dotnet build Pt_Optimize.sln -c Release
```

两个项目，目标框架**故意不同**，不要「统一」它们：

| 项目 | 框架 | 为什么 |
|---|---|---|
| `Pt_Optimize` | net8.0-windows | 主程序 |
| `Pt_Optimize.Geom` | net7.0-windows, x64 | Rhino.Inside 只支持到 net7；必须 x64；必须独立进程 |

几何子进程需要**本机装 Rhino 8**。没装时主程序照常运行，只有 3DM 相关功能报错。

### 两个会浪费你半天的坑

- **WebView2 版本已钉死在 1.0.4078.44**（本机缓存里有）。改成别的版本会触发一次
  完整 restore，实测 **48 分钟**。
- **`dotnet run` 的参数会被吃掉**：`dotnet run --project X -c Release -- args` 里
  `--nologo` 之类的会混进 args。要传参数就直接跑 `bin/.../*.exe`。

---

## 3. 交付件在哪

`deliverable/`：

| 文件 | 内容 |
|---|---|
| `设计记录_管壁0.8mm.3dm` / `设计记录_管壁0.6mm.3dm` | 整机几何（管 + 四片法兰 + 角焊缝 + 压接参考线） |
| `*.spec.json` | 生成该 3DM 的规格（子进程的输入，可复现） |
| `交付件-管壁0.8mm.html` / `-0.6mm.html` | 单页交付说明 |

重出：

```bash
dotnet Pt_Optimize/bin/Release/net8.0-windows/Pt_Optimize.dll --cli --make3dm
```

它会自校三项（方位 round-trip、角焊缝体积、**逐件质量对账**），
**任一项不过就明说「不要把这些 3DM 当交付件」**。判据本身写在程序里，说明在 F1 §7。

---

## 4. 单一来源清单（改代码前先看）

| 东西 | 唯一来源 | 别处只准读 |
|---|---|---|
| 判定过 / 不过 | `Core/LineRunner.Judge` | 界面、命令行、报告 |
| 设计记录几何 | `Core/DesignSpec` | 所有命令、3DM、说明书的图 |
| 板厚分布（含焊缝） | `Core/PlateCurrent2D.ThicknessAt` | `Pt_Optimize.Geom` 的 3DM、说明书的剖面图 |

第三行是 2026-08-16 才补上的：焊缝在 FE 里算了很久，而 3DM 和说明书都没画它。
三处现在用同一个式子，且 `--make3dm` 会拿**解析积分**回头验 3DM 画出来的体积。

---

## 5. 交接文档

设计推演、判据演变、每一次「安静失败」的现场记录：`HANDOVER.md`。
那是给接手的人看的，不是给用户看的。
