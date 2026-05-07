using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 真实检测结果提供器。
/// 你的模型 / YOLO / CV 脚本在拿到检测结果后，调用 UpdateFromModelDetections()。
/// 然后 MRHUDController 会自动读取 currentDetections。
/// </summary>
public class RealDetectionProvider : DetectionProviderBase
{
    [System.Serializable]
    public class ModelDetection
    {
        public string label;
        public float confidence;
        public Rect pixelRect; // 像素坐标，默认假设原点在左上角
    }

    [Header("Current Detections (Runtime)")]
    public List<DetectedObjectData> currentDetections = new List<DetectedObjectData>();

    [Header("Fallback Text")]
    [TextArea(2, 4)] public string defaultSummaryTemplate = "{label} detected in the current scene.";
    [TextArea(2, 6)] public string defaultDescribeTemplate = "This appears to be a {label}.";
    [TextArea(2, 6)] public string defaultMoreInfoTemplate = "{label} is part of the current study desk environment.";
    [TextArea(2, 4)] public string defaultSuggestionTemplate = "Try selecting another detected object.";

    [TextArea(2, 4)] public string defaultAskIntroTemplate = "You can ask more about this {label}.";
    [TextArea(2, 6)] public string defaultAskUseTemplate = "This {label} may support the desk workflow depending on context.";
    [TextArea(2, 6)] public string defaultAskWhyTemplate = "This object matters because it contributes to how the desk is used.";
    [TextArea(2, 6)] public string defaultAskNextTemplate = "Try selecting another object nearby.";
    [TextArea(2, 6)] public string defaultAskCompareTemplate = "This object can be compared with other desk items in function and role.";

    public override IReadOnlyList<DetectedObjectData> GetDetections()
    {
        return currentDetections;
    }

    /// <summary>
    /// 把模型输出的像素坐标检测框，转换成 MRHUDController 可用的 0~1 viewportRect。
    /// 注意：
    /// - 这里假设模型输出 pixelRect 的原点在左上角
    /// - MRHUDController 里用的是左下角原点
    /// </summary>
    public void UpdateFromModelDetections(List<ModelDetection> modelDetections, int imageWidth, int imageHeight)
    {
        currentDetections.Clear();

        if (modelDetections == null || imageWidth <= 0 || imageHeight <= 0)
            return;

        foreach (var det in modelDetections)
        {
            if (det == null) continue;
            if (string.IsNullOrWhiteSpace(det.label)) continue;

            // 像素坐标 -> 归一化 viewportRect
            float x = det.pixelRect.x / imageWidth;
            float y = 1f - ((det.pixelRect.y + det.pixelRect.height) / imageHeight);
            float w = det.pixelRect.width / imageWidth;
            float h = det.pixelRect.height / imageHeight;

            // 防止越界
            x = Mathf.Clamp01(x);
            y = Mathf.Clamp01(y);
            w = Mathf.Clamp01(w);
            h = Mathf.Clamp01(h);

            // 如果框尺寸为 0，就跳过
            if (w <= 0f || h <= 0f)
                continue;

            DetectedObjectData data = new DetectedObjectData
            {
                id = det.label,
                label = det.label,
                viewportRect = new Rect(x, y, w, h),
                visible = true,

                summary = Fill(defaultSummaryTemplate, det.label),
                describeText = Fill(defaultDescribeTemplate, det.label),
                moreInfoText = Fill(defaultMoreInfoTemplate, det.label),
                suggestionText = Fill(defaultSuggestionTemplate, det.label),

                askIntroText = Fill(defaultAskIntroTemplate, det.label),
                askUseText = Fill(defaultAskUseTemplate, det.label),
                askWhyText = Fill(defaultAskWhyTemplate, det.label),
                askNextText = Fill(defaultAskNextTemplate, det.label),
                askCompareText = Fill(defaultAskCompareTemplate, det.label)
            };

            currentDetections.Add(data);
        }
    }

    /// <summary>
    /// 用于调试：手动塞一组测试像素框
    /// </summary>
    public void SetSingleDebugDetection(string label, Rect pixelRect, int imageWidth, int imageHeight)
    {
        List<ModelDetection> list = new List<ModelDetection>
        {
            new ModelDetection
            {
                label = label,
                confidence = 0.99f,
                pixelRect = pixelRect
            }
        };

        UpdateFromModelDetections(list, imageWidth, imageHeight);
    }

    private string Fill(string template, string label)
    {
        if (string.IsNullOrEmpty(template))
            return label;

        return template.Replace("{label}", label);
    }
}