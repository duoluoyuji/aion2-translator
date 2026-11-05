import sys, struct
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

EXE = r"E:\SteamLibrary\steamapps\common\AION2\Aion2\Binaries\Win64\AION2.exe"
VTABLE_RVA = 230191584          # Steam-20261028 ChatBuildProfile.VtableRva
PROPVT_RVA = 224633096
DATA_RVA   = 209459712

pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
sections = [(s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData, s.SizeOfRawData, s.Name.rstrip(b"\0").decode()) for s in pe.sections]
print("imagebase=0x%X" % base)
for s in sections: print("  sec %-8s va=0x%08X vsize=0x%X raw=0x%X" % (s[4], s[0], s[1], s[2]))

def r2o(rva):
    for va, vs, raw, rawsz, name in sections:
        if va <= rva < va + max(vs, rawsz):
            return raw + (rva - va)
    return None

def read(rva, n):
    o = r2o(rva)
    if o is None: return None
    with open(EXE, "rb") as f:
        f.seek(o); return f.read(n)

md = Cs(CS_ARCH_X86, CS_MODE_64); md.detail = True

def is_code_rva(rva):
    for va, vs, raw, rawsz, name in sections:
        if name == ".text" and va <= rva < va + vs: return True
    return False

def dump_vtable(rva, label, maxslots=250):
    print("\n==== %s @ rva 0x%X ====" % (label, rva))
    data = read(rva, 8 * maxslots)
    if not data: print("  read fail"); return []
    out = []
    for i in range(maxslots):
        v = struct.unpack_from("<Q", data, i * 8)[0]
        r = v - base if base <= v < base + 0x40000000 else None
        ok = r is not None and is_code_rva(r)
        out.append((i, r, ok))
        if i < 40 or ok:
            print("  [%3d] 0x%016X -> rva 0x%X %s" % (i, v, r if r else 0, "" if ok else "(not code)"))
    return out

def score_slot(rva):
    code = read(rva, 320)
    if not code: return None
    insns = list(md.disasm(code, base + rva))
    text = " ; ".join("%s %s" % (i.mnemonic, i.op_str) for i in insns[:70])
    s = 0
    if "+ 0x30]" in text or ", 0x30]" in text: s += 0  # placeholder
    for pat, w in (("[rcx + 0x30]", 6), ("+ 0x30]", 4), (", 0x30]", 3), ("0x150", 6), ("0x150h", 6),
                   ("336", 4), ("[rcx + 0x8]", 1), ("[rcx + 0x10]", 1)):
        if pat in text: s += w
    # 336 = 0x150 ; also "(rax,rax,4)" style indexing *4 etc.
    if "imul" in text and "0x150" in text: s += 6
    if "lea r" in text and "* 4" in text: s += 1
    calls = sum(1 for i in insns[:120] if i.mnemonic == "call")
    s += min(calls, 6)
    return s, text

for rva, label in ((VTABLE_RVA, "Chat Vtable"), (PROPVT_RVA, "Property Vtable")):
    slots = dump_vtable(rva, label)
    scored = []
    for i, r, ok in slots:
        if not ok: continue
        res = score_slot(r)
        if res: scored.append((res[0], i, r, res[1]))
    scored.sort(reverse=True)
    print("\n---- %s 候选前 8 ----" % label)
    for s, i, r, text in scored[:8]:
        print("\n slot[%d] rva 0x%X score %d\n   %s" % (i, r, s, text[:900]))
