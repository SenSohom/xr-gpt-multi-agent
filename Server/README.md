# VR-XR HUD Python Server

Single FastAPI process that serves both the YOLO detection stream (WebSocket) and
the moondream2 VLM responses (HTTP) for the Quest standalone build.

```
ws://<your-laptop-ip>:8765/yolo       # binary frame in, JSON detections out
http://<your-laptop-ip>:8765/vlm/ask  # JSON in/out
http://<your-laptop-ip>:8765/healthz  # GET, sanity check
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
* `vikhyatk/moondream2` (~3.5 GB) - via Hugging Face, but only when the first
  `/vlm/ask` request arrives. Pass `--preload-vlm` to `server.py` if you want it
  loaded up front.

## Running

```bat
start.bat
```

The Quest app should point at `http://<laptop-LAN-ip>:8765` (HTTP) and
`ws://<laptop-LAN-ip>:8765/yolo` (WebSocket).

To find the laptop's LAN IP:

```
ipconfig
```

Look for the IPv4 address on the same Wi-Fi as the Quest (likely `192.168.x.y`).

### Firewall

Windows Defender will likely prompt to allow Python through the firewall on first
run. Allow it for **Private** networks (the local Wi-Fi).

If no prompt appears or you denied it, manually open port 8765 inbound:

```powershell
New-NetFirewallRule -DisplayName "VR-XR Server 8765" -Direction Inbound -LocalPort 8765 -Protocol TCP -Action Allow
```

## Wire-up in Unity

Two scene-level components need the server URL:

* `YoloWebSocketClient.serverUrl` = `ws://192.168.X.X:8765/yolo`
* `VlmClient.serverBaseUrl` = `http://192.168.X.X:8765`

Both are inspector fields on prefabs / scene GameObjects, so no code change is
needed when the laptop IP changes.

## Custom YOLO weights

If you want to swap `yolov8n.pt` for a fine-tuned model, edit `get_yolo()` in
`server.py` and point `YOLO("path/to/your.pt")` at the new file. Everything else
stays the same.
