using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class DetectionBoxUI : MonoBehaviour
{
    [Header("References")]
    public RectTransform rectTransform;
    public Image[] cornerLines;
    public Image chipBG;
    public TMP_Text labelText;
    public CanvasGroup glowGroup;

    [Header("Colors")]
    public Color normalColor = new Color(0.37f, 0.91f, 1f, 0.85f);
    public Color hoverColor = new Color(0.60f, 0.98f, 1f, 1f);
    public Color selectedColor = new Color(0.49f, 1f, 0.88f, 1f);

    [Header("Scale")]
    public float normalScale = 1f;
    public float hoverScale = 1.03f;
    public float selectedScale = 1.06f;

    private void Awake()
    {
        if (rectTransform == null)
            rectTransform = GetComponent<RectTransform>();
    }

    public void Setup(DetectedObjectData data)
    {
        if (labelText != null)
            labelText.text = data.label;
    }

    public void SetRect(Rect r)
    {
        if (rectTransform == null) return;

        rectTransform.anchorMin = new Vector2(0f, 0f);
        rectTransform.anchorMax = new Vector2(0f, 0f);
        rectTransform.pivot = new Vector2(0f, 0f);

        rectTransform.anchoredPosition = new Vector2(r.x, r.y);
        rectTransform.sizeDelta = new Vector2(r.width, r.height);
    }

    public void SetState(bool hovered, bool selected)
    {
        Color targetColor = normalColor;
        float targetScale = normalScale;
        float glowAlpha = 0.02f;

        if (selected)
        {
            targetColor = selectedColor;
            targetScale = selectedScale;
            glowAlpha = 0.18f;
        }
        else if (hovered)
        {
            targetColor = hoverColor;
            targetScale = hoverScale;
            glowAlpha = 0.10f;
        }

        if (cornerLines != null)
        {
            foreach (var line in cornerLines)
            {
                if (line != null)
                    line.color = targetColor;
            }
        }

        if (chipBG != null)
        {
            Color c = targetColor;
            c.a = 0.22f;
            chipBG.color = c;
        }

        if (labelText != null)
            labelText.color = targetColor;

        if (rectTransform != null)
            rectTransform.localScale = Vector3.one * targetScale;

        if (glowGroup != null)
            glowGroup.alpha = glowAlpha;
    }
}