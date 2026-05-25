using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HUD 总调度。
/// 流程: Notice → Focus → Select → Confirm → Explain → Expand → Continue
///
/// 关键行为:
///  - Detection Box 实时跟随 YOLO 的最新检测(每帧更新位置)
///  - "Detected Items" 列表锁定 4 项,每 lockRefreshSeconds(默认 10 秒)整批刷新一次
///  - 锁定期间若某项不再被 YOLO 检测到,标记为 stale(列表保留但置灰,detection box 不画)
///  - 输入仅保留 Quest 手柄 A键/扳机 = 确认, B键 = 取消
///  - hover 由控制器 raycast 决定,而不是屏幕中心
///  - VLM 仅在 Open / More Info / Chat 时调用
/// </summary>
public class MRHUDController : MonoBehaviour
{
    [Header("References")]
    public DetectionProviderBase detectionProvider;
    public RectTransform detectionOverlayRoot;
    public DetectionBoxUI detectionBoxPrefab;
    public ObjectGuidePanelUI panelUI;
    public DetectedItemsListPanelUI detectedItemsListPanel;
    public TMP_Text softOnboardingText;
    public ControllerHintUI controllerHintUI;
    [Tooltip("【推荐】直接拖 PassthroughCameraViewer。读它的 CurrentTexture")]
    public PassthroughCameraViewer passthroughViewer;
    [Tooltip("可选:passthrough RawImage(只要 viewer 设了,这个就可以为空)")]
    public RawImage passthroughRawImage;
    public VlmClient vlmClient;

    [Header("Controller Ray (Quest)")]
    [Tooltip("用作射线起点的控制器 Transform(右手 OVR Anchor)。为空则退化用相机方向。")]
    public Transform rayOriginTransform;
    public Camera hudCamera;

    [Header("Behavior")]
    public int maxDisplayedDetections = 4;

    [Tooltip("每隔多少秒整批刷新一次 Detected Items 列表。<= 0 表示完全手动,只有 RefreshDetectionsNow() 被调用时才更新。")]
    public float lockRefreshSeconds = 0f;

    [Header("Debug")]
    public bool debugShowFirstObjectOnStart = false;
    [Tooltip("Quest logcat 用:每多少秒打印一次 detection-box 状态(0 = 关)")]
    public float boxDiagnosticIntervalSeconds = 5f;
    private float nextBoxDiagAt;

    // ---- 运行时 ----
    private readonly List<DetectionBoxUI> boxPool = new List<DetectionBoxUI>();

    // 当前锁定的 4 项(Stage 2 列表内容,10s 内稳定)
    private readonly List<DetectedObjectData> lockedDetections = new List<DetectedObjectData>();

    // 上一次锁定时间
    private float lastLockTime = -999f;

    private int hoveredIndex = -1;
    private int selectedIndex = -1;
    private bool hasSelectedOnce = false;

    private void Start()
    {
        // 不再显示 detection box → 直接关掉 overlay 根节点,确保它不会生成、渲染或拦截任何输入。
        if (detectionOverlayRoot != null)
            detectionOverlayRoot.gameObject.SetActive(false);

        if (lockRefreshSeconds > 0f)
            RefreshLockedDetections();

        if (debugShowFirstObjectOnStart)
        {
            RefreshLockedDetections();
            if (lockedDetections.Count > 0)
                SelectDetection(0);
        }

        // 当 Right Info Panel / Chat Panel 全部关闭时,恢复 YOLO 实时检测。
        if (panelUI != null)
            panelUI.onClosed += OnGuidePanelClosed;
    }

    private void OnGuidePanelClosed()
    {
        selectedIndex = -1;
    }

    /// <summary>
    /// true = 当前有物品被选中(Right Info / Chat Panel 至少一个开着)
    /// → 冻结 detection box + 锁定列表,不再跟随 YOLO 实时刷新。
    /// </summary>
    private bool IsDetectionFrozen => selectedIndex >= 0;

    /// <summary>
    /// 给 Refresh 按钮的 onClick 用。立即抓一份当前可见检测,锁定到列表里。
    /// 如果某项当前正被选中,会保留它的快照,避免 Right Info Panel 内容跳变。
    /// </summary>
    public void RefreshDetectionsNow()
    {
        RefreshLockedDetections();
    }

    private void Update()
    {
        if (detectionProvider == null) return;

        // 1. 拉当前帧的最新可见检测,用于实时跟踪 stale。
        var liveDetections = detectionProvider.GetDetections();

        // 2. 维护 lockedDetections 中每项的 visible / stale / viewportRect。
        //    选中物品(Right Info / Chat Panel 开着)期间也维护,但不强制刷新整批锁定列表。
        if (!IsDetectionFrozen)
        {
            ReconcileLockedWithLive(liveDetections);
            if (lockRefreshSeconds > 0f && Time.unscaledTime - lastLockTime >= lockRefreshSeconds)
                RefreshLockedDetections();
        }

        // 3. 不再显示 detection box —— 直接刷新 Detected Items 列表面板。
        //    玩家通过列表按钮选中物品 → SelectDetection → 弹 Right Info Panel(原交互流程)。
        if (detectedItemsListPanel != null)
        {
            detectedItemsListPanel.RefreshList(
                lockedDetections,
                selectedIndex,
                OnDetectedItemClicked
            );
        }

        if (controllerHintUI != null) controllerHintUI.Hide();

        // 4. cancel 关闭 Right Info / Chat Panel,回到只剩 Detected Items Panel 的状态。
        if (selectedIndex >= 0 && GetCancelDown())
        {
            selectedIndex = -1;
            if (panelUI != null) panelUI.HideAll();
        }
    }

    // ============================================================
    // 锁定列表的维护
    // ============================================================

    /// <summary>
    /// 用最新的 live detections 整批替换锁定列表(取前 maxDisplayedDetections 项)。
    /// 释放旧 snapshot,触发新 snapshot 抓取。
    /// 当前正被选中的物品(其快照在 Right Info Panel 显示中)不会被释放。
    /// </summary>
    private void RefreshLockedDetections()
    {
        var live = detectionProvider != null ? detectionProvider.GetDetections() : null;

        // 缓存当前选中项,避免它被换掉/快照被释放
        DetectedObjectData preservedSelected = null;
        if (selectedIndex >= 0 && selectedIndex < lockedDetections.Count)
            preservedSelected = lockedDetections[selectedIndex];

        // 释放旧 snapshot(跳过 preservedSelected,它由 ObjectGuidePanelUI 管理生命周期)
        for (int i = 0; i < lockedDetections.Count; i++)
        {
            var d = lockedDetections[i];
            if (d == null) continue;
            if (d == preservedSelected) continue; // 保留选中的
            d.DisposeSnapshot();
        }
        lockedDetections.Clear();

        // 优先把 preservedSelected 放回列表第一位(无论它是否还在 live 中)
        if (preservedSelected != null)
        {
            lockedDetections.Add(preservedSelected);
            // 检查它在 live 中是否还存在;如果不在,标记为 stale
            var match = FindMatchByLabel(live, preservedSelected.label);
            if (match != null && match.visible)
            {
                preservedSelected.viewportRect = match.viewportRect;
                preservedSelected.visible = true;
                preservedSelected.stale = false;
            }
            else
            {
                preservedSelected.stale = true;
            }
        }

        if (live != null)
        {
            for (int i = 0; i < live.Count && lockedDetections.Count < maxDisplayedDetections; i++)
            {
                var d = live[i];
                if (d == null || !d.visible) continue;
                if (d == preservedSelected) continue; // already in list

                d.stale = false;
                d.snapshot = SnapshotCapture.Crop(GetPassthroughTexture(), d.viewportRect);
                lockedDetections.Add(d);
            }
        }

        lastLockTime = Time.unscaledTime;

        // selectedIndex 现在永远是 0(preservedSelected 在最前面)或保持 -1
        if (preservedSelected != null)
            selectedIndex = 0;
        else if (selectedIndex >= lockedDetections.Count)
        {
            selectedIndex = -1;
            if (panelUI != null) panelUI.HideAll();
        }
    }

    /// <summary>
    /// 在锁定期内,根据 live 检测更新每个锁定项的 viewportRect(实时位置)
    /// 找不到的标记 stale。
    /// </summary>
    private void ReconcileLockedWithLive(IReadOnlyList<DetectedObjectData> live)
    {
        if (live == null) return;

        for (int i = 0; i < lockedDetections.Count; i++)
        {
            var locked = lockedDetections[i];
            if (locked == null) continue;

            DetectedObjectData match = FindMatchByLabel(live, locked.label);
            if (match != null && match.visible)
            {
                locked.viewportRect = match.viewportRect;
                locked.stale = false;
                locked.visible = true;
            }
            else
            {
                locked.stale = true;
            }
        }
    }

    private DetectedObjectData FindMatchByLabel(IReadOnlyList<DetectedObjectData> live, string label)
    {
        if (string.IsNullOrEmpty(label)) return null;
        for (int i = 0; i < live.Count; i++)
        {
            var d = live[i];
            if (d == null) continue;
            if (d.label == label) return d;
        }
        return null;
    }

    // ============================================================
    // 控制器射线 hover
    // ============================================================

    /// <summary>
    /// 把控制器的 forward 投到 detectionOverlayRoot 的 UI 平面上,看落在哪个 box。
    /// 屏幕坐标 -> Canvas 坐标 -> 与每个锁定 box 的 viewportRect 比对。
    /// </summary>
    private int ComputeHoveredIndex()
    {
        if (lockedDetections.Count == 0) return -1;

        Vector2 viewportPoint;
        if (!TryGetRayViewportPoint(out viewportPoint))
            return -1;

        int best = -1;
        float smallestArea = float.MaxValue;

        for (int i = 0; i < lockedDetections.Count; i++)
        {
            var d = lockedDetections[i];
            if (d == null || !d.visible || d.stale) continue;

            Rect r = d.viewportRect;
            if (r.Contains(viewportPoint))
            {
                float area = r.width * r.height;
                if (area < smallestArea)
                {
                    smallestArea = area;
                    best = i;
                }
            }
        }

        return best;
    }

    private bool TryGetRayViewportPoint(out Vector2 viewportPoint)
    {
        viewportPoint = Vector2.zero;

        Camera cam = hudCamera != null ? hudCamera : Camera.main;
        if (cam == null) return false;

        Vector3 originWorld;
        Vector3 dirWorld;

        if (rayOriginTransform != null)
        {
            originWorld = rayOriginTransform.position;
            dirWorld = rayOriginTransform.forward;
        }
        else
        {
            originWorld = cam.transform.position;
            dirWorld = cam.transform.forward;
        }

        // 把世界射线投影到摄像机的近裁面前方一段距离的点上
        Vector3 worldPoint = originWorld + dirWorld * 1.0f;
        Vector3 vp = cam.WorldToViewportPoint(worldPoint);

        if (vp.z < 0f) return false;

        viewportPoint = new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));
        return true;
    }

    private void UpdateControllerHint(float canvasW, float canvasH)
    {
        if (controllerHintUI == null) return;

        bool show = hoveredIndex >= 0
                    && hoveredIndex < lockedDetections.Count
                    && hoveredIndex != selectedIndex;

        if (!show)
        {
            controllerHintUI.Hide();
            return;
        }

        var d = lockedDetections[hoveredIndex];
        Rect canvasRect = ViewportToCanvasRect(d.viewportRect, canvasW, canvasH);
        Vector2 topMid = new Vector2(canvasRect.x + canvasRect.width * 0.5f, canvasRect.y + canvasRect.height);
        controllerHintUI.PositionAbove(topMid);
        controllerHintUI.Show();
    }

    // ============================================================
    // 选中 + VLM
    // ============================================================

    private void OnDetectedItemClicked(int displayedIndex)
    {
        Debug.Log($"[MRHUD] OnDetectedItemClicked index={displayedIndex}, lockedCount={lockedDetections.Count}");
        SelectDetection(displayedIndex);
    }

    private void SelectDetection(int displayedIndex)
    {
        if (displayedIndex < 0 || displayedIndex >= lockedDetections.Count)
        {
            Debug.LogWarning($"[MRHUD] SelectDetection EARLY RETURN: index={displayedIndex}, lockedCount={lockedDetections.Count}");
            return;
        }

        selectedIndex = displayedIndex;
        var data = lockedDetections[selectedIndex];

        Debug.Log($"[MRHUD] SelectDetection OK: label={data.label}, panelUI={(panelUI != null ? "set" : "NULL")}");
        if (panelUI != null)
            panelUI.ShowObject(data);

        // 触发 VLM short description(只在第一次选中此物体时跑)
        if (vlmClient != null && data.snapshot != null && string.IsNullOrEmpty(data.vlmShortDescription))
        {
            vlmClient.AskAgent(data.snapshot, data.label, vlmClient.promptDescribe, "describe", false, (answer, ok) =>
            {
                if (ok)
                {
                    data.vlmShortDescription = answer;
                    if (panelUI != null && selectedIndex >= 0 && selectedIndex < lockedDetections.Count
                        && lockedDetections[selectedIndex] == data)
                    {
                        panelUI.ApplyShortDescription(answer);
                    }
                }
                else
                {
                    if (panelUI != null) panelUI.ApplyShortDescription("(description unavailable)");
                }
            });
        }
        else if (panelUI != null && !string.IsNullOrEmpty(data.vlmShortDescription))
        {
            panelUI.ApplyShortDescription(data.vlmShortDescription);
        }

        if (!hasSelectedOnce)
        {
            hasSelectedOnce = true;
            if (softOnboardingText != null)
                softOnboardingText.gameObject.SetActive(false);
        }
    }

    // ============================================================
    // 工具
    // ============================================================

    private Texture GetPassthroughTexture()
    {
        if (passthroughViewer != null && passthroughViewer.CurrentTexture != null)
            return passthroughViewer.CurrentTexture;
        if (passthroughRawImage != null && passthroughRawImage.texture != null)
            return passthroughRawImage.texture;
        return null;
    }

    private void SyncPool(int targetCount)
    {
        while (boxPool.Count < targetCount)
        {
            var newBox = Instantiate(detectionBoxPrefab, detectionOverlayRoot, false);

            // 重置 RectTransform / scale,防止从 prefab 继承的怪异数值(scale=500 等)
            // 使后面 SetRect 写入的 anchoredPosition + sizeDelta 直接是世界尺寸的来源。
            var rt = newBox.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = Vector2.zero;
                rt.anchoredPosition3D = new Vector3(0f, 0f, 0f);
            }

            // detection box 是纯展示元素,禁掉所有 Graphic 的 raycastTarget,
            // 避免框框拦截控制器射线、把 Button hover/click 事件吞掉。
            var graphics = newBox.GetComponentsInChildren<Graphic>(true);
            for (int g = 0; g < graphics.Length; g++)
                graphics[g].raycastTarget = false;

            // 强制激活(prefab 可能在 inactive 容器里 → clone 默认 active=true,但显式确保)
            newBox.gameObject.SetActive(true);

            Debug.Log($"[MRHUD] Detection box pool[{boxPool.Count}] instantiated under {detectionOverlayRoot.name} (prefab activeSelf={detectionBoxPrefab.gameObject.activeSelf})");
            boxPool.Add(newBox);
        }
        // 多余的隐藏即可,不销毁(下一帧主循环会再控制 active)
        for (int i = targetCount; i < boxPool.Count; i++)
        {
            if (boxPool[i] != null)
                boxPool[i].gameObject.SetActive(false);
        }
    }

    private Rect ViewportToCanvasRect(Rect viewportRect, float canvasWidth, float canvasHeight)
    {
        float x = viewportRect.x * canvasWidth;
        float y = viewportRect.y * canvasHeight;
        float w = viewportRect.width * canvasWidth;
        float h = viewportRect.height * canvasHeight;
        return new Rect(x, y, w, h);
    }

    // ============================================================
    // 输入(仅 Quest)
    // ============================================================

    private bool GetConfirmDown()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return OVRInput.GetDown(OVRInput.Button.One)
            || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger);
#elif ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        bool space = kb != null && kb.spaceKey.wasPressedThisFrame;
        bool enter = kb != null && kb.enterKey.wasPressedThisFrame;
        bool lmb = mouse != null && mouse.leftButton.wasPressedThisFrame;
        return space || enter || lmb;
#else
        return Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space);
#endif
    }

    private bool GetCancelDown()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return OVRInput.GetDown(OVRInput.Button.Two);
#elif ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        bool esc = kb != null && kb.escapeKey.wasPressedThisFrame;
        bool rmb = mouse != null && mouse.rightButton.wasPressedThisFrame;
        return esc || rmb;
#else
        return Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape);
#endif
    }

    private void OnDestroy()
    {
        if (panelUI != null)
            panelUI.onClosed -= OnGuidePanelClosed;

        for (int i = 0; i < lockedDetections.Count; i++)
            lockedDetections[i]?.DisposeSnapshot();
        lockedDetections.Clear();
    }
}
