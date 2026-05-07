using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 假数据版本。用于先调通 UI / UX / HUD 流程。
/// Inspector 里直接填 detections 即可。
/// </summary>
public class FakeDetectionProvider : DetectionProviderBase
{
    public List<DetectedObjectData> detections = new List<DetectedObjectData>();

    public override IReadOnlyList<DetectedObjectData> GetDetections()
    {
        return detections;
    }
}