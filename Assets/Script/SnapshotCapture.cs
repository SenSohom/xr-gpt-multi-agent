using UnityEngine;

/// <summary>
/// 从 passthrough RawImage 的 Texture 上,按 viewport 矩形裁剪一张 Texture2D 快照。
/// viewportRect 约定:左下角原点,0~1 归一化(和 MRHUDController 保持一致)。
/// </summary>
public static class SnapshotCapture
{
    /// <summary>
    /// 从源 Texture 裁出指定 viewport 区域,返回独立的 Texture2D。
    /// 调用方负责在不需要时 Destroy()。
    /// </summary>
    public static Texture2D Crop(Texture source, Rect viewportRect, int maxLongEdge = 384)
    {
        if (source == null) return null;

        int srcW = source.width;
        int srcH = source.height;
        if (srcW <= 0 || srcH <= 0) return null;

        // viewportRect: 左下角原点,0~1
        // 转成像素坐标(左下角原点)
        float vx = Mathf.Clamp01(viewportRect.x);
        float vy = Mathf.Clamp01(viewportRect.y);
        float vw = Mathf.Clamp01(viewportRect.width);
        float vh = Mathf.Clamp01(viewportRect.height);

        int pxW = Mathf.Max(1, Mathf.RoundToInt(vw * srcW));
        int pxH = Mathf.Max(1, Mathf.RoundToInt(vh * srcH));
        int pxX = Mathf.Clamp(Mathf.RoundToInt(vx * srcW), 0, srcW - 1);
        int pxY = Mathf.Clamp(Mathf.RoundToInt(vy * srcH), 0, srcH - 1);

        if (pxX + pxW > srcW) pxW = srcW - pxX;
        if (pxY + pxH > srcH) pxH = srcH - pxY;
        if (pxW <= 0 || pxH <= 0) return null;

        // 计算输出尺寸(按最长边降采样到 maxLongEdge,保持比例)
        int outW = pxW;
        int outH = pxH;
        int longEdge = Mathf.Max(outW, outH);
        if (longEdge > maxLongEdge)
        {
            float scaleFactor = (float)maxLongEdge / longEdge;
            outW = Mathf.Max(1, Mathf.RoundToInt(outW * scaleFactor));
            outH = Mathf.Max(1, Mathf.RoundToInt(outH * scaleFactor));
        }

        // 用 RenderTexture 做 GPU 裁剪 + 缩放
        RenderTexture rt = RenderTexture.GetTemporary(outW, outH, 0, RenderTextureFormat.ARGB32);
        rt.filterMode = FilterMode.Bilinear;

        // Graphics.Blit 默认全图。这里我们用 Blit + scale/offset 来实现裁剪。
        // Unity Blit 用左下角原点的 UV
        Vector2 scale = new Vector2((float)pxW / srcW, (float)pxH / srcH);
        Vector2 offset = new Vector2((float)pxX / srcW, (float)pxY / srcH);

        Graphics.Blit(source, rt, scale, offset);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        Texture2D outTex = new Texture2D(outW, outH, TextureFormat.RGB24, false);
        outTex.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
        outTex.Apply(false, false);

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        return outTex;
    }

    /// <summary>
    /// 把 Texture2D 编码成 JPEG 字节流,适合传给 VLM 服务器。
    /// </summary>
    public static byte[] EncodeToJpeg(Texture2D tex, int quality = 80)
    {
        if (tex == null) return null;
        return tex.EncodeToJPG(quality);
    }
}
