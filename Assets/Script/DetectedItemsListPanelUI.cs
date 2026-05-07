using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 管理 "Detected Items" 列表面板。
/// 列表内容由 MRHUDController 刷新。
/// item button 点击后会回调 MRHUDController.OnDetectedItemClicked(index)，
/// 然后打开 Right Info Panel。
/// </summary>
public class DetectedItemsListPanelUI : MonoBehaviour
{
    [Header("Panel")]
    public GameObject panelRoot;
    public TMP_Text titleText;
    public TMP_Text emptyText;
    public HUDPanelAnimator panelAnimator;

    [Header("Fixed Buttons (Exactly 4 Slots)")]
    public Button[] itemButtons = new Button[4];
    public TMP_Text[] itemButtonTexts = new TMP_Text[4];
    public RawImage[] itemButtonImages = new RawImage[4];
    public CanvasGroup[] itemButtonStaleGroups = new CanvasGroup[4];

    [Header("Visual State")]
    public Color normalButtonColor = new Color(0.08f, 0.18f, 0.28f, 0.35f);
    public Color hoveredButtonColor = new Color(0.20f, 0.50f, 0.80f, 0.60f);
    public Color pressedButtonColor = new Color(0.30f, 0.65f, 1.00f, 0.85f);
    public Color selectedButtonColor = new Color(0.15f, 0.45f, 0.60f, 0.75f);

    public Color normalTextColor = new Color(0.85f, 0.97f, 1f, 0.95f);
    public Color selectedTextColor = new Color(1f, 1f, 1f, 1f);

    [Tooltip("stale(已不在视野)的项的整体透明度")]
    [Range(0.1f, 1f)]
    public float staleAlpha = 0.45f;

    [Header("Behavior")]
    public int maxDisplayedItems = 4;
    public bool hideUnusedButtons = true;

    [Header("Debug")]
    public bool logItemClicks = true;

    private readonly List<DetectedObjectData> displayedDetections = new List<DetectedObjectData>();

    // 当前 MRHUDController 传进来的点击回调
    private Action<int> currentOnItemClicked;

    // 防止 PointerClick 和 Button.onClick 在同一帧重复触发
    private int lastClickedIndex = -1;
    private float lastClickedTime = -999f;

    // hover / press 状态由 EventTrigger 写入，不依赖 Button.ColorTint
    private int hoveredButtonIndex = -1;
    private int pressedButtonIndex = -1;
    private int currentSelectedIndex = -1;

    private void Awake()
    {
        if (panelRoot == null)
            panelRoot = gameObject;

        // Detected Items Panel 是常驻 UI，不应该一开始被 HUDPanelAnimator 隐藏。
        // 否则 CanvasGroup.interactable / blocksRaycasts 可能会被设成 false，导致点不到按钮。
        if (panelAnimator != null)
            panelAnimator.hideOnStart = false;

        PrepareChildRaycastTargets();
        RegisterButtonPointerEvents();
    }

    private void Start()
    {
        // 兜底：确保面板一开始就是可交互状态
        if (panelAnimator != null)
        {
            panelAnimator.SetShownImmediate();
        }
        else
        {
            CanvasGroup cg = GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.alpha = 1f;
                cg.interactable = true;
                cg.blocksRaycasts = true;
            }
        }
    }

    /// <summary>
    /// 让文字和图片不要抢 UI Raycast。
    /// 射线应该优先打到 Button 自己，而不是 Text / Snapshot RawImage。
    /// </summary>
    private void PrepareChildRaycastTargets()
    {
        if (itemButtonTexts != null)
        {
            for (int i = 0; i < itemButtonTexts.Length; i++)
            {
                if (itemButtonTexts[i] != null)
                    itemButtonTexts[i].raycastTarget = false;
            }
        }

        if (itemButtonImages != null)
        {
            for (int i = 0; i < itemButtonImages.Length; i++)
            {
                if (itemButtonImages[i] != null)
                    itemButtonImages[i].raycastTarget = false;
            }
        }
    }

    /// <summary>
    /// 给每个 itemButton 注册 PointerEnter / Exit / Down / Up / Click。
    /// 重点是 PointerClick：VR 自定义射线有时不会稳定触发 Button.onClick，
    /// 所以这里额外用 EventTrigger 直接调用 TriggerItemClicked。
    /// </summary>
    private void RegisterButtonPointerEvents()
    {
        for (int i = 0; i < itemButtons.Length; i++)
        {
            if (itemButtons[i] == null) continue;

            // 关掉 Button 自带 ColorTint，颜色完全由本脚本控制
            itemButtons[i].transition = Selectable.Transition.None;

            EventTrigger trigger = itemButtons[i].gameObject.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = itemButtons[i].gameObject.AddComponent<EventTrigger>();

            trigger.triggers.Clear();

            int idx = i;

            AddTriggerEntry(trigger, EventTriggerType.PointerEnter, _ =>
            {
                hoveredButtonIndex = idx;
                RefreshButtonColor(idx);
            });

            AddTriggerEntry(trigger, EventTriggerType.PointerExit, _ =>
            {
                if (hoveredButtonIndex == idx)
                    hoveredButtonIndex = -1;

                RefreshButtonColor(idx);
            });

            AddTriggerEntry(trigger, EventTriggerType.PointerDown, _ =>
            {
                pressedButtonIndex = idx;
                RefreshButtonColor(idx);
            });

            AddTriggerEntry(trigger, EventTriggerType.PointerUp, _ =>
            {
                if (pressedButtonIndex == idx)
                    pressedButtonIndex = -1;

                RefreshButtonColor(idx);
            });

            // 关键新增：VR pointer click 直接触发 item select
            AddTriggerEntry(trigger, EventTriggerType.PointerClick, _ =>
            {
                TriggerItemClicked(idx);
            });
        }
    }

    private static void AddTriggerEntry(
        EventTrigger trigger,
        EventTriggerType type,
        UnityEngine.Events.UnityAction<BaseEventData> callback)
    {
        EventTrigger.Entry entry = new EventTrigger.Entry();
        entry.eventID = type;
        entry.callback.AddListener(callback);
        trigger.triggers.Add(entry);
    }

    /// <summary>
    /// Button.onClick 和 EventTrigger.PointerClick 都统一走这里。
    /// </summary>
    private void TriggerItemClicked(int index)
    {
        if (index < 0 || index >= displayedDetections.Count)
        {
            Debug.LogWarning("[DetectedItemsListPanelUI] Click ignored: index out of range " + index);
            return;
        }

        DetectedObjectData data = displayedDetections[index];
        if (data == null)
        {
            Debug.LogWarning("[DetectedItemsListPanelUI] Click ignored: data is null at index " + index);
            return;
        }

        // 防止同一帧 PointerClick + Button.onClick 重复触发
        if (lastClickedIndex == index && Time.unscaledTime - lastClickedTime < 0.08f)
            return;

        lastClickedIndex = index;
        lastClickedTime = Time.unscaledTime;

        if (logItemClicks)
        {
            Debug.Log("[DetectedItemsListPanelUI] item clicked: index=" + index + ", label=" + data.label);
        }

        currentOnItemClicked?.Invoke(index);
    }

    private void RefreshButtonColor(int buttonIndex)
    {
        bool isSelected = buttonIndex == currentSelectedIndex;
        SetButtonVisualState(buttonIndex, isSelected);
    }

    /// <summary>
    /// 由 MRHUDController 每帧调用。
    /// lockedDetections 是当前锁定的检测物体列表。
    /// onItemClicked 一般是 MRHUDController.OnDetectedItemClicked。
    /// </summary>
    public void RefreshList(
        IReadOnlyList<DetectedObjectData> lockedDetections,
        int selectedIndex,
        Action<int> onItemClicked)
    {
        currentOnItemClicked = onItemClicked;
        currentSelectedIndex = selectedIndex;

        displayedDetections.Clear();

        if (lockedDetections != null)
        {
            for (int i = 0; i < lockedDetections.Count; i++)
            {
                if (lockedDetections[i] == null) continue;

                displayedDetections.Add(lockedDetections[i]);

                if (displayedDetections.Count >= maxDisplayedItems)
                    break;
            }
        }

        if (displayedDetections.Count == 0)
        {
            ShowEmptyState();
            return;
        }

        ShowPanel();

        if (titleText != null)
            titleText.text = "Detected Items";

        if (emptyText != null)
            emptyText.gameObject.SetActive(false);

        for (int i = 0; i < itemButtons.Length; i++)
        {
            bool hasData = i < displayedDetections.Count;

            Button btn = itemButtons[i];

            if (btn != null)
            {
                if (hideUnusedButtons)
                    btn.gameObject.SetActive(hasData);
                else
                    btn.gameObject.SetActive(true);

                btn.onClick.RemoveAllListeners();

                if (hasData)
                {
                    int capturedIndex = i;

                    // 保留普通 Button.onClick 路径。
                    // 如果普通 Unity UI click 成功，也能触发。
                    btn.onClick.AddListener(() =>
                    {
                        TriggerItemClicked(capturedIndex);
                    });

                    btn.interactable = true;
                }
                else
                {
                    btn.interactable = false;
                }
            }

            if (itemButtonTexts != null && i < itemButtonTexts.Length && itemButtonTexts[i] != null)
            {
                itemButtonTexts[i].raycastTarget = false;

                if (hasData)
                    itemButtonTexts[i].text = displayedDetections[i].label;
                else
                    itemButtonTexts[i].text = hideUnusedButtons ? "" : "---";
            }

            if (itemButtonImages != null && i < itemButtonImages.Length && itemButtonImages[i] != null)
            {
                itemButtonImages[i].raycastTarget = false;

                if (hasData && displayedDetections[i].snapshot != null)
                {
                    itemButtonImages[i].texture = displayedDetections[i].snapshot;
                    itemButtonImages[i].enabled = true;
                }
                else
                {
                    itemButtonImages[i].texture = null;
                    itemButtonImages[i].enabled = false;
                }
            }

            bool isStale = hasData && displayedDetections[i].stale;

            if (itemButtonStaleGroups != null &&
                i < itemButtonStaleGroups.Length &&
                itemButtonStaleGroups[i] != null)
            {
                itemButtonStaleGroups[i].alpha = isStale ? staleAlpha : 1f;
                itemButtonStaleGroups[i].interactable = true;
                itemButtonStaleGroups[i].blocksRaycasts = true;
            }

            SetButtonSelectedState(i, hasData && i == selectedIndex);
        }
    }

    public IReadOnlyList<DetectedObjectData> GetDisplayedDetections()
    {
        return displayedDetections;
    }

    public void HidePanel()
    {
        if (panelAnimator != null)
            panelAnimator.Hide();
        else if (panelRoot != null)
            panelRoot.SetActive(false);
    }

    private void ShowPanel()
    {
        if (panelAnimator != null)
            panelAnimator.Show();
        else if (panelRoot != null)
            panelRoot.SetActive(true);
    }

    private void ShowEmptyState()
    {
        HideAllButtons();
        ShowPanel();

        if (emptyText != null)
        {
            emptyText.gameObject.SetActive(true);
            emptyText.text = "No objects detected.";
        }
        else if (titleText != null)
        {
            titleText.text = "No objects detected";
        }
    }

    private void HideAllButtons()
    {
        for (int i = 0; i < itemButtons.Length; i++)
        {
            if (itemButtons[i] != null)
            {
                itemButtons[i].onClick.RemoveAllListeners();

                if (hideUnusedButtons)
                    itemButtons[i].gameObject.SetActive(false);
                else
                    itemButtons[i].interactable = false;
            }

            if (itemButtonTexts != null && i < itemButtonTexts.Length && itemButtonTexts[i] != null)
                itemButtonTexts[i].text = "";

            if (itemButtonImages != null && i < itemButtonImages.Length && itemButtonImages[i] != null)
            {
                itemButtonImages[i].texture = null;
                itemButtonImages[i].enabled = false;
            }

            if (itemButtonStaleGroups != null &&
                i < itemButtonStaleGroups.Length &&
                itemButtonStaleGroups[i] != null)
            {
                itemButtonStaleGroups[i].alpha = 1f;
                itemButtonStaleGroups[i].blocksRaycasts = false;
            }
        }
    }

    private void SetButtonSelectedState(int buttonIndex, bool isSelected)
    {
        SetButtonVisualState(buttonIndex, isSelected);

        TMP_Text txt = null;

        if (itemButtonTexts != null &&
            buttonIndex >= 0 &&
            buttonIndex < itemButtonTexts.Length)
        {
            txt = itemButtonTexts[buttonIndex];
        }

        if (txt != null)
            txt.color = isSelected ? selectedTextColor : normalTextColor;
    }

    /// <summary>
    /// 根据 selected / hover / pressed 状态决定背景色。
    /// 优先级：pressed > hovered > selected > normal
    /// </summary>
    private void SetButtonVisualState(int buttonIndex, bool isSelected)
    {
        if (buttonIndex < 0 || buttonIndex >= itemButtons.Length)
            return;

        Button btn = itemButtons[buttonIndex];
        if (btn == null || btn.image == null)
            return;

        Color c;

        if (pressedButtonIndex == buttonIndex)
            c = pressedButtonColor;
        else if (hoveredButtonIndex == buttonIndex)
            c = hoveredButtonColor;
        else if (isSelected)
            c = selectedButtonColor;
        else
            c = normalButtonColor;

        btn.image.color = c;
    }
}