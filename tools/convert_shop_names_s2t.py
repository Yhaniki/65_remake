# -*- coding: utf-8 -*-
"""
Convert the `name` column of a shop_names.tsv (id<TAB>name, UTF-8) from Simplified
Chinese to Taiwan Traditional (OpenCC s2twp), IN PLACE.

WHY: iteminfo.dat item names are Mainland Simplified (GBK/CP936). tools/package_build.ps1
and tools/build_clean_data.ps1 decode them to shop_names.tsv as UTF-8; this pass then
converts them to 台灣正體 so the game shows no Simplified text at all. The 635 curated
official Taiwan names in shop_names_tw.tsv still overlay LAST and win
(see AvatarItemCatalog.ApplyTwNames) — this only reshapes the bulk that would otherwise
stay Simplified.

WHY IT MUST RUN EXACTLY ONCE ON FRESH SIMPLIFIED (and how we enforce it): OpenCC's S→T is
NOT a fixed point. Feeding already-Traditional text back through s2twp mangles two ambiguous
characters — 后→後 and 郁→鬱 — because the S→T phrase dictionaries are keyed on Simplified,
so once the input is Traditional the disambiguating phrase context is gone and those chars
fall to the wrong char-level default. No per-character test can tell a "keep 后" (皇后) from a
"make 後" (以后→以後); only the Simplified phrase context can. So instead of guessing, we make a
re-run a guaranteed no-op with a content-hash sentinel: after converting we record the sha1 of
the file we wrote in `<tsv>.s2t-done`; a later run whose file still matches that hash is skipped.
The build pipeline regenerates fresh Simplified before calling us, so the hash differs and we
convert every rebuild — but a stray manual re-run on our own output does nothing.

- Only the text after the FIRST tab (the name) is converted; the id column is untouched.
- Line endings and the trailing newline are preserved (UTF-8, no BOM, LF).
- Requires the `opencc` python package. If it is unavailable this exits non-zero and leaves
  the file unchanged, so the caller (PowerShell) can warn and keep Simplified rather than
  break the build.

usage: convert_shop_names_s2t.py <shop_names.tsv> [config]   (config default: s2twp)
"""
import hashlib
import io
import os
import sys


def _sha1_bytes(b):
    h = hashlib.sha1()
    h.update(b)
    return h.hexdigest()


def main(argv):
    if len(argv) < 2:
        print("usage: convert_shop_names_s2t.py <shop_names.tsv> [config]", file=sys.stderr)
        return 2
    path = argv[1]
    config = argv[2] if len(argv) > 2 else "s2twp"
    sentinel = path + ".s2t-done"

    try:
        with io.open(path, "rb") as f:
            raw_bytes = f.read()
    except Exception as e:
        print("[s2t] cannot read %s: %s" % (path, e), file=sys.stderr)
        return 1

    # Idempotency guard: if we already converted THIS exact content, do nothing. Regeneration
    # (fresh Simplified) changes the bytes → hash differs → we convert; a re-run on our own
    # output matches → skip, dodging the 后→後 / 郁→鬱 second-pass corruption.
    try:
        if os.path.exists(sentinel):
            with io.open(sentinel, "r", encoding="ascii") as f:
                if f.read().strip() == _sha1_bytes(raw_bytes):
                    print("[s2t] %s already 台灣正體 (sentinel match) — skip" % path)
                    return 0
    except Exception:
        pass  # unreadable sentinel → fall through and convert (safe default)

    try:
        import opencc
        cc = opencc.OpenCC(config)     # Simplified → 台灣正體 (phrase-aware)
    except Exception as e:  # opencc missing or bad config → leave file untouched
        print("[s2t] opencc unavailable (%s); leaving %s unchanged" % (e, path), file=sys.stderr)
        return 1

    text = raw_bytes.decode("utf-8")
    out_lines = []
    changed = 0
    for raw in text.split("\n"):
        line = raw.rstrip("\r")
        if not line:
            out_lines.append("")            # preserve blank / trailing-newline slot
            continue
        tab = line.find("\t")
        if tab <= 0:                         # no id, or leading tab → pass through verbatim
            out_lines.append(line)
            continue
        head = line[:tab]
        name = line[tab + 1:]
        conv = cc.convert(name)
        if conv != name:
            changed += 1
        out_lines.append(head + "\t" + conv)

    result = "\n".join(out_lines).encode("utf-8")  # faithfully reproduces the trailing newline
    try:
        with io.open(path, "wb") as f:
            f.write(result)
    except Exception as e:
        print("[s2t] cannot write %s: %s" % (path, e), file=sys.stderr)
        return 1

    try:
        with io.open(sentinel, "w", encoding="ascii") as f:
            f.write(_sha1_bytes(result))
    except Exception:
        pass  # sentinel is an optimisation; failing to write it only costs an extra pass later

    print("[s2t] %s: converted %d names to 台灣正體 (%s)" % (path, changed, config))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
