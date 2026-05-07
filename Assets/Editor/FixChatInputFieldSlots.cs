using TMPro;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class FixChatInputFieldSlots
{
    private const string RanKey = "VRXR.FixChatInputFieldSlots.Ran.v3";

    [DidReloadScripts]
    private static void OnReload()
    {
        if (SessionState.GetBool(RanKey, false)) return;
        EditorApplication.delayCall += () =>
        {
            SessionState.SetBool(RanKey, true);
            Run();
        };
    }

    [MenuItem("Tools/VR-XR/Fix Chat InputField Slots")]
    public static void Run()
    {
        // Find the Chat InputField by full path so this works no matter what the instance IDs are.
        const string path = "Main_MR_Desk/[BuildingBlock] Camera Rig/TrackingSpace/CenterEyeAnchor/MR HUD Canvas （主）/Overlay Root/Expand Root/Ask AI Chat Panel （chat）/Chat Input Row/Chat InputField";
        var inputGo = GameObject.Find(path);
        if (inputGo == null)
        {
            // Fall back to search by component
            foreach (var f in Object.FindObjectsByType<TMP_InputField>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (f.gameObject.name == "Chat InputField") { inputGo = f.gameObject; break; }
            }
        }
        if (inputGo == null) { Debug.LogError("[FixChatInputFieldSlots] Chat InputField not found."); return; }

        var input = inputGo.GetComponent<TMP_InputField>();
        if (input == null) { Debug.LogError("[FixChatInputFieldSlots] no TMP_InputField"); return; }

        Transform tText = inputGo.transform.Find("Text Component");
        Transform tPh   = inputGo.transform.Find("Placeholder");
        if (tText == null || tPh == null)
        {
            Debug.LogError($"[FixChatInputFieldSlots] children missing. text={tText} placeholder={tPh}");
            return;
        }

        var textTmp   = tText.GetComponent<TMP_Text>();
        var phGraphic = tPh.GetComponent<Graphic>();
        var viewport  = inputGo.GetComponent<RectTransform>();

        Undo.RecordObject(input, "Wire InputField slots");
        input.textViewport  = viewport;
        input.textComponent = textTmp;
        input.placeholder   = phGraphic;

        // Also add VR keyboard helper if not already present.
        if (inputGo.GetComponent<VRInputFieldKeyboard>() == null)
            Undo.AddComponent<VRInputFieldKeyboard>(inputGo);

        EditorUtility.SetDirty(input);
        EditorUtility.SetDirty(inputGo);
        EditorSceneManager.MarkSceneDirty(inputGo.scene);

        Debug.Log($"[FixChatInputFieldSlots] OK. textComponent={input.textComponent}, placeholder={input.placeholder}, textViewport={input.textViewport}");
    }
}
