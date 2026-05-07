using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 把当前 YOLO / 检测输出接进 RealDetectionProvider 的桥接脚本。
///
/// 你只需要在拿到模型输出后，调用：
/// - UpdateFromXYXY(...)
/// 或
/// - UpdateFromXYWH(...)
///
/// 然后 RealDetectionProvider 会自动更新 currentDetections，
/// MRHUDController 就会继续读取并显示 detection box / panel。
/// </summary>
public class YoloToRealDetectionBridge : MonoBehaviour
{
    [Header("References")]
    public RealDetectionProvider realDetectionProvider;

    [Header("Image Info")]
    [Tooltip("当前送进 YOLO 的图像宽度")]
    public int currentImageWidth = 1280;

    [Tooltip("当前送进 YOLO 的图像高度")]
    public int currentImageHeight = 720;

    [Header("Filtering")]
    [Range(0f, 1f)]
    public float minConfidence = 0.35f;

    [Tooltip("是否忽略特别小的框")]
    public bool ignoreTinyBoxes = true;

    [Tooltip("最小框宽（像素）")]
    public float minBoxWidth = 8f;

    [Tooltip("最小框高（像素）")]
    public float minBoxHeight = 8f;

    [Header("Debug")]
    public bool logDetections = false;

    [System.Serializable]
    public class YoloDetectionXYXY
    {
        public string label;
        public float confidence;

        // 常见 YOLO 输出：左上 + 右下
        public float x1;
        public float y1;
        public float x2;
        public float y2;
    }

    [System.Serializable]
    public class YoloDetectionXYWH
    {
        public string label;
        public float confidence;

        // 常见另一种输出：左上 + 宽高
        public float x;
        public float y;
        public float width;
        public float height;
    }

    /// <summary>
    /// 如果你的 YOLO 输出是 x1,y1,x2,y2（左上角 + 右下角），就调用这个。
    /// 假设原点在左上角。
    /// </summary>
    public void UpdateFromXYXY(List<YoloDetectionXYXY> yoloDetections, int imageWidth, int imageHeight)
    {
        if (realDetectionProvider == null)
        {
            Debug.LogError("YoloToRealDetectionBridge: realDetectionProvider is not assigned.");
            return;
        }

        currentImageWidth = imageWidth;
        currentImageHeight = imageHeight;

        List<RealDetectionProvider.ModelDetection> converted = new List<RealDetectionProvider.ModelDetection>();

        if (yoloDetections != null)
        {
            foreach (var det in yoloDetections)
            {
                if (det == null) continue;
                if (string.IsNullOrWhiteSpace(det.label)) continue;
                if (det.confidence < minConfidence) continue;

                float x = Mathf.Min(det.x1, det.x2);
                float y = Mathf.Min(det.y1, det.y2);
                float w = Mathf.Abs(det.x2 - det.x1);
                float h = Mathf.Abs(det.y2 - det.y1);

                if (ignoreTinyBoxes && (w < minBoxWidth || h < minBoxHeight))
                    continue;

                Rect pixelRect = ClampRectToImage(new Rect(x, y, w, h), imageWidth, imageHeight);
                if (pixelRect.width <= 0f || pixelRect.height <= 0f)
                    continue;

                RealDetectionProvider.ModelDetection modelDet = new RealDetectionProvider.ModelDetection
                {
                    label = det.label,
                    confidence = det.confidence,
                    pixelRect = pixelRect
                };

                converted.Add(modelDet);

                if (logDetections)
                {
                    Debug.Log($"[YOLO XYXY] {det.label} conf={det.confidence:F2} rect={pixelRect}");
                }
            }
        }

        realDetectionProvider.UpdateFromModelDetections(converted, imageWidth, imageHeight);
    }

    /// <summary>
    /// 如果你的 YOLO 输出是 x,y,width,height（左上角 + 宽高），就调用这个。
    /// 假设原点在左上角。
    /// </summary>
    public void UpdateFromXYWH(List<YoloDetectionXYWH> yoloDetections, int imageWidth, int imageHeight)
    {
        if (realDetectionProvider == null)
        {
            Debug.LogError("YoloToRealDetectionBridge: realDetectionProvider is not assigned.");
            return;
        }

        currentImageWidth = imageWidth;
        currentImageHeight = imageHeight;

        List<RealDetectionProvider.ModelDetection> converted = new List<RealDetectionProvider.ModelDetection>();

        if (yoloDetections != null)
        {
            foreach (var det in yoloDetections)
            {
                if (det == null) continue;
                if (string.IsNullOrWhiteSpace(det.label)) continue;
                if (det.confidence < minConfidence) continue;

                if (ignoreTinyBoxes && (det.width < minBoxWidth || det.height < minBoxHeight))
                    continue;

                Rect pixelRect = ClampRectToImage(
                    new Rect(det.x, det.y, det.width, det.height),
                    imageWidth,
                    imageHeight
                );

                if (pixelRect.width <= 0f || pixelRect.height <= 0f)
                    continue;

                RealDetectionProvider.ModelDetection modelDet = new RealDetectionProvider.ModelDetection
                {
                    label = det.label,
                    confidence = det.confidence,
                    pixelRect = pixelRect
                };

                converted.Add(modelDet);

                if (logDetections)
                {
                    Debug.Log($"[YOLO XYWH] {det.label} conf={det.confidence:F2} rect={pixelRect}");
                }
            }
        }

        realDetectionProvider.UpdateFromModelDetections(converted, imageWidth, imageHeight);
    }

    /// <summary>
    /// 调试用：手动推一个测试框。
    /// </summary>
    [ContextMenu("Push Debug Laptop Box")]
    public void PushDebugLaptopBox()
    {
        if (realDetectionProvider == null)
        {
            Debug.LogError("YoloToRealDetectionBridge: realDetectionProvider is not assigned.");
            return;
        }

        List<RealDetectionProvider.ModelDetection> converted = new List<RealDetectionProvider.ModelDetection>();

        RealDetectionProvider.ModelDetection det = new RealDetectionProvider.ModelDetection
        {
            label = "Laptop",
            confidence = 0.98f,
            pixelRect = new Rect(700f, 300f, 350f, 220f)
        };

        converted.Add(det);

        realDetectionProvider.UpdateFromModelDetections(converted, currentImageWidth, currentImageHeight);

        if (logDetections)
        {
            Debug.Log("[Bridge] Pushed debug laptop detection.");
        }
    }

    private Rect ClampRectToImage(Rect r, int imageWidth, int imageHeight)
    {
        float x = Mathf.Clamp(r.x, 0f, imageWidth);
        float y = Mathf.Clamp(r.y, 0f, imageHeight);
        float maxX = Mathf.Clamp(r.x + r.width, 0f, imageWidth);
        float maxY = Mathf.Clamp(r.y + r.height, 0f, imageHeight);

        float w = Mathf.Max(0f, maxX - x);
        float h = Mathf.Max(0f, maxY - y);

        return new Rect(x, y, w, h);
    }
}