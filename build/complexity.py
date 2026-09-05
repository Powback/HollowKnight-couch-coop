#!/usr/bin/env python3
"""Cyclomatic complexity gate for the C# sources.

Counts decision points per method and refuses new code above a cap, while
grandfathering what is already here through a budget file. A ratchet rather
than a wall: existing methods may not get worse, and new ones start honest.

Strings and comments are stripped before counting. Without that, the word
"if" inside a comment or a `||` inside a string literal counts as branching —
and this repo is heavily commented, so the noise would swamp the signal.
"""
import json, os, re, sys

CAP_NEW = 15
BUDGET = os.path.join(os.path.dirname(os.path.abspath(__file__)), "complexity-budget.json")

# A method signature at class-member level: optional attributes and modifiers,
# a return type, a name, then an open paren. Deliberately conservative — a
# missed method is a method not measured, which is safer than a false one whose
# body is really the next method's.
SIG = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'(?:(?:public|private|internal|protected|static|readonly|sealed|override|'
    r'virtual|abstract|async|extern|unsafe|new|partial)\s+)*'
    r'[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?]*\s+'
    r'([A-Za-z_][A-Za-z0-9_]*)\s*\('
)

DECISIONS = [
    re.compile(r'(?<![A-Za-z0-9_])if(?![A-Za-z0-9_])'),
    re.compile(r'(?<![A-Za-z0-9_])for(?:each)?(?![A-Za-z0-9_])'),
    re.compile(r'(?<![A-Za-z0-9_])while(?![A-Za-z0-9_])'),
    re.compile(r'(?<![A-Za-z0-9_])case(?![A-Za-z0-9_])'),
    re.compile(r'(?<![A-Za-z0-9_])catch(?![A-Za-z0-9_])'),
    re.compile(r'&&'), re.compile(r'\|\|'),
    re.compile(r'\?\?'), re.compile(r'\?\.'),
]


def strip_code(text):
    """Remove comments and string/char literals, keeping line structure."""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ''
        if c == '/' and nxt == '/':
            while i < n and text[i] != '\n':
                i += 1
        elif c == '/' and nxt == '*':
            i += 2
            while i + 1 < n and not (text[i] == '*' and text[i + 1] == '/'):
                if text[i] == '\n':
                    out.append('\n')
                i += 1
            i += 2
        elif c == '"' or c == "'":
            quote, i = c, i + 1
            while i < n and text[i] != quote:
                if text[i] == '\\':
                    i += 1
                elif text[i] == '\n':
                    out.append('\n')
                i += 1
            i += 1
            out.append('""')
        else:
            out.append(c)
            i += 1
    return ''.join(out)


TYPE = re.compile(
    r'^\s*(?:(?:public|private|internal|protected|static|sealed|abstract|partial|new)\s+)*'
    r'(?:class|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)')


def methods(path):
    """Yield (qualified_name, complexity) for each method, by brace matching.

    Qualified by the enclosing type, NOT bare. Two methods can share a name in
    one file — HealthPool.Begin and FriendlyFire.Begin did — and a bare key
    made one budget entry govern both, silently taking the larger score and
    masking a regression in the smaller one. PowOS's shell version documents
    the same bug one level up, where basenames collided across directories.
    """
    src = strip_code(open(path, encoding='utf-8').read())
    lines = src.split('\n')
    owner = ''
    for idx, line in enumerate(lines):
        t = TYPE.match(line)
        if t:
            owner = t.group(1)
        m = SIG.match(line)
        if not m or line.rstrip().endswith(';'):
            continue
        name = m.group(1)
        if name in ('if', 'for', 'foreach', 'while', 'switch', 'catch', 'using', 'lock', 'return'):
            continue
        depth, started, body = 0, False, []
        for l in lines[idx:]:
            body.append(l)
            depth += l.count('{') - l.count('}')
            if '{' in l:
                started = True
            if started and depth <= 0:
                break
        if not started:
            continue                      # expression-bodied member
        text = '\n'.join(body)
        score = 1 + sum(len(d.findall(text)) for d in DECISIONS)
        yield (f"{owner}.{name}" if owner else name), score


def scan(paths):
    found = {}
    for p in sorted(paths):
        rel = os.path.relpath(p, os.getcwd())
        for name, score in methods(p):
            found[f"{rel}:{name}"] = max(score, found.get(f"{rel}:{name}", 0))
    return found


def main(argv):
    check = '--check' in argv
    write = '--write-budget' in argv
    files = [a for a in argv[1:] if a.endswith('.cs')]
    if not files:
        print("usage: complexity.py [--check|--write-budget] <files.cs>")
        return 2

    found = scan(files)
    budget = {}
    if os.path.exists(BUDGET):
        budget = json.load(open(BUDGET))

    if write:
        json.dump(dict(sorted(found.items())), open(BUDGET, 'w'), indent=2)
        print(f"complexity: budget written, {len(found)} methods")
        return 0

    problems = []
    for key, score in sorted(found.items()):
        allowed = budget.get(key)
        if allowed is None:
            # Keys used to be file:name and are now file:TYPE.name, because two
            # types with a same-named method shared one entry and hid 19 of
            # them. Fall back to the old key rather than migrating the file
            # wholesale: rewriting it would re-baseline every method at
            # whatever it measures today, which quietly accepts the growth the
            # gate exists to catch.
            path, _, qualified = key.partition(':')
            allowed = budget.get(f"{path}:{qualified.split('.')[-1]}")
        if allowed is None:
            if score > CAP_NEW:
                problems.append(f"{key}: {score} > {CAP_NEW} (new code)")
        elif score > allowed:
            problems.append(f"{key}: {score} > {allowed} (was budgeted)")

    worst = sorted(found.items(), key=lambda kv: -kv[1])[:3]
    print(f"complexity: {len(found)} methods, cap {CAP_NEW} for new code; "
          f"worst: " + ", ".join(f"{k.split(':')[-1]} {v}" for k, v in worst))
    for p in problems:
        print(f"  OVER  {p}")
    if problems and check:
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
