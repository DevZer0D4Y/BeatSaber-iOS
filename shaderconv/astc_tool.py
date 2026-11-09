"""Client for the ImageIO ASTC batch encoder (tools/astc/astc_imageio.swift)."""
import struct, subprocess, os

ENCODER = os.environ.get("ASTC_ENCODER") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "build", "astc_imageio")


class AstcEncoder:
    def __init__(self, exe=ENCODER):
        self.p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE)

    def encode(self, rgba: bytes, w: int, h: int, block=4, quality=0.9) -> bytes:
        assert len(rgba) == w * h * 4
        # ImageIO stores the image bottom row first; flip so raw row order is preserved
        row = w * 4
        flipped = b"".join(rgba[y * row:(y + 1) * row] for y in range(h - 1, -1, -1))
        self.p.stdin.write(struct.pack("<IIIf", w, h, block, quality) + flipped); self.p.stdin.flush()
        n = struct.unpack("<I", self.p.stdout.read(4))[0]
        data = self.p.stdout.read(n)
        if n == 0: raise RuntimeError(f"ASTC encode failed {w}x{h}")
        exp = ((w + block - 1) // block) * ((h + block - 1) // block) * 16
        if len(data) != exp: raise RuntimeError(f"ASTC size {len(data)} != {exp} for {w}x{h} b{block}")
        return data
