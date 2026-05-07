using System.Collections;
using UnityEngine;

public class HUDPanelAnimator : MonoBehaviour
{
    [Header("Initial State")]
    public bool hideOnStart = true;

    [Header("References")]
    public RectTransform rectTransform;
    public CanvasGroup canvasGroup;

    [Header("Animation")]
    public Vector2 hiddenOffset = new Vector2(40f, 0f);
    public float duration = 0.35f;
    [Tooltip("淡入时还可以放大一点点提升出现感")]
    public float scaleStart = 0.94f;

    private Vector2 shownPos;
    private Coroutine currentRoutine;

    // 当前是否处于"显示中"目标状态。重要:Show()/Hide() 会被调用方(如 RefreshList)
    // 每帧无脑重复调用 — 这个标志保证已经在目标状态时直接 no-op,不会反复重启动画
    // 把 CanvasGroup.interactable/blocksRaycasts 永久卡在 false。
    private bool isCurrentlyShown;

    private void Awake()
    {
        if (rectTransform == null)
            rectTransform = GetComponent<RectTransform>();

        if (canvasGroup == null)
            canvasGroup = GetComponent<CanvasGroup>();

        if (rectTransform != null)
            shownPos = rectTransform.anchoredPosition;
    }

    private void Start()
    {
        if (hideOnStart)
            SetHiddenImmediate();
        else
            SetShownImmediate();
    }

    public void SetHiddenImmediate()
    {
        isCurrentlyShown = false;

        if (currentRoutine != null)
        {
            StopCoroutine(currentRoutine);
            currentRoutine = null;
        }

        if (rectTransform != null)
        {
            rectTransform.anchoredPosition = shownPos + hiddenOffset;
            rectTransform.localScale = Vector3.one * scaleStart;
        }

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }
    }

    public void SetShownImmediate()
    {
        isCurrentlyShown = true;

        if (currentRoutine != null)
        {
            StopCoroutine(currentRoutine);
            currentRoutine = null;
        }

        if (rectTransform != null)
        {
            rectTransform.anchoredPosition = shownPos;
            rectTransform.localScale = Vector3.one;
        }

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;
        }
    }

    public void Show()
    {
        if (rectTransform == null || canvasGroup == null)
            return;

        // 已经在显示状态 → 不重启动画。否则每帧调用会让 interactable / blocksRaycasts
        // 永远卡在 AnimatePanel 开头的 false 上,玩家点不到任何按钮。
        if (isCurrentlyShown)
            return;

        isCurrentlyShown = true;

        if (currentRoutine != null)
            StopCoroutine(currentRoutine);

        gameObject.SetActive(true);

        currentRoutine = StartCoroutine(AnimatePanel(
            rectTransform.anchoredPosition,
            shownPos,
            canvasGroup.alpha,
            1f,
            true
        ));
    }

    public void Hide()
    {
        if (rectTransform == null || canvasGroup == null)
            return;

        if (!isCurrentlyShown)
            return;

        isCurrentlyShown = false;

        if (currentRoutine != null)
            StopCoroutine(currentRoutine);

        currentRoutine = StartCoroutine(AnimatePanel(
            rectTransform.anchoredPosition,
            shownPos + hiddenOffset,
            canvasGroup.alpha,
            0f,
            false
        ));
    }

    private IEnumerator AnimatePanel(
        Vector2 fromPos,
        Vector2 toPos,
        float fromAlpha,
        float toAlpha,
        bool enableInteractionAtEnd
    )
    {
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        float fromScale = enableInteractionAtEnd ? scaleStart : 1f;
        float toScale = enableInteractionAtEnd ? 1f : scaleStart;

        float t = 0f;

        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float p = Mathf.Clamp01(t / duration);
            p = Mathf.SmoothStep(0f, 1f, p);

            rectTransform.anchoredPosition = Vector2.Lerp(fromPos, toPos, p);
            canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, p);
            float s = Mathf.Lerp(fromScale, toScale, p);
            rectTransform.localScale = new Vector3(s, s, s);

            yield return null;
        }

        rectTransform.anchoredPosition = toPos;
        canvasGroup.alpha = toAlpha;
        rectTransform.localScale = Vector3.one * toScale;
        canvasGroup.interactable = enableInteractionAtEnd;
        canvasGroup.blocksRaycasts = enableInteractionAtEnd;

        if (!enableInteractionAtEnd)
            gameObject.SetActive(false);

        currentRoutine = null;
    }
}