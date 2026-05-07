using System.Collections;
using UnityEngine;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Quest 3 / 3S 的 Passthrough Camera Access 需要运行时申请 HEADSET_CAMERA 权限。
/// 把这个组件挂在场景里任意一个启动时就 active 的 GameObject 上(例如 Passthrough Camera Manager)。
/// 第一次启动会弹系统权限对话框,用户点 Allow 之后下一帧 PassthroughCameraAccess.GetTexture() 才会返回非 null。
/// </summary>
public class QuestCameraPermissionRequester : MonoBehaviour
{
    private const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";

    [Tooltip("是否在 Editor 也走这条路径(通常没必要)")]
    public bool runInEditor = false;

    [Tooltip("没拿到权限时的轮询间隔秒")]
    public float retryIntervalSeconds = 1.5f;

    [Tooltip("最多轮询多少次后放弃")]
    public int maxRetries = 30;

    private bool granted;

    private IEnumerator Start()
    {
#if !UNITY_EDITOR && UNITY_ANDROID
        yield return RequestAndWait();
#else
        if (runInEditor)
            yield return RequestAndWait();
        else
            yield break;
#endif
    }

    private IEnumerator RequestAndWait()
    {
#if UNITY_ANDROID
        // 1) 先请求 HEADSET_CAMERA(Quest 专用)
        if (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
        {
            Debug.Log("[QuestCamPerm] Requesting horizonos.permission.HEADSET_CAMERA ...");
            Permission.RequestUserPermission(HeadsetCameraPermission);
        }

        // 2) 再请求标准 CAMERA(某些路径会用到)
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Debug.Log("[QuestCamPerm] Requesting android.permission.CAMERA ...");
            Permission.RequestUserPermission(Permission.Camera);
        }

        // 3) 轮询直到拿到 HEADSET_CAMERA(用户可能要点弹窗)
        for (int i = 0; i < maxRetries; i++)
        {
            if (Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
            {
                granted = true;
                Debug.Log("[QuestCamPerm] HEADSET_CAMERA granted.");
                yield break;
            }
            yield return new WaitForSecondsRealtime(retryIntervalSeconds);
        }

        Debug.LogWarning("[QuestCamPerm] HEADSET_CAMERA not granted after retries. PassthroughCameraAccess will not deliver frames.");
#else
        yield break;
#endif
    }

    public bool IsGranted => granted;
}
