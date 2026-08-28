using System;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **交付的 3DM 必须与设计记录是同一个零件。**
///
/// ★ 病灶（2026-08-25 查出）：`git log` 显示 `deliverable/设计记录_管壁0.8mm.3dm`
///   最后一次提交是 2026-08-16，而那一版里舌长是 **−90.0**、半宽 **15**
///   —— 也就是今天的 <see cref="DesignSpec.Retired08"/>（判据⑤ 不过的那个作废档）。
///   正确的那份 2026-08-17 13:45 就生成好了，**在工作区躺了八天没提交**。
///   ⇒ 谁 clone 这个仓库，拿到的图纸和设计记录不是同一个零件，而文件名叫「设计记录」。
///
/// 验法：3dm 是二进制，但坐标与厚度都是小端 double。
/// 直接数字节 —— 本档的四个板厚各出现 114 次、舌长 −140.0 出现 64 次，
/// 而作废档的板厚一次都没有。不需要解析 3dm，也就不需要起 Rhino 子进程。
///
/// ⚠ **这道门读的是工作区的文件，挡不住「文件对但没提交」** —— 而 2026-08-25
///   栽的正是后者。那一条只能靠提交时看 git status，代码里验不了。写在这里，不假装覆盖到了。
/// </summary>
public class Deliverable3dmTests
{
    private static byte[] Read(string rel)
    {
        string p = Path.Combine(HandoverDoc.Root(), rel);
        if (!File.Exists(p))
            throw new FileNotFoundException("交付件不见了：" + rel + " —— 断言失去了对象，**不能算通过**");
        return File.ReadAllBytes(p);
    }

    private static int Count(byte[] hay, double v)
    {
        byte[] n = BitConverter.GetBytes(v);
        int c = 0;
        for (int i = 0; i + n.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < n.Length && hay[i + j] == n[j]) j++;
            if (j == n.Length) c++;
        }
        return c;
    }

    private static void SamePart(string rel, DesignSpec live, DesignSpec retired)
    {
        byte[] b = Read(rel);
        foreach (double t in live.TabThickMm)
            Assert.True(Count(b, t) > 0, $"{rel} 里找不到本档的板厚 {t:0.00} mm —— 这张图不是这个零件");
        Assert.True(Count(b, -live.TabLengthMm) > 0,
            $"{rel} 里找不到本档的舌长 −{live.TabLengthMm:0} mm");

        // 自证：作废档的特征值一次都不该出现。没有这半条，
        // 「图里同时含有两个设计」也会被判成通过。
        foreach (double t in retired.TabThickMm)
            Assert.Equal(0, Count(b, t));
        Assert.Equal(0, Count(b, -retired.TabLengthMm));
    }

    [Fact]
    public void 交付的0_8图纸与W08是同一个零件()
        => SamePart("deliverable/设计记录_管壁0.8mm.3dm", DesignSpec.W08, DesignSpec.Retired08);

    [Fact]
    public void 交付的0_6图纸与W06是同一个零件()
        => SamePart("deliverable/设计记录_管壁0.6mm.3dm", DesignSpec.W06, DesignSpec.Retired06);

    [Fact]
    public void 自证_两个现役档的板厚互不相同_否则上面两条会互相通过()
    {
        // 若 W08 与 W06 的板厚碰巧相同，拿 0.6 的图去对 0.8 的档也会过。
        Assert.NotEqual(DesignSpec.W08.TabThickMm, DesignSpec.W06.TabThickMm);
        Assert.NotEqual(DesignSpec.W08.TabThickMm, DesignSpec.Retired08.TabThickMm);
    }
}
