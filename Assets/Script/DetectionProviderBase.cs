using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 所有检测数据提供器的统一基类。
/// MRHUDController 只依赖这个接口，不再绑定具体实现。
/// </summary>
public abstract class DetectionProviderBase : MonoBehaviour
{
    public abstract IReadOnlyList<DetectedObjectData> GetDetections();
}