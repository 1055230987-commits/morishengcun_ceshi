#!/usr/bin/env python3
"""扫描 C# 源码里【字符串字面量内部】的英文双引号。

【为什么要这个工具】
本工程反复出现同一个编译错误：在中文提示文字里用了英文双引号，例如：

    $"没有物品时不该因为"不支持查物品"被拒"

C# 看到的是 `"没有物品时不该因为"` 然后跟着一个标识符 `不支持查物品`，
于是报 `CS1003: 语法错误，应输入","` —— 报错位置还常常指在别的行上。
**这个错误在本工程已经发生过至少 6 次。**

约定：中文文案里的引号一律用「」。

【判据（第一版错了，产生了 29 个假警报）】
第一版只看"结束引号后面紧跟字母/中文"，结果把这种合法写法全报了出来：

    $"装载模式：{(Application.isPlaying ? "Play" : "编辑")}"

—— 插值洞 `{...}` 里本来就可以有字符串。假警报比没有工具更糟：
它会让人不再相信这个工具（本工程在 shader 扫描上踩过同一个坑）。

【现在的判据】
正确解析插值字符串的【洞】：
  - 遇到 $" 时，进入"插值字符串"状态
  - 字符串内容里遇到 { 就进入"洞"，洞里是【普通 C# 代码】，
    洞里的引号是合法的，按代码处理
  - 洞里要处理嵌套的花括号（如 {a ? new[]{1} : null}）与嵌套字符串
  - 遇到 } 且深度归零，回到字符串内容
  - {{ 和 }} 是转义，不是洞

只有"字符串内容里，结束引号后面紧跟标识符/中文"才算可疑。

用法：
    python check_csharp_quotes.py [根目录]
"""

import io
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")


def _is_ident_char(ch):
    if not ch:
        return False
    if ch.isalnum() or ch == "_":
        return True
    # 中日韩统一表意文字 + 中文标点（全角），这些都是"文案"而不是代码分隔符
    return "\u3000" <= ch <= "\u9fff" or "\uff00" <= ch <= "\uffef"


def scan_file(path):
    """返回 [(行号, 行内容), ...]"""
    problems = []

    with open(path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()

    i = 0
    length = len(text)

    while i < length:
        ch = text[i]
        nxt = text[i + 1] if i + 1 < length else ""

        # ---- 注释 ----
        if ch == "/" and nxt == "/":
            end = text.find("\n", i)
            i = length if end < 0 else end
            continue
        if ch == "/" and nxt == "*":
            end = text.find("*/", i + 2)
            i = length if end < 0 else end + 2
            continue

        # ---- 逐字字符串 @"..." ----
        if ch == "@" and nxt == '"':
            i += 2
            while i < length:
                if text[i] == '"':
                    if i + 1 < length and text[i + 1] == '"':
                        i += 2          # "" 是转义
                        continue
                    i += 1
                    break
                i += 1
            continue

        # ---- 字符字面量 '...' ----
        if ch == "'":
            i += 1
            while i < length:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                if text[i] == "\n":
                    break
                i += 1
            continue

        # ---- 普通字符串 / 插值字符串 ----
        if ch == '"' or (ch == "$" and nxt == '"'):
            interpolated = ch == "$"
            i += 2 if interpolated else 1
            i = _scan_string_body(text, i, interpolated, path, problems)
            continue

        i += 1

    return problems


def _scan_string_body(text, i, interpolated, path, problems):
    """扫完一个字符串的内容，返回结束后的下标。"""
    length = len(text)

    while i < length:
        ch = text[i]

        if ch == "\\":
            i += 2                  # 转义序列
            continue

        if ch == "\n":
            # 普通字符串不能跨行 —— 说明上面的判断有偏差，放弃这一处
            return i

        if ch == "{":
            if interpolated and i + 1 < length and text[i + 1] == "{":
                i += 2              # {{ 转义
                continue
            if interpolated:
                i = _scan_hole(text, i + 1)     # 跳过整个插值洞（洞内是代码）
                continue
            i += 1
            continue

        if ch == "}":
            if interpolated and i + 1 < length and text[i + 1] == "}":
                i += 2
                continue
            i += 1
            continue

        if ch == '"':
            nxt = text[i + 1] if i + 1 < length else ""
            if _is_ident_char(nxt):
                line_no, content = _locate(text, i)
                problems.append((line_no, content))
            return i + 1

        i += 1

    return i


def _scan_hole(text, i):
    """扫过一个插值洞（i 指向 { 之后）。返回 } 之后的下标。

    洞内是普通 C# 代码，可能有嵌套花括号、嵌套字符串、嵌套字符字面量。
    """
    depth = 1
    length = len(text)

    while i < length and depth > 0:
        ch = text[i]

        if ch == "/" and i + 1 < length and text[i + 1] == "/":
            end = text.find("\n", i)
            i = length if end < 0 else end
            continue

        if ch == "@" and i + 1 < length and text[i + 1] == '"':
            i += 2
            while i < length:
                if text[i] == '"':
                    if i + 1 < length and text[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            continue

        if ch == "'":
            i += 1
            while i < length:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'" or text[i] == "\n":
                    i += 1
                    break
                i += 1
            continue

        if ch == '"' or (ch == "$" and i + 1 < length and text[i + 1] == '"'):
            nested_interp = ch == "$"
            i += 2 if nested_interp else 1
            # 洞里的字符串【不报】—— 它是代码的一部分
            i = _scan_string_body(text, i, nested_interp, None, [])
            continue

        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1

        i += 1

    return i


def _locate(text, index):
    line_no = text.count("\n", 0, index) + 1
    start = text.rfind("\n", 0, index) + 1
    end = text.find("\n", index)
    if end < 0:
        end = len(text)
    return line_no, text[start:end].strip()


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "_Project")

    total_files = 0
    all_problems = []

    for folder, _, files in os.walk(root):
        if os.sep + "obj" + os.sep in folder + os.sep:
            continue

        for name in files:
            if not name.endswith(".cs"):
                continue

            path = os.path.join(folder, name)
            total_files += 1

            for line_no, content in scan_file(path):
                all_problems.append((path, line_no, content))

    if not all_problems:
        print(f"PASS：{total_files} 个 C# 文件里没有「字符串内英文双引号」。")
        return 0

    print(f"发现 {len(all_problems)} 处可疑（共扫描 {total_files} 个文件）：")
    print()
    base = os.path.dirname(os.path.abspath(__file__))
    for path, line_no, content in all_problems:
        try:
            shown = os.path.relpath(path, base)
        except ValueError:
            # 不同盘符时 relpath 会抛异常（Windows 上 E: 和 C: 没有公共前缀），
            # 直接给绝对路径就好 —— 这只是显示，不该让工具崩掉。
            shown = path
        print(f"  {shown}:{line_no}")
        print(f"      {content[:120]}")
    print()
    print("约定：中文文案里的引号用「」，不要用英文双引号。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
