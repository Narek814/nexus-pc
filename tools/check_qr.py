#!/usr/bin/env python3
"""Decodes QR codes produced by Nexus.Core's encoder with an independent decoder (zxing-cpp).

Dev/CI check only; zxing-cpp is never shipped. Requires: pip install zxing-cpp pillow
Usage: python3 -I tools/check_qr.py   (runs `dotnet run` to generate the codes)
"""
import json
import pathlib
import subprocess
import sys
import tempfile

import zxingcpp
from PIL import Image

root = pathlib.Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory() as tmp:
    out = pathlib.Path(tmp) / "qr.json"
    subprocess.run(["dotnet", "run", "--project", str(root / "windows/Nexus.DevClient"), "--", "qr-selftest", str(out)], check=True)
    cases = json.loads(out.read_text(encoding="utf-8"))["cases"]

failures = 0
versions = set()
for case in cases:
    rows = case["rows"]
    size, quiet, scale = len(rows), 4, 4
    image = Image.new("L", ((size + 2 * quiet) * scale,) * 2, 255)
    pixels = image.load()
    for y, row in enumerate(rows):
        for x, bit in enumerate(row):
            if bit == "1":
                for dy in range(scale):
                    for dx in range(scale):
                        pixels[(x + quiet) * scale + dx, (y + quiet) * scale + dy] = 0
    results = zxingcpp.read_barcodes(image, formats=zxingcpp.BarcodeFormat.QRCode)
    decoded = results[0].text if results else None
    ok = decoded == case["text"] and (not results or results[0].ec_level in ("M", ""))
    versions.add(case["version"])
    if not ok:
        failures += 1
        print(f"FAIL version {case['version']} mask {case['mask']} len {len(case['text'])}: decoded={decoded!r:.60}")
print(f"{len(cases) - failures}/{len(cases)} QR codes decoded correctly; versions covered: {min(versions)}-{max(versions)} ({len(versions)} distinct)")
sys.exit(1 if failures else 0)
