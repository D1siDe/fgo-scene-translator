# -*- coding: utf-8 -*-
"""
Рабочие файлы для перевода сцен: японский текст рядом с местом для русского, без тегов управления.

  python scene_tool.py export [--chapter 02] [--scene 0000000010] [--limit N]
        из scripts_ja\\ делает work\\<сцена>.txt для сцен, которых ещё нет в translations\\scenarios\\ (готовые work-файлы не трогает)
  python scene_tool.py build  [--partial] [--scene ID]
        собирает work\\*.txt обратно в формат игры -> translations\\scenarios\\<сцена>.txt, проверяя теги
        (--partial: непереведённые реплики остаются на японском; без ключа сцена собирается только целиком)
  python scene_tool.py status
        сколько реплик переведено в каждой сцене work\\
  python scene_tool.py glossary
        собирает имена говорящих из scripts_ja\\ в glossary.json (ru вписываете сами; применяется при сборке)
  python scene_tool.py selftest
        проверка: сцена -> work -> сцена без изменений

Формат work-файла: у каждой реплики блок
    ## 7 | キャラA
    JA: こんにちは、[%1][&君:ちゃん]。[r]これは例文です。
    RU: Привет, [%1], [&друг:подруга]. [r]Это пример.
Теги ([r] [k] [line 2] [%1] [&муж:жен] и т.п.) переносятся в русский текст как есть. [&а:б] - тег рода: переводится текст внутри.
"""
import argparse, json, os, re, sys
from collections import Counter
from pathlib import Path

BASE = Path(__file__).parent
DATA = Path(os.environ["FGO_DATA_DIR"]) if os.environ.get("FGO_DATA_DIR") else BASE   # FGO_DATA_DIR - общая папка данных
SRC = DATA / "scripts_ja"
WORK = DATA / "work"
TRANS = Path(os.environ["FGO_TRANSLATIONS"]) if os.environ.get("FGO_TRANSLATIONS") else BASE / "translations" / "scenarios"   # переменная FGO_TRANSLATIONS - своя папка переводов
GLOSS = BASE / "glossary.json"

TAG = re.compile(r"\[[^\]]*\]")
CHOICE = re.compile(r"^(？\d+：)(.*)$")
KANA = re.compile(r"[぀-ヿ]")


def has_text(line):
    return bool(TAG.sub("", line).strip())


def signature(text):
    """Мультимножество тегов: [&а:б] считается одним видом (внутри переводится текст)."""
    c = Counter()
    for t in TAG.findall(text):
        c["[&]" if t.startswith("[&") else t] += 1
    return c


def read_scene(path):
    raw = Path(path).read_bytes().decode("utf-8")
    eol = "\r\n" if "\r\n" in raw else "\n"
    lines = raw.split(eol)
    return lines, eol


def parse_units(lines):
    """[(kind, start, end, prefix, text_lines, speaker)]; end не включается."""
    units, i, speaker = [], 0, ""
    while i < len(lines):
        ln = lines[i]
        if ln.startswith("＠"):
            speaker = ln[1:]
            i += 1
            continue
        m = CHOICE.match(ln)
        if m:
            if m.group(2).strip():
                units.append(("choice", i, i + 1, m.group(1), [m.group(2)], speaker))
            i += 1
            continue
        if ln.startswith(("？", "＄", "＃")) or not has_text(ln):
            i += 1
            continue
        j = i
        while j < len(lines) and has_text(lines[j]) and not lines[j].startswith(("＠", "？", "＄", "＃")):
            j += 1
        units.append(("text", i, j, "", lines[i:j], speaker))
        i = j
    return units


# ---------------------------------------------------------------- export
def make_work(scene_id, src, header_extra=""):
    lines, _ = read_scene(src)
    out = [f"# {scene_id}{header_extra}", "# Пишите перевод в строках RU:. Теги в [] переносите как есть. Пустой RU: = не переведено.", ""]
    for n, (kind, s, e, pre, tl, spk) in enumerate(parse_units(lines), 1):
        out.append(f"## {n} | {spk}" + (" | выбор" if kind == "choice" else ""))
        out += [f"JA: {x}" for x in tl]
        out += ["RU: " for _ in tl]
        out.append("")
    return "\n".join(out)


def cmd_export(a):
    WORK.mkdir(parents=True, exist_ok=True)
    done = {p.stem for p in TRANS.glob("*.txt")} if TRANS.exists() else set()
    n = skipped = 0
    for folder in sorted(SRC.glob("ScriptActionEncrypt_*")):
        if a.chapter and not folder.name.endswith("_" + a.chapter):
            continue
        for src in sorted(folder.glob("*.txt")):
            sid = src.stem
            if a.scene and sid != a.scene:
                continue
            if sid in done or (WORK / f"{sid}.txt").exists():
                skipped += 1
                continue
            lines, _ = read_scene(src)
            if not parse_units(lines):
                continue
            (WORK / f"{sid}.txt").write_text(make_work(sid, src, f"  ({folder.name})"), encoding="utf-8")
            n += 1
            if a.limit and n >= a.limit:
                break
        if a.limit and n >= a.limit:
            break
    print(f"Создано work-файлов: {n}; пропущено (уже переведены или уже есть): {skipped}. Папка: {WORK}")


# ---------------------------------------------------------------- парсинг work
def parse_work(path):
    blocks, cur = {}, None
    for ln in Path(path).read_text(encoding="utf-8").splitlines():
        m = re.match(r"^## (\d+)\b", ln)
        if m:
            cur = int(m.group(1))
            blocks[cur] = {"ja": [], "ru": []}
        elif cur is not None and ln.startswith("JA:"):
            blocks[cur]["ja"].append(ln[3:].lstrip(" ") if ln[3:4] == " " else ln[3:])
        elif cur is not None and ln.startswith("RU:"):
            blocks[cur]["ru"].append(ln[4:] if ln[3:4] == " " else ln[3:])
    return blocks


def load_gloss():
    try:
        g = json.loads(GLOSS.read_text(encoding="utf-8"))
        return {k: v for k, v in g.items() if v}
    except Exception:
        return {}


def build_scene(sid, partial=False, gloss=None):
    """-> (text|None, [ошибки], [предупреждения], переведено, всего)"""
    src = next(iter(SRC.glob(f"ScriptActionEncrypt_*/{sid}.txt")), None)
    if src is None:
        return None, [f"нет исходника scripts_ja\\*\\{sid}.txt"], [], 0, 0
    lines, eol = read_scene(src)
    units = parse_units(lines)
    blocks = parse_work(WORK / f"{sid}.txt")
    gloss = gloss or {}
    errs, warns, tr = [], [], 0
    new = list(lines)
    # замены с конца, чтобы не сбивать индексы
    repl = {}
    for n, (kind, s, e, pre, tl, spk) in enumerate(units, 1):
        b = blocks.get(n)
        if b is None:
            errs.append(f"реплики {n} нет в work-файле")
            continue
        if [x.rstrip() for x in b["ja"]] != [x.rstrip() for x in tl]:
            errs.append(f"#{n}: строка JA изменена (правьте только RU)")
            continue
        ru = b["ru"]
        if not any(x.strip() for x in ru):
            if not partial:
                errs.append(f"#{n}: не переведено")
            continue
        if len(ru) != len(tl):
            errs.append(f"#{n}: строк RU {len(ru)}, а JA {len(tl)}")
            continue
        if any(not x.strip() for x in ru):
            errs.append(f"#{n}: часть строк RU пустая")
            continue
        sj, sr = signature("".join(tl)), signature("".join(ru))
        if sj != sr:
            lost = sj - sr
            extra = sr - sj
            msg = []
            if lost: msg.append("нет тегов " + " ".join(lost.elements()))
            if extra: msg.append("лишние теги " + " ".join(extra.elements()))
            errs.append(f"#{n}: " + "; ".join(msg))
            continue
        if KANA.search(TAG.sub("", "".join(ru))):
            warns.append(f"#{n}: в русском тексте остались японские знаки")
        repl[(s, e)] = [pre + ru[0]] + ru[1:] if kind == "choice" else ru
        tr += 1
    for (s, e), val in sorted(repl.items(), reverse=True):
        new[s:e] = val
    # имена говорящих
    for i, ln in enumerate(new):
        if ln.startswith("＠") and ln[1:] in gloss:
            new[i] = "＠" + gloss[ln[1:]]
    return (None if errs else eol.join(new)), errs, warns, tr, len(units)


def cmd_build(a):
    TRANS.mkdir(parents=True, exist_ok=True)
    gloss = load_gloss()
    ok = bad = 0
    for p in sorted(WORK.glob("*.txt")):
        sid = p.stem
        if a.scene and sid != a.scene:
            continue
        text, errs, warns, tr, total = build_scene(sid, a.partial, gloss)
        for w in warns:
            print(f"  [предупреждение] {sid} {w}")
        if text is None:
            if tr == 0 and total and all("не переведено" in e for e in errs):
                continue       # сцена ещё не начата - не шумим
            print(f"[НЕ СОБРАНО] {sid} ({tr}/{total}):")
            for e in errs[:15]:
                print("   ", e)
            bad += 1
            continue
        (TRANS / f"{sid}.txt").write_bytes(text.encode("utf-8"))
        print(f"[OK] {sid}: {tr}/{total} реплик -> translations\\scenarios\\{sid}.txt")
        ok += 1
    print(f"\nСобрано: {ok}, с ошибками: {bad}.")


def cmd_status(a):
    tot = done = 0
    for p in sorted(WORK.glob("*.txt")):
        bl = parse_work(p)
        t = sum(1 for b in bl.values() if any(x.strip() for x in b["ru"]))
        tot += len(bl); done += t
        mark = "готово" if t == len(bl) else ("начато" if t else "—")
        print(f"{p.stem}: {t}/{len(bl)} {mark}")
    print(f"\nВсего реплик {tot}, переведено {done}")


def cmd_glossary(a):
    old = {}
    if GLOSS.exists():
        old = json.loads(GLOSS.read_text(encoding="utf-8"))
    cnt = Counter()
    for src in SRC.glob("ScriptActionEncrypt_*/*.txt"):
        lines, _ = read_scene(src)
        for ln in lines:
            if ln.startswith("＠") and ln[1:].strip():
                cnt[ln[1:]] += 1
    res = {k: old.get(k, "") for k, _ in cnt.most_common()}
    GLOSS.write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"Имён говорящих: {len(res)}; заполнено: {sum(1 for v in res.values() if v)}. Файл: {GLOSS}")


def cmd_selftest(a):
    import tempfile, shutil
    global SRC, WORK
    tmp = Path(tempfile.mkdtemp())
    sample = ("＄00-00-00-00-1-0\r\n\r\n[soundStopAll]\r\n[charaSet A 0000000 1 キャラA]\r\n\r\n[scene 10000]\r\n[charaTalk A]\r\n"
              "＠キャラA\r\nこんにちは、[%1][&君:ちゃん]。[r]これは試験用の文です。\r\n[k]\r\n\r\n？1：はい、そうです。\r\n？2：いいえ。\r\n？！\r\n\r\n"
              "＠キャラB\r\nそれでは始めましょう。[r]準備はいいですか[line 2]\r\n続きの文です。\r\n[k]\r\n")
    (tmp / "scripts_ja" / "ScriptActionEncrypt_00").mkdir(parents=True)
    (tmp / "scripts_ja" / "ScriptActionEncrypt_00" / "0000000010.txt").write_bytes(sample.encode("utf-8"))
    SRC, WORK = tmp / "scripts_ja", tmp / "work"
    WORK.mkdir()
    src = SRC / "ScriptActionEncrypt_00" / "0000000010.txt"
    wf = make_work("0000000010", src)
    # RU = JA
    lines = wf.splitlines()
    out = []
    ja_buf = []
    for l in lines:
        if l.startswith("JA: "):
            ja_buf.append(l[4:]); out.append(l)
        elif l.startswith("RU:"):
            out.append("RU: " + ja_buf.pop(0))
        else:
            ja_buf = []; out.append(l)
    (WORK / "0000000010.txt").write_text("\n".join(out), encoding="utf-8")
    text, errs, warns, tr, total = build_scene("0000000010")
    assert text is not None, errs
    assert text.encode("utf-8") == sample.encode("utf-8"), "round-trip не совпал"
    print(f"[OK] круговая проверка: реплик {total}, файл совпал байт в байт")
    # проверка ошибок: потерян тег
    broken = "\n".join(out).replace("RU: こんにちは、[%1][&君:ちゃん]。[r]", "RU: Привет, [&друг:подруга]. ")
    (WORK / "0000000010.txt").write_text(broken, encoding="utf-8")
    text, errs, *_ = build_scene("0000000010")
    assert text is None and any("[%1]" in e and "[r]" in e for e in errs), errs
    print("[OK] потерянные теги обнаружены:", errs[0])
    shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    e = sub.add_parser("export"); e.add_argument("--chapter"); e.add_argument("--scene"); e.add_argument("--limit", type=int)
    b = sub.add_parser("build"); b.add_argument("--partial", action="store_true"); b.add_argument("--scene")
    sub.add_parser("status"); sub.add_parser("glossary"); sub.add_parser("selftest")
    a = ap.parse_args()
    {"export": cmd_export, "build": cmd_build, "status": cmd_status, "glossary": cmd_glossary, "selftest": cmd_selftest}[a.cmd](a)
