# -*- coding: utf-8 -*-
"""
Расставляет переносы [r] в русских сценариях, чтобы строка не вылезала за окно диалога.
    python -m core.wrap_ru <папка_с_переводами> <папка_вывода> [--width 34] [--lines 2]
Исходные файлы не меняются. Обрабатываются только реплики (блок от строки "＠имя" до "[k]").
Если реплика не помещается в --lines строк, она режется на несколько страниц ([k] + та же строка с именем).
Ширина считается в «условных буквах»: обычная строчная = 1, широкие/заглавные = 1.2, пробел и знаки = 0.6.
"""
import re, sys, argparse
from pathlib import Path

TAG = re.compile(r"\[[^\]\n]*\]")
LEAD = re.compile(r"^(?:\[[0-9a-fA-F]{6}\]|\[line \d+\])+")
WIDE = set("ЫШЩЖЮМФЪЭ") | set("шщжюымф")
NARROW = set(" .,:;!?…-—–()«»\"'")


def cw(ch):
    if ch in NARROW:
        return 0.6
    if ch.isupper() or ch in WIDE:
        return 1.2
    return 1.0


def tokens(s):
    """Разбивает строку на неделимые куски: ('t', слово/пробел) и ('g', тег)."""
    out, i = [], 0
    for m in TAG.finditer(s):
        if m.start() > i:
            out += [("t", x) for x in re.findall(r"\s+|\S+", s[i:m.start()])]
        out.append(("g", m.group()))
        i = m.end()
    if i < len(s):
        out += [("t", x) for x in re.findall(r"\s+|\S+", s[i:])]
    return out


def tag_width(tag):
    if tag.startswith("[#") and ":" in tag:
        return sum(cw(c) for c in tag[2:tag.index(":")])
    if tag.startswith("[%"):
        return 4.0
    return 0.0


def wrap_body(body, width):
    """Строка (без ведущих/замыкающих цветовых тегов) -> список строк. Режет только по пробелам вне тегов."""
    words, cur, curw = [], [], 0.0     # слова = последовательности токенов без пробела между ними
    parts = []                         # [(текст_слова, ширина)], пробелы отдельно
    for kind, v in tokens(body):
        if kind == "g":
            parts.append((v, tag_width(v), False))
        elif v.isspace():
            parts.append((" ", 0.6, True))
        else:
            parts.append((v, sum(cw(c) for c in v), False))
    # склеиваем в слова
    units, w, ww = [], "", 0.0
    for text, wd, is_space in parts:
        if is_space:
            if w:
                units.append((w, ww)); w, ww = "", 0.0
            units.append((" ", 0.6))
        else:
            w += text; ww += wd
    if w:
        units.append((w, ww))
    lines, line, lw = [], "", 0.0
    for text, wd in units:
        if text == " ":
            if line:
                line += " "; lw += wd
            continue
        if line.strip() and lw - 0.6 + wd > width and lw > 0:
            lines.append(line.rstrip()); line, lw = "", 0.0
        line += text; lw += wd
    if line.strip():
        lines.append(line.rstrip())
    return lines or [body]


def wrap_balanced(body, width):
    base = wrap_body(body, width)
    n = len(base)
    if n == 1:
        return base
    lo, hi, best = 1.0, width, base
    for _ in range(14):
        mid = (lo + hi) / 2
        t = wrap_body(body, mid)
        if len(t) <= n:
            best, hi = t, mid
        else:
            lo = mid
    return best


def wrap_segment(seg, width):
    m = LEAD.match(seg)
    lead = m.group() if m else ""
    rest = seg[len(lead):]
    trail = ""
    if lead and rest.endswith("[-]"):
        trail, rest = "[-]", rest[:-3]
    return [lead + ln + trail for ln in wrap_balanced(rest, width)]


def process(text, width, max_lines, tag=""):
    lines = text.replace("\r\n", "\n").split("\n")
    out, i, n, pages_split = [], 0, 0, 0
    while i < len(lines):
        ln = lines[i]
        if ln.startswith("＠"):
            j = i + 1
            body = []
            while j < len(lines) and lines[j].strip() != "[k]" and not lines[j].startswith("＠"):
                body.append(lines[j]); j += 1
            if j < len(lines) and lines[j].strip() == "[k]" and body:
                joined = "[r]".join(b for b in body if b.strip())
                pages, cur = [], []
                for seg in joined.split("[r]"):
                    rows = wrap_segment(seg, width)
                    if cur and len(cur) + len(rows) > max_lines:
                        pages.append(cur); cur = []
                    cur += rows
                    while len(cur) > max_lines:
                        pages.append(cur[:max_lines]); cur = cur[max_lines:]
                if cur:
                    pages.append(cur)
                for pi, pg in enumerate(pages):
                    body_txt = "[r]".join(pg)
                    if tag:                                   # опыт: NGUI-тег, например sup/sub (уменьшает шрифт)
                        body_txt = f"[{tag}]{body_txt}[/{tag}]"
                    out += [ln, body_txt, "[k]"]
                    if pi < len(pages) - 1:
                        out.append("")
                pages_split += len(pages) - 1
                n += 1
                i = j + 1
                continue
        out.append(ln)
        i += 1
    return "\n".join(out), n, pages_split



def prepare(src_dir, dst_dir, width=34.0, lines=2, tag=""):
    """Готовит dst_dir из src_dir (переносы [r]). Файлы, которые не менялись, не пересобирает. Возвращает статистику."""
    src_dir, dst_dir = Path(src_dir), Path(dst_dir)
    dst_dir.mkdir(parents=True, exist_ok=True)
    stamp = dst_dir / ".wrap_params"
    params = f"{width}:{lines}:{tag}"
    same = stamp.exists() and stamp.read_text(encoding="utf-8") == params
    done, pages = 0, 0
    names = set()
    for f in sorted(src_dir.glob("*.txt")):
        names.add(f.name)
        out = dst_dir / f.name
        if same and out.exists() and out.stat().st_mtime >= f.stat().st_mtime:
            continue
        new, n, extra = process(f.read_text(encoding="utf-8-sig"), width, lines, tag)
        out.write_text(new, encoding="utf-8", newline="\n")
        done += 1
        pages += extra
    for old in dst_dir.glob("*.txt"):          # удалённые из исходников переводы не должны остаться
        if old.name not in names:
            old.unlink()
    stamp.write_text(params, encoding="utf-8")
    return {"files": len(names), "rebuilt": done, "extra_pages": pages, "width": width, "lines": lines, "tag": tag}


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("src"); ap.add_argument("dst")
    ap.add_argument("--width", type=float, default=30.0)
    ap.add_argument("--lines", type=int, default=2)
    a = ap.parse_args()
    Path(a.dst).mkdir(parents=True, exist_ok=True)
    for f in sorted(Path(a.src).glob("*.txt")):
        t = f.read_text(encoding="utf-8-sig")
        new, n, extra = process(t, a.width, a.lines)
        (Path(a.dst) / f.name).write_text(new, encoding="utf-8", newline="\n")
        print(f"{f.name}: реплик {n}, добавлено страниц {extra}, размер {len(t.encode())} -> {len(new.encode())} Б")
