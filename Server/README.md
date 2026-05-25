# VR-XR HUD Python Server

Single FastAPI process that serves the YOLO detection stream (WebSocket), the
VLM agent router (HTTP), and speech-to-text (HTTP) for the Quest standalone build.

```
ws://<your-laptop-ip>:8766/yolo       # binary frame in, JSON detections out
http://<your-laptop-ip>:8766/vlm/ask  # JSON in/out
http://<your-laptop-ip>:8766/healthz  # GET, sanity check
```

## One-time setup (Windows / your laptop)

```bat
cd Server

REM 1. virtual env
python -m venv .venv
.venv\Scripts\activate

REM 2. install PyTorch first (pick the right CUDA tag for your GPU)
pip install torch --index-url https://download.pytorch.org/whl/cu121

REM    (CPU-only fallback)
REM pip install torch --index-url https://download.pytorch.org/whl/cpu

REM 3. install everything else
pip install -r requirements.txt
```

The first `start.bat` run downloads:

* `yolov8n.pt` (~6 MB) - via ultralytics
* `apple/FastVLM-0.5B` - via Hugging Face, used by the multi-agent VLM router.
  Set `VRXR_VLM_BACKEND=moondream` before launching if you need the old
  centralized `vikhyatk/moondream2` backend.

## Running

```bat
start.bat
```

The Quest app should point at `http://<laptop-LAN-ip>:8766` (HTTP) and
`ws://<laptop-LAN-ip>:8766/yolo` (WebSocket).

To find the laptop's LAN IP:

```
ipconfig
```

Look for the IPv4 address on the same Wi-Fi as the Quest (likely `192.168.x.y`).

### Firewall

Windows Defender will likely prompt to allow Python through the firewall on first
run. Allow it for **Private** networks (the local Wi-Fi).

If no prompt appears or you denied it, manually open port 8766 inbound:

```powershell
New-NetFirewallRule -DisplayName "VR-XR Server 8766" -Direction Inbound -LocalPort 8766 -Protocol TCP -Action Allow
```

## Wire-up in Unity

Two scene-level components need the server URL:

* `YoloWebSocketClient.serverUrl` = `ws://192.168.X.X:8766/yolo`
* `VlmClient.serverBaseUrl` = `http://192.168.X.X:8766`

Both are inspector fields on prefabs / scene GameObjects, so no code change is
needed when the laptop IP changes.

## Custom YOLO weights

If you want to swap `yolov8n.pt` for a fine-tuned model, edit `get_yolo()` in
`server.py` and point `YOLO("path/to/your.pt")` at the new file. Everything else
stays the same.
