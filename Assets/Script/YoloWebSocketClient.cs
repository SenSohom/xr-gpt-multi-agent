using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 跟 Python YOLO 服务器维护一个 WebSocket 长连接。
/// - 主线程定时从 passthrough RawImage.texture 抓帧 -> JPEG -> 发送
/// - 背景线程接收 JSON 检测结果 -> 入队
/// - 主线程 Update() 把检测结果喂给 YoloToRealDetectionBridge
///
/// 设计要点(低延迟):
/// 1. 单帧 in-flight: 还没收到结果就不发下一帧(防止排队堆积)
/// 2. 服务器侧 drop-old: 这边只确保不堆,server 那边也不堆
/// 3. 抓帧用 GPU Blit + AsyncGPUReadback,不阻塞主线程渲染
/// </summary>
public class YoloWebSocketClient : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("WebSocket URL。运行时会被 Assets/Resources/NetworkConfig.asset 覆盖；这里只是兜底默认值。")]
    public string serverUrl = "ws://192.168.1.182:8766/yolo";

    [Header("Source")]
    [Tooltip("【推荐】直接拖 PassthroughCameraViewer。读它的 CurrentTexture,不需要 RawImage 在场景里")]
    public PassthroughCameraViewer passthroughViewer;

    [Tooltip("可选:passthrough 画面的 RawImage,YOLO 输入就从它的 texture 抓。如果 viewer 已设,这个不必要")]
    public RawImage passthroughRawImage;

    [Tooltip("最后的兜底:如果上面都拿不到,用这张固定 Texture")]
    public Texture fallbackTexture;

    [Tooltip("强制使用 fallbackTexture 不读 viewer/rawImage(Quest Link/Air Link 下 PCA 给的是占位灰阶图,这时打开它能跑通整条链路调试 YOLO/UI)")]
    public bool forceUseFallback = false;

    [Header("Capture")]
    [Tooltip("送给 YOLO 的图像最长边像素数(640 是 YOLOv8 标准)")]
    public int captureLongEdge = 640;

    [Tooltip("JPEG 质量(0-100)")]
    [Range(10, 95)]
    public int jpegQuality = 75;

    [Tooltip("最大发送帧率")]
    [Range(1, 30)]
    public int targetFps = 15;

    [Header("Bridge")]
    [Tooltip("把检测结果转交给 RealDetectionProvider 的桥")]
    public YoloToRealDetectionBridge bridge;

    [Header("Reconnect")]
    [Tooltip("断线后多少秒后自动重连")]
    public float reconnectDelaySeconds = 2f;

    [Header("Debug")]
    public bool logConnection = true;
    public bool logFrames = false;
    [Tooltip("Quest 上诊断:每多少秒 Debug.Log 一次状态(连接/纹理/帧数)")]
    public float diagnosticLogIntervalSeconds = 5f;

    // ---- 内部状态 ----
    private ClientWebSocket socket;
    private CancellationTokenSource cts;
    private Task receiveTask;
    private bool connecting;
    private float reconnectAt;
    private float nextDiagnosticAt;
    private int framesSentInWindow;
    private int detectionsReceivedInWindow;
    private string lastSourceTextureStatus = "init";

    // 从背景线程入队检测结果,主线程 Update 出队
    private readonly ConcurrentQueue<DetectionMessage> incomingMessages = new ConcurrentQueue<DetectionMessage>();

    // 单帧 in-flight 标志:发出去还没收到回复就不发下一帧
    private volatile bool waitingForResult;
    private int currentFrameId;

    private float sendInterval;
    private float sendTimer;

    private RenderTexture captureRT;
    private Texture2D captureCpuTex;

    // ---- 服务器协议 ----
    [Serializable]
    private class DetectionMessage
    {
        public int frame_id;
        public int image_w;
        public int image_h;
        public Detection[] detections;
    }

    [Serializable]
    private class Detection
    {
        public string label;
        public float confidence;
        public float x;       // 左上角 x (像素,基于 image_w)
        public float y;       // 左上角 y (像素,基于 image_h)
        public float w;
        public float h;
    }

    private void Awake()
    {
        // Centralised IP config — same source as VlmClient. See NetworkConfig.cs.
        var cfg = NetworkConfig.Load();
        if (cfg != null)
        {
            string newUrl = cfg.YoloWebSocketUrl;
            if (serverUrl != newUrl)
            {
                Debug.Log($"[YoloWS] serverUrl overridden by NetworkConfig: '{serverUrl}' → '{newUrl}'");
                serverUrl = newUrl;
            }
        }
    }

    private void OnEnable()
    {
        sendInterval = 1f / Mathf.Max(1, targetFps);
        ConnectAsync();
    }

    private void OnDisable()
    {
        DisconnectAsync();

        if (captureRT != null)
        {
            captureRT.Release();
            Destroy(captureRT);
            captureRT = null;
        }
        if (captureCpuTex != null)
        {
            Destroy(captureCpuTex);
            captureCpuTex = null;
        }
    }

    private async void ConnectAsync()
    {
        if (connecting) return;
        connecting = true;
        try
        {
            cts = new CancellationTokenSource();
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            if (logConnection)
                Debug.Log($"[YoloWS] Connecting to {serverUrl} ...");

            await socket.ConnectAsync(new Uri(serverUrl), cts.Token);

            if (logConnection)
                Debug.Log($"[YoloWS] Connected.");

            receiveTask = Task.Run(() => ReceiveLoop(cts.Token));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[YoloWS] Connect failed: {e.Message}. Will retry in {reconnectDelaySeconds}s.");
            ScheduleReconnect();
        }
        finally
        {
            connecting = false;
        }
    }

    private void ScheduleReconnect()
    {
        try { socket?.Dispose(); } catch { }
        socket = null;
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        cts = null;
        reconnectAt = Time.unscaledTime + Mathf.Max(0.5f, reconnectDelaySeconds);
    }

    private async void DisconnectAsync()
    {
        try
        {
            if (cts != null)
            {
                cts.Cancel();
            }
            if (socket != null && socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None);
            }
        }
        catch { /* ignore */ }
        finally
        {
            socket?.Dispose();
            socket = null;
            cts?.Dispose();
            cts = null;
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        var buffer = new byte[1 << 16]; // 64KB 临时
        var sb = new StringBuilder();

        try
        {
            while (!token.IsCancellationRequested && socket != null && socket.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                string json = sb.ToString();
                if (string.IsNullOrEmpty(json)) continue;

                DetectionMessage msg;
                try
                {
                    msg = JsonUtility.FromJson<DetectionMessage>(json);
                }
                catch
                {
                    continue;
                }

                if (msg != null)
                {
                    incomingMessages.Enqueue(msg);
                    waitingForResult = false;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception e)
        {
            Debug.LogWarning($"[YoloWS] Receive loop error: {e.Message}");
            waitingForResult = false;
            // Trigger reconnect from main thread
            try { socket?.Abort(); } catch { }
        }
    }

    private void Update()
    {
        // 1. 处理服务器返回的最新检测(只用最后一条,避免堆积)
        DetectionMessage latest = null;
        int dequeued = 0;
        while (incomingMessages.TryDequeue(out var msg))
        {
            latest = msg;
            dequeued++;
        }
        detectionsReceivedInWindow += dequeued;

        if (latest != null && bridge != null)
        {
            ApplyDetections(latest);
        }

        // 2. 重连
        if ((socket == null || socket.State != WebSocketState.Open) && !connecting)
        {
            if (Time.unscaledTime >= reconnectAt)
                ConnectAsync();
        }

        // 3. 周期性诊断输出(Quest logcat 看)
        EmitDiagnostic();

        // 4. 控制发送节奏
        sendTimer += Time.unscaledDeltaTime;
        if (sendTimer < sendInterval) return;
        if (waitingForResult) return; // 还在等上一帧的结果

        if (socket == null || socket.State != WebSocketState.Open) return;

        Texture src = GetSourceTexture();
        if (src == null) return;

        sendTimer = 0f;
        SendFrame(src);
    }

    private void EmitDiagnostic()
    {
        if (diagnosticLogIntervalSeconds <= 0f) return;
        if (Time.unscaledTime < nextDiagnosticAt) return;
        nextDiagnosticAt = Time.unscaledTime + diagnosticLogIntervalSeconds;

        string socketState = socket == null ? "null" : socket.State.ToString();
        Texture src = GetSourceTexture();
        string textureStatus;
        if (src == null)
        {
            string from = passthroughViewer != null ? "viewer.CurrentTexture"
                          : passthroughRawImage != null ? "rawImage.texture"
                          : "no source assigned";
            textureStatus = $"NULL ({from} returned null — passthrough not started?)";
        }
        else
        {
            string from = (passthroughViewer != null && passthroughViewer.CurrentTexture != null) ? "viewer"
                          : (passthroughRawImage != null && passthroughRawImage.texture != null) ? "rawImage"
                          : "fallback";
            textureStatus = $"OK {src.width}x{src.height} (from {from})";
        }

        Debug.Log($"[YoloWS-DIAG] socket={socketState} tex=[{textureStatus}] sent={framesSentInWindow}/win received={detectionsReceivedInWindow}/win bridge={(bridge != null ? "OK" : "NULL")}");

        framesSentInWindow = 0;
        detectionsReceivedInWindow = 0;
        lastSourceTextureStatus = textureStatus;
    }

    private Texture GetSourceTexture()
    {
        if (forceUseFallback && fallbackTexture != null)
            return fallbackTexture;
        if (passthroughViewer != null && passthroughViewer.CurrentTexture != null)
            return passthroughViewer.CurrentTexture;
        if (passthroughRawImage != null && passthroughRawImage.texture != null)
            return passthroughRawImage.texture;
        return fallbackTexture;
    }

    private async void SendFrame(Texture src)
    {
        try
        {
            int srcW = src.width;
            int srcH = src.height;
            if (srcW <= 0 || srcH <= 0) return;

            // 计算输出尺寸
            int outW = srcW;
            int outH = srcH;
            int longEdge = Mathf.Max(outW, outH);
            if (longEdge > captureLongEdge)
            {
                float scale = (float)captureLongEdge / longEdge;
                outW = Mathf.Max(1, Mathf.RoundToInt(outW * scale));
                outH = Mathf.Max(1, Mathf.RoundToInt(outH * scale));
            }

            if (captureRT == null || captureRT.width != outW || captureRT.height != outH)
            {
                if (captureRT != null) { captureRT.Release(); Destroy(captureRT); }
                captureRT = new RenderTexture(outW, outH, 0, RenderTextureFormat.ARGB32);
                captureRT.filterMode = FilterMode.Bilinear;
            }
            if (captureCpuTex == null || captureCpuTex.width != outW || captureCpuTex.height != outH)
            {
                if (captureCpuTex != null) Destroy(captureCpuTex);
                captureCpuTex = new Texture2D(outW, outH, TextureFormat.RGB24, false);
            }

            Graphics.Blit(src, captureRT);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = captureRT;
            captureCpuTex.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
            captureCpuTex.Apply(false, false);
            RenderTexture.active = prev;

            // 黑帧检测:如果整张图几乎全黑/全白/纯色,YOLO 也不可能识别出东西,直接跳过省带宽
            if (!IsFrameUseful(captureCpuTex))
            {
                waitingForResult = false; // 没发出去,允许下一帧立即尝试
                return;
            }

            byte[] jpeg = captureCpuTex.EncodeToJPG(jpegQuality);
            if (jpeg == null || jpeg.Length == 0) return;

            currentFrameId++;
            // 协议:前 4 字节是 frame_id (little endian),后面是 jpeg
            byte[] payload = new byte[4 + jpeg.Length];
            payload[0] = (byte)(currentFrameId & 0xFF);
            payload[1] = (byte)((currentFrameId >> 8) & 0xFF);
            payload[2] = (byte)((currentFrameId >> 16) & 0xFF);
            payload[3] = (byte)((currentFrameId >> 24) & 0xFF);
            Buffer.BlockCopy(jpeg, 0, payload, 4, jpeg.Length);

            waitingForResult = true;

            await socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Binary,
                true,
                cts != null ? cts.Token : CancellationToken.None
            );

            framesSentInWindow++;

            if (logFrames)
                Debug.Log($"[YoloWS] Sent frame {currentFrameId} {outW}x{outH} {jpeg.Length}B");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[YoloWS] SendFrame error: {e.Message}");
            waitingForResult = false;
            ScheduleReconnect();
        }
    }

    private float blackFrameLogCooldownAt;
    private bool IsFrameUseful(Texture2D tex)
    {
        // 采样一小撮像素就够了——我们只想区分"全黑/纯色" vs "真实画面"
        var px = tex.GetPixels32();
        int n = px.Length;
        if (n == 0) return false;

        int step = Mathf.Max(1, n / 256); // 最多 256 个采样点
        long r = 0, g = 0, b = 0;
        int sampled = 0;
        int minR = 255, maxR = 0;
        for (int i = 0; i < n; i += step)
        {
            r += px[i].r; g += px[i].g; b += px[i].b;
            if (px[i].r < minR) minR = px[i].r;
            if (px[i].r > maxR) maxR = px[i].r;
            sampled++;
        }
        float mr = r / (float)sampled, mg = g / (float)sampled, mb = b / (float)sampled;
        int rRange = maxR - minR;

        bool isBlack = mr < 5f && mg < 5f && mb < 5f;
        bool isUniform = rRange < 6;

        if (isBlack || isUniform)
        {
            if (Time.unscaledTime >= blackFrameLogCooldownAt)
            {
                blackFrameLogCooldownAt = Time.unscaledTime + 2f;
                string why = isBlack ? "BLACK" : "UNIFORM";
                Debug.LogWarning($"[YoloWS] Skipping {why} frame meanRGB=({mr:F0},{mg:F0},{mb:F0}) rRange={rRange} — Quest passthrough not delivering real content. Check HEADSET_CAMERA permission, exit Link/Air Link mode, close other camera-using apps.");
            }
            return false;
        }
        return true;
    }

    private void ApplyDetections(DetectionMessage msg)
    {
        if (bridge == null) return;
        if (msg.detections == null)
        {
            bridge.UpdateFromXYWH(new List<YoloToRealDetectionBridge.YoloDetectionXYWH>(), msg.image_w, msg.image_h);
            return;
        }

        var list = new List<YoloToRealDetectionBridge.YoloDetectionXYWH>(msg.detections.Length);
        for (int i = 0; i < msg.detections.Length; i++)
        {
            var d = msg.detections[i];
            list.Add(new YoloToRealDetectionBridge.YoloDetectionXYWH
            {
                label = d.label,
                confidence = d.confidence,
                x = d.x,
                y = d.y,
                width = d.w,
                height = d.h
            });
        }

        bridge.UpdateFromXYWH(list, msg.image_w, msg.image_h);
    }
}
