# -*- coding: utf-8 -*-
# ============================================================================
#  量一量：读源码的断言里，有多少条是「钉别人方法里的实现文字」
#  （2026-09-08，督导第 12/13 封）
#
#  ══ 为什么要有这段命令
#
#  BrittleAssertionTests（全仓门 A）的注释写了**两类**要防的断言：
#      ① 钉个数    Assert.Equal(25, Flow.Commands.Count)
#      ② 钉措辞    Assert.Contains("C 整线", prefixes)
#  而它的正则**只实现了第一类** ⇒ 全仓几百条钉措辞的断言一条都不在射程内。
#  也就是说：连「防止门被窄化」的那道门，自己也被窄化了。
#
#  ⚠ 更要紧的是：这个数以前是各自 grep 数出来的，两边口径一差就漏
#    （督导数 Solver 5 处、我数 6 处，就是这么来的）。
#    **名单与数字都必须由一段可复跑的命令产生。** 这段就是那个命令。
#
#  ══ 判据（督导第 13 封给的，不用人维护白名单）
#
#      Assert.Contains(str, <运行时产生的值>)   ✅ 行为门，措辞变了就是行为变了，本来就该红
#      Assert.Contains(str, <读进来的源码文本>) ⚠ 拿实现文字当行为的代理
#        └ 其中被钉的字符串**也出现在产品码的字符串字面量里** ⇒ 正当
#            （那是对外文案／被禁的词，字符串本身就是要求）
#        └ 只出现在产品码的**语句或标识符**里         ⇒ **嫌疑**
#
#  ══ 已知的低报（这个数是下界，不是精确值）
#
#   · 内插洞 {..} 里是代码不是文案，已剔掉；不剔会把 TTabEndC 这类算成正当（-16 条）
#   · 第二参数只认 s/src/body/text/txt/code 这几个惯用名，别的变量名漏掉
#   · `pin in LIT` 是在所有字面量**拼起来**的大串里找，跨边界会误判成正当
#
#  用法： python tools/量一量_钉字面的断言.py     （在仓库根目录跑）
#  基准： 2026-09-08 于 ff3777b —— 正当 113 / **嫌疑 305** / 合计 418
# ============================================================================
import io, os, re, glob, collections

ROOTS = ("Pt_Optimize/Core", "Pt_Optimize/UI", "Pt_Optimize")
LITRE = re.compile(r'"((?:[^"\\' + '\n' + r']|\\.)*)"')

lits, code_parts = [], []
for root in ROOTS:
    for f in glob.glob(os.path.join(root, "*.cs")):
        t = io.open(f, encoding="utf-8", errors="ignore").read()
        # 内插洞 {..} 里是**代码**，不是对外文案 —— 不剔的话 TTabEndC 这类标识符
        # 会被算成「正当」，嫌疑数被低报。
        lits += [re.sub(r'\{[^{}]*\}', ' ', x) for x in LITRE.findall(t)]
        code_parts.append(LITRE.sub(" ", t))
LIT = "".join(lits)
CODE = "".join(code_parts)

SRCVAR = re.compile(
    r'Assert\.(?:Contains|DoesNotContain)\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*(s|src|body|text|txt|code)\b')

sus, ok = collections.Counter(), collections.Counter()
sus_ex, ok_ex = [], []
for f in sorted(glob.glob("Pt_Optimize.Tests/*.cs")):
    t = io.open(f, encoding="utf-8", errors="ignore").read()
    if "File.ReadAllText" not in t and "ReadAllLines" not in t:
        continue
    b = os.path.basename(f)
    for m in SRCVAR.finditer(t):
        pin = m.group(1)
        if len(pin) < 2:
            continue
        if pin in LIT:
            ok[b] += 1
            ok_ex.append((b, pin))
        else:
            sus[b] += 1
            sus_ex.append((b, pin))

print("=== mechanical criterion: where does the pinned string live in product code ===")
print("  legit  (inside a product string literal = outward text / banned word): %d" % sum(ok.values()))
print("  SUSPECT(only in statements or identifiers = proxy for implementation): %d" % sum(sus.values()))
print("  total: %d" % (sum(ok.values()) + sum(sus.values())))
print()
print("heaviest suspect files:")
for b, n in sus.most_common(12):
    print("  %4d  %s" % (n, b))
print()
print("suspect samples:")
for b, p in sus_ex[:16]:
    print("  - %-34s %s" % (b, p[:60]))
print()
print("legit samples:")
for b, p in ok_ex[:8]:
    print("  - %-34s %s" % (b, p[:60]))
