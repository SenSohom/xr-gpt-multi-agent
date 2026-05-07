using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Conversational chat UI (ChatGPT-style) for the Ask AI panel.
/// Displays scrollable message bubbles, accepts text input (TMP_InputField),
/// and optional voice input (Microphone -> WAV -> /stt/transcribe on server).
///
/// Setup: add this component to the chat panel root, then assign references
/// in the Inspector. Call OpenForObject() when the panel becomes visible.
/// </summary>
public class ChatConversationUI : MonoBehaviour
{
    // ── Inspector refs ────────────────────────────────────────────────────

    [Header("Conversation Scroll Area")]
    public ScrollRect scrollRect;
    [Tooltip("Content child of the ScrollRect — has VerticalLayoutGroup + ContentSizeFitter")]
    public RectTransform messageContainer;

    [Header("Input Row")]
    public TMP_InputField inputField;
    public Button sendButton;
    public Button voiceButton;
    public TMP_Text voiceButtonLabel;       // text on the voice button

    [Header("Quick Chips (optional — kept for fast questions)")]
    public Button introChipButton;
    public Button useChipButton;
    public Button whyChipButton;
    public Button nextChipButton;
    public Button compareChipButton;

    [Header("Bubble Visuals")]
    public Color userBubbleColor  = new Color(0.13f, 0.42f, 0.72f, 0.92f);
    public Color aiBubbleColor    = new Color(0.10f, 0.14f, 0.20f, 0.88f);
    public Color userTextColor    = Color.white;
    public Color aiTextColor      = new Color(0.88f, 0.94f, 1.00f, 1f);
    [Range(16, 48)]
    public int   bubbleFontSize   = 26;
    public float bubblePadding    = 14f;
    [Tooltip("Fraction of container width a bubble can occupy (0-1)")]
    [Range(0.4f, 0.95f)]
    public float bubbleMaxWidth   = 0.78f;

    [Header("VLM")]
    public VlmClient vlmClient;

    [Header("Voice Input")]
    public bool  enableVoiceInput  = true;
    public float maxRecordSeconds  = 10f;
    public string sttPath          = "/stt/transcribe";

    // ── Private state ─────────────────────────────────────────────────────

    private DetectedObjectData currentData;
    private Texture2D          frozenSnapshot;

    // Each entry: ("user"|"ai", displayed text)
    private readonly List<(string role, string text)> history = new();

    private bool      isRecording;
    private AudioClip recordingClip;
    private float     recordingStartTime;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    private void Awake()
    {
        if (sendButton  != null) sendButton.onClick.AddListener(OnSendClicked);
        if (voiceButton != null) voiceButton.onClick.AddListener(OnVoiceToggled);
        if (inputField  != null) inputField.onSubmit.AddListener(_ => OnSendClicked());

        if (introChipButton)   introChipButton.onClick.AddListener(()   => SendChip(c => c.promptAskIntro,   "What is it?"));
        if (useChipButton)     useChipButton.onClick.AddListener(()     => SendChip(c => c.promptAskUse,     "How to use?"));
        if (whyChipButton)     whyChipButton.onClick.AddListener(()     => SendChip(c => c.promptAskWhy,     "How it works?"));
        if (nextChipButton)    nextChipButton.onClick.AddListener(()    => SendChip(c => c.promptAskNext,    "Is it safe?"));
        if (compareChipButton) compareChipButton.onClick.AddListener(() => SendChip(c => c.promptAskCompare, "Fun fact"));

        if (voiceButton != null) voiceButton.gameObject.SetActive(enableVoiceInput);
    }

    private void Update()
    {
        if (isRecording && Time.unscaledTime - recordingStartTime >= maxRecordSeconds)
            StopAndTranscribe();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Called by ObjectGuidePanelUI when the chat panel is shown.</summary>
    public void OpenForObject(DetectedObjectData data, Texture2D snapshot)
    {
        currentData    = data;
        frozenSnapshot = snapshot;
        ClearHistory();
        AppendBubble("ai",
            $"Hi! I'm looking at \"{data.label}\". Ask me anything about it.\n\n" +
            "• Tap a quick question below, OR\n" +
            "• Tap the mic 🎤 and speak, OR\n" +
            "• Tap the input box to type with the system keyboard, then press Send.");
    }

    public void ClearHistory()
    {
        history.Clear();
        if (messageContainer == null) return;
        for (int i = messageContainer.childCount - 1; i >= 0; i--)
            Destroy(messageContainer.GetChild(i).gameObject);
    }

    // ── Send logic ────────────────────────────────────────────────────────

    private void OnSendClicked()
    {
        if (inputField == null) return;
        string text = inputField.text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        inputField.text = "";
        // Use the user's raw text as both the display label and the VLM prompt
        SendMessage(text, text);
    }

    private void SendChip(Func<VlmClient, string> getPrompt, string displayLabel)
    {
        string prompt = vlmClient != null ? getPrompt(vlmClient) : displayLabel;
        SendMessage(displayLabel, prompt);
    }

    private void SendMessage(string displayText, string vlmPrompt)
    {
        AppendBubble("user", displayText);
        var thinkingBubble = AppendBubble("ai", "Thinking…");

        string fullPrompt = BuildContextPrompt(vlmPrompt);

        if (vlmClient != null && frozenSnapshot != null)
        {
            vlmClient.Ask(frozenSnapshot, currentData?.label ?? "", fullPrompt, (answer, ok) =>
            {
                string reply = ok ? answer
                                  : "AI server unavailable — make sure the server is running.";
                SetBubbleText(thinkingBubble, reply);
                history.Add(("user", displayText));
                history.Add(("ai",   reply));
                ScrollToBottom();
            });
        }
        else
        {
            SetBubbleText(thinkingBubble, "VlmClient not assigned or no snapshot available.");
        }
    }

    // Build prompt that includes the last few turns as context
    private string BuildContextPrompt(string newQuestion)
    {
        if (history.Count == 0) return newQuestion;

        var sb = new System.Text.StringBuilder();
        int start = Math.Max(0, history.Count - 6); // last 3 exchanges
        for (int i = start; i < history.Count; i++)
        {
            var (role, text) = history[i];
            sb.AppendLine(role == "user" ? $"User: {text}" : $"AI: {text}");
        }
        sb.AppendLine($"User: {newQuestion}");
        sb.Append("AI:");
        return sb.ToString();
    }

    // ── Bubble creation ───────────────────────────────────────────────────

    /// <summary>Appends a message bubble and returns the bubble background GameObject.</summary>
    private GameObject AppendBubble(string role, string text)
    {
        if (messageContainer == null)
        {
            Debug.LogWarning("[ChatConversationUI] messageContainer not assigned.");
            return null;
        }

        bool isUser = role == "user";

        // ── Row (full width) ──
        var row = new GameObject(isUser ? "Row_User" : "Row_AI", typeof(RectTransform));
        row.transform.SetParent(messageContainer, false);

        var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.childControlWidth    = true;
        rowLayout.childControlHeight   = true;
        rowLayout.childForceExpandWidth  = true;
        rowLayout.childForceExpandHeight = false;

        // Push bubble to the right for user, left for AI
        if (isUser)
            rowLayout.padding = new RectOffset(80, 8, 4, 4);
        else
            rowLayout.padding = new RectOffset(8, 80, 4, 4);

        var rowFitter = row.AddComponent<ContentSizeFitter>();
        rowFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // ── Bubble background ──
        var bubble = new GameObject("Bubble", typeof(RectTransform));
        bubble.transform.SetParent(row.transform, false);

        var img = bubble.AddComponent<Image>();
        img.color         = isUser ? userBubbleColor : aiBubbleColor;
        img.raycastTarget = false;

        var bubbleLayout = bubble.AddComponent<VerticalLayoutGroup>();
        int pad = (int)bubblePadding;
        bubbleLayout.padding             = new RectOffset(pad, pad, (int)(pad * 0.65f), (int)(pad * 0.65f));
        bubbleLayout.childControlWidth   = true;
        bubbleLayout.childControlHeight  = true;
        bubbleLayout.childForceExpandWidth  = true;
        bubbleLayout.childForceExpandHeight = false;

        var bubbleFitter = bubble.AddComponent<ContentSizeFitter>();
        bubbleFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        bubbleFitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        // ── Text ──
        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(bubble.transform, false);

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.text              = text;
        tmp.fontSize          = bubbleFontSize;
        tmp.color             = isUser ? userTextColor : aiTextColor;
        tmp.enableWordWrapping = true;
        tmp.raycastTarget     = false;

        var textFitter = textGo.AddComponent<ContentSizeFitter>();
        textFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollToBottom();
        return bubble;
    }

    private static void SetBubbleText(GameObject bubble, string text)
    {
        if (bubble == null) return;
        var tmp = bubble.GetComponentInChildren<TMP_Text>();
        if (tmp != null) tmp.text = text;
    }

    private void ScrollToBottom() => StartCoroutine(ScrollNextFrame());

    private IEnumerator ScrollNextFrame()
    {
        yield return null; // wait one frame for layout rebuild
        yield return null;
        if (scrollRect != null)
            scrollRect.verticalNormalizedPosition = 0f;
    }

    // ── Voice input ───────────────────────────────────────────────────────

    private void OnVoiceToggled()
    {
        if (!isRecording) StartCoroutine(EnsureMicPermissionThenRecord());
        else              StopAndTranscribe();
    }

    private IEnumerator EnsureMicPermissionThenRecord()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string MIC_PERM = "android.permission.RECORD_AUDIO";
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(MIC_PERM))
        {
            UnityEngine.Android.Permission.RequestUserPermission(MIC_PERM);
            // Wait up to 10 seconds for the user to accept the system dialog
            float deadline = Time.unscaledTime + 10f;
            while (Time.unscaledTime < deadline &&
                   !UnityEngine.Android.Permission.HasUserAuthorizedPermission(MIC_PERM))
            {
                yield return null;
            }
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(MIC_PERM))
            {
                AppendBubble("ai", "Microphone permission denied. Please grant it in Quest Settings → Apps → Permissions to use voice input.");
                yield break;
            }
        }
#endif
        StartRecording();
        yield break;
    }

    private void StartRecording()
    {
        string[] mics = Microphone.devices;
        if (mics == null || mics.Length == 0)
        {
            Debug.LogWarning("[ChatConversationUI] No microphone found.");
            AppendBubble("ai", "No microphone detected on this device.");
            return;
        }

        recordingClip      = Microphone.Start(null, false, (int)maxRecordSeconds, 16000);
        recordingStartTime = Time.unscaledTime;
        isRecording        = true;

        if (voiceButtonLabel != null) voiceButtonLabel.text = "■ Stop";
        if (inputField != null)       inputField.text        = "Listening… speak now (tap mic again to stop)";
    }

    private void StopAndTranscribe()
    {
        if (!isRecording) return;

        int pos = Microphone.GetPosition(null);
        Microphone.End(null);
        isRecording = false;

        if (voiceButtonLabel != null) voiceButtonLabel.text = "🎤";
        if (inputField != null) inputField.text = "";

        if (pos <= 0 || recordingClip == null)
        {
            AppendBubble("ai", "Didn't catch that — try again and speak after the mic turns red.");
            return;
        }

        float[] samples = new float[pos * recordingClip.channels];
        recordingClip.GetData(samples, 0);
        byte[] wav = BuildWav(samples, recordingClip.channels, recordingClip.frequency);

        StartCoroutine(TranscribeRoutine(wav));
    }

    private IEnumerator TranscribeRoutine(byte[] wav)
    {
        if (vlmClient == null) yield break;
        if (inputField != null) inputField.text = "Transcribing…";

        string url  = vlmClient.serverBaseUrl.TrimEnd('/') + sttPath;
        string b64  = Convert.ToBase64String(wav);
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"audio_b64\":\"" + b64 + "\"}");

        using var req = new UnityEngine.Networking.UnityWebRequest(url, "POST");
        req.uploadHandler   = new UnityEngine.Networking.UploadHandlerRaw(body);
        req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 20;

        yield return req.SendWebRequest();

        if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            try
            {
                var resp = JsonUtility.FromJson<SttResponse>(req.downloadHandler.text);
                string transcript = resp != null ? (resp.transcript ?? "").Trim() : "";
                if (inputField != null) inputField.text = "";
                if (!string.IsNullOrEmpty(transcript))
                {
                    // Auto-send the recognised question — typing in VR is painful.
                    SendMessage(transcript, transcript);
                }
                else
                {
                    AppendBubble("ai", "I heard silence. Try again, closer to the mic.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ChatConversationUI] STT parse error: " + e.Message);
                if (inputField != null) inputField.text = "";
                AppendBubble("ai", $"Voice transcription failed: {e.Message}");
            }
        }
        else
        {
            Debug.LogWarning("[ChatConversationUI] STT request failed: " + req.error);
            if (inputField != null) inputField.text = "";
            AppendBubble("ai", $"Couldn't reach the speech server ({req.error}). Make sure server.py is running.");
        }
    }

    [Serializable]
    private class SttResponse { public string transcript; }

    // PCM float[] → WAV byte[]
    private static byte[] BuildWav(float[] samples, int channels, int sampleRate)
    {
        int byteCount = samples.Length * 2;
        using var mem = new System.IO.MemoryStream(44 + byteCount);
        using var w   = new System.IO.BinaryWriter(mem);

        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + byteCount);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(byteCount);

        foreach (float s in samples)
            w.Write((short)(Mathf.Clamp(s, -1f, 1f) * 32767f));

        return mem.ToArray();
    }
}
