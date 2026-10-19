#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ShaderLab 属性块静态校验。

【为什么需要】
本工程在 ShaderLab 属性块上踩过几次坑，而每次都报同一个错：
    Parse error: syntax error, unexpected $undefined,
    expecting TVAL_ID or TVAL_VARREF at line N
这个 N 常常指向 [Header(...)] 那一行，而真正原因在别处 —— 光看行号查不出来。

【判据是怎么定下来的】
关键：本工程有【一直在正常工作】的 shader（ProceduralSkyCelestial / RealisticWater），
它们同样有中文属性显示名、同样在属性块里写了 `//` 注释 —— 它们能正常工作。
所以那些【不是】问题。判据必须拿这些已证实可用的 shader 来校准，
否则脚本会天天误报，最后没人看（本工程的诊断工具踩过这个坑）。

真正会导致解析失败的是这两类：
  1. [Header(...)] / [Space] 等装饰器里出现非 ASCII。
     —— 实测：中文破折号导致过 `unexpected $undefined`。
  2. 使用了 ShaderLab 不认识的属性特性（例如 [Tooltip(...)] 是 C# 的，不是 ShaderLab 的）。

另外检查：
  3. 属性名重复 —— 重复会让整个 shader 静默失效（材质 Inspector 变空白）。
  4. `_WindowGlowAmount` 这类"全局驱动属性"不应出现在属性块里 ——
     一旦写进属性块，材质上的值会覆盖 Shader.SetGlobalFloat，全局控制就失效。

用法：
    python OptimizationReview/check_shader_properties.py

退出码 0 = 通过；1 = 有问题。
"""
import io
import os
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets")

# 已确认可用的 ShaderLab 属性特性。
# 来源：本工程在用的 shader + Unity 文档。
KNOWN_ATTRIBUTES = {
    "Header", "Space",              # 装饰器
    "HDR", "Gamma", "Linear",       # 数值语义
    "MainColor", "MainTexture",     # 主属性标记
    "NoScaleOffset", "Normal",
    "PerRendererData",
    "HideInInspector",
    "IntRange", "PowerSlider", "Min", "Max",
}

# 装饰器特性的内容不允许非 ASCII（这是实测会解析失败的唯一一类）
DECORATOR_ATTRIBUTES = {"Header", "Space"}

# 由全局脚本驱动、刻意不能出现在属性块里的属性名
GLOBAL_DRIVEN = {"_WindowGlowAmount"}


def find_properties_block(lines):
    """返回 (起始行, 结束行)，0-based，含 Properties 行与其右花括号；找不到返回 None。"""
    start = None
    for i, line in enumerate(lines):
        if re.match(r"^\s*Properties\s*$", line):
            start = i
            break
    if start is None:
        return None

    depth = 0
    for i in range(start, len(lines)):
        depth += lines[i].count("{") - lines[i].count("}")
        if depth == 0 and i > start:
            return (start, i)
    return (start, len(lines) - 1)


def check_file(path):
    problems = []
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = f.read().split("\n")

    block = find_properties_block(lines)
    if block is None:
        return problems

    start, end = block
    names = []

    for i in range(start + 1, end):
        raw = lines[i]
        stripped = raw.strip()

        if stripped.startswith("//"):
            continue      # 注释是允许的（在用的 shader 里就有）

        for m in re.finditer(r"\[(\w+)\s*(\(([^)]*)\))?", raw):
            attr = m.group(1)
            arg = m.group(3) or ""

            if attr not in KNOWN_ATTRIBUTES:
                problems.append((i + 1, f"ShaderLab 不认识的属性特性 [{attr}]", stripped[:70]))
                continue

            if attr in DECORATOR_ATTRIBUTES:
                bad = "".join(ch for ch in arg if ord(ch) > 127)
                if bad:
                    problems.append((
                        i + 1,
                        f"[{attr}] 的内容里有非 ASCII 字符 {bad!r} —— 会导致整块解析失败",
                        stripped[:70],
                    ))

        m = re.match(r"^\s*(_\w+)\s*\(", raw)
        if m:
            names.append((i + 1, m.group(1)))

    seen = {}
    for line_no, name in names:
        if name in GLOBAL_DRIVEN:
            problems.append((
                line_no,
                f"{name} 不应出现在属性块里（写进来会让材质值覆盖 Shader.SetGlobalFloat）",
                "应改为只在 HLSL 里声明为全局 uniform",
            ))
        if name in seen:
            problems.append((
                line_no,
                f"属性名重复 {name}（重复会让整个 shader 静默失效）",
                f"首次出现在第 {seen[name]} 行",
            ))
        else:
            seen[name] = line_no

    return problems


def main():
    if not os.path.isdir(ROOT):
        print(f"找不到 Assets 目录：{ROOT}")
        return 1

    shaders = []
    for dirpath, _, filenames in os.walk(ROOT):
        for name in filenames:
            if name.endswith(".shader"):
                shaders.append(os.path.join(dirpath, name))
    shaders.sort()

    total = 0
    passed = 0
    print(f"检查 {len(shaders)} 个 shader 的属性块...")
    print()

    for path in shaders:
        problems = check_file(path)
        rel = os.path.relpath(path, os.path.join(ROOT, ".."))
        if problems:
            total += len(problems)
            print(f"  ✗ {rel}")
            for line_no, reason, ctx in problems:
                print(f"      行 {line_no}: {reason}")
                print(f"                {ctx}")
        else:
            passed += 1

    print()
    if total:
        print(f"FAIL：{total} 个问题（{passed} 个 shader 通过）")
        print()
        print("修法速查：")
        print("  - [Header(中文)] / [Space(中文)] → 改成 ASCII（显示名可以是中文，装饰器内容不行）")
        print("  - [Tooltip(...)]                 → ShaderLab 不支持，删掉，说明写成块外的注释")
        print("  - 属性名重复                     → 改名或合并")
        print("  - 全局驱动属性出现在属性块里      → 移出属性块，只留 HLSL 里的全局声明")
        return 1

    print(f"PASS：{passed} 个 shader 的属性块全部合规。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
