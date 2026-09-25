using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ 搜形状检查点续跑（2026-09-25，业主「你自己拉下来跑」「用可靠的方式跑」；云端容器一天重启两次，几小时的搜索从头来两次）。
///
/// 每个形状在导航网格上解完，就把驱动会读的那几个数落盘一行 JSON（<see cref="Entry"/>）；重开时同一把钥匙直接复用，不再解。
/// 钥匙 = 候选设计（DesignSpec 全字段 JSON）＋ 求解选项（SolverOptions 全字段）＋ 工艺参数（DesignInputs）＋ 代码戳（提交号，有未提交改动时加改动哈希）
/// 的 SHA-256：任何一项不同都是另一把钥匙，别的代码、别的参数解出的数进不来。
///
/// 复用的数逐位等于当时的求解结果（同代码同输入求解器是确定的；落盘的字段就是 ShapeSearchDriver.Fill／Deficit／肩部预筛读的那几个，
/// 落盘时从 SolverResult 原样抄，读回时原样装回）。**只缓存导航网格那一遍**（FineMm = 0）：赢家的判决网格精算照常重解，方案卡要它的全部字段。
/// 复用行在证据里逐行点名（「续跑复用：…」），档头与档尾写复用了几条、实解了几条，不静默。
/// 改回：不给路径 ⇒ 不读不写，逐位同改前。
/// </summary>
public static class ShapeSearchCheckpoint
{
    /// <summary>一行检查点：驱动从 SolverResult 读的全部字段（少一个都算不上「同一份结果」）。</summary>
    public sealed class Entry
    {
        public string Key = "";
        public string Stamp = "";
        public string When = "";
        public double DiscRadiusMm, TabHalfWidthMm;
        public bool TabTaper;
        public string DesignJson = "";
        public bool Feasible;
        public double MassG = double.NaN;
        public string Message = "", StopWhy = "", UndeterminedWhy = "", NullWhy = "";
        public bool HitBound, Undetermined, FineRefined;
        public double FineMmUsed, SkippedRadiusGrowthToMm = double.NaN;
        public string SkippedRadiusGrowthWhy = "";
        public int Solves, SameStateReuses;
        public ConstraintOut[]? Checks;
        public bool BestOk, BestConverged, BestRampChecked, BestEmptyTube;
        public double[]? PlateA, SegPeakA;
    }

    static readonly JsonSerializerOptions J = new()
    {
        IncludeFields = true, WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,   // NaN／∞ 照写照读
    };

    /// <summary>钥匙：设计 + 求解选项 + 工艺参数 + 代码戳 的 SHA-256（十六进制）。</summary>
    public static string KeyOf(DesignSpec d, DesignInputs baseIn, SolverOptions o, string stamp)
    {
        string s = JsonSerializer.Serialize(d, J) + "\n" + JsonSerializer.Serialize(baseIn, J) + "\n"
                 + JsonSerializer.Serialize(o, J) + "\n" + (stamp ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }

    /// <summary>短哈希（代码戳里「未提交改动」那一截用）。</summary>
    public static string Sha12(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s ?? "")))[..12];

    /// <summary>一份检查点档（JSONL）。读进来的坏行跳过并计数，不让一行坏档废掉整份。</summary>
    public sealed class Store
    {
        readonly string _path, _stamp;
        readonly Dictionary<string, Entry> _map = new();
        readonly object _lk = new();
        public int Loaded, BadLines, Hits, Misses, NotCached;
        public string Path => _path;

        public Store(string path, string stamp)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _stamp = stamp ?? "";
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(line, J);
                    if (e is null || string.IsNullOrEmpty(e.Key)) { BadLines++; continue; }
                    _map[e.Key] = e; Loaded++;
                }
                catch (JsonException) { BadLines++; }
            }
        }

        /// <summary>把求解函数包一层：命中复用、未命中解完落盘；FineMm &gt; 0（精算）不经过缓存。</summary>
        public Func<DesignSpec, DesignInputs, SolverOptions, IProgress<string>?, CancellationToken, SolverResult> Wrap(
            Func<DesignSpec, DesignInputs, SolverOptions, IProgress<string>?, CancellationToken, SolverResult> inner, Action<string>? say)
            => (d, b, o, p, t) =>
            {
                if (o.FineMm > 0) { Interlocked.Increment(ref NotCached); return inner(d, b, o, p, t); }
                string key = KeyOf(d, b, o, _stamp);
                Entry? e;
                lock (_lk) _map.TryGetValue(key, out e);
                if (e is not null)
                {
                    Interlocked.Increment(ref Hits);
                    say?.Invoke($"　　续跑复用：盘Ø{2 * d.DiscRadiusMm:0.0}／舌宽{2 * d.TabHalfWidthMm:0.0}{(d.TabTaper ? "／锥形" : "")}　检查点 {e.When}（代码戳 {e.Stamp}）　键 {key[..12]}");
                    return Rebuild(e, b);
                }
                var sr = inner(d, b, o, p, t);
                Interlocked.Increment(ref Misses);
                if (sr?.Design is null) return sr!;   // 没有设计的结果没法复原，不落盘
                var ne = ToEntry(sr, key, _stamp);
                lock (_lk)
                {
                    _map[key] = ne;
                    File.AppendAllText(_path, JsonSerializer.Serialize(ne, J) + "\n");
                }
                return sr;
            };
    }

    static Entry ToEntry(SolverResult sr, string key, string stamp) => new()
    {
        Key = key, Stamp = stamp, When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        DiscRadiusMm = sr.Design.DiscRadiusMm, TabHalfWidthMm = sr.Design.TabHalfWidthMm, TabTaper = sr.Design.TabTaper,
        DesignJson = JsonSerializer.Serialize(sr.Design, J),
        Feasible = sr.Feasible, MassG = sr.MassG,
        Message = sr.Message ?? "", StopWhy = sr.StopWhy ?? "", UndeterminedWhy = sr.UndeterminedWhy ?? "", NullWhy = sr.NullWhy ?? "",
        HitBound = sr.HitBound, Undetermined = sr.Undetermined, FineRefined = sr.FineRefined, FineMmUsed = sr.FineMmUsed,
        SkippedRadiusGrowthToMm = sr.SkippedRadiusGrowthToMm, SkippedRadiusGrowthWhy = sr.SkippedRadiusGrowthWhy ?? "",
        Solves = sr.Solves, SameStateReuses = sr.SameStateReuses,
        Checks = sr.Best?.Checks, BestOk = sr.Best?.Ok ?? false, BestConverged = sr.Best?.Converged ?? false,
        BestRampChecked = sr.Best?.RampChecked ?? false, BestEmptyTube = sr.Best?.EmptyTube ?? false,
        PlateA = sr.DesignCurrent?.PlateA, SegPeakA = sr.DesignCurrent?.SegPeakA,
    };

    /// <summary>从检查点装回驱动要读的 SolverResult（Best 只带判据表与几个状态位：Fill／Deficit／HardCritValue 读的就是这些）。</summary>
    public static SolverResult Rebuild(Entry e, DesignInputs baseIn)
    {
        var d = JsonSerializer.Deserialize<DesignSpec>(e.DesignJson, J) ?? throw new InvalidDataException("检查点里的设计读不回来：" + e.Key);
        LineResult? best = e.Checks is null ? null : new LineResult
        {
            Checks = e.Checks, Ok = e.BestOk, Converged = e.BestConverged, RampChecked = e.BestRampChecked, EmptyTube = e.BestEmptyTube,
            RuleSet = baseIn.CriteriaRuleSet,
        };
        var sr = new SolverResult
        {
            Design = d, Best = best, Feasible = e.Feasible, MassG = e.MassG,
            Message = e.Message, StopWhy = e.StopWhy, UndeterminedWhy = e.UndeterminedWhy, NullWhy = e.NullWhy,
            HitBound = e.HitBound, Undetermined = e.Undetermined, FineRefined = e.FineRefined, FineMmUsed = e.FineMmUsed,
            SkippedRadiusGrowthToMm = e.SkippedRadiusGrowthToMm, SkippedRadiusGrowthWhy = e.SkippedRadiusGrowthWhy,
            Solves = e.Solves, SameStateReuses = e.SameStateReuses,
            DesignCurrent = e.PlateA is null ? null : new DesignCurrent.Result { PlateA = e.PlateA, SegPeakA = e.SegPeakA ?? Array.Empty<double>() },
        };
        sr.Trace.Add("续跑：来自检查点 " + e.When + "（代码戳 " + e.Stamp + "）");
        return sr;
    }
}
