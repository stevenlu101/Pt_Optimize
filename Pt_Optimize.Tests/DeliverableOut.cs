using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 I 路（2026-09-15，Opus 5；合并把关待办 P2-2／P2-3）：**测试往 deliverable 写输出的入口** —— 只给带本进程开跑时刻的新文件名，不覆盖 deliverable 里已有的任何文件。
///
/// 为什么：deliverable 下的探针输出被 Core 注释、HANDOVER、别的测试引作证据；老探针按原文件名 File.WriteAllText／AppendAllText，一跑就把被引的那份换成当前代码的数（或往里追加），
///   引用它的注释还在原地说「依据是它」（合并树上查出 12 份以上，G1 的 EvidenceFile 只管点名的几个）。
/// 用法：写 <c>DeliverableOut.Stamped("R48_某探针_2026-09-14.txt")</c> 得到 deliverable\R48_某探针_2026-09-14_本次开跑于{RunStamp}.txt；
///   同一进程里多次调用得到同一个路径（逐行整份重写的探针照旧能用）；出图产物目录用 <see cref="StampedDir"/>；读回同名探针最新一跑用 <see cref="Latest"/>。
/// 门：R48DeliverableWriteGuardTests（源码扫描：测试里构造 deliverable 路径且有写文件调用的，必须带时刻、走本类或 EvidenceFile，否则在名单里写明理由）。
/// </summary>
internal static class DeliverableOut
{
    /// <summary>本进程开跑时刻（第一次用到本类时定，精确到秒）。</summary>
    public static readonly string RunStamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>文件名里时刻前的标记。</summary>
    public const string Marker = "_本次开跑于";

    /// <summary>{主名}_本次开跑于{RunStamp}{扩展名}（只动文件名，不含目录）。</summary>
    public static string StampedName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            throw new ArgumentException("只给文件名，不带目录：" + fileName, nameof(fileName));
        return Path.GetFileNameWithoutExtension(fileName) + Marker + RunStamp + Path.GetExtension(fileName);
    }

    /// <summary>deliverable 下带开跑时刻的新文件路径（目录不在就建）。</summary>
    public static string Stamped(string fileName)
    {
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, StampedName(fileName));
    }

    /// <summary>deliverable 下带开跑时刻的新子目录（出图产物：.3dm 与回显放一起）；建好返回。</summary>
    public static string StampedDir(string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName) || dirName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            throw new ArgumentException("只给一级目录名：" + dirName, nameof(dirName));
        string d = Path.Combine(HandoverDoc.Root(), "deliverable", dirName + Marker + RunStamp);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>读回：deliverable 下 <paramref name="fileName"/> 的带时刻版本里最新的一份（按文件名里的时刻排序）；一份都没有就退回原文件名（可能不存在）。</summary>
    public static string Latest(string fileName)
    {
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        var hit = Directory.Exists(dir)
            ? Directory.GetFiles(dir, stem + Marker + "*" + ext).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).LastOrDefault()
            : null;
        return hit ?? Path.Combine(dir, fileName);
    }
}
