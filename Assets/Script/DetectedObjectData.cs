using UnityEngine;

[System.Serializable]
public class DetectedObjectData
{
    public string id;
    public string label;
    public Rect viewportRect;   // 0~1 normalized coordinates, origin at bottom-left
    public bool visible = true;

    // 当物品在 10s 锁定期内但已不再被 YOLO 检测到时为 true
    [System.NonSerialized] public bool stale;

    // 选中那一刻从 passthrough texture 上 crop 的快照,生命周期由 MRHUDController 管理
    [System.NonSerialized] public Texture2D snapshot;

    // VLM 异步返回的文案缓存(选中后才填),为 null 表示尚未请求或 still loading
    [System.NonSerialized] public string vlmShortDescription;
    [System.NonSerialized] public string vlmMoreInfo;

    [TextArea(2, 4)] public string summary;
    [TextArea(3, 8)] public string describeText;
    [TextArea(3, 8)] public string moreInfoText;
    [TextArea(2, 4)] public string suggestionText;

    [TextArea(2, 6)] public string askIntroText;
    [TextArea(2, 6)] public string askUseText;
    [TextArea(2, 6)] public string askWhyText;
    [TextArea(2, 6)] public string askNextText;
    [TextArea(2, 6)] public string askCompareText;

    public void DisposeSnapshot()
    {
        if (snapshot != null)
        {
            Object.Destroy(snapshot);
            snapshot = null;
        }
    }
}
