# Linux 镜像：在没有 Windows 的机器上编译 Core、跑不碰界面的单元测试

主程序是 `net8.0-windows` 的 WinForms 应用，Linux 上编译不了、界面也跑不了。
但 `Pt_Optimize/Core` 是纯计算，只在一个特性参数里引用了两个界面类型。
本目录的两个脚本在 `<仓库根>/.linux/` 下生成两个 `net8.0` 项目，把 Core 源码与测试源码**原地引用**进去（不拷贝、不改仓库里的任何源文件）：

| 生成物 | 内容 |
|---|---|
| `.linux/Core/Core.csproj` | 编译 `Pt_Optimize/Core/**/*.cs`，程序集名仍是 `Pt_Optimize`；`LinuxShims.cs` 给那两个界面类型各一个空占位 |
| `.linux/Tests/Tests.csproj` | 编译 `Pt_Optimize.Tests/*.cs`，排除引用界面的测试档（名单写在 `.linux/ui_excluded.txt`） |

## 用法

```bash
# 一次性：装 .NET 8 SDK（云端环境里 dot.net 下载站被网络策略拦，Ubuntu 源里的包可用）
apt-get install -y dotnet-sdk-8.0

echo '.linux/' >> .git/info/exclude          # 镜像目录不进版控
tools/linux_mirror/mk_linux.sh .              # 生成镜像项目
python3 tools/linux_mirror/fix_mirror.py .    # 编译；缺类型就放回定义它的测试档，碰界面就排除，直到编译通过
cd .linux && dotnet test Tests/Tests.csproj -c Release --no-build --filter "速度!=慢"
```

镜像必须放在仓库里面（`.linux/`）：测试靠「从程序集目录往上找 HANDOVER.md」定位仓库根。

## 它覆盖什么、不覆盖什么

- **覆盖**：Core 的全部源码编译；测试项目里不引用界面的那部分（2026-09-23 实测约 1200 条快门中的绝大部分）。
- **不覆盖**：`Pt_Optimize/UI`、`Program.cs`（命令行与 `--selfcheck` 都在里面）、`tests/UiWiring` 界面走查、抓图、
  需要 Rhino 的 `Pt_Optimize.Geom` 子进程。这些仍只能在 Windows 上跑，提交钩子（`.githooks/pre-commit`）也只在 Windows 上成立。
- **`HandoverGateCountTests.Handover_StatedTestCount_MatchesReality` 在镜像里必红**：它按反射数全部用例，
  镜像少了界面测试，数目对不上。这一条不是回归；判断它要在 Windows 的完整测试项目里。
  需要「合并后应为多少条」时，用镜像数出**差量**（同一份排除名单下两棵树各数一次）加到上一次 Windows 实数上。
