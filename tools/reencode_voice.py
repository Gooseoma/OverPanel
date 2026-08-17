#!/usr/bin/env python3
"""
Перекодирует голосовые файлы Overpanel (.bin) в тот профиль Opus, который
реально проигрывает клиент Rust.

Было:  Hybrid Fullband, моно, одиночные 20-мс фреймы  -> клиент молчит
Стало: SILK Wideband, стерео, мультифрейм ~60 мс      -> как у IQReportSystem

Формат .bin в обе стороны: подряд [int32 length][length байт Opus-пакета].
"""
import base64, gzip, json, os, struct, subprocess, sys, tempfile

# ── Ogg CRC32 (poly 0x04c11db7, без отражений и финального xor) ──────────
def _crc_table():
    tbl = []
    for i in range(256):
        r = i << 24
        for _ in range(8):
            r = ((r << 1) ^ 0x04c11db7) & 0xFFFFFFFF if r & 0x80000000 else (r << 1) & 0xFFFFFFFF
        tbl.append(r)
    return tbl

CRC = _crc_table()

def ogg_crc(data: bytes) -> int:
    r = 0
    for b in data:
        r = ((r << 8) & 0xFFFFFFFF) ^ CRC[((r >> 24) & 0xFF) ^ b]
    return r


def ogg_page(serial: int, seq: int, granule: int, packets: list, bos=False, eos=False) -> bytes:
    """Одна Ogg-страница. Пакеты должны умещаться в 255 сегментов."""
    segs = []
    for p in packets:
        n = len(p)
        while n >= 255:
            segs.append(255)
            n -= 255
        segs.append(n)
    if len(segs) > 255:
        raise ValueError("too many segments for one page")

    header = b"OggS" + bytes([0, (0x02 if bos else 0) | (0x04 if eos else 0)])
    header += struct.pack("<q", granule)
    header += struct.pack("<I", serial)
    header += struct.pack("<I", seq)
    header += b"\x00\x00\x00\x00"            # место под CRC
    header += bytes([len(segs)]) + bytes(segs)
    body = b"".join(packets)
    crc = ogg_crc(header + body)
    return header[:22] + struct.pack("<I", crc) + header[26:] + body


def read_bin(path: str) -> list:
    data = open(path, "rb").read()
    out, off = [], 0
    while off + 4 <= len(data):
        (n,) = struct.unpack_from("<i", data, off)
        off += 4
        if n <= 0 or off + n > len(data):
            break
        out.append(data[off:off + n])
        off += n
    return out


def write_bin(path: str, packets: list) -> None:
    with open(path, "wb") as f:
        for p in packets:
            f.write(struct.pack("<i", len(p)))
            f.write(p)


def packets_to_ogg(packets: list, path: str, channels=1, frame_samples=960) -> None:
    """Оборачивает сырые Opus-пакеты в Ogg, чтобы их понял ffmpeg."""
    serial, seq = 0x4F565031, 0
    head = (b"OpusHead" + bytes([1, channels]) + struct.pack("<H", 312)
            + struct.pack("<I", 48000) + struct.pack("<h", 0) + bytes([0]))
    tags = b"OpusTags" + struct.pack("<I", 8) + b"overpnl" + b"\x00" + struct.pack("<I", 0)

    chunks = [ogg_page(serial, seq, 0, [head], bos=True)]
    seq += 1
    chunks.append(ogg_page(serial, seq, 0, [tags]))
    seq += 1

    granule, i = 0, 0
    while i < len(packets):
        batch = packets[i:i + 50]
        i += len(batch)
        granule += frame_samples * len(batch)
        chunks.append(ogg_page(serial, seq, granule, batch, eos=(i >= len(packets))))
        seq += 1
    open(path, "wb").write(b"".join(chunks))


def ogg_to_packets(path: str) -> list:
    """Разбирает Ogg обратно в пакеты, склеивая сегменты по лейсингу."""
    data = open(path, "rb").read()
    packets, cur, off = [], b"", 0
    while off < len(data):
        if data[off:off + 4] != b"OggS":
            break
        nsegs = data[off + 26]
        table = data[off + 27:off + 27 + nsegs]
        body = off + 27 + nsegs
        for n in table:
            cur += data[body:body + n]
            body += n
            if n < 255:
                packets.append(cur)
                cur = b""
        off = body
    return [p for p in packets if not (p.startswith(b"OpusHead") or p.startswith(b"OpusTags"))]


def describe(packets: list, label: str) -> None:
    if not packets:
        print(f"{label}: пусто")
        return
    toc = packets[0][0]
    lens = [len(p) for p in packets]
    print(f"{label}: пакетов={len(packets)} средний={sum(lens)//len(lens)}Б "
          f"TOC=0x{toc:02x} config={toc >> 3} stereo={(toc >> 2) & 1} code={toc & 3}")


def convert(src: str, dst: str) -> None:
    packets = read_bin(src)
    describe(packets, f"ДО  {os.path.basename(src)}")

    with tempfile.TemporaryDirectory() as tmp:
        raw_ogg = os.path.join(tmp, "in.ogg")
        wav = os.path.join(tmp, "mid.wav")
        out_ogg = os.path.join(tmp, "out.ogg")

        packets_to_ogg(packets, raw_ogg)
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", raw_ogg, wav], check=True)
        # Профиль Rust: голосовой SILK, широкая полоса (cutoff 8k), стерео, 60-мс пакеты
        subprocess.run([
            "ffmpeg", "-y", "-loglevel", "error", "-i", wav,
            "-c:a", "libopus", "-b:a", "48k", "-ar", "48000", "-ac", "2",
            "-application", "voip", "-cutoff", "8000",
            "-frame_duration", "60", "-vbr", "off",
            out_ogg,
        ], check=True)

        new_packets = ogg_to_packets(out_ogg)

    describe(new_packets, f"ПОСЛЕ {os.path.basename(dst)}")
    write_bin(dst, new_packets)


if __name__ == "__main__":
    for src, dst in [(a, b) for a, b in zip(sys.argv[1::2], sys.argv[2::2])]:
        convert(src, dst)
