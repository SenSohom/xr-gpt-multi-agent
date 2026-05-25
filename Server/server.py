"""
Unified Python server for the VR-XR HUD project.

Endpoints:
  WebSocket  /yolo       - Quest pushes binary frames; server pushes JSON detections.
  HTTP POST  /vlm/ask    - {image_b64, label, prompt, agent_task} -> {answer, latency_ms}.
  HTTP GET   /healthz    - basic liveness.

Design notes:
  * Detection is the latency-critical path. Each WebSocket connection has its own
    asyncio loop; we only ever keep the LATEST received frame (drop-old) so a slow
    inference never causes queue buildup. Quest also enforces single-frame in-flight
    on its side, so this is belt-and-suspenders.
  * VLM defaults to apple/FastVLM-0.5B and is wrapped by a logical multi-agent
    router. Set VRXR_VLM_BACKEND=moondream to use the old centralized path.
  * VLM is only triggered by user interaction (Open / More Info / Chat), so it
    never competes with YOLO unless explicitly requested.
  * Both YOLO model and VLM backend are loaded once globally. They move to CUDA
    if available, otherwise CPU.
"""

import asyncio
import base64
import io
import struct
import time
import logging
from typing import List, Optional

import numpy as np
from PIL import Image

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field

from vlm_agents import (
    AgentRouter,
    FastVlmEngine,
    MoondreamEngine,
    desired_backend,
)

# Lazy model holders - imported when needed so server can start fast.
_yolo_model = None
_vlm_engine = None
_vlm_router = None
_torch = None
_device = "cpu"

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("vrxr.server")


# ----------------------------------------------------------------
# App + CORS
# ----------------------------------------------------------------
app = FastAPI(title="VR-XR HUD Server", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


# ----------------------------------------------------------------
# Model loaders
# ----------------------------------------------------------------
def _ensure_torch():
    global _torch, _device
    if _torch is None:
        import torch
        _torch = torch
        _device = "cuda" if torch.cuda.is_available() else "cpu"
        log.info(f"Torch device = {_device}")


def get_yolo():
    """Load YOLOv8n once. Replace with custom weights if you have them."""
    global _yolo_model
    if _yolo_model is None:
        _ensure_torch()
        from ultralytics import YOLO
        log.info("Loading YOLO model (yolov8n.pt) ...")
        _yolo_model = YOLO("yolov8n.pt")
        # Warm up
        try:
            dummy = np.zeros((640, 640, 3), dtype=np.uint8)
            _yolo_model.predict(dummy, verbose=False, device=_device)
            log.info("YOLO warm-up complete.")
        except Exception as e:
            log.warning(f"YOLO warm-up failed: {e}")
    return _yolo_model


def get_vlm_engine():
    """Load the configured VLM backend once."""
    global _vlm_engine
    if _vlm_engine is None:
        _ensure_torch()
        backend = desired_backend()
        if backend == "moondream":
            _vlm_engine = MoondreamEngine(_torch, _device)
        elif backend == "fastvlm":
            _vlm_engine = FastVlmEngine(_torch)
        else:
            raise RuntimeError(
                f"Unsupported VRXR_VLM_BACKEND='{backend}'. "
                "Use 'fastvlm' or 'moondream'."
            )
    return _vlm_engine


def get_vlm_router():
    """Create the logical multi-agent router over the configured VLM backend."""
    global _vlm_router
    if _vlm_router is None:
        _vlm_router = AgentRouter(get_vlm_engine)
    return _vlm_router


# ----------------------------------------------------------------
# /healthz
# ----------------------------------------------------------------
@app.get("/healthz")
def healthz():
    return {
        "ok": True,
        "device": _device if _torch is not None else "unloaded",
        "yolo_loaded": _yolo_model is not None,
        "vlm_backend": desired_backend(),
        "vlm_loaded": _vlm_engine is not None,
    }


# ----------------------------------------------------------------
# WebSocket /yolo
# ----------------------------------------------------------------
@app.websocket("/yolo")
async def yolo_ws(ws: WebSocket):
    await ws.accept()
    log.info(f"YOLO client connected: {ws.client}")

    yolo = get_yolo()

    # latest_frame is the only "queue" - drop-old behavior.
    latest_frame: Optional[bytes] = None
    latest_frame_id: int = 0
    new_frame_event = asyncio.Event()
    closing = asyncio.Event()
    frames_received_count = 0
    last_diag_log_ts = time.perf_counter()

    async def reader():
        nonlocal latest_frame, latest_frame_id, frames_received_count, last_diag_log_ts
        try:
            while not closing.is_set():
                msg = await ws.receive()
                if msg.get("type") == "websocket.disconnect":
                    return
                payload = msg.get("bytes")
                if payload is None or len(payload) < 4:
                    text_payload = msg.get("text")
                    if text_payload is not None:
                        log.warning(f"Received unexpected text message ({len(text_payload)} chars): {text_payload[:120]}")
                    continue

                frame_id = struct.unpack_from("<I", payload, 0)[0]
                jpeg = payload[4:]
                frames_received_count += 1

                # Periodic diagnostic
                now = time.perf_counter()
                if now - last_diag_log_ts >= 5.0:
                    log.info(f"[YOLO-RX] frames={frames_received_count} latest_id={frame_id} jpeg={len(jpeg)}B in last {now - last_diag_log_ts:.1f}s")
                    frames_received_count = 0
                    last_diag_log_ts = now

                # Drop-old: simply overwrite.
                latest_frame = jpeg
                latest_frame_id = frame_id
                new_frame_event.set()
        except WebSocketDisconnect:
            log.info("YOLO client disconnected (reader).")
        except Exception as e:
            log.warning(f"YOLO reader error: {e}")
        finally:
            closing.set()
            new_frame_event.set()

    async def worker():
        nonlocal latest_frame, latest_frame_id
        infer_frames = 0
        infer_total_dets = 0
        infer_label_counts: dict = {}
        last_infer_log_ts = time.perf_counter()
        first_frame_dumped = False
        last_frame_dump_ts = 0.0
        try:
            while not closing.is_set():
                await new_frame_event.wait()
                if closing.is_set():
                    return
                # Snapshot the latest then clear the flag
                jpeg = latest_frame
                fid = latest_frame_id
                latest_frame = None
                new_frame_event.clear()

                if jpeg is None:
                    continue

                t0 = time.perf_counter()
                try:
                    img = Image.open(io.BytesIO(jpeg)).convert("RGB")
                except Exception as e:
                    log.warning(f"JPEG decode failed: {e}")
                    continue

                np_img = np.array(img)
                h, w, _ = np_img.shape

                # Diagnostic: dump first frame + one frame every 10s so we can eyeball passthrough quality.
                now_dump = time.perf_counter()
                if (not first_frame_dumped) or (now_dump - last_frame_dump_ts >= 10.0):
                    try:
                        import os
                        dump_dir = os.path.join(os.path.dirname(__file__), "frame_dumps")
                        os.makedirs(dump_dir, exist_ok=True)
                        fname = "first.jpg" if not first_frame_dumped else f"frame_{int(time.time())}.jpg"
                        with open(os.path.join(dump_dir, fname), "wb") as f:
                            f.write(jpeg)
                        mean_rgb = np_img.reshape(-1, 3).mean(axis=0)
                        log.info(f"[YOLO-DUMP] saved {fname} ({len(jpeg)}B) wxh={w}x{h} mean_rgb=({mean_rgb[0]:.1f},{mean_rgb[1]:.1f},{mean_rgb[2]:.1f})")
                        first_frame_dumped = True
                        last_frame_dump_ts = now_dump
                    except Exception as e:
                        log.warning(f"frame dump failed: {e}")

                # Run YOLO. ultralytics handles BGR/RGB internally if we pass numpy.
                results = yolo.predict(
                    np_img,
                    verbose=False,
                    device=_device,
                    conf=0.05,
                    iou=0.45,
                    imgsz=640,
                )

                detections = []
                if results and len(results) > 0:
                    r = results[0]
                    names = r.names
                    if r.boxes is not None and len(r.boxes) > 0:
                        xyxy = r.boxes.xyxy.cpu().numpy()
                        confs = r.boxes.conf.cpu().numpy()
                        cls = r.boxes.cls.cpu().numpy().astype(int)
                        for i in range(xyxy.shape[0]):
                            x1, y1, x2, y2 = xyxy[i].tolist()
                            detections.append({
                                "label": str(names.get(int(cls[i]), str(int(cls[i])))),
                                "confidence": float(confs[i]),
                                "x": float(x1),
                                "y": float(y1),
                                "w": float(x2 - x1),
                                "h": float(y2 - y1),
                            })

                latency_ms = (time.perf_counter() - t0) * 1000.0

                # Inference diagnostic — aggregate every 5s
                infer_frames += 1
                infer_total_dets += len(detections)
                for d in detections:
                    lbl = d["label"]
                    infer_label_counts[lbl] = infer_label_counts.get(lbl, 0) + 1
                now2 = time.perf_counter()
                if now2 - last_infer_log_ts >= 5.0:
                    avg = infer_total_dets / max(1, infer_frames)
                    top = sorted(infer_label_counts.items(), key=lambda kv: -kv[1])[:5]
                    top_str = ", ".join(f"{k}:{v}" for k, v in top) if top else "(none)"
                    log.info(f"[YOLO-INF] frames={infer_frames} dets={infer_total_dets} avg={avg:.2f}/frame top=[{top_str}] last_latency={latency_ms:.1f}ms wxh={w}x{h}")
                    infer_frames = 0
                    infer_total_dets = 0
                    infer_label_counts = {}
                    last_infer_log_ts = now2

                resp = {
                    "frame_id": fid,
                    "image_w": w,
                    "image_h": h,
                    "detections": detections,
                    "latency_ms": latency_ms,
                }
                try:
                    await ws.send_json(resp)
                except Exception as e:
                    log.warning(f"YOLO send error: {e}")
                    closing.set()
                    return
        except Exception as e:
            log.warning(f"YOLO worker error: {e}")
            closing.set()

    try:
        await asyncio.gather(reader(), worker())
    finally:
        try:
            await ws.close()
        except Exception:
            pass
        log.info(f"YOLO client closed: {ws.client}")


# ----------------------------------------------------------------
# HTTP /vlm/ask
# ----------------------------------------------------------------
class VlmAskRequest(BaseModel):
    image_b64: str
    label: str = ""
    prompt: str
    max_new_tokens: int = 96
    agent_task: str = ""
    enable_critic: bool = False


class VlmAskResponse(BaseModel):
    answer: str = ""
    latency_ms: float = 0.0
    error: str = ""
    backend: str = ""
    agent: str = ""
    trace: List[dict] = Field(default_factory=list)


@app.post("/vlm/ask", response_model=VlmAskResponse)
async def vlm_ask(req: VlmAskRequest):
    if not req.image_b64:
        return VlmAskResponse(error="image_b64 required")
    if not req.prompt:
        return VlmAskResponse(error="prompt required")

    try:
        raw = base64.b64decode(req.image_b64)
        img = Image.open(io.BytesIO(raw)).convert("RGB")
    except Exception as e:
        return VlmAskResponse(error=f"image decode failed: {e}")

    t0 = time.perf_counter()
    try:
        router = get_vlm_router()

        # Run blocking VLM work in a thread so the event loop isn't stalled.
        def _infer():
            return router.ask(
                image=img,
                label=req.label,
                prompt=req.prompt,
                task=req.agent_task,
                max_new_tokens=req.max_new_tokens,
                enable_critic=req.enable_critic,
            )

        answer, trace = await asyncio.to_thread(_infer)
        latency_ms = (time.perf_counter() - t0) * 1000.0
        primary_agent = trace[0].agent if trace else ""
        return VlmAskResponse(
            answer=answer.strip(),
            latency_ms=latency_ms,
            backend=desired_backend(),
            agent=primary_agent,
            trace=[
                {
                    "agent": r.agent,
                    "latency_ms": r.latency_ms,
                    "error": r.error,
                }
                for r in trace
            ],
        )
    except Exception as e:
        log.exception("VLM inference failed")
        return VlmAskResponse(
            error=str(e),
            latency_ms=(time.perf_counter() - t0) * 1000.0,
            backend=desired_backend(),
        )


# ----------------------------------------------------------------
# HTTP /stt/transcribe  (Speech-to-Text via faster-whisper or whisper)
# ----------------------------------------------------------------
_stt_model = None

def get_stt():
    """Load whisper model once (lazy). Uses faster-whisper if installed, else openai-whisper."""
    global _stt_model
    if _stt_model is None:
        try:
            from faster_whisper import WhisperModel
            log.info("Loading faster-whisper model (base)…")
            _stt_model = WhisperModel("base", device="cpu", compute_type="int8")
            log.info("faster-whisper ready.")
        except ImportError:
            try:
                import whisper as _whisper
                log.info("Loading openai-whisper model (base)…")
                _stt_model = _whisper.load_model("base")
                log.info("openai-whisper ready.")
            except ImportError:
                raise RuntimeError(
                    "No STT library found. Install one:\n"
                    "  pip install faster-whisper\n"
                    "  -- or --\n"
                    "  pip install openai-whisper"
                )
    return _stt_model


class SttRequest(BaseModel):
    audio_b64: str          # WAV file encoded as base64


class SttResponse(BaseModel):
    transcript: str = ""
    latency_ms: float = 0.0
    error: str = ""


@app.post("/stt/transcribe", response_model=SttResponse)
async def stt_transcribe(req: SttRequest):
    if not req.audio_b64:
        return SttResponse(error="audio_b64 required")

    try:
        wav_bytes = base64.b64decode(req.audio_b64)
    except Exception as e:
        return SttResponse(error=f"base64 decode failed: {e}")

    t0 = time.perf_counter()
    try:
        model = get_stt()

        def _transcribe():
            audio_buf = io.BytesIO(wav_bytes)
            # Detect whether we're using faster-whisper or openai-whisper by duck typing
            if hasattr(model, "transcribe") and callable(model.transcribe):
                # openai-whisper: model.transcribe(audio) — needs numpy array or file path
                import tempfile, os
                with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as f:
                    f.write(wav_bytes)
                    tmp_path = f.name
                try:
                    result = model.transcribe(tmp_path, language="en")
                    return result["text"].strip()
                finally:
                    os.unlink(tmp_path)
            else:
                # faster-whisper: model.transcribe(audio_buf) returns (segments, info)
                segments, _ = model.transcribe(audio_buf, language="en", beam_size=1)
                return " ".join(seg.text for seg in segments).strip()

        transcript = await asyncio.to_thread(_transcribe)
        latency_ms = (time.perf_counter() - t0) * 1000.0
        log.info(f"[STT] transcript='{transcript}' latency={latency_ms:.0f}ms")
        return SttResponse(transcript=transcript, latency_ms=latency_ms)

    except Exception as e:
        log.exception("STT failed")
        return SttResponse(error=str(e), latency_ms=(time.perf_counter() - t0) * 1000.0)


# ----------------------------------------------------------------
# Entrypoint helper
# ----------------------------------------------------------------
if __name__ == "__main__":
    import uvicorn
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8766)
    parser.add_argument("--preload-yolo", action="store_true",
                        help="Eagerly load YOLO weights on startup")
    parser.add_argument("--preload-vlm", action="store_true",
                        help="Eagerly load the configured VLM backend on startup")
    args = parser.parse_args()

    if args.preload_yolo:
        get_yolo()
    if args.preload_vlm:
        get_vlm_engine()

    log.info(f"Starting server on {args.host}:{args.port}")
    uvicorn.run(app, host=args.host, port=args.port, log_level="info")
