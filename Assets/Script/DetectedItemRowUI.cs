using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Detected Items 列表中的单行 UI(预制可用,这里保持兼容,但实际项目用 DetectedItemsListPanelUI 的固定 4 槽位)。
/// </summary>
public class DetectedItemRowUI : MonoBehaviour
{
    [Header("References")]
    public Button button;
    public TMP_Text indexText;
    public TMP_Text labelText;
    public RawImage snapshotImage;
    public Image bgImage;
    public CanvasGroup glowGroup;
    public CanvasGroup staleGroup;

    [Header("Colors")]
    public Color normalBG = new Color(0.08f, 0.18f, 0.28f, 0.35f);
    public Color selectedBG = new Color(0.15f, 0.45f, 0.60f, 0.75f);

    public Color normalText = new Color(0.85f, 0.97f, 1f, 0.95f);
    public Color selectedText = new Color(1f, 1f, 1f, 1f);

    private void Awake()
    {
        if (button == null)
            button = GetComponent<Button>();
    }

    public void Setup(int displayIndex, string itemLabel, Texture2D snapshot, bool isSelected, bool stale, Action onClicked)
    {
        if (indexText != null)
            indexText.text = $"{displayIndex}.";

        if (labelText != null)
            labelText.text = itemLabel;

        if (snapshotImage != null)
        {
            snapshotImage.texture = snapshot;
            snapshotImage.enabled = (snapshot != null);
        }

        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => onClicked?.Invoke());
        }

        SetSelected(isSelected);
        SetStale(stale);
    }

    public void SetSelected(bool isSelected)
    {
        if (bgImage != null)
            bgImage.color = isSelected ? selectedBG : normalBG;

        if (indexText != null)
            indexText.color = isSelected ? selectedText : normalText;

        if (labelText != null)
            labelText.color = isSelected ? selectedText : normalText;

        if (glowGroup != null)
            glowGroup.alpha = isSelected ? 1f : 0f;
    }

    public void SetStale(bool stale)
    {
        if (staleGroup != null)
            staleGroup.alpha = stale ? 0.45f : 1f;
    }
}
