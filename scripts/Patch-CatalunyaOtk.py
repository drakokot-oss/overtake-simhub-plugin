#!/usr/bin/env python3
"""Patch Catalunya_20260622_231048_05C0F3.otk using screenshot ground truth.

Fixes: NaN tokens, broken driver tags/keys from wrong UDP layout at capture time.
Does NOT re-parse raw UDP — maps carIdx via quali bestLapTimeMs + screenshot names.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import math
import re
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

AES_A = bytes([
    202, 234, 66, 218, 231, 105, 143, 115, 130, 40, 220, 134, 133, 1, 223, 11,
    57, 25, 143, 152, 205, 172, 139, 222, 15, 163, 149, 188, 96, 50, 113, 67,
])
AES_M = bytes([
    112, 149, 248, 201, 134, 232, 229, 178, 21, 121, 191, 75, 13, 158, 83, 152,
    242, 197, 98, 222, 207, 81, 27, 156, 137, 130, 61, 254, 27, 55, 169, 71,
])
HMAC_A = bytes([
    150, 118, 1, 19, 37, 70, 224, 184, 248, 146, 204, 164, 97, 161, 154, 168,
    127, 23, 215, 35, 11, 66, 210, 153, 204, 210, 141, 135, 93, 121, 115, 80,
])
HMAC_M = bytes([
    225, 37, 214, 31, 49, 195, 24, 166, 108, 246, 106, 71, 83, 216, 116, 34,
    187, 200, 237, 226, 181, 159, 226, 79, 84, 153, 151, 181, 165, 126, 135, 130,
])
AES_KEY = bytes(a ^ b for a, b in zip(AES_A, AES_M))
HMAC_KEY = bytes(a ^ b for a, b in zip(HMAC_A, HMAC_M))

# Quali classification order from in-game screenshot (P1..P18).
QUALI_NAMES_BY_POS = [
    "IMT_ELCoentro",
    "AYT Cleber Benedet",
    "[FRW] Zoltraak",
    "FRW MIGUEL15",
    "TSL MARTINS",
    "IMT GabzKk",
    "DUT Sopena",
    "BRPRO CHOKITO",
    "BRPRO RODOLFO",
    "PDK_Snake",
    "DUT Muniz",
    "LHT_MARCUS77",
    "PRT_martbryt",
    "PDK_dede_ttsl",
    "PRT Douglas",
    "LHT_FEITOSA",
    "WILLIAM7_EXTREME",
    "AYT_Ayrton",
]


def decrypt_otk(path: Path) -> bytes:
    data = path.read_bytes()
    if data[:4] != b"OTK1":
        raise ValueError("not OTK1")
    iv = data[6:22]
    ct_len = struct.unpack_from("<I", data, 22)[0]
    ciphertext = data[26 : 26 + ct_len]
    signed = data[: 26 + ct_len]
    stored_hmac = data[26 + ct_len : 26 + ct_len + 32]
    if not hmac.compare_digest(hmac.new(HMAC_KEY, signed, hashlib.sha256).digest(), stored_hmac):
        raise ValueError("HMAC failed")
    with tempfile.NamedTemporaryFile(delete=False) as kf:
        kf.write(AES_KEY)
        keypath = kf.name
    with tempfile.NamedTemporaryFile(delete=False) as cf:
        cf.write(ciphertext)
        ctpath = cf.name
    pt = subprocess.check_output(
        ["openssl", "enc", "-d", "-aes-256-cbc", "-K", AES_KEY.hex(), "-iv", iv.hex(), "-in", ctpath],
        stderr=subprocess.DEVNULL,
    )
    Path(keypath).unlink(missing_ok=True)
    Path(ctpath).unlink(missing_ok=True)
    return pt


def encrypt_otk(plaintext: bytes, path: Path) -> None:
    iv = subprocess.check_output(["openssl", "rand", "-hex", "16"]).decode().strip()
    iv_bytes = bytes.fromhex(iv)
    with tempfile.NamedTemporaryFile(delete=False) as pf:
        pf.write(plaintext)
        ptpath = pf.name
    with tempfile.NamedTemporaryFile(delete=False) as kf:
        kf.write(AES_KEY)
        keypath = kf.name
    ciphertext = subprocess.check_output(
        ["openssl", "enc", "-aes-256-cbc", "-K", AES_KEY.hex(), "-iv", iv, "-in", ptpath],
        stderr=subprocess.DEVNULL,
    )
    Path(ptpath).unlink(missing_ok=True)
    Path(keypath).unlink(missing_ok=True)
    version = struct.pack("<H", 1)
    ct_len = struct.pack("<I", len(ciphertext))
    signed = b"OTK1" + version + iv_bytes + ct_len + ciphertext
    mac = hmac.new(HMAC_KEY, signed, hashlib.sha256).digest()
    path.write_bytes(signed + mac)


def sanitize_nonfinite(obj):
    if isinstance(obj, float):
        if not math.isfinite(obj):
            return None
        return obj
    if isinstance(obj, dict):
        return {k: sanitize_nonfinite(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [sanitize_nonfinite(v) for v in obj]
    return obj


def build_car_idx_map(data: dict) -> dict[int, str]:
    quali_results = sorted(
        data["sessions"][0]["results"],
        key=lambda r: r.get("position") or 999,
    )
    mapping: dict[int, str] = {}
    for i, row in enumerate(quali_results):
        if i >= len(QUALI_NAMES_BY_POS):
            break
        car_idx = row.get("carIdx")
        if car_idx is None:
            continue
        mapping[int(car_idx)] = QUALI_NAMES_BY_POS[i]
    return mapping


def rename_drivers(sess: dict, car_idx_to_name: dict[int, str]) -> None:
    drivers = sess.get("drivers") or {}
    tag_by_car: dict[int, str] = {}
    for row in sess.get("results") or []:
        car_idx = row.get("carIdx")
        tag = row.get("tag")
        if car_idx is not None and tag is not None:
            tag_by_car[int(car_idx)] = tag

    new_drivers = {}
    for car_idx, name in car_idx_to_name.items():
        old_tag = tag_by_car.get(car_idx)
        if old_tag and old_tag in drivers:
            new_drivers[name] = drivers[old_tag]
        elif name in drivers:
            new_drivers[name] = drivers[name]
    # Keep any unmapped drivers (shouldn't happen for this file).
    for tag, drv in drivers.items():
        if tag not in new_drivers.values() and tag not in new_drivers:
            new_drivers[tag] = drv
    sess["drivers"] = new_drivers

    for row in sess.get("results") or []:
        car_idx = row.get("carIdx")
        if car_idx is not None and int(car_idx) in car_idx_to_name:
            row["tag"] = car_idx_to_name[int(car_idx)]

    awards = sess.get("awards") or {}
    for key in ("fastestLap", "mostConsistent", "mostPositionsGained"):
        aw = awards.get(key)
        if not aw:
            continue
        car_idx = aw.get("carIdx")
        if car_idx is not None and int(car_idx) in car_idx_to_name:
            aw["tag"] = car_idx_to_name[int(car_idx)]


def patch(data: dict) -> dict:
    data = sanitize_nonfinite(data)
    car_idx_to_name = build_car_idx_map(data)

    for sess in data.get("sessions") or []:
        rename_drivers(sess, car_idx_to_name)

    # Root participants list (tags only).
    if isinstance(data.get("participants"), list):
        quali = data["sessions"][0]
        tags = []
        seen = set()
        for row in sorted(quali.get("results") or [], key=lambda r: r.get("position") or 999):
            tag = row.get("tag")
            if tag and tag not in seen:
                tags.append(tag)
                seen.add(tag)
        data["participants"] = tags

    dbg = data.setdefault("_debug", {})
    notes = dbg.setdefault("notes", [])
    notes.append(
        "MANUAL PATCH v1: driver tags restored from screenshot ground truth "
        "(carIdx map via quali results). Capture-time UDP layout was wrong; "
        "re-capture with plugin >= v1.1.46 for native parsing."
    )
    dbg_game = dbg.setdefault("game", {})
    dbg_game["patchedManually"] = True
    dbg_game["bodyWireFormat"] = 2025

    li = dbg.setdefault("diagnostics", {}).setdefault("lobbyInfo", {})
    li["fullMyTeamGrid"] = True
    li["lobbyResolved"] = len(car_idx_to_name)

    return data


def main() -> int:
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.home() / "Downloads/Catalunya_20260622_231048_05C0F3.otk"
    dst = Path(sys.argv[2]) if len(sys.argv) > 2 else src.with_name(src.stem + "_FIXED.otk")

    raw = decrypt_otk(src).decode("utf-8")
    fixed_raw = re.sub(r"\bNaN\b", "null", raw)
    data = json.loads(fixed_raw)
    patched = patch(data)
    out = json.dumps(patched, ensure_ascii=False, separators=(",", ":"))
    if re.search(r"(?<![A-Za-z_])NaN(?![A-Za-z_])", out):
        raise SystemExit("still contains NaN after patch")

    encrypt_otk(out.encode("utf-8"), dst)
    print(f"Wrote {dst} ({dst.stat().st_size} bytes)")
    print(f"Drivers mapped: {len(build_car_idx_map(patched))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
