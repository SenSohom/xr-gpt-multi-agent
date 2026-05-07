using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VK = VisualKeyboard;

/// <summary>
/// Bridges the third-party VisualKeyboard prefab to the Chat panel's TMP_InputField.
/// - Each character key → appended to InputField.text
/// - Backspace → removes last char of InputField.text
/// - Return / KeypadEnter → invokes the Send Button (auto-submit)
///
/// Drop this on the Visual Keyboard prefab instance in the scene, assign:
///   - keyboard: the VisualKeyboard component on the same GameObject
///   - inputField: the Chat InputField (TMP)
///   - sendButton: the Chat panel's Send button (optional)
/// </summary>
public class VisualKeyboardChatBridge : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("The VisualKeyboard component (same GameObject by default).")]
    public VK.VisualKeyboard keyboard;

    [Header("Target")]
    [Tooltip("Chat panel's TMP_InputField. Text typed on the visual keyboard goes here.")]
    public TMP_InputField inputField;

    [Tooltip("Optional: Chat panel's Send button. Pressing Return on the visual keyboard clicks it.")]
    public Button sendButton;

    [Header("Behavior")]
    [Tooltip("Hide the visual keyboard at startup. Show it only when the InputField is focused.")]
    public bool hideUntilInputFocused = true;

    private void Reset()
    {
        keyboard = GetComponent<VK.VisualKeyboard>();
    }

    private void Awake()
    {
        if (keyboard == null) keyboard = GetComponent<VK.VisualKeyboard>();

        if (hideUntilInputFocused && keyboard != null)
            keyboard.gameObject.SetActive(false);

        if (inputField != null)
        {
            inputField.onSelect.AddListener(_ =>
            {
                if (keyboard != null) keyboard.gameObject.SetActive(true);
            });
        }
    }

    private void OnEnable()
    {
        if (keyboard != null)
        {
            keyboard.OnCharacterInput += OnChar;
            keyboard.OnKeyClick       += OnKey;
        }
    }

    private void OnDisable()
    {
        if (keyboard != null)
        {
            keyboard.OnCharacterInput -= OnChar;
            keyboard.OnKeyClick       -= OnKey;
        }
    }

    private void OnChar(char c)
    {
        if (inputField == null) return;
        inputField.text += c;
        inputField.caretPosition = inputField.text.Length;
    }

    private void OnKey(VK.VisualKeyForKeyboard key)
    {
        if (inputField == null) return;

        switch (key.oldKeyCode)
        {
            case KeyCode.Backspace:
                if (inputField.text.Length > 0)
                {
                    inputField.text = inputField.text.Substring(0, inputField.text.Length - 1);
                    inputField.caretPosition = inputField.text.Length;
                }
                break;

            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                Submit();
                break;

            case KeyCode.Space:
                // VisualKeyboard already sends ' ' as a character via OnCharacterInput,
                // so no extra work — just here to document.
                break;
        }
    }

    public void Submit()
    {
        if (inputField == null) return;
        if (string.IsNullOrWhiteSpace(inputField.text)) return;

        if (sendButton != null && sendButton.interactable)
            sendButton.onClick.Invoke();
        else
            inputField.onSubmit?.Invoke(inputField.text);

        // Hide keyboard after sending so the chat fills the panel again.
        if (hideUntilInputFocused && keyboard != null)
            keyboard.gameObject.SetActive(false);
    }
}
