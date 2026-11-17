#!/usr/bin/env python3
"""Prueft relative Markdown-Verweise und Ueberschriften-Anker ohne Netzzugriff.

Aufruf: check-doc-links.py [Wurzel]. Exit 0 ohne Befund, 1 bei kaputten Verweisen, 2 bei Eingabefehlern.
Externe Adressen (http, https, mailto) werden bewusst nicht geprueft, weil sie das Ergebnis vom Netz abhaengig machen.
"""
import re
import sys
from pathlib import Path

SKIP_PARTS = {".git", ".tools", "bin", "obj", "node_modules", "ThirdParty", "TestResults"}
LINK = re.compile(r"(?<!\!)\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
FENCE = re.compile(r"^\s*(```|~~~)")
HEADING = re.compile(r"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")


def markdown_files(root: Path):
    for path in sorted(root.rglob("*.md")):
        if not SKIP_PARTS.intersection(path.relative_to(root).parts):
            yield path


def visible_lines(text: str, strip_code: bool = True):
    """Zeilen ausserhalb von Codebloecken; mit strip_code ohne Inline-Code (Verweise darin sind keine Verweise)."""
    fenced = False
    for number, line in enumerate(text.splitlines(), 1):
        if FENCE.match(line):
            fenced = not fenced
            continue
        if not fenced:
            yield number, re.sub(r"`[^`]*`", "", line) if strip_code else line


def slug(heading: str) -> str:
    """GitHub-Anker: Auszeichnung entfernen, kleinschreiben, Satzzeichen weg, Leerzeichen zu Bindestrichen."""
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", heading)
    text = re.sub(r"[`*~]|</?[a-zA-Z][^>]*>", "", text).strip().lower()
    return re.sub(r"[^\w\- ]", "", text).replace(" ", "-")


def anchors(path: Path, cache: dict) -> set[str]:
    if path not in cache:
        found, seen = set(), {}
        for _, line in visible_lines(path.read_text(encoding="utf-8"), strip_code=False):
            match = HEADING.match(line)
            if not match:
                continue
            base = slug(match.group(2))
            count = seen.get(base, 0)
            seen[base] = count + 1
            found.add(base if count == 0 else f"{base}-{count}")
        cache[path] = found
    return cache[path]


def check(root: Path) -> list[str]:
    problems, cache = [], {}
    for source in markdown_files(root):
        for number, line in visible_lines(source.read_text(encoding="utf-8")):
            for match in LINK.finditer(line):
                target = match.group(1)
                if re.match(r"[a-zA-Z][a-zA-Z0-9+.-]*:", target):
                    continue
                path_part, _, fragment = target.partition("#")
                resolved = source if not path_part else (source.parent / path_part).resolve()
                where = f"{source.relative_to(root).as_posix()}:{number}"
                if not resolved.exists():
                    problems.append(f"{where}: Ziel fehlt: {target}")
                elif fragment and resolved.suffix == ".md" and fragment.lower() not in anchors(resolved, cache):
                    problems.append(f"{where}: Anker fehlt: {target}")
    return problems


def main(argv: list[str]) -> int:
    root = Path(argv[1] if len(argv) > 1 else ".").resolve()
    if not root.is_dir():
        print(f"Wurzel nicht gefunden: {root}", file=sys.stderr)
        return 2
    problems = check(root)
    for problem in problems:
        print(problem)
    print(f"{len(problems)} kaputte(r) Verweis(e)" if problems else "Alle relativen Verweise sind gueltig.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
