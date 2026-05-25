using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// HTTP 客户端,把 image + prompt 发到本地 Python 服务器(moondream2 后端),返回文本。
/// 仅在用户交互(Open / More Info / Chat)时调用,跟 YOLO 流完全解耦。
/// </summary>
public class VlmClient : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("Python 服务器地址。运行时会被 Assets/Resources/NetworkConfig.asset 覆盖；这里只是兜底默认值。")]
    public string serverBaseUrl = "http://192.168.1.182:8766";

    [Tooltip("超时秒数。moondream2 首次冷启动可能要几十秒，CPU 推理一次也要 5~15s。给到 90s。")]
    public int timeoutSeconds = 90;

    [Header("Default Prompts")]
    public string promptDescribe = "Identify and describe this object in one short sentence (max 20 words).";
    public string promptMoreInfo = "In 2-3 sentences, explain what this object is and what it is typically used for.";
    public string promptAskIntro = "In 1-2 sentences, identify this object and describe what it is for someone seeing it for the first time.";
    public string promptAskUse = "Give 2-3 clear, practical steps or tips for using this object effectively.";
    public string promptAskWhy = "Explain simply how this object works or functions. Keep it to 1-2 sentences.";
    public string promptAskNext = "Are there any important safety warnings, precautions, or handling tips for this object? Answer in 1-2 sentences.";
    public string promptAskCompare = "Share one surprising or little-known fact about this object. Keep it to 1-2 sentences.";

    [Header("Debug")]
    public bool logRequests = true;

    private void Awake()
    {
        // Centralised IP config — single source of truth across the project.
        // Falls back silently to inspector value if Resources/NetworkConfig.asset is missing.
        var cfg = NetworkConfig.Load();
        if (cfg != null)
        {
            string newUrl = cfg.HttpBaseUrl;
            if (serverBaseUrl != newUrl)
            {
                Debug.Log($"[VlmClient] serverBaseUrl overridden by NetworkConfig: '{serverBaseUrl}' → '{newUrl}'");
                serverBaseUrl = newUrl;
            }
        }
    }

    [Serializable]
    private class VlmRequest
    {
        public string image_b64;
        public string label;
        public string prompt;
        public int max_new_tokens = 96;
        public string agent_task;
        public bool enable_critic;
    }

    [Serializable]
    private class VlmResponse
    {
        public string answer;
        public float latency_ms;
        public string error;
        public string backend;
        public string agent;
    }

    /// <summary>
    /// 异步请求 VLM,完成时回调 onDone(answer)。失败时 answer 是错误消息。
    /// </summary>
    public Coroutine Ask(Texture2D snapshot, string label, string prompt, Action<string, bool> onDone)
    {
        return AskAgent(snapshot, label, prompt, "", false, onDone);
    }

    public Coroutine AskAgent(
        Texture2D snapshot,
        string label,
        string prompt,
        string agentTask,
        bool enableCritic,
        Action<string, bool> onDone)
    {
        if (snapshot == null)
        {
            onDone?.Invoke("(no snapshot)", false);
            return null;
        }
        if (string.IsNullOrEmpty(prompt))
        {
            onDone?.Invoke("(no prompt)", false);
            return null;
        }

        return StartCoroutine(AskRoutine(snapshot, label, prompt, agentTask, enableCritic, onDone));
    }

    private IEnumerator AskRoutine(
        Texture2D snapshot,
        string label,
        string prompt,
        string agentTask,
        bool enableCritic,
        Action<string, bool> onDone)
    {
        byte[] jpeg = SnapshotCapture.EncodeToJpeg(snapshot, 80);
        if (jpeg == null || jpeg.Length == 0)
        {
            onDone?.Invoke("(snapshot encode failed)", false);
            yield break;
        }

        string b64 = Convert.ToBase64String(jpeg);

        VlmRequest req = new VlmRequest
        {
            image_b64 = b64,
            label = label ?? "",
            prompt = prompt,
            agent_task = agentTask ?? "",
            enable_critic = enableCritic
        };

        string json = JsonUtility.ToJson(req);
        byte[] body = Encoding.UTF8.GetBytes(json);

        string url = serverBaseUrl.TrimEnd('/') + "/vlm/ask";

        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = timeoutSeconds;

            if (logRequests)
                Debug.Log($"[VlmClient] POST {url} label={label} agent={agentTask} critic={enableCritic} jpegBytes={jpeg.Length}");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                long code = www.responseCode;
                string respBody = www.downloadHandler != null ? www.downloadHandler.text : "";
                string err = $"VLM request failed: {www.error} (HTTP {code}, url={url}) body={respBody}";
                Debug.LogWarning(err);
                onDone?.Invoke($"AI server unreachable ({www.error}). Check that server.py is running on {serverBaseUrl}.", false);
                yield break;
            }

            string respText = www.downloadHandler.text;
            VlmResponse resp = null;
            try
            {
                resp = JsonUtility.FromJson<VlmResponse>(respText);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VLM response parse failed: {e.Message} body={respText}");
                onDone?.Invoke("(server response parse failed)", false);
                yield break;
            }

            if (resp == null)
            {
                onDone?.Invoke("(empty server response)", false);
                yield break;
            }
            if (!string.IsNullOrEmpty(resp.error))
            {
                onDone?.Invoke($"(server error: {resp.error})", false);
                yield break;
            }

            if (logRequests)
                Debug.Log($"[VlmClient] backend={resp.backend} agent={resp.agent} answer={resp.answer} latency={resp.latency_ms:F0}ms");

            onDone?.Invoke(resp.answer ?? "", true);
        }
    }
}
