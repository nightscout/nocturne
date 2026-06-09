#!/usr/bin/env python3
"""
Post-generation patch for the Swift SDK.

Extracts large mapValuesToQueryItems dict literals into explicitly-typed
local variables to work around Swift's "compiler is unable to type-check
this expression in reasonable time" error on dicts with many entries.
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
        typed = f"let {varname}: [String: (wrappedValue: Any?, isExplode: Bool)] = {entries}"
        assign = f"localVariableUrlComponents?.queryItems = APIHelper.mapValuesToQueryItems({varname})"
        return f"{typed}\n        {assign}"

    patched = PATTERN.sub(replacer, src)
    if patched != src:
        with open(path, "w") as f:
            f.write(patched)
        print(f"Patched: {path}")


for swift_file in glob.glob(f"{root}/**/*.swift", recursive=True):
    fix_file(swift_file)
