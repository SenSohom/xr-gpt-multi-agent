using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// 极简控制器射线 + UI 点击。
/// 不依赖 Meta Interaction SDK 的 RayInteractor / PointableCanvas / Active State Provider 链路。
///
/// 工作原理:
///   1. 从 rayOrigin (RightControllerAnchor) 沿 forward 投一条 1.5 米射线
///   2. 把射线终点投影到 hudCamera 的屏幕坐标,用每个 GraphicRaycaster 做 UI hit-test
///   3. hover 时发送 pointerEnter/pointerExit 事件 → Button 进入 highlighted 视觉状态
///   4. 按下 A 键/扳机时:发送 pointerDown → Button 进入 pressed 视觉状态
///      松开/瞬时:发送 pointerClick (触发 Button.onClick) + pointerUp (恢复)
///   5. 画一条 LineRenderer 显示射线
///
/// Quest 真机用 OVRInput.Button.One / PrimaryIndexTrigger 按下/松开。
/// </summary>
public class ControllerUIClicker : MonoBehaviour
{
    [Header("Ray Source")]
    [Tooltip("射线起点(右手 OVR Anchor)")]
    public Transform rayOrigin;

    [Tooltip("用作屏幕坐标投影的相机(中心眼)")]
    public Camera hudCamera;

    [Header("Targets")]
    [Tooltip("要测试 UI 命中的所有 Canvas 上的 GraphicRaycaster")]
    public GraphicRaycaster[] canvasRaycasters;

    [Header("Behavior")]
    [Tooltip("射线最大长度(米)")]
    public float rayDistance = 1.5f;

    [Tooltip("UI 鼠标光标(可选,RawImage 或 Image),会被移到射线终点")]
    public RectTransform cursorReticle;

    [Header("Line")]
    public LineRenderer lineRenderer;
    public Color rayColor = new Color(0.4f, 0.9f, 1f, 0.85f);

    [Tooltip("没指到 UI 时射线的颜色(更暗/更透明)")]
    public Color rayMissColor = new Color(0.4f, 0.9f, 1f, 0.35f);

    [Header("Debug")]
    public bool logClicks = true;

    // ---- 内部 ----
    private readonly List<RaycastResult> raycastResults = new List<RaycastResult>();
    private GameObject currentHover;
    private GameObject pressedTarget;     // 用于 down 时记录,up 时发 pointerUp 给同一对象
    private bool wasPressedLastFrame;
    private PointerEventData reusablePed;

    private void Reset()
    {
        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();
    }

    private void Awake()
    {
        if (lineRenderer != null)
        {
            lineRenderer.startWidth = 0.005f;
            lineRenderer.endWidth = 0.005f;
            lineRenderer.startColor = rayColor;
            lineRenderer.endColor = rayColor;
            lineRenderer.useWorldSpace = true;
            lineRenderer.positionCount = 2;

            if (lineRenderer.sharedMaterial == null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh != null)
                {
                    var mat = new Material(sh);
                    mat.color = rayColor;
                    lineRenderer.sharedMaterial = mat;
                }
            }
        }
    }

    private void Update()
    {
        if (rayOrigin == null) return;
        Camera cam = hudCamera != null ? hudCamera : Camera.main;
        if (cam == null) return;

        Vector3 origin = rayOrigin.position;
        Vector3 dir = rayOrigin.forward;
        Vector3 endPoint = origin + dir * rayDistance;

        // 1. UI hit-test
        Vector3 screenPos = cam.WorldToScreenPoint(endPoint);
        GameObject hitObject = null;
        if (screenPos.z > 0f)
        {
            PointerEventData ped = AcquirePed(new Vector2(screenPos.x, screenPos.y));
            raycastResults.Clear();
            if (canvasRaycasters != null)
            {
                for (int i = 0; i < canvasRaycasters.Length; i++)
                {
                    var rc = canvasRaycasters[i];
                    if (rc == null) continue;
                    rc.Raycast(ped, raycastResults);
                }
            }

            if (raycastResults.Count > 0)
            {
                raycastResults.Sort((a, b) => a.distance.CompareTo(b.distance));
                hitObject = raycastResults[0].gameObject;
                ped.pointerCurrentRaycast = raycastResults[0];
            }
        }

        // 2. Hover highlight — use ExecuteHierarchy so events bubble up past child text/images to the Button
        if (hitObject != currentHover)
        {
            if (currentHover != null)
                ExecuteEvents.ExecuteHierarchy(currentHover, AcquirePed(screenPos), ExecuteEvents.pointerExitHandler);
            if (hitObject != null)
                ExecuteEvents.ExecuteHierarchy(hitObject, AcquirePed(screenPos), ExecuteEvents.pointerEnterHandler);
            currentHover = hitObject;
        }

        // 3. Cursor reticle
        if (cursorReticle != null)
        {
            cursorReticle.gameObject.SetActive(hitObject != null);
            if (hitObject != null)
            {
                cursorReticle.position = endPoint;
                cursorReticle.LookAt(cursorReticle.position + cam.transform.forward);
            }
        }

        // 4. Line renderer
        if (lineRenderer != null)
        {
            lineRenderer.SetPosition(0, origin);
            lineRenderer.SetPosition(1, endPoint);
            Color c = hitObject != null ? rayColor : rayMissColor;
            lineRenderer.startColor = c;
            lineRenderer.endColor = c;
        }

        // 5. Press / release / click
        bool pressed = GetClickHeld();
        bool pressedDown = pressed && !wasPressedLastFrame;
        bool pressedUp = !pressed && wasPressedLastFrame;
        wasPressedLastFrame = pressed;

        if (pressedDown && hitObject != null)
        {
            // 把 pointerDown 发给 Button → 触发 Button.OnPointerDown → pressed 视觉状态
            pressedTarget = ExecuteEvents.ExecuteHierarchy(hitObject, AcquirePed(screenPos), ExecuteEvents.pointerDownHandler);
            if (pressedTarget == null) pressedTarget = hitObject;

            // 通知 Selectable 选中(让它进入 selected 颜色)
            ExecuteEvents.ExecuteHierarchy(hitObject, AcquirePed(screenPos), ExecuteEvents.selectHandler);

            if (logClicks)
                Debug.Log($"[ControllerUIClicker] DOWN on '{(pressedTarget != null ? pressedTarget.name : "null")}'");
        }

        if (pressedUp)
        {
            // pointerClick 必须发给"按下"和"松开"时都命中的同一对象,这里我们用"按下时记录的"
            GameObject target = pressedTarget != null ? pressedTarget : hitObject;
            if (target != null)
            {
                ExecuteEvents.ExecuteHierarchy(target, AcquirePed(screenPos), ExecuteEvents.pointerUpHandler);

                // 松开时只要 pressedTarget 有效就发 click（VR 手部抖动容易导致 hitObject=null）
                bool canClick = pressedTarget != null ||
                    (hitObject != null && (hitObject == target ||
                        hitObject.transform.IsChildOf(target.transform) ||
                        target.transform.IsChildOf(hitObject.transform)));

                if (canClick)
                {
                    GameObject clickReceiver = ExecuteEvents.ExecuteHierarchy(target, AcquirePed(screenPos), ExecuteEvents.pointerClickHandler);
                    if (logClicks)
                        Debug.Log($"[ControllerUIClicker] CLICK on '{target.name}' -> received by '{(clickReceiver != null ? clickReceiver.name : "null")}'");
                }
                else
                {
                    if (logClicks)
                        Debug.Log($"[ControllerUIClicker] UP without click on '{target.name}' (moved off)");
                }
            }
            pressedTarget = null;
        }
    }

    private PointerEventData AcquirePed(Vector2 pos)
    {
        // 每次重建，避免 pointerPress / pointerDrag / pointerCurrentRaycast 等字段跨帧污染
        reusablePed = new PointerEventData(EventSystem.current)
        {
            position = pos,
            button = PointerEventData.InputButton.Left
        };
        return reusablePed;
    }

    private PointerEventData AcquirePed(Vector3 pos) => AcquirePed(new Vector2(pos.x, pos.y));

    private bool GetClickHeld()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return OVRInput.Get(OVRInput.Button.One)
            || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger)
            || OVRInput.Get(OVRInput.Button.SecondaryIndexTrigger);
#else
        return Mouse.current != null && Mouse.current.leftButton.isPressed;
#endif
    }
}
