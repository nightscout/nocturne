#!/usr/bin/env python3
r"""
Post-generation patch for the Swift SDK.

Extracts large mapValuesToQueryItems dict literals into explicitly-typed
local variables to work around Swift's "compiler is unable to type-check
this expression in reasonable time" error on dicts with many entries.

Rewrites the escapes in validation patterns (StringRule(pattern: "...")) that
Swift string literals reject: the generator escapes a regex's slashes as \/,
JavaScript-style, and copies a regex escape such as \d through unchanged.
"""
import re
import glob
import sys

root = sys.argv[1] if len(sys.argv) > 1 else "Sources"

PATTERN = re.compile(
    r"(localVariableUrlComponents\?\.queryItems = APIHelper\.mapValuesToQueryItems\()"
    r"(\[[\s\S]*?\])"
    r"(\))",
    re.MULTILINE,
)


RULE_PATTERN = re.compile(r'(pattern: ")((?:[^"\\]|\\.)*)(")')
# What may follow a backslash in a Swift string literal (\u is followed by {...}).
SWIFT_ESCAPES = set("0\\tnr\"'u")


def swift_escape(m: re.Match) -> str:
    char = m.group(1)
    if char == "/":
        return "/"
    if char in SWIFT_ESCAPES:
        return m.group(0)
    return "\\\\" + char


def swift_pattern(m: re.Match) -> str:
    return m.group(1) + re.sub(r"\\(.)", swift_escape, m.group(2)) + m.group(3)


def fix_file(path: str) -> None:
    with open(path) as f:
        src = f.read()

    counter = [0]

    def replacer(m: re.Match) -> str:
        entries = m.group(2)
        if entries.count("isExplode:") < 5:
            return m.group(0)
        counter[0] += 1
        varname = f"_qp{counter[0]}"
        typed = f"let {varname}: [String: (wrappedValue: (any Sendable)?, isExplode: Bool)] = {entries}"
        assign = f"localVariableUrlComponents?.queryItems = APIHelper.mapValuesToQueryItems({varname})"
        return f"{typed}\n        {assign}"

    patched = RULE_PATTERN.sub(swift_pattern, PATTERN.sub(replacer, src))
    if patched != src:
        with open(path, "w") as f:
            f.write(patched)
        print(f"Patched: {path}")


for swift_file in glob.glob(f"{root}/**/*.swift", recursive=True):
    fix_file(swift_file)
