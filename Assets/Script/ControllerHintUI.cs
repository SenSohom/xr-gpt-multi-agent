using TMPro;
using UnityEngine;

/// <summary>
/// 手柄指到 detection box 时弹出的提示("A: Open / B: Cancel")。
/// 由 MRHUDController 控制显隐和位置。
/// </summary>
public class ControllerHintUI : MonoBehaviour
{
    [Header("References")]
    public RectTransform rectTransform;
    public CanvasGroup canvasGroup;
    public TMP_Text hintText;

    [Header("Default Hint Text")]
    [TextArea(1, 3)]
    public string defaultHint = "<b>A</b> / Trigger: Open    <b>B</b>: Cancel";

    [Header("Animation")]
    public float fadeDuration = 0.12f;
    public Vector2 hintOffset = new Vector2(0f, 32f);

    private float currentAlpha;
    private float targetAlpha;

    private void Awake()
    {
        if (rectTransform == null)
            rectTransform = GetComponent<RectTransform>();

        if (canvasGroup == null)
            canvasGroup = GetComponent<CanvasGroup>();

        SetHintText(defaultHint);
        Hide();
    }

    public void SetHintText(string text)
    {
        if (hintText != null)
            hintText.text = text;
    }

    public void Show()
    {
        targetAlpha = 1f;
        gameObject.SetActive(true);
    }

    public void Hide()
    {
        targetAlpha = 0f;
    }

    /// <summary>
    /// 把提示放在 detection box 顶部正上方。
    /// pos 应已经是 boxRect 顶部中点(以 detection overlay root 为参考)。
    /// </summary>
    public void PositionAbove(Vector2 anchorPos)
    {
        if (rectTransform == null) return;
        rectTransform.anchoredPosition = anchorPos + hintOffset;
    }

    private void Update()
    {
        if (canvasGroup == null) return;

        if (Mathf.Approximately(currentAlpha, targetAlpha))
        {
            if (currentAlpha <= 0.001f && gameObject.activeSelf)
                gameObject.SetActive(false);
            return;
        }

        float step = (fadeDuration <= 0f) ? 1f : Time.unscaledDeltaTime / fadeDuration;
        currentAlpha = Mathf.MoveTowards(currentAlpha, targetAlpha, step);
        canvasGroup.alpha = currentAlpha;
    }
}
