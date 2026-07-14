#!/usr/bin/env python3
import argparse
import datetime as dt
import json
import os
import queue
import re
import socket
import struct
import subprocess
import sys
import threading
import time

import requests
import yaml

from vohive_rtp_probe import (
    CALL_RE,
    LOG,
    OUT_DIR,
    decode_amr_to_wav,
    decoded_log_text,
    dtmf_packets,
    parse_rtp,
    rtp_amr_nb_payload_to_storage_frame,
    rtp_packet,
    start_ffplay,
    text_to_dtmf,
    write_amr_nb,
)


DEFAULT_MODEL = os.environ.get("VOHIVE_ASR_MODEL", "/opt/vohive/asr-models/vosk-model-small-en-us-0.15")
DEFAULT_CONFIG = os.environ.get("VOHIVE_CONFIG", "/opt/vohive/config/config.yaml")
VOHIVE_API = os.environ.get("VOHIVE_API", "http://127.0.0.1:7575")
VOHIVE_API_USERNAME = os.environ.get("VOHIVE_API_USERNAME", "admin")
VOHIVE_API_PASSWORD = os.environ.get("VOHIVE_API_PASSWORD", "admin")
VOCALL_RE = re.compile(r'(?:^|\s)/vocall\s+(\S+)\s+(\S+)(?:\s+(\d+))?', re.IGNORECASE)


def compact(text):
    return " ".join((text or "").strip().split())


class QQPusher:
    def __init__(self, config_path=DEFAULT_CONFIG, timeout=10):
        with open(config_path, encoding="utf-8") as f:
            cfg = yaml.safe_load(f) or {}
        qq = cfg.get("qq") or {}
        self.app_id = compact(qq.get("app_id"))
        self.app_secret = compact(qq.get("app_secret"))
        self.direct_ids = [x.strip() for x in compact(qq.get("direct_ids")).split(",") if x.strip()]
        self.group_ids = [x.strip() for x in compact(qq.get("group_ids")).split(",") if x.strip()]
        self.timeout = timeout
        self.token = ""
        self.expires_at = 0.0
        if not self.app_id or not self.app_secret:
            raise RuntimeError("QQ app_id/app_secret not configured")
        if not self.direct_ids and not self.group_ids:
            raise RuntimeError("QQ direct_ids/group_ids not configured")

    def get_token(self):
        if self.token and time.time() < self.expires_at - 300:
            return self.token
        resp = requests.post(
            "https://bots.qq.com/app/getAppAccessToken",
            json={"appId": self.app_id, "clientSecret": self.app_secret},
            timeout=self.timeout,
        )
        resp.raise_for_status()
        data = resp.json()
        self.token = data["access_token"]
        self.expires_at = time.time() + int(data.get("expires_in") or 7200)
        return self.token

    def _send_one(self, kind, openid, text):
        base = "https://api.sgroup.qq.com"
        if kind == "direct":
            url = f"{base}/v2/users/{openid}/messages"
        elif kind == "group":
            url = f"{base}/v2/groups/{openid}/messages"
        else:
            raise ValueError(kind)
        token = self.get_token()
        resp = requests.post(
            url,
            headers={"Authorization": "QQBot " + token, "Content-Type": "application/json"},
            json={"content": text, "msg_type": 0},
            timeout=self.timeout,
        )
        if resp.status_code == 401:
            self.token = ""
            token = self.get_token()
            resp = requests.post(
                url,
                headers={"Authorization": "QQBot " + token, "Content-Type": "application/json"},
                json={"content": text, "msg_type": 0},
                timeout=self.timeout,
            )
        resp.raise_for_status()

    def send(self, text):
        text = compact(text)
        if not text:
            return
        last_error = None
        for openid in self.direct_ids:
            try:
                self._send_one("direct", openid, text)
            except Exception as exc:
                last_error = exc
        for openid in self.group_ids:
            try:
                self._send_one("group", openid, text)
            except Exception as exc:
                last_error = exc
        if last_error:
            raise last_error


class VoskAMRTranscriber:
    def __init__(self, model, on_text, partials=False):
        import vosk

        self.model = model
        self.on_text = on_text
        self.partials = partials
        self.recognizer = vosk.KaldiRecognizer(model, 16000)
        self.recognizer.SetWords(False)
        self.proc = None
        self.reader = None
        self.lock = threading.Lock()
        self.last_partial = ""
        self.last_partial_at = 0.0
        self.last_text = ""

    def start(self):
        self.proc = subprocess.Popen(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "amr",
                "-i",
                "pipe:0",
                "-ar",
                "16000",
                "-ac",
                "1",
                "-f",
                "s16le",
                "pipe:1",
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            bufsize=0,
        )
        self.proc.stdin.write(b"#!AMR\n")
        self.proc.stdin.flush()
        self.reader = threading.Thread(target=self._read_pcm, daemon=True)
        self.reader.start()

    def feed_amr_frame(self, frame):
        if not frame or not self.proc or not self.proc.stdin:
            return
        try:
            self.proc.stdin.write(frame)
            self.proc.stdin.flush()
        except Exception:
            pass

    def _emit(self, text, partial=False):
        text = compact(text)
        if not text:
            return
        now = time.time()
        if partial:
            if text == self.last_partial or now - self.last_partial_at < 4:
                return
            self.last_partial = text
            self.last_partial_at = now
        else:
            if text == self.last_text:
                return
            self.last_text = text
        self.on_text(text, partial)

    def _read_pcm(self):
        while self.proc and self.proc.stdout:
            data = self.proc.stdout.read(4000)
            if not data:
                break
            with self.lock:
                if self.recognizer.AcceptWaveform(data):
                    result = json.loads(self.recognizer.Result())
                    self._emit(result.get("text", ""), partial=False)
                elif self.partials:
                    result = json.loads(self.recognizer.PartialResult())
                    self._emit(result.get("partial", ""), partial=True)

    def stop(self):
        if self.proc and self.proc.stdin:
            try:
                self.proc.stdin.close()
            except Exception:
                pass
        if self.reader:
            self.reader.join(timeout=3)
        with self.lock:
            try:
                result = json.loads(self.recognizer.FinalResult())
                self._emit(result.get("text", ""), partial=False)
            except Exception:
                pass
        if self.proc:
            try:
                self.proc.wait(timeout=2)
            except Exception:
                try:
                    self.proc.kill()
                except Exception:
                    pass


class CallSidecar:
    def __init__(self, args, model=None, qq=None):
        self.args = args
        self.model = model
        self.qq = qq
        self.last_vocall = None
        self.last_recovery = 0.0

    def _api_token(self):
        resp = requests.post(
            VOHIVE_API + "/api/auth/login",
            json={"username": VOHIVE_API_USERNAME, "password": VOHIVE_API_PASSWORD},
            timeout=5,
        )
        resp.raise_for_status()
        return resp.json()["token"]

    def _vowifi_state(self, token):
        resp = requests.get(
            VOHIVE_API + "/api/devices/eSIM/overview",
            headers={"Authorization": "Bearer " + token},
            timeout=5,
        )
        resp.raise_for_status()
        devices = resp.json().get("devices") or []
        if not devices:
            return {}
        return devices[0].get("vowifi_runtime") or {}

    def wait_vowifi_ready(self, timeout=45):
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                token = self._api_token()
                state = self._vowifi_state(token)
                if state.get("ims_ready") and state.get("sms_ready"):
                    print("VoWiFi IMS/SMS ready; call listener is ready.", flush=True)
                    return True
            except Exception as exc:
                print(f"VoWiFi readiness check retrying: {exc}", flush=True)
            time.sleep(2)
        print("VoWiFi IMS/SMS did not become ready before timeout.", flush=True)
        return False

    def recover_after_sip_failure(self, reason):
        # Collapse duplicate failure lines and let the active recovery finish once.
        if time.time() - self.last_recovery < 30:
            return
        self.last_recovery = time.time()
        print(f"SIP path unhealthy ({reason}); refreshing VoWiFi/IMS before the next call...", flush=True)
        try:
            token = self._api_token()
            resp = requests.post(
                VOHIVE_API + "/api/devices/eSIM/vowifi/actions/reconnect",
                headers={"Authorization": "Bearer " + token},
                json={},
                timeout=15,
            )
            resp.raise_for_status()
        except Exception as exc:
            print(f"VoWiFi/IMS refresh request failed: {exc}", flush=True)
            return
        self.wait_vowifi_ready(timeout=60)

    def send_qq(self, text):
        if not self.qq:
            return
        try:
            self.qq.send(text)
        except Exception as exc:
            print(f"QQ push failed: {exc}", flush=True)

    def note_vocall(self, text):
        m = VOCALL_RE.search(compact(text))
        if not m:
            return
        hold = self.args.duration
        if m.group(3):
            try:
                hold = max(1, int(m.group(3)))
            except ValueError:
                hold = self.args.duration
        self.last_vocall = {
            "device_id": m.group(1),
            "callee": m.group(2),
            "hold_seconds": hold,
            "seen_at": time.time(),
        }
        print(f"Noted /vocall command: device={m.group(1)} callee={m.group(2)} hold={hold}s", flush=True)

    def call_duration_seconds(self):
        if self.args.duration_from_vocall and self.last_vocall:
            age = time.time() - float(self.last_vocall.get("seen_at") or 0)
            if age <= self.args.vocall_ttl:
                return int(self.last_vocall.get("hold_seconds") or self.args.duration) + int(self.args.duration_padding)
        return int(self.args.duration)

    def run_call(self, lan_port):
        os.makedirs(OUT_DIR, exist_ok=True)
        stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        amr_path = os.path.join(OUT_DIR, f"asr-sidecar-{stamp}.amr")
        wav_path = os.path.join(OUT_DIR, f"asr-sidecar-{stamp}.wav")
        json_path = os.path.join(OUT_DIR, f"asr-sidecar-{stamp}.json")

        target = ("127.0.0.1", lan_port)
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("127.0.0.1", 0))
        sock.settimeout(0.2)

        stop = threading.Event()
        got_downlink = threading.Event()
        call_connected = threading.Event()
        dtmf_queue = queue.Queue()
        received = []
        transcript = []
        started = time.time()
        duration_seconds = self.call_duration_seconds()
        print(f"Recording window for this call: {duration_seconds}s", flush=True)

        def on_text(text, partial=False):
            item = {
                "elapsed_seconds": round(time.time() - started, 2),
                "partial": partial,
                "text": compact(text),
            }
            transcript.append(item)
            label = "实时" if partial else "语音"
            line = f"[888 {label} {item['elapsed_seconds']}s] {item['text']}"
            print(line, flush=True)
            if self.args.qq_push_asr and not partial:
                self.send_qq(line)
            elif self.args.qq_push_partials and partial:
                self.send_qq(line)

        transcriber = None
        if self.model:
            transcriber = VoskAMRTranscriber(self.model, on_text, partials=self.args.asr_partials)
            transcriber.start()

        live_clients = []
        live_server = None
        if self.args.live_tcp_port:
            live_server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            live_server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            live_server.bind(("0.0.0.0", int(self.args.live_tcp_port)))
            live_server.listen(2)
            live_server.settimeout(0.2)
            print(f"Windows live audio TCP server listening on 127.0.0.1:{self.args.live_tcp_port}", flush=True)

            def accept_live_clients():
                while not stop.is_set():
                    try:
                        conn, addr = live_server.accept()
                    except socket.timeout:
                        continue
                    except Exception:
                        break
                    try:
                        conn.sendall(b"#!AMR\n")
                        conn.setblocking(False)
                        live_clients.append(conn)
                        print(f"Windows live audio client connected: {addr}", flush=True)
                    except Exception:
                        try:
                            conn.close()
                        except Exception:
                            pass

            threading.Thread(target=accept_live_clients, daemon=True).start()

        player = None
        if self.args.play_live:
            player, player_error = start_ffplay()
            if player_error:
                print(f"live player disabled: {player_error}", flush=True)

        def enqueue_dtmf(source, digit):
            digit = compact(digit).upper()
            if len(digit) == 1:
                dtmf_queue.put((source, digit))

        def qq_reader():
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
                print(f"QQ DTMF reader failed: {exc}", flush=True)

        if self.args.qq_dtmf:
            threading.Thread(target=qq_reader, daemon=True).start()

        def call_state_reader():
            try:
                with open(LOG, "r", encoding="utf-8", errors="replace") as f:
                    f.seek(0, os.SEEK_END)
                    while not stop.is_set():
                        line = f.readline()
                        if not line:
                            time.sleep(0.1)
                            continue
                        if "模拟呼叫已接通" in line:
                            call_connected.set()
                        elif "模拟呼叫接通失败" in line or "RTPRelay 已停止" in line:
                            break
            except Exception as exc:
                print(f"Call state reader failed: {exc}", flush=True)

        threading.Thread(target=call_state_reader, daemon=True).start()

        def sender():
            seq = 1
            ts = 0
            ssrc = 0x43445831
            while not stop.is_set():
                can_send_keepalive = call_connected.is_set() or time.time() - started >= self.args.rtp_keepalive_delay
                try:
                    source, digit = dtmf_queue.get_nowait()
                except queue.Empty:
                    source = None
                    digit = None
                if digit:
                    for packet in dtmf_packets(digit, seq, ts, ssrc, payload_type=self.args.dtmf_pt):
                        sock.sendto(packet, target)
                        seq += 1
                        time.sleep(0.05)
                    print(f"DTMF sent from {source}: {digit}", flush=True)
                if can_send_keepalive:
                    sock.sendto(rtp_packet(seq, ts, ssrc), target)
                    seq += 1
                    ts += 160
                time.sleep(0.02 if got_downlink.is_set() else 0.5)

        threading.Thread(target=sender, daemon=True).start()

        if self.args.qq_push_asr:
            self.send_qq("[888 voice] ASR sidecar connected; waiting for audio.")

        end = time.time() + duration_seconds
        try:
            while time.time() < end:
                if not got_downlink.is_set() and time.time() - started > self.args.no_downlink_timeout:
                    print(
                        f"No downlink RTP within {self.args.no_downlink_timeout}s on port {lan_port}, skipping relay.",
                        flush=True,
                    )
                    break
                try:
                    pkt, _ = sock.recvfrom(4096)
                except socket.timeout:
                    continue
                parsed = parse_rtp(pkt)
                if not parsed:
                    continue
                got_downlink.set()
                received.append(parsed)
                frame = rtp_amr_nb_payload_to_storage_frame(parsed["payload"])
                if frame:
                    if transcriber:
                        transcriber.feed_amr_frame(frame)
                    if live_clients:
                        dead_clients = []
                        for client in list(live_clients):
                            try:
                                client.sendall(frame)
                            except Exception:
                                dead_clients.append(client)
                        for client in dead_clients:
                            try:
                                client.close()
                            except Exception:
                                pass
                            try:
                                live_clients.remove(client)
                            except ValueError:
                                pass
                    if player and player.stdin:
                        try:
                            player.stdin.write(frame)
                            player.stdin.flush()
                        except Exception:
                            player = None
        finally:
            stop.set()
            if transcriber:
                transcriber.stop()
            if player:
                try:
                    player.stdin.close()
                except Exception:
                    pass
                try:
                    player.wait(timeout=2)
                except Exception:
                    pass
            if live_server:
                try:
                    live_server.close()
                except Exception:
                    pass
            for client in list(live_clients):
                try:
                    client.close()
                except Exception:
                    pass
            sock.close()
        payloads = [p["payload"] for p in received]
        ok, frames, skipped = write_amr_nb(amr_path, payloads)
        if ok:
            decode_amr_to_wav(amr_path, wav_path)
        meta = {
            "started_at": dt.datetime.fromtimestamp(started).isoformat(),
            "duration_seconds": duration_seconds,
            "vocall": self.last_vocall,
            "lan_port": lan_port,
            "packets_received": len(received),
            "amr_file": amr_path if ok else "",
            "wav_file": wav_path if os.path.exists(wav_path) else "",
            "amr_frames": frames if ok else 0,
            "amr_skipped_payloads": skipped if ok else 0,
            "transcript": transcript,
        }
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump(meta, f, ensure_ascii=False, indent=2)
        print(json.dumps(meta, ensure_ascii=False, indent=2), flush=True)
        if self.args.qq_push_asr and transcript:
            self.send_qq(f"[888 voice] Recognition finished, {len(transcript)} text segments.")

    def follow_log(self):
        self.wait_vowifi_ready(timeout=45)
        print(f"Watching {LOG}", flush=True)
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
                if "QQ Bot" in line and '"text":' in line:
                    self.note_vocall(decoded_log_text(line))
                if "模拟呼叫接通失败" in line:
                    self.recover_after_sip_failure("INVITE dialog failure")
                    continue
                if '"outcome_kind": "no_answer"' in line:
                    self.recover_after_sip_failure("INVITE no answer")
                    continue
                if "IMS SUBSCRIBE 发送失败" in line or "client transport error detected" in line:
                    self.recover_after_sip_failure("IMS transport failure")
                    continue
                m = CALL_RE.search(line)
                if not m:
                    continue
                port = int(m.group(1))
                if port in seen_ports:
                    continue
                seen_ports.add(port)
                print(f"Detected RTP relay LAN port {port}, starting ASR sidecar.", flush=True)
                self.run_call(port)


def main():
    parser = argparse.ArgumentParser(description="VoHive RTP ASR + QQ sidecar")
    parser.add_argument("--duration", type=int, default=40)
    parser.add_argument("--duration-from-vocall", action="store_true", help="use the latest QQ Bot /vocall hold seconds for each recording window")
    parser.add_argument("--duration-padding", type=int, default=8, help="extra recording seconds added to /vocall hold seconds")
    parser.add_argument("--vocall-ttl", type=float, default=180.0, help="seconds a seen /vocall command remains associated with the next RTP relay")
    parser.add_argument("--dtmf-pt", type=int, default=101)
    parser.add_argument("--qq-dtmf", action="store_true")
    parser.add_argument("--play-live", action="store_true")
    parser.add_argument("--live-tcp-port", type=int, default=0, help="stream AMR audio to a local TCP port for Windows ffplay")
    parser.add_argument("--rtp-keepalive-delay", type=float, default=6.0, help="fallback seconds to wait after RTP relay creation before sending LAN RTP keepalive")
    parser.add_argument("--asr-vosk", action="store_true")
    parser.add_argument("--asr-model", default=DEFAULT_MODEL)
    parser.add_argument("--asr-partials", action="store_true")
    parser.add_argument("--qq-push-asr", action="store_true")
    parser.add_argument("--qq-push-partials", action="store_true")
    parser.add_argument("--qq-config", default=DEFAULT_CONFIG)
    parser.add_argument("--no-downlink-timeout", type=float, default=8.0)
    args = parser.parse_args()

    model = None
    if args.asr_vosk:
        import vosk

        if not os.path.isdir(args.asr_model):
            raise SystemExit(f"Vosk model not found: {args.asr_model}")
        vosk.SetLogLevel(-1)
        print(f"Loading Vosk model: {args.asr_model}", flush=True)
        model = vosk.Model(args.asr_model)

    qq = None
    if args.qq_push_asr or args.qq_push_partials:
        qq = QQPusher(args.qq_config)
        print("QQ ASR push enabled.", flush=True)

    CallSidecar(args, model=model, qq=qq).follow_log()


if __name__ == "__main__":
    main()
