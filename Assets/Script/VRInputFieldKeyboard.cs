using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 给 TMP_InputField 加上 VR 系统键盘支持。
/// Meta XR All-In-One SDK 201.0.0 没有官方 OVRVirtualKeyboard building block，
/// 所以这里走 TouchScreenKeyboard.Open() —— Quest OS v54+ 会弹出 system overlay 键盘，
/// 输入完成后写回 InputField.text。
///
/// 用法：把这个组件挂在含 TMP_InputField 的 GameObject 上（或在 Inspector 里指定）。
/// 如果设备不支持系统键盘（编辑器里），会忽略，让 PC 键盘正常工作。
/// </summary>
[RequireComponent(typeof(TMP_InputField))]
public class VRInputFieldKeyboard : MonoBehaviour, ISelectHandler, IPointerClickHandler
{
    [Tooltip("键盘标题（Quest 系统键盘左上角的提示）")]
    public string title = "Type your question";

    [Tooltip("最大字符数")]
    public int characterLimit = 256;

    [Tooltip("如果系统键盘不可用时，把这个组件设为 inactive 也能保留 PC/编辑器输入")]
    public bool fallbackToDefaultOnEditor = true;

    private TMP_InputField inputField;
    private TouchScreenKeyboard keyboard;

    private void Awake()
    {
        inputField = GetComponent<TMP_InputField>();
        if (inputField != null)
        {
            // The InputField's own onSelect UnityEvent fires whenever EventSystem
            // selects it (controller raycast click + keyboard focus). This is more
            // reliable on Quest than ISelectHandler alone.
            inputField.onSelect.AddListener(_ => OpenKeyboard());
            // shouldHideMobileInput=true is the default which hides the dummy
            // input at the bottom of the screen. We want the OS keyboard overlay
            // to fully drive the text — disable Unity's caret handling.
            inputField.shouldHideMobileInput = true;
        }
    }

    public void OnSelect(BaseEventData eventData) => OpenKeyboard();
    public void OnPointerClick(PointerEventData eventData) => OpenKeyboard();

    private void OpenKeyboard()
    {
        if (inputField == null) return;
        if (keyboard != null && keyboard.active) return;

#if UNITY_EDITOR
        if (fallbackToDefaultOnEditor) return; // 编辑器里用 PC 键盘
#endif

        if (!TouchScreenKeyboard.isSupported)
        {
            Debug.LogWarning("[VRInputFieldKeyboard] TouchScreenKeyboard not supported on this device. Use voice input instead.");
            return;
        }

        keyboard = TouchScreenKeyboard.Open(
            inputField.text ?? "",
            TouchScreenKeyboardType.Default,
            autocorrection: true,
            multiline: false,
            secure: false,
            alert: false,
            textPlaceholder: title,
            characterLimit: characterLimit
        );
    }

    private void Update()
    {
        if (keyboard == null) return;

        // 实时同步键盘里的 text 到 InputField
        if (keyboard.status == TouchScreenKeyboard.Status.Visible)
        {
            if (inputField.text != keyboard.text)
                inputField.text = keyboard.text;
        }
        else
        {
            // Done / Canceled / LostFocus 都把 keyboard 置空
            if (keyboard.status == TouchScreenKeyboard.Status.Done)
            {
                inputField.text = keyboard.text;
                inputField.onSubmit?.Invoke(inputField.text);
            }
            keyboard = null;
        }
    }

    private void OnDisable()
    {
        if (keyboard != null)
        {
            keyboard.active = false;
            keyboard = null;
        }
    }
}
