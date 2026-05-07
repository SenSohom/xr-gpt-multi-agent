using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 把 PassthroughCameraAccess 的当前帧 Texture 暴露给:
///   1) targetRawImage(可选,如果你想真的渲染显示出来)
///   2) CurrentTexture(public 属性,任何脚本都能直接读,推荐这种方式)
/// </summary>
public class PassthroughCameraViewer : MonoBehaviour
{
    [Header("References")]
    public MonoBehaviour passthroughCameraAccessComponent;

    [Tooltip("可选:如果想把帧画到一张 RawImage 上(比如调试),指定它。否则可以为空。")]
    public RawImage targetRawImage;

    [Header("Behavior")]
    [Tooltip("在 Start 中容忍 targetRawImage 为空(不再报 Error)")]
    public bool allowNullRawImage = true;

    /// <summary>
    /// 当前帧的相机 Texture。null = 还没拿到。
    /// 其它脚本(YoloWebSocketClient / MRHUDController / SnapshotCapture)可以直接读这个。
    /// </summary>
    public Texture CurrentTexture { get; private set; }

    [Header("Diagnostics")]
    [Tooltip("每隔多少秒采样一次纹理像素,确认画面是否真有内容(0=禁用)")]
    public float sampleIntervalSeconds = 2f;

    private object cachedAccess;
    private MethodInfo getTextureMethod;
    private float nextSampleAt;
    private RenderTexture sampleRT;
    private Texture2D sampleCpuTex;

    private void Start()
    {
        if (passthroughCameraAccessComponent == null)
        {
            Debug.LogError("PassthroughCameraViewer: PassthroughCameraAccess component is not assigned.");
            return;
        }

        if (targetRawImage == null && !allowNullRawImage)
        {
            Debug.LogError("PassthroughCameraViewer: Target RawImage is not assigned.");
            return;
        }

        cachedAccess = passthroughCameraAccessComponent;
        var accessType = passthroughCameraAccessComponent.GetType();
        getTextureMethod = accessType.GetMethod("GetTexture");

        if (getTextureMethod == null)
        {
            Debug.LogError("PassthroughCameraViewer: GetTexture() method not found on PassthroughCameraAccess.");
        }
    }

    private void Update()
    {
        if (cachedAccess == null || getTextureMethod == null)
            return;

        Texture tex = getTextureMethod.Invoke(cachedAccess, null) as Texture;
        if (tex == null) return;

        CurrentTexture = tex;

        if (targetRawImage != null && targetRawImage.texture != tex)
        {
            targetRawImage.texture = tex;
        }

        // 周期性采样一小块,判定画面到底是不是黑的
        if (sampleIntervalSeconds > 0f && Time.unscaledTime >= nextSampleAt)
        {
            nextSampleAt = Time.unscaledTime + sampleIntervalSeconds;
            SampleAndReport(tex);
        }
    }

    private void SampleAndReport(Texture src)
    {
        if (src == null || src.width <= 0 || src.height <= 0) return;

        const int sw = 32;
        const int sh = 32;
        if (sampleRT == null || sampleRT.width != sw || sampleRT.height != sh)
        {
            if (sampleRT != null) { sampleRT.Release(); Destroy(sampleRT); }
            sampleRT = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32);
        }
        if (sampleCpuTex == null)
            sampleCpuTex = new Texture2D(sw, sh, TextureFormat.RGBA32, false);

        try
        {
            Graphics.Blit(src, sampleRT);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = sampleRT;
            sampleCpuTex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
            sampleCpuTex.Apply(false, false);
            RenderTexture.active = prev;

            var px = sampleCpuTex.GetPixels32();
            long r = 0, g = 0, b = 0;
            int n = px.Length;
            // 统计方差用于判定纯色
            int minR = 255, maxR = 0;
            for (int i = 0; i < n; i++)
            {
                r += px[i].r; g += px[i].g; b += px[i].b;
                if (px[i].r < minR) minR = px[i].r;
                if (px[i].r > maxR) maxR = px[i].r;
            }
            float mr = r / (float)n, mg = g / (float)n, mb = b / (float)n;
            int rRange = maxR - minR;

            // 检测 Link/Air Link 下 PCA 返回的占位灰阶图:
            // 三通道几乎相等(灰阶) + 偏中间灰 + range 接近 255(完整灰梯度) = 占位测试图
            float channelDiff = Mathf.Max(Mathf.Abs(mr - mg), Mathf.Abs(mg - mb), Mathf.Abs(mr - mb));
            bool looksLikePlaceholderGray =
                channelDiff < 2f &&
                mr > 100f && mr < 160f &&
                rRange > 200;

            string verdict;
            if (mr < 5f && mg < 5f && mb < 5f)
                verdict = "BLACK (camera not delivering frames — permission/Link mode/occupied?)";
            else if (mr > 250f && mg > 250f && mb > 250f)
                verdict = "WHITE (saturated/blank)";
            else if (rRange < 6)
                verdict = "UNIFORM (no real content)";
            else if (looksLikePlaceholderGray)
                verdict = "PLACEHOLDER GRAY GRADIENT (Quest Link/Air Link doesn't expose real passthrough — must Build&Run on device)";
            else
                verdict = "OK";

            Debug.Log($"[PCViewer-DIAG] tex={src.width}x{src.height} meanRGB=({mr:F0},{mg:F0},{mb:F0}) rRange={rRange} channelDiff={channelDiff:F1} -> {verdict}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[PCViewer-DIAG] sample failed: {e.Message}");
        }
    }

    private void OnDisable()
    {
        if (sampleRT != null) { sampleRT.Release(); Destroy(sampleRT); sampleRT = null; }
        if (sampleCpuTex != null) { Destroy(sampleCpuTex); sampleCpuTex = null; }
    }
}
