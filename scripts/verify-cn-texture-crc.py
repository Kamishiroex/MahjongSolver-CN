#!/usr/bin/env python3
"""Verify the pinned CN texture CRC evidence using an installed EXE, offline.

Python standard library only. Does not load or execute the EXE, contact the game
process, export client bytes, or modify files. The three signature patterns below
come from the linked, pinned FFXIVClientStructs member declarations.
"""

import argparse
import hashlib
import json
import struct
from pathlib import Path


EXE_SHA256 = "7BA28760BC53CBBBF66B63105FEA6C1BFE09CB6B1C6353FC8FB0FF03CC1A30F4"
EXE_SIZE = 51881216
CLIENTSTRUCTS_COMMIT = "f824354f4a6a2b1cd16cc8fcb670c7a66bf64880"
GAME_VERSION = "2026.09.15.0000.0000"
SIGNATURES = {
    "AtkTextureResourceManager.LoadTexture": (
        "E8 ?? ?? ?? ?? 4C 8B F8 48 85 C0 74 52", 0x6C85B0, 0x63D590
    ),
    "Crc32.GetDigest": ("E8 ?? ?? ?? ?? 39 43 0C", 0x95C79A, 0x1F6370),
    "Crc32.FromBuffer": ("E8 ?? ?? ?? ?? 8B 7B EE", 0x95F492, 0x1F62C0),
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


class PeImage:
    def __init__(self, data):
        self.data = data
        require(data[:2] == b"MZ", "Not an MZ image")
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        require(data[pe:pe + 4] == b"PE\0\0", "Not a PE image")
        sections = struct.unpack_from("<H", data, pe + 6)[0]
        optional_size = struct.unpack_from("<H", data, pe + 20)[0]
        optional = pe + 24
        require(struct.unpack_from("<H", data, optional)[0] == 0x20B,
                "Expected PE32+")
        self.image_base = struct.unpack_from("<Q", data, optional + 24)[0]
        self.sections = []
        for i in range(sections):
            at = optional + optional_size + i * 40
            name = data[at:at + 8].rstrip(b"\0").decode("ascii")
            virtual_size, rva, raw_size, raw_offset = struct.unpack_from("<IIII", data, at + 8)
            self.sections.append((name, rva, virtual_size, raw_offset, raw_size))

    def offset(self, rva, length=1):
        for _, start, _, raw, size in self.sections:
            if start <= rva and rva + length <= start + size:
                return raw + rva - start
        raise ValueError(f"RVA {rva:X} is not backed by the file")

    def read(self, rva, length):
        at = self.offset(rva, length)
        return self.data[at:at + length]

    def relative_call(self, rva):
        instruction = self.read(rva, 5)
        require(instruction[0] == 0xE8, f"Expected direct call at RVA {rva:X}")
        return rva + 5 + struct.unpack_from("<i", instruction, 1)[0]

    def rip_lea_target(self, rva):
        instruction = self.read(rva, 7)
        require(instruction[0] in (0x48, 0x4C) and instruction[1] == 0x8D
                and instruction[2] & 0xC7 == 0x05,
                f"Expected 64-bit RIP-relative LEA at RVA {rva:X}")
        return rva + 7 + struct.unpack_from("<i", instruction, 3)[0]

    def is_not_ecx(self, rva):
        # x86 NOT r/m32, register-direct operand ECX; instruction syntax only.
        opcode, modrm = self.read(rva, 2)
        return opcode == 0xF7 and modrm >> 6 == 3 and (modrm >> 3) & 7 == 2 and modrm & 7 == 1

    def matches(self, pattern):
        parts = [None if item == "??" else int(item, 16) for item in pattern.split()]
        found = []
        for name, rva, _, raw, size in self.sections:
            if name != ".text":
                continue
            section = self.data[raw:raw + size]
            at = -1
            while True:
                at = section.find(bytes([parts[0]]), at + 1)
                if at < 0:
                    break
                if at + len(parts) <= size and all(
                    value is None or section[at + i] == value for i, value in enumerate(parts)
                ):
                    found.append(rva + at)
        return found


def verify(exe_path):
    data = exe_path.read_bytes()
    digest = hashlib.sha256(data).hexdigest().upper()
    require(len(data) == EXE_SIZE and digest == EXE_SHA256,
            "EXE differs from the documented CN build; no inference is permitted")
    image = PeImage(data)
    require(image.image_base == 0x140000000, "Unexpected image base")
    signature_report = {}
    for name, (pattern, expected_call, expected_target) in SIGNATURES.items():
        matches = image.matches(pattern)
        require(matches == [expected_call], f"Signature is not uniquely matched: {name}")
        target = image.relative_call(matches[0])
        require(target == expected_target, f"Unexpected target: {name}")
        signature_report[name] = {"callRva": f"0x{matches[0]:X}", "targetRva": f"0x{target:X}"}

    call_chain = [(0x63D651, 0x1F62C0), (0x63D65A, 0x1F6370), (0x1F62EB, 0x1FCEF0)]
    for call, target in call_chain:
        require(image.relative_call(call) == target, f"Call chain differs at RVA {call:X}")
    seed_instruction = image.read(0x1F62E9, 2)
    require(seed_instruction[0] in (0x31, 0x33) and seed_instruction[1] == 0xC9,
            "FromBuffer no longer supplies a zero seed")
    require(image.is_not_ecx(0x1FCF08), "CRC initial inversion differs")
    require(image.is_not_ecx(0x1FD221), "CRC final inversion differs")

    table_rva = image.rip_lea_target(0x1FCEFB)
    require(table_rva == 0x2137910, "Unexpected CRC table target")
    table = struct.unpack("<1024I", image.read(table_rva, 4096))
    for value in range(256):
        state = value
        for block in range(4):
            for _ in range(8):
                state = (state >> 1) ^ (0xEDB88320 if state & 1 else 0)
            require(table[block * 256 + value] == state, "CRC table polynomial differs")

    suffix_rva = image.rip_lea_target(0x63D605)
    require(suffix_rva == 0x21789B4, "Unexpected texture suffix location")
    suffix = image.read(suffix_rva, 5).rstrip(b"\0").decode("ascii")
    require(suffix == "_hr1", "Unexpected high-resolution texture suffix")

    paths = []
    for path, allowed in [
        ("ui/uld/EmjTile.tex", True), ("ui/uld/EmjTile_hr1.tex", True),
        ("ui/uld/emjtile.tex", False), ("ui/uld/emjtile_hr1.tex", False),
    ]:
        state = 0xFFFFFFFF
        for value in path.encode("utf-8"):
            state = (state >> 8) ^ table[(state ^ value) & 0xFF]
        paths.append({"path": path, "luminaUnfinalized": f"{state:08X}",
                      "nativeFinalized": f"{state ^ 0xFFFFFFFF:08X}",
                      "supportedByUldSpelling": allowed})

    return {
        "status": "OFFLINE_BINARY_AND_ALGORITHM_CHECK_PASSED",
        "gameVersion": GAME_VERSION, "exeSha256": digest, "exeSize": len(data),
        "clientStructsCommit": CLIENTSTRUCTS_COMMIT, "signatureMatches": signature_report,
        "verifiedDirectCalls": [{"callRva": f"0x{a:X}", "targetRva": f"0x{b:X}"}
                                for a, b in call_chain],
        "crc": {"seed": 0, "initialInversion": True, "finalInversion": True,
                "reflectedPolynomial": "EDB88320", "tableRva": f"0x{table_rva:X}",
                "verifiedTableEntries": len(table)},
        "highResolutionSuffix": suffix, "suffixRva": f"0x{suffix_rva:X}", "paths": paths,
        "scope": "Offline shell-resource identity only; no live face or hand validation",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True, type=Path, help="Installed ffxiv_dx11.exe; read only")
    args = parser.parse_args()
    try:
        print(json.dumps(verify(args.exe), ensure_ascii=False, indent=2))
    except (OSError, ValueError, struct.error) as error:
        parser.exit(1, f"Verification failed: {error}\n")


if __name__ == "__main__":
    main()
