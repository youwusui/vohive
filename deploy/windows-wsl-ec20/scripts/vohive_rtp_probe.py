#!/usr/bin/env python3
import audioop
import argparse
import datetime as dt
import json
import os
import queue
import re
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
import wave


LOG = os.environ.get("VOHIVE_LOG", "/opt/vohive/logs/app-%s.log" % dt.date.today().isoformat())
OUT_DIR = os.environ.get("VOHIVE_RTP_OUT_DIR", "/opt/vohive/rtp-probe")
CALL_RE = re.compile(r'RTPRelay 创建成功 .*"lan_addr": "0\.0\.0\.0:(\d+)".*"lan_rtcp_addr": "0\.0\.0\.0:(\d+)"')
LOG_TEXT_RE = re.compile(r'"text":\s*"((?:\\.|[^"\\])*)"')

AMR_SID_PAYLOAD = bytes.fromhex("70400000000000")
DTMF_DIGITS = set("0123456789*#ABCDabcd")


def rtp_packet(seq, ts, ssrc, payload_type=114, payload=AMR_SID_PAYLOAD, marker=0):
    # RTP v2, no padding/extension/CSRC.
    b1 = ((1 if marker else 0) << 7) | (payload_type & 0x7F)
    return struct.pack("!BBHII", 0x80, b1, seq & 0xFFFF, ts & 0xFFFFFFFF, ssrc) + payload


def telephone_event_payload(digit, duration, end=False, volume=10):
    event_map = {
        "0": 0, "1": 1, "2": 2, "3": 3, "4": 4,
        "5": 5, "6": 6, "7": 7, "8": 8, "9": 9,
        "*": 10, "#": 11, "A": 12, "B": 13, "C": 14, "D": 15,
    }
    event = event_map[str(digit).upper()]
    flags = (0x80 if end else 0x00) | (volume & 0x3F)
    return struct.pack("!BBH", event, flags, duration & 0xFFFF)


def dtmf_packets(digit, start_seq, start_ts, ssrc, payload_type=101, rate=8000, event_ms=180):
    # RFC 4733 telephone-event. Timestamp stays fixed for the event; duration
    # increases. Send a few final packets with E bit set for reliability.
    seq = start_seq
    step = int(rate * 0.05)
    total = int(rate * event_ms / 1000)
    packets = []
    duration = step
    first = True
    while duration < total:
        packets.append(rtp_packet(seq, start_ts, ssrc, payload_type, telephone_event_payload(digit, duration, False), marker=first))
        seq += 1
        duration += step
        first = False
    for _ in range(3):
        packets.append(rtp_packet(seq, start_ts, ssrc, payload_type, telephone_event_payload(digit, total, True), marker=False))
        seq += 1
    return packets


def parse_rtp(pkt):
    if len(pkt) < 12:
        return None
    b0, b1, seq, ts, ssrc = struct.unpack("!BBHII", pkt[:12])
    version = b0 >> 6
    if version != 2:
        return None
    cc = b0 & 0x0F
    ext = (b0 >> 4) & 1
    marker = (b1 >> 7) & 1
    pt = b1 & 0x7F
    off = 12 + cc * 4
    if len(pkt) < off:
        return None
    if ext:
        if len(pkt) < off + 4:
            return None
        _, ext_len = struct.unpack("!HH", pkt[off:off + 4])
        off += 4 + ext_len * 4
    if len(pkt) < off:
        return None
    return {
        "marker": marker,
        "pt": pt,
        "seq": seq,
        "ts": ts,
        "ssrc": ssrc,
        "payload": pkt[off:],
    }


def write_wav(path, payload_type, payloads):
    if payload_type not in (0, 8) or not payloads:
        return False
    pcm = bytearray()
    for payload in payloads:
        if payload_type == 0:
            pcm.extend(audioop.ulaw2lin(payload, 2))
        else:
            pcm.extend(audioop.alaw2lin(payload, 2))
    with wave.open(path, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(8000)
        wf.writeframes(bytes(pcm))
    return True


AMR_NB_FRAME_SIZES = {
    0: 12,  # 4.75
    1: 13,  # 5.15
    2: 15,  # 5.90
    3: 17,  # 6.70
    4: 19,  # 7.40
    5: 20,  # 7.95
    6: 26,  # 10.2
    7: 31,  # 12.2
    8: 5,   # SID
}


def rtp_amr_nb_payload_to_storage_frame(payload):
    # RFC 4867 octet-aligned single-channel AMR-NB. The payloads observed from
    # VoHive are CMR + one ToC + frame bytes. Storage format keeps ToC + frame.
    if len(payload) < 2:
        return None
    toc = payload[1]
    ft = (toc >> 3) & 0x0F
    size = AMR_NB_FRAME_SIZES.get(ft)
    if size is None:
        return None
    frame = payload[2:2 + size]
    if len(frame) != size:
        return None
    storage_toc = toc & 0x7F
    return bytes([storage_toc]) + frame


def write_amr_nb(path, payloads):
    frames = []
    skipped = 0
    for payload in payloads:
        frame = rtp_amr_nb_payload_to_storage_frame(payload)
        if frame is None:
            skipped += 1
            continue
        frames.append(frame)
    if not frames:
        return False, 0, skipped
    with open(path, "wb") as f:
        f.write(b"#!AMR\n")
        for frame in frames:
            f.write(frame)
    return True, len(frames), skipped


def decode_amr_to_wav(amr_path, wav_path):
    if not shutil.which("ffmpeg"):
        return False, "ffmpeg_not_found"
    proc = subprocess.run(
        ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-i", amr_path, "-ar", "16000", "-ac", "1", wav_path],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if proc.returncode != 0:
        return False, (proc.stderr or proc.stdout).strip()
    return True, ""


def stringsafe(value):
    return "" if value is None else str(value)


def decoded_log_text(line):
    m = LOG_TEXT_RE.search(line)
    if not m:
        return ""
    raw = m.group(1)
    try:
        return json.loads('"%s"' % raw)
    except Exception:
        return raw


def text_to_dtmf(text):
    text = stringsafe(text).strip()
    if len(text) == 1 and text in DTMF_DIGITS:
        return text.upper()
    lower = text.lower()
    for prefix in ("/dtmf", "/key", "/press"):
        if lower.startswith(prefix):
            parts = text.split()
            if len(parts) >= 2 and len(parts[1]) == 1 and parts[1] in DTMF_DIGITS:
                return parts[1].upper()
    return None


def start_ffplay():
    if not shutil.which("ffplay"):
        return None, "ffplay_not_found"
    try:
        proc = subprocess.Popen(
            ["ffplay", "-nodisp", "-autoexit", "-loglevel", "error", "-f", "amr", "-i", "-"],
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
        )
        proc.stdin.write(b"#!AMR\n")
        proc.stdin.flush()
        return proc, ""
    except Exception as exc:
        return None, str(exc)


def probe(lan_port, duration=25, dtmf_digit=None, dtmf_after=7.0, dtmf_pt=101, stdin_dtmf=False, qq_dtmf=False, play_live=False):
    os.makedirs(OUT_DIR, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    meta_path = os.path.join(OUT_DIR, "rtp-probe-%s.json" % stamp)
    raw_path = os.path.join(OUT_DIR, "rtp-probe-%s.rtp" % stamp)
    wav_path = os.path.join(OUT_DIR, "rtp-probe-%s.wav" % stamp)
    amr_path = os.path.join(OUT_DIR, "rtp-probe-%s.amr" % stamp)

    target = ("127.0.0.1", lan_port)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.settimeout(0.2)

    stop = threading.Event()
    got_downlink = threading.Event()
    dtmf_queue = queue.Queue()
    received = []
    raw_packets = []
    stats = {
        "target": "%s:%d" % target,
        "local": "%s:%d" % sock.getsockname(),
        "started_at": dt.datetime.now().isoformat(),
        "duration_seconds": duration,
        "dtmf_digit": dtmf_digit,
        "dtmf_after_seconds": dtmf_after if dtmf_digit else None,
        "dtmf_payload_type": dtmf_pt if dtmf_digit else None,
        "dtmf_sent": False,
        "dtmf_events": [],
        "stdin_dtmf": stdin_dtmf,
        "qq_dtmf": qq_dtmf,
        "play_live": play_live,
        "packets_received": 0,
        "payload_types": {},
        "first_sources": [],
    }

    player = None
    if play_live:
        player, player_error = start_ffplay()
        if player:
            stats["live_player"] = "ffplay"
        else:
            stats["live_player_error"] = player_error

    def enqueue_dtmf(source, digit):
        digit = stringsafe(digit).strip().upper()
        if len(digit) == 1 and digit in DTMF_DIGITS:
            dtmf_queue.put((source, digit))

    def stdin_reader():
        print("stdin DTMF enabled: type 2 or /dtmf 2, then Enter.", flush=True)
        while not stop.is_set():
            line = sys.stdin.readline()
            if not line:
                break
            digit = text_to_dtmf(line)
            if digit:
                enqueue_dtmf("stdin", digit)

    def qq_reader():
        print("QQ DTMF enabled: send 2 or /dtmf 2 while the call is active.", flush=True)
        try:
            with open(LOG, "r", encoding="utf-8", errors="replace") as f:
                f.seek(0, os.SEEK_END)
                while not stop.is_set():
                    line = f.readline()
                    if not line:
                        time.sleep(0.1)
                        continue
                    if "QQ Bot" not in line or '"text":' not in line:
                        continue
                    digit = text_to_dtmf(decoded_log_text(line))
                    if digit:
                        enqueue_dtmf("qq", digit)
        except Exception as exc:
            stats["qq_dtmf_error"] = str(exc)

    if stdin_dtmf:
        threading.Thread(target=stdin_reader, daemon=True).start()
    if qq_dtmf:
        threading.Thread(target=qq_reader, daemon=True).start()

    def sender():
        seq = 1
        ts = 0
        ssrc = 0x43445831
        started = time.time()
        sent_dtmf = False
        while not stop.is_set():
            elapsed = time.time() - started
            if dtmf_digit and (not sent_dtmf) and elapsed >= dtmf_after:
                enqueue_dtmf("auto", dtmf_digit)
                sent_dtmf = True
            try:
                source, queued_digit = dtmf_queue.get_nowait()
            except queue.Empty:
                source = None
                queued_digit = None
            if queued_digit:
                for packet in dtmf_packets(queued_digit, seq, ts, ssrc, payload_type=dtmf_pt):
                    sock.sendto(packet, target)
                    seq += 1
                    time.sleep(0.05)
                stats["dtmf_sent"] = True
                stats["dtmf_events"].append({
                    "source": source,
                    "digit": queued_digit,
                    "elapsed_seconds": round(time.time() - started, 3),
                })
            sock.sendto(rtp_packet(seq, ts, ssrc), target)
            seq += 1
            ts += 160
            time.sleep(0.02 if got_downlink.is_set() else 0.5)

    t = threading.Thread(target=sender, daemon=True)
    t.start()

    end = time.time() + duration
    with open(raw_path, "wb") as raw:
        while time.time() < end:
            try:
                pkt, src = sock.recvfrom(4096)
            except socket.timeout:
                continue
            parsed = parse_rtp(pkt)
            if not parsed:
                continue
            got_downlink.set()
            raw.write(struct.pack("!H", len(pkt)) + pkt)
            raw_packets.append(pkt)
            received.append(parsed)
            if player and player.stdin:
                frame = rtp_amr_nb_payload_to_storage_frame(parsed["payload"])
                if frame:
                    try:
                        player.stdin.write(frame)
                        player.stdin.flush()
                    except Exception as exc:
                        stats["live_player_write_error"] = str(exc)
                        try:
                            player.kill()
                        except Exception:
                            pass
                        player = None
            pt = str(parsed["pt"])
            stats["packets_received"] += 1
            stats["payload_types"][pt] = stats["payload_types"].get(pt, 0) + 1
            src_text = "%s:%d" % src
            if src_text not in stats["first_sources"] and len(stats["first_sources"]) < 5:
                stats["first_sources"].append(src_text)

    stop.set()
    if player:
        try:
            player.stdin.close()
        except Exception:
            pass
        try:
            player.wait(timeout=2)
        except Exception:
            try:
                player.kill()
            except Exception:
                pass
    sock.close()

    dominant_pt = None
    if stats["payload_types"]:
        dominant_pt = int(max(stats["payload_types"].items(), key=lambda kv: kv[1])[0])
    stats["dominant_payload_type"] = dominant_pt
    stats["raw_rtp_file"] = raw_path
    if dominant_pt in (0, 8):
        payloads = [p["payload"] for p in received if p["pt"] == dominant_pt]
        if write_wav(wav_path, dominant_pt, payloads):
            stats["wav_file"] = wav_path
    else:
        payloads = [p["payload"] for p in received if p["pt"] == dominant_pt]
        ok, frames, skipped = write_amr_nb(amr_path, payloads)
        if ok:
            stats["amr_file"] = amr_path
            stats["amr_frames"] = frames
            stats["amr_skipped_payloads"] = skipped
            decoded, decode_error = decode_amr_to_wav(amr_path, wav_path)
            if decoded:
                stats["wav_file"] = wav_path
            elif decode_error:
                stats["wav_decode_error"] = decode_error
    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(stats, f, ensure_ascii=False, indent=2)
    print(json.dumps(stats, ensure_ascii=False, indent=2), flush=True)


def follow_log(args):
    print("Watching %s" % LOG, flush=True)
    seen_ports = set()
    while not os.path.exists(LOG):
        time.sleep(1)
    with open(LOG, "r", encoding="utf-8", errors="replace") as f:
        f.seek(0, os.SEEK_END)
        while True:
            line = f.readline()
            if not line:
                time.sleep(0.2)
                continue
            m = CALL_RE.search(line)
            if not m:
                continue
            port = int(m.group(1))
            if port in seen_ports:
                continue
            seen_ports.add(port)
            print("Detected RTP relay LAN port %d, starting probe." % port, flush=True)
            probe(
                port,
                duration=args.duration,
                dtmf_digit=args.dtmf,
                dtmf_after=args.dtmf_after,
                dtmf_pt=args.dtmf_pt,
                stdin_dtmf=args.stdin_dtmf,
                qq_dtmf=args.qq_dtmf,
                play_live=args.play_live,
            )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="VoHive RTP sidecar probe")
    parser.add_argument("port", nargs="?", type=int)
    parser.add_argument("--duration", type=int, default=25)
    parser.add_argument("--dtmf", choices=list("0123456789*#ABCDabcd"))
    parser.add_argument("--dtmf-after", type=float, default=7.0)
    parser.add_argument("--dtmf-pt", type=int, default=101)
    parser.add_argument("--stdin-dtmf", action="store_true", help="read DTMF digits from stdin while the call is active")
    parser.add_argument("--qq-dtmf", action="store_true", help="read DTMF digits from QQ Bot log messages while the call is active")
    parser.add_argument("--play-live", action="store_true", help="try to play AMR downlink audio through ffplay while recording")
    args = parser.parse_args()
    if args.port:
        probe(
            args.port,
            duration=args.duration,
            dtmf_digit=args.dtmf,
            dtmf_after=args.dtmf_after,
            dtmf_pt=args.dtmf_pt,
            stdin_dtmf=args.stdin_dtmf,
            qq_dtmf=args.qq_dtmf,
            play_live=args.play_live,
        )
    else:
        follow_log(args)
