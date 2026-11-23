import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
I18N_DIR = ROOT / "Resources" / "Raw" / "i18n"
LAUNCHER = ROOT / "Resources" / "Raw" / "launcher.html"
REFERENCE = "en"
NAME_KEY = "_name"

CYRILLIC = re.compile("[\u0400-\u04FF]")
PLACEHOLDER = re.compile(r"\{(\d+)\}")
HTML_KEY_ATTR = re.compile(r"""data-i18n(?:-aria-label|-title)?\s*=\s*["']([^"']+)["']""")
JS_T_CALL = re.compile(r"""\bt\(\s*'([^'\\]+)'\s*\)""")
CS_T_CALL = re.compile(r"""\bT\(\s*"([^"\\]+)\"""")
CS_KEY_LITERAL = re.compile(r"""\"([a-z]+(?:\.[a-z0-9]+)+)\"""")
HTML_LITERAL = re.compile(r"""'([a-z]+(?:\.[a-z0-9]+)+)'""")
DEBUG_MARKERS = ("Debug.WriteLine", "onProgress")

errors = []
warnings = []


def fail(message):
    errors.append(message)


def warn(message):
    warnings.append(message)


def read_text(path):
    return path.read_text(encoding="utf-8-sig")


def reject_duplicates(pairs):
    seen = {}
    for key, value in pairs:
        if key in seen:
            raise ValueError("повторяющийся ключ: " + key)
        seen[key] = value
    return seen


def load_dictionaries():
    result = {}
    files = sorted(I18N_DIR.glob("*.json"))
    if not files:
        fail("в каталоге " + str(I18N_DIR) + " нет файлов словарей")
        return result
    for path in files:
        code = path.stem
        raw = path.read_bytes()
        if raw.startswith(b"\xef\xbb\xbf"):
            fail(path.name + ": файл содержит BOM, нужен UTF-8 без BOM")
        try:
            data = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=reject_duplicates)
        except (ValueError, UnicodeDecodeError) as exc:
            fail(path.name + ": некорректный JSON: " + str(exc))
            continue
        if not isinstance(data, dict):
            fail(path.name + ": корень должен быть объектом")
            continue
        broken = False
        for key, value in data.items():
            if not isinstance(value, str):
                fail(path.name + ": значение ключа " + key + " не является строкой")
                broken = True
        if NAME_KEY not in data or not isinstance(data.get(NAME_KEY), str) or not data[NAME_KEY].strip():
            fail(path.name + ": нет непустого ключа " + NAME_KEY)
            broken = True
        if not re.fullmatch(r"[a-z]{2,3}(-[A-Za-z0-9]{2,8})*", code):
            fail(path.name + ": имя файла не является допустимым кодом языка")
            broken = True
        if not broken:
            result[code] = data
    return result


def check_parity(dictionaries):
    if REFERENCE not in dictionaries:
        fail("нет эталонного словаря " + REFERENCE + ".json")
        return
    reference = dictionaries[REFERENCE]
    for code, data in sorted(dictionaries.items()):
        if code == REFERENCE:
            continue
        missing = sorted(set(reference) - set(data))
        extra = sorted(set(data) - set(reference))
        for key in missing:
            fail(code + ": отсутствует ключ " + key)
        for key in extra:
            fail(code + ": лишний ключ " + key)
        for key in sorted(set(reference) & set(data)):
            expected = set(PLACEHOLDER.findall(reference[key]))
            actual = set(PLACEHOLDER.findall(data[key]))
            if expected != actual:
                fail(
                    code + ": ключ " + key + ": подстановки "
                    + sorted(actual).__str__() + " вместо " + sorted(expected).__str__()
                )


def scan_cs(text):
    literals = []
    i = 0
    n = len(text)
    line = 1
    prefix = re.compile(r'(\$@|@\$|\$|@)?"')
    while i < n:
        c = text[i]
        if c == "\n":
            line += 1
            i += 1
            continue
        if text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            line += text.count("\n", i, j)
            i = j
            continue
        if c == "'":
            j = i + 1
            if j < n and text[j] == "\\":
                j += 2
            else:
                j += 1
            j = text.find("'", j)
            i = n if j < 0 else j + 1
            continue
        match = prefix.match(text, i)
        if match:
            verbatim = "@" in (match.group(1) or "")
            start_line = line
            j = match.end()
            buffer = []
            while j < n:
                ch = text[j]
                if verbatim:
                    if ch == '"':
                        if text.startswith('""', j):
                            buffer.append('"')
                            j += 2
                            continue
                        break
                else:
                    if ch == "\\":
                        buffer.append(text[j:j + 2])
                        j += 2
                        continue
                    if ch == '"' or ch == "\n":
                        break
                if ch == "\n":
                    line += 1
                buffer.append(ch)
                j += 1
            literals.append((start_line, "".join(buffer)))
            i = j if j < n and text[j] == "\n" else j + 1
            continue
        i += 1
    return literals


def code_files():
    files = [ROOT / "MainPage.xaml", ROOT / "MainPage.xaml.cs", ROOT / "App.xaml", ROOT / "App.xaml.cs",
             ROOT / "AppShell.xaml", ROOT / "AppShell.xaml.cs"]
    files += sorted((ROOT / "Services").glob("*.cs"))
    files += sorted((ROOT / "Platforms").rglob("*.cs"))
    return [path for path in files if path.exists()]


def strip_markup_comments(text):
    text = re.sub(r"<!--.*?-->", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)
    return re.sub(r"/\*.*?\*/", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)


def check_cyrillic_cs(path, text):
    lines = text.split("\n")
    for line_no, value in scan_cs(text):
        if not CYRILLIC.search(value):
            continue
        source_line = lines[line_no - 1] if line_no - 1 < len(lines) else ""
        if any(marker in source_line for marker in DEBUG_MARKERS):
            continue
        fail(str(path.relative_to(ROOT)) + ":" + str(line_no) + ": кириллица в строковом литерале: " + value)


def check_cyrillic_markup(path, text):
    cleaned = strip_markup_comments(text)
    if path.suffix == ".html":
        cleaned = "\n".join(
            "" if re.match(r"\s*//", row) else row for row in cleaned.split("\n")
        )
    for number, row in enumerate(cleaned.split("\n"), 1):
        if CYRILLIC.search(row):
            fail(str(path.relative_to(ROOT)) + ":" + str(number) + ": кириллица в разметке или скрипте: " + row.strip())


def collect_used_keys(prefixes, reference_keys):
    used = {}

    def add(key, where):
        used.setdefault(key, []).append(where)

    html = read_text(LAUNCHER)
    html_clean = strip_markup_comments(html)
    for match in HTML_KEY_ATTR.finditer(html_clean):
        add(match.group(1), "launcher.html")
    for match in JS_T_CALL.finditer(html_clean):
        add(match.group(1), "launcher.html")
    for match in HTML_LITERAL.finditer(html_clean):
        key = match.group(1)
        if key.split(".")[0] in prefixes:
            add(key, "launcher.html")

    for path in code_files():
        if path.suffix != ".cs":
            continue
        text = read_text(path)
        for match in CS_T_CALL.finditer(text):
            add(match.group(1), str(path.relative_to(ROOT)))
        for match in CS_KEY_LITERAL.finditer(text):
            key = match.group(1)
            if key.split(".")[0] in prefixes and key in reference_keys:
                add(key, str(path.relative_to(ROOT)))
    return used


def check_usage(dictionaries):
    if REFERENCE not in dictionaries:
        return
    reference = dictionaries[REFERENCE]
    prefixes = {key.split(".")[0] for key in reference if "." in key}
    used = collect_used_keys(prefixes, set(reference))
    for key, places in sorted(used.items()):
        if key not in reference:
            fail("ключ " + key + " используется в " + ", ".join(sorted(set(places))) + ", но отсутствует в " + REFERENCE + ".json")
    for key in sorted(set(reference) - set(used) - {NAME_KEY}):
        warn("ключ " + key + " не используется в коде")


def check_cyrillic():
    for path in code_files():
        text = read_text(path)
        if path.suffix == ".cs":
            check_cyrillic_cs(path, text)
        else:
            check_cyrillic_markup(path, text)
    check_cyrillic_markup(LAUNCHER, read_text(LAUNCHER))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    dictionaries = load_dictionaries()
    check_parity(dictionaries)
    check_usage(dictionaries)
    check_cyrillic()

    for message in warnings:
        print("ПРЕДУПРЕЖДЕНИЕ: " + message)
    for message in errors:
        print("ОШИБКА: " + message)

    languages = ", ".join(sorted(dictionaries)) or "нет"
    print("Проверено словарей: " + str(len(dictionaries)) + " (" + languages + ")")
    if errors:
        print("Найдено ошибок: " + str(len(errors)))
        return 1
    print("Ошибок нет")
    return 0


if __name__ == "__main__":
    sys.exit(main())
