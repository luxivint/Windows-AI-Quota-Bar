"""Lists every L.T / L.F text in the app and checks the translation files.

    python tools/extract-strings.py            # check src/QuotaBar.App/Lang/*.json
    python tools/extract-strings.py --dump F   # write all {en, tr} pairs to F (for translators)

Each Lang/xx.json maps the English text (exactly as in the code) to the translation.
Placeholders like {0} or {1:0} must stay; missing entries fall back to English.
"""
import glob, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(ROOT, 'src', 'QuotaBar.App')
LIT = r'"((?:[^"\\]|\\.)*)"'
CALL = re.compile(r'\bL\.([TF])\(\s*' + LIT + r'\s*,\s*' + LIT, re.S)
PH = re.compile(r'\{\d+(?::[^}]*)?\}')


def unescape(s):
    return re.sub(r'\\(u[0-9a-fA-F]{4}|.)', lambda m: chr(int(m.group(1)[1:], 16)) if m.group(1)[0] == 'u' and len(m.group(1)) == 5
                  else {'n': '\n', 't': '\t', '"': '"', '\\': '\\', "'": "'"}.get(m.group(1), m.group(1)), s)


def extract():
    pairs = {}
    for f in sorted(glob.glob(os.path.join(APP, '*.cs'))):
        src = open(f, encoding='utf-8-sig').read()
        for m in CALL.finditer(src):
            en, tr = unescape(m.group(2)), unescape(m.group(3))
            pairs.setdefault(en, tr)
    return pairs


def unmatched():
    """L.T / L.F calls whose first two arguments are not plain string literals (they cannot be translated)."""
    ok = re.compile(r'\bL\.([TF])\(\s*' + LIT + r'\s*,\s*' + LIT + r'\s*[,)]', re.S)
    for f in sorted(glob.glob(os.path.join(APP, '*.cs'))):
        src = open(f, encoding='utf-8-sig').read()
        for m in re.finditer(r'\bL\.[TF]\(', src):
            if not ok.match(src, m.start()):
                yield os.path.basename(f), src[m.start():m.start() + 90].replace('\n', ' ')


def main():
    sys.stdout.reconfigure(encoding='utf-8')  # Windows consoles default to a code page without "ı", "ü", …
    pairs = extract()
    for f, text in unmatched():
        print(f'not a literal ({f}): {text}')
    if '--dump' in sys.argv:
        out = sys.argv[sys.argv.index('--dump') + 1]
        json.dump([{'en': k, 'tr': v} for k, v in pairs.items()], open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
        print(f'{len(pairs)} texts -> {out}')
        return 0
    bad = 0
    for f in sorted(glob.glob(os.path.join(APP, 'Lang', '*.json'))):
        tr = json.load(open(f, encoding='utf-8'))
        missing = [k for k in pairs if k not in tr]
        stale = [k for k in tr if k not in pairs]
        wrong = [k for k in tr if k in pairs and sorted(PH.findall(k)) != sorted(PH.findall(tr[k]))]
        print(f'{os.path.basename(f)}: {len(tr) - len(stale)}/{len(pairs)} translated, {len(missing)} missing, {len(stale)} stale, {len(wrong)} placeholder errors')
        for k in wrong: print('   placeholder:', k)
        for k in missing[:5]: print('   missing:', k)
        bad += len(wrong) + len(missing)
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
