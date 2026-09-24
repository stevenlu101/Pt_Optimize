using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PtOptimize.Core;

/// <summary>
/// ★ 同状态整线解复用（2026-09-24，搜形状算力工程 A；全局解决方案第 2 版 §5「归因每轮场解来源并去重复」）。
///
/// 病：<see cref="Solver"/> 一轮里有几处场解**解的是刚解过的同一个状态**（归因见实施记录「09-24 按夜跑数据改的六件事」A 节）：
///   · <c>ChooseKnob</c> 开头的 <c>r0 = Eval(d, …)</c>：第一条违反的 d 就是本轮基准场那个状态；其后每一条的 d 是上一条 RaiseUntil
///     落地（或退回）后的状态，而那个状态在上一条的二分／上界探针／比价基准里已经解过；
///   · 第 2 轮起每轮开头的基准场 <c>Eval(d, …)</c>：d 是上一轮最后一条落地后的状态（ApplySectionFloor 没抬厚时）。
/// 这些重解**逐位**得到同一个结果（LineRunner.Run 对同一个算例是确定的），只花机时。
///
/// 治法（不改判定、不改阈值、不改旋钮分派）：<see cref="Solver"/> 的 EvalRaw 在造好算例（BuildCase + ApplyCaseMesh）之后、
/// 调 <see cref="LineRunner.Run"/> 之前，用 <see cref="KeyOf"/> 给算例取一个**全字段**指纹；同一次 Solve 里指纹相同的算例
/// 直接交回上次的 <see cref="LineResult"/>（同一个对象；Solver 不改写 LineResult，见实施记录的核查），不再解场。
/// 只在 <see cref="SolverOptions.ReuseSameStateSolves"/> = true 时启用；**缺省 false = 改前逐位**（改回参数就是它）。
///
/// 指纹：沿算例对象图逐字段（公开与非公开实例字段、数组逐元、集合逐项）写出位级表示再取 SHA-256。
/// double 按位（<see cref="BitConverter.DoubleToInt64Bits"/>），不做容差 ⇒ 只有逐位相同的算例才复用。
/// 对象图里出现委托（如 <see cref="LineCase.CoupleTrace"/> 非空）、指针、反射对象、或深度超过 <see cref="MaxDepth"/> ⇒ 不取指纹（返回 null）⇒ 该次照常解场。
/// 漏字段的风险方向：只会「本该相同却不同」（少复用，照常解），不会「本该不同却相同」：写出的是全部字段，不是挑出来的几个。
/// 静态状态（全局开关）不进指纹：复用只在**同一次 Solve 之内**，静态状态在一次 Solve 里不变。
/// </summary>
public sealed class SameStateReuse
{
    /// <summary>
    /// 最近用过的状态最多留几个（LRU）。**选定**，依据是「上一条违反落地的状态」到「下一处重解它」之间最多隔几次场解：
    /// 一次 RaiseUntil ≤ 1（上界）+ <see cref="SolverOptions.BisectMaxIter"/>（14）+ 1（对齐）+ <see cref="SolverOptions.CertWalkMaxSteps"/>（10）= 26 次，
    /// 一次 ChooseKnob ≤ 1（基准）+ 5（候选最多 5 根，Solver.Allocation）+ 形状族探针；取 64 盖住这两段之和并留余量。
    /// 只影响命中率（没命中就照常解），不影响任何结果；导航网格一次整线解的结果约几百 KB，64 个 × 并发 4 路在 15 GB 机器上可忽略。
    /// </summary>
    public const int Capacity = 64;

    /// <summary>对象图最大深度（防病态的深链把栈打穿；超过 ⇒ 不取指纹、照常解）。选定：LineCase 实测对象图深度远小于它。</summary>
    public const int MaxDepth = 64;

    private readonly Dictionary<string, LinkedListNode<(string Key, LineResult R)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, LineResult R)> _lru = new();

    /// <summary>命中次数（= 省下的场解次数）。</summary>
    public int Hits { get; private set; }
    /// <summary>取了指纹、没命中、照常解场后存进来的次数。</summary>
    public int Stored { get; private set; }

    public bool TryGet(string key, out LineResult r)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _lru.Remove(node); _lru.AddFirst(node);
            r = node.Value.R; Hits++;
            return true;
        }
        r = null!;
        return false;
    }

    public void Put(string key, LineResult r)
    {
        if (r is null) return;
        if (_map.TryGetValue(key, out var old)) { _lru.Remove(old); _map.Remove(key); }
        var node = _lru.AddFirst((key, r));
        _map[key] = node;
        Stored++;
        while (_lru.Count > Capacity)
        {
            var tail = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(tail.Value.Key);
        }
    }

    /// <summary>算例的全字段指纹（SHA-256 十六进制）；取不了 ⇒ null（调用方照常解场）。</summary>
    public static string? KeyOf(LineCase lc)
    {
        if (lc is null) return null;
        try
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                new Walker(w).Visit(lc, 0);
            ms.Position = 0;
            return Convert.ToHexString(SHA256.HashData(ms));
        }
        catch (NotSupportedException) { return null; }
    }

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldsCache = new();

    private static FieldInfo[] FieldsOf(Type t) => FieldsCache.GetOrAdd(t, static tt =>
    {
        var list = new List<FieldInfo>();
        for (var x = tt; x is not null && x != typeof(object) && x != typeof(ValueType); x = x.BaseType)
            list.AddRange(x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                           .OrderBy(f => f.MetadataToken));
        return list.ToArray();
    });

    private sealed class Walker
    {
        private readonly BinaryWriter _w;
        private readonly Dictionary<object, int> _seen = new(ReferenceEqualityComparer.Instance);
        public Walker(BinaryWriter w) => _w = w;

        public void Visit(object? o, int depth)
        {
            if (depth > MaxDepth) throw new NotSupportedException("对象图太深");
            if (o is null) { _w.Write((byte)0); return; }
            var t = o.GetType();
            switch (o)
            {
                case string s: _w.Write((byte)1); _w.Write(s); return;
                case double d: _w.Write((byte)2); _w.Write(BitConverter.DoubleToInt64Bits(d)); return;
                case float f: _w.Write((byte)3); _w.Write(BitConverter.SingleToInt32Bits(f)); return;
                case bool b: _w.Write((byte)4); _w.Write(b); return;
                case int i: _w.Write((byte)5); _w.Write(i); return;
                case long l: _w.Write((byte)6); _w.Write(l); return;
                case decimal m: _w.Write((byte)7); foreach (int bits in decimal.GetBits(m)) _w.Write(bits); return;
                case Delegate: throw new NotSupportedException("对象图里有委托");
                case MemberInfo: throw new NotSupportedException("对象图里有反射对象");
                case IntPtr or UIntPtr: throw new NotSupportedException("对象图里有指针");
            }
            if (t.IsPointer) throw new NotSupportedException("对象图里有指针");
            if (t.IsEnum) { _w.Write((byte)8); _w.Write(t.FullName ?? t.Name); _w.Write(Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (t.IsPrimitive)
            {
                // 其余基元（byte、sbyte、short、ushort、uint、ulong、char）：写类型名与按位的 64 位值
                _w.Write((byte)9); _w.Write(t.FullName ?? t.Name);
                _w.Write(o is char c ? ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                     : Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "");   // 整数的十进制串是逐位的
                return;
            }
            if (!t.IsValueType)
            {
                if (_seen.TryGetValue(o, out int id)) { _w.Write((byte)10); _w.Write(id); return; }
                _seen[o] = _seen.Count;
            }
            if (o is Array a)
            {
                _w.Write((byte)11); _w.Write(t.FullName ?? t.Name); _w.Write(a.Rank);
                for (int r = 0; r < a.Rank; r++) _w.Write(a.GetLength(r));
                var et = t.GetElementType()!;
                if (a.Rank == 1 && et == typeof(double)) { _w.Write(MemoryMarshal.AsBytes(((double[])a).AsSpan())); return; }
                if (a.Rank == 1 && et == typeof(int)) { _w.Write(MemoryMarshal.AsBytes(((int[])a).AsSpan())); return; }
                if (a.Rank == 1 && et == typeof(bool)) { foreach (bool x in (bool[])a) _w.Write(x); return; }
                foreach (object? x in a) Visit(x, depth + 1);
                return;
            }
            if (o is IDictionary dict)
            {
                _w.Write((byte)12); _w.Write(t.FullName ?? t.Name); _w.Write(dict.Count);
                foreach (DictionaryEntry e in dict) { Visit(e.Key, depth + 1); Visit(e.Value, depth + 1); }
                return;
            }
            if (o is IList list)
            {
                _w.Write((byte)13); _w.Write(t.FullName ?? t.Name); _w.Write(list.Count);
                foreach (object? x in list) Visit(x, depth + 1);
                return;
            }
            if (o is IEnumerable seq && (t.Namespace?.StartsWith("System", StringComparison.Ordinal) ?? false))
            {
                // HashSet<T>、Queue<T> 之类的系统集合：逐项（不走它们的内部字段，那里有容量与版本号）
                _w.Write((byte)14); _w.Write(t.FullName ?? t.Name);
                int n = 0;
                foreach (object? x in seq) { Visit(x, depth + 1); n++; }
                _w.Write(n);
                return;
            }
            _w.Write((byte)15); _w.Write(t.FullName ?? t.Name);
            foreach (var f in FieldsOf(t)) Visit(f.GetValue(o), depth + 1);
        }
    }
}
