"""Local-only fixed Bongo payload. Never downloads or publishes any file."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import struct
import zipfile

MAX_BYTES = 8 * 1024 * 1024
HELPER_NAME = "DianDao.BongoRuntime.exe"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def safe_name(name: str) -> bool:
    if not re.fullmatch(r"[A-Za-z0-9._/-]{1,200}", name):
        return False
    for part in name.split("/"):
        if not part or part in {".", ".."} or part.endswith("."):
            return False
        stem = part.split(".")[0].upper()
        if stem in {"CON", "PRN", "AUX", "NUL"} or re.fullmatch(r"(?:COM|LPT)[1-9]", stem):
            return False
    return True


def ordinary(path: Path) -> None:
    current = path
    while current != current.parent:
        if current.exists() and (current.is_symlink() or getattr(current.stat(follow_symlinks=False), "st_file_attributes", 0) & 0x400):
            raise ValueError(f"Refusing link/junction input: {current}")
        current = current.parent


def imports(executable: bytes) -> list[str]:
    if len(executable) < 1024 or executable[:2] != b"MZ":
        raise ValueError("Native helper is not a bounded PE executable")
    pe = struct.unpack_from("<I", executable, 0x3C)[0]
    if executable[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", executable, pe + 4)[0] != 0x8664:
        raise ValueError("Native helper must be Windows AMD64")
    sections = struct.unpack_from("<H", executable, pe + 6)[0]
    optional_bytes = struct.unpack_from("<H", executable, pe + 20)[0]
    optional = pe + 24
    if struct.unpack_from("<H", executable, optional)[0] != 0x20B:
        raise ValueError("Native helper must use PE32+")
    import_rva = struct.unpack_from("<I", executable, optional + 112 + 8)[0]
    ranges = []
    for index in range(sections):
        size, address, raw_bytes, raw_offset = struct.unpack_from("<IIII", executable, optional + optional_bytes + 40 * index + 8)
        ranges.append((address, max(size, raw_bytes), raw_offset))

    def offset(rva: int) -> int:
        for address, size, raw in ranges:
            if address <= rva < address + size:
                return raw + rva - address
        raise ValueError("Invalid PE import RVA")

    result = []
    cursor = offset(import_rva)
    for _ in range(256):
        descriptor = struct.unpack_from("<IIIII", executable, cursor)
        if not any(descriptor):
            return result
        name = offset(descriptor[3])
        result.append(executable[name:executable.index(0, name)].decode("ascii"))
        cursor += 20
    raise ValueError("Unbounded PE import table")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--helper", type=Path, required=True)
    parser.add_argument("--expected-helper-sha256", required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--native-license", type=Path, required=True)
    parser.add_argument("--core-license", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    for path in (args.helper, args.model, args.native_license, args.core_license):
        ordinary(path.absolute())
    helper = args.helper.read_bytes()
    if not re.fullmatch(r"[A-Fa-f0-9]{64}", args.expected_helper_sha256) or digest(helper) != args.expected_helper_sha256.upper():
        raise ValueError("Native helper hash differs from the reviewed build")
    imported = imports(helper)
    if any(name.lower().startswith(("vcruntime", "msvcp", "msvcr")) or name.lower() == "live2dcubismcore.dll" for name in imported):
        raise ValueError("Native helper still requires an unpackaged VC/Core runtime; use the approved static build")
    files = {HELPER_NAME: helper,
             "licenses/BongoCat-Apache-2.0.txt": args.native_license.read_bytes(),
             "licenses/Live2D-Core.txt": args.core_license.read_bytes()}
    for path in sorted(args.model.rglob("*")):
        ordinary(path.absolute())
        if path.is_file():
            name = "standard/" + path.relative_to(args.model).as_posix()
            if not safe_name(name):
                raise ValueError(f"Unsafe model asset name: {name}")
            files[name] = path.read_bytes()
    for name in ("standard/cat.model3.json", "standard/demomodel.moc3", "standard/LICENSE-MIT.txt"):
        if name not in files:
            raise ValueError(f"Missing fixed model asset or license: {name}")
    if not 1 <= len(files) <= 1024 or any(not data for data in files.values()) or sum(map(len, files.values())) > MAX_BYTES:
        raise ValueError("Payload file count/size is outside its runtime boundary")
    if len({name.casefold() for name in files}) != len(files):
        raise ValueError("Case-aliased asset names are not allowed")
    memory = io.BytesIO()
    with zipfile.ZipFile(memory, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            if not safe_name(name):
                raise ValueError(f"Unsafe payload name: {name}")
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    payload = memory.getvalue()
    if len(payload) > MAX_BYTES:
        raise ValueError("Archive exceeds the runtime payload limit")
    manifest = {"schema": 1, "archiveBytes": len(payload), "archiveSha256": digest(payload),
                "files": [{"path": name, "bytes": len(data), "sha256": digest(data)} for name, data in sorted(files.items())]}
    ordinary(args.out.absolute())
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "payload.zip").write_bytes(payload)
    (args.out / "payload-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"archiveBytes": len(payload), "expandedBytes": sum(map(len, files.values())), "files": len(files),
                      "archiveSha256": digest(payload), "helperSha256": digest(helper), "imports": imported}))


if __name__ == "__main__":
    main()
