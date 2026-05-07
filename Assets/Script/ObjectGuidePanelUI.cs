using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Right Info Panel(选中物品后弹出的主面板)。
/// 显示物品名称、快照、AI 简短描述,以及 More Info / Chat / Close 三个按钮。
/// VLM 是异步的,期间显示 loading 文案,返回后由 MRHUDController 调 ApplyShortDescription 填入。
/// </summary>
public class ObjectGuidePanelUI : MonoBehaviour
{
    [Header("Main Panel")]
    public TMP_Text objectPillText;
    public TMP_Text titleText;
    public TMP_Text summaryText;       // VLM short description 显示在这里
    public TMP_Text detailText;        // More Info 后填这里
    public TMP_Text suggestionText;
    public RawImage snapshotImage;     // 选中那一刻的物体快照

    [Header("Main Panel Buttons")]
    public Button moreInfoButton;
    public Button chatButton;
    public Button closeButton;

    [Header("Chat Panel — Conversation UI (assign ChatConversationUI component)")]
    [Tooltip("New conversational chat UI. When assigned, the old chip/answer fields below are ignored.")]
    public ChatConversationUI conversationUI;
    public Button chatCloseButton;

    [Header("Chat Panel — Legacy (used only if conversationUI is null)")]
    public TMP_Text chatAnswerText;
    public Button introChipButton;
    public Button useChipButton;
    public Button whyChipButton;
    public Button nextChipButton;
    public Button compareChipButton;

    [Header("Animators")]
    public HUDPanelAnimator mainPanelAnimator;
    public HUDPanelAnimator chatPanelAnimator;

    [Header("Loading Text")]
    public string loadingDescribeText = "Generating description…";
    public string loadingMoreInfoText = "Thinking…";

    [Header("VLM (optional)")]
    public VlmClient vlmClient;

    private DetectedObjectData currentData;

    // 选中那一刻的 snapshot 拷贝。Right Info Panel 打开期间不会因为 MRHUDController 刷新而变化。
    private Texture2D frozenSnapshot;

    /// <summary>
    /// 当 Right Info Panel + Chat Panel 全部关闭时触发。
    /// MRHUDController 用它来恢复 YOLO 实时检测。
    /// </summary>
    public System.Action onClosed;

    private void Awake()
    {
        if (moreInfoButton != null) moreInfoButton.onClick.AddListener(OnMoreInfoClicked);
        if (chatButton     != null) chatButton.onClick.AddListener(OnAskAIClicked);
        if (closeButton    != null) closeButton.onClick.AddListener(HideAll);
        if (chatCloseButton != null) chatCloseButton.onClick.AddListener(HideChatOnly);

        // Legacy chip buttons — only registered when conversationUI is not assigned.
        // When conversationUI is assigned it handles its own chip listeners internally.
        if (conversationUI == null)
        {
            if (introChipButton   != null) introChipButton.onClick.AddListener(OnIntroChipClicked);
            if (useChipButton     != null) useChipButton.onClick.AddListener(OnUseChipClicked);
            if (whyChipButton     != null) whyChipButton.onClick.AddListener(OnWhyChipClicked);
            if (nextChipButton    != null) nextChipButton.onClick.AddListener(OnNextChipClicked);
            if (compareChipButton != null) compareChipButton.onClick.AddListener(OnCompareChipClicked);
        }
    }

    private void Start()
    {
        if (mainPanelAnimator != null && mainPanelAnimator.hideOnStart)
            mainPanelAnimator.SetHiddenImmediate();

        if (chatPanelAnimator != null && chatPanelAnimator.hideOnStart)
            chatPanelAnimator.SetHiddenImmediate();
    }

    public void ShowObject(DetectedObjectData data)
    {
        Debug.Log($"[RightInfoPanel] ShowObject: label={data?.label}, mainAnim={(mainPanelAnimator != null ? mainPanelAnimator.name : "NULL")}");
        currentData = data;

        if (objectPillText != null) objectPillText.text = "Selected Item";
        if (titleText != null) titleText.text = data.label;

        // summaryText 会被 VLM 异步替换;这里先显示 fallback 或 loading
        if (summaryText != null)
        {
            if (!string.IsNullOrEmpty(data.vlmShortDescription))
                summaryText.text = data.vlmShortDescription;
            else if (!string.IsNullOrEmpty(data.summary))
                summaryText.text = data.summary;
            else
                summaryText.text = loadingDescribeText;
        }

        if (detailText != null) detailText.text = "Press More Info to learn more, or Chat to ask AI.";
        if (suggestionText != null) suggestionText.text = data.suggestionText ?? "";

        // 冻结快照:复制到独立 Texture2D,在面板期间一直显示这一张,
        // 即使 MRHUDController 后来刷新了 lockedDetections 也不受影响。
        ReleaseFrozenSnapshot();
        frozenSnapshot = CloneTexture(data.snapshot);
        if (snapshotImage != null)
        {
            snapshotImage.texture = frozenSnapshot;
            snapshotImage.enabled = (frozenSnapshot != null);
        }

        if (mainPanelAnimator != null)
            mainPanelAnimator.Show();

        if (chatPanelAnimator != null)
            chatPanelAnimator.Hide();
    }

    private static Texture2D CloneTexture(Texture2D src)
    {
        if (src == null) return null;
        // 使用 Graphics.Blit 在 GPU 上复制,避免再次走 ReadPixels 的 CPU 拷贝。
        var copy = new Texture2D(src.width, src.height, TextureFormat.RGB24, false);
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
        copy.Apply(false, false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return copy;
    }

    private void ReleaseFrozenSnapshot()
    {
        if (frozenSnapshot != null)
        {
            if (snapshotImage != null && snapshotImage.texture == frozenSnapshot)
                snapshotImage.texture = null;
            Destroy(frozenSnapshot);
            frozenSnapshot = null;
        }
    }

    private void OnDestroy()
    {
        ReleaseFrozenSnapshot();
    }

    /// <summary>
    /// VLM short description 异步返回时由 MRHUDController 调用。
    /// </summary>
    public void ApplyShortDescription(string text)
    {
        if (summaryText != null && currentData != null)
            summaryText.text = text;
    }

    public void HideAll()
    {
        currentData = null;
        ReleaseFrozenSnapshot();

        if (mainPanelAnimator != null)
            mainPanelAnimator.Hide();

        if (chatPanelAnimator != null)
            chatPanelAnimator.Hide();

        onClosed?.Invoke();
    }

    /// <summary>
    /// 关闭 Chat,返回 Right Info Panel(Main)。
    /// </summary>
    private void HideChatOnly()
    {
        if (chatPanelAnimator != null)
            chatPanelAnimator.Hide();

        // 重新展示 Main panel
        if (mainPanelAnimator != null)
            mainPanelAnimator.Show();
    }

    private void OnMoreInfoClicked()
    {
        if (currentData == null) return;

        if (!string.IsNullOrEmpty(currentData.vlmMoreInfo))
        {
            if (detailText != null) detailText.text = currentData.vlmMoreInfo;
            return;
        }

        if (detailText != null) detailText.text = loadingMoreInfoText;

        if (vlmClient != null && frozenSnapshot != null)
        {
            var captured = currentData;
            vlmClient.Ask(frozenSnapshot, captured.label, vlmClient.promptMoreInfo, (answer, ok) =>
            {
                if (currentData != captured) return; // 用户已切换选中
                if (ok)
                {
                    captured.vlmMoreInfo = answer;
                    if (detailText != null) detailText.text = answer;
                }
                else if (detailText != null)
                {
                    // 显示错误本身，避免回退到空白让用户以为一直在 Thinking。
                    string fallback = !string.IsNullOrEmpty(currentData.moreInfoText)
                        ? currentData.moreInfoText
                        : answer; // answer 现在带可读错误信息
                    detailText.text = fallback;
                }
            });
        }
        else
        {
            if (detailText != null)
                detailText.text = !string.IsNullOrEmpty(currentData.moreInfoText)
                    ? currentData.moreInfoText
                    : (vlmClient == null
                        ? "VlmClient not assigned in inspector."
                        : "No snapshot available — try selecting the object again.");
        }
    }

    private void OnAskAIClicked()
    {
        if (currentData == null) return;

        // 关闭 Main panel，打开 Chat panel（两者互斥）
        if (mainPanelAnimator != null) mainPanelAnimator.Hide();
        if (chatPanelAnimator != null) chatPanelAnimator.Show();

        // ── 新版：conversational UI ──────────────────────────────────────
        if (conversationUI != null)
        {
            conversationUI.OpenForObject(currentData, frozenSnapshot);
            return;
        }

        // ── 旧版 fallback（conversationUI 未赋值时） ─────────────────────
        if (chatAnswerText != null)
            chatAnswerText.text = string.IsNullOrEmpty(currentData.askIntroText)
                ? $"Ask me about \"{currentData.label}\" — tap a question below to get started."
                : currentData.askIntroText;

        if (vlmClient != null && frozenSnapshot != null)
        {
            var captured = currentData;
            vlmClient.Ask(frozenSnapshot, captured.label, vlmClient.promptAskIntro, (answer, ok) =>
            {
                if (currentData != captured) return;
                if (ok && chatAnswerText != null) chatAnswerText.text = answer;
            });
        }
    }

    private void OnIntroChipClicked() => RunChipPrompt(p => p.promptAskIntro, currentData?.askIntroText);
    private void OnUseChipClicked() => RunChipPrompt(p => p.promptAskUse, currentData?.askUseText);
    private void OnWhyChipClicked() => RunChipPrompt(p => p.promptAskWhy, currentData?.askWhyText);
    private void OnNextChipClicked() => RunChipPrompt(p => p.promptAskNext, currentData?.askNextText);
    private void OnCompareChipClicked() => RunChipPrompt(p => p.promptAskCompare, currentData?.askCompareText);

    private void RunChipPrompt(System.Func<VlmClient, string> getPrompt, string fallback)
    {
        if (currentData == null) return;

        if (chatAnswerText != null) chatAnswerText.text = "Thinking…";

        if (vlmClient != null && frozenSnapshot != null)
        {
            var captured = currentData;
            string prompt = getPrompt(vlmClient);
            vlmClient.Ask(frozenSnapshot, captured.label, prompt, (answer, ok) =>
            {
                if (currentData != captured) return;
                if (chatAnswerText != null)
                    chatAnswerText.text = ok ? answer : (fallback ?? "AI server unavailable. Make sure the server is running.");
            });
        }
        else if (chatAnswerText != null)
        {
            chatAnswerText.text = fallback ?? "AI server unavailable. Make sure the server is running.";
        }
    }
}
