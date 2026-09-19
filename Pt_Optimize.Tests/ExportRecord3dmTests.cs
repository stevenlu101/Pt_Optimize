using System.IO;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R43（2026-09-12，用户看立体示意图两次说「不是实体」；退路是截取 3DM 结果来表示）：
/// 把现役设计记录用 APP 出图的同一个函数（Geometry3dm.WriteFinal3dm，与「导出设计记录 3DM」同一条路）写成实体 .3dm，
/// 落在 deliverable/现役记录_3DM/ 下，给说明书与两份 docx 在 Rhino 8 里截「一根实体管 + 实体法兰」用。
/// deliverable/优化后3dm/ 里那份 09-07 的整机.3dm 是旧的曲线轮廓（Rhino 着色下只有线），不是实体，不能当图。
/// </summary>
public class ExportRecord3dmTests
{
    [Trait("速度", "慢")]   // 要开 Rhino（几何子进程）
    [Fact]
    public void 现役记录_出实体3DM()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）；出图目录同样带时刻（原 deliverable/现役记录_3DM 是说明书截图用的那份）
        string dir = DeliverableOut.StampedDir("现役记录_3DM");
        string out3dm = Path.Combine(dir, "现役记录_整机.3dm");
        string echo = Geometry3dm.WriteFinal3dm(d, out3dm, tubeIdMm: 50.0, segLenMm: 300.0, segCount: d.FlangeCount - 1, baseIn: p);
        File.WriteAllText(Path.Combine(dir, "出图回显.txt"), "设计：" + d.Describe() + "\n" + echo, new UTF8Encoding(false));
        Assert.True(File.Exists(out3dm), "3dm 没写出来");
        Assert.True(new FileInfo(out3dm).Length > 10_000, "3dm 太小，多半是空的");
    }
}
