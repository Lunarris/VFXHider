import json
import os
import re
import struct
import sys
import urllib.request

ROOT = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(ROOT, "VfxFilter", "VfxFilterModule.cs")
LAUNCHER_CONFIG = os.path.join(os.environ.get("APPDATA", ""), "XIVLauncher", "launcherConfigV3.json")
THALIAK = "https://thaliak.xiv.dev/graphql/2022-08-14"
GAME_REPO = "4e9a232b"

SIG_RE = re.compile(r'private const string (\w+Sig) = "([0-9A-F? ]+)";')
VERSION_RE = re.compile(r'(public const string VerifiedGameVersion = ")([^"]*)(";)')


def fail(message, code=2):
    print(f"ERROR: {message}")
    sys.exit(code)


def game_dir():
    if len(sys.argv) > 1:
        return sys.argv[1]
    try:
        with open(LAUNCHER_CONFIG, encoding="utf-8-sig") as f:
            return os.path.join(json.load(f)["GamePath"], "game")
    except Exception as ex:
        fail(f"could not read the game path from {LAUNCHER_CONFIG} ({ex}). Pass the game folder as an argument.")


def latest_version():
    query = {"query": '{ repository(slug:"%s") { latestVersion { versionString } } }' % GAME_REPO}
    request = urllib.request.Request(THALIAK, json.dumps(query).encode(), {"Content-Type": "application/json", "User-Agent": "VFXHider-verify-sigs/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return json.load(response)["data"]["repository"]["latestVersion"]["versionString"]
    except Exception as ex:
        print(f"WARNING: could not reach Thaliak ({ex}), skipping the latest patch check.")
        return None


def text_section(exe):
    with open(exe, "rb") as f:
        data = f.read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    count = struct.unpack_from("<H", data, pe + 6)[0]
    table = pe + 24 + struct.unpack_from("<H", data, pe + 20)[0]
    for i in range(count):
        entry = table + i * 40
        if data[entry:entry + 8].rstrip(b"\0") == b".text":
            _, va, size, raw = struct.unpack_from("<IIII", data, entry + 8)
            return data[raw:raw + size], 0x140000000 + va
    fail("no .text section in the game executable.")


def scan(text, sig):
    pattern = b"".join(b"." if t == "??" else re.escape(bytes([int(t, 16)])) for t in sig.split())
    return [m.start() for m in re.finditer(pattern, text, re.S)]


def main():
    with open(SOURCE, "rb") as f:
        raw = f.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    source = raw.decode("utf-8-sig")

    sigs = SIG_RE.findall(source)
    version_match = VERSION_RE.search(source)
    if not sigs or not version_match:
        fail(f"could not find the signatures or VerifiedGameVersion in {SOURCE}.")
    verified = version_match.group(2)

    folder = game_dir()
    exe = os.path.join(folder, "ffxiv_dx11.exe")
    try:
        with open(os.path.join(folder, "ffxivgame.ver"), encoding="utf-8") as f:
            installed = f.read().strip()
    except OSError as ex:
        fail(f"could not read the game version in {folder} ({ex}).")

    latest = latest_version()
    print(f"Installed game:   {installed}")
    print(f"Latest patch:     {latest or 'unknown'}")
    print(f"Verified in code: {verified}")
    print()

    if latest and installed != latest:
        print(f"WARNING: the installed game is not the latest patch. Update through XIVLauncher, then run this again.")
        print()

    text, base = text_section(exe)
    ok = True
    for name, sig in sigs:
        hits = scan(text, sig)
        where = ", ".join(hex(base + h) for h in hits[:5])
        status = "OK" if len(hits) == 1 else "FAIL"
        print(f"[{status}] {name}: {len(hits)} match(es) {where}")
        ok &= len(hits) == 1
    print()

    if not ok:
        fail(f"signatures do not match on {installed}. They need updating before VerifiedGameVersion can change.", 1)

    if installed == verified:
        print(f"All signatures match. VerifiedGameVersion is already {installed}, nothing to change.")
        return

    updated = VERSION_RE.sub(lambda m: m.group(1) + installed + m.group(3), source, count=1)
    with open(SOURCE, "wb") as f:
        f.write((b"\xef\xbb\xbf" if bom else b"") + updated.encode("utf-8"))
    print(f"All signatures match. VerifiedGameVersion updated: {verified} -> {installed}")


if __name__ == "__main__":
    main()
