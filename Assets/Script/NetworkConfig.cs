using UnityEngine;

/// <summary>
/// Single source of truth for the LAN server IP/port.
///
/// To change the server address (e.g. switching between home wifi, hotspot,
/// classroom wifi), edit ONE asset:
///     Assets/Resources/NetworkConfig.asset
/// Both VlmClient (HTTP) and YoloWebSocketClient (WebSocket) read this at
/// startup and override their inspector defaults.
///
/// Why Resources/? Anything in Assets/Resources/* is bundled into the build
/// and loadable at runtime via Resources.Load — no need to drag references
/// into the scene, no need to re-build when only the IP changes (just rebuild
/// the player; the asset value travels with the build).
/// </summary>
[CreateAssetMenu(fileName = "NetworkConfig", menuName = "VR-XR/Network Config", order = 1)]
public class NetworkConfig : ScriptableObject
{
    [Header("Server Address")]
    [Tooltip("LAN IP of the laptop running server.py. Get it via `ipconfig` → IPv4 Address.")]
    public string serverIp = "192.168.1.182";

    [Tooltip("Port server.py is listening on. Must match the --port flag in start.bat (default 8766).")]
    public int serverPort = 8766;

    [Header("Paths")]
    [Tooltip("WebSocket path for YOLO frame stream.")]
    public string yoloWsPath = "/yolo";

    /// <summary>Full HTTP base URL, e.g. "http://192.168.1.182:8766" — for VlmClient.</summary>
    public string HttpBaseUrl => $"http://{serverIp}:{serverPort}";

    /// <summary>Full WebSocket URL for YOLO, e.g. "ws://192.168.1.182:8766/yolo".</summary>
    public string YoloWebSocketUrl => $"ws://{serverIp}:{serverPort}{yoloWsPath}";

    private static NetworkConfig _cached;

    /// <summary>
    /// Loads the singleton NetworkConfig from Resources/NetworkConfig.asset.
    /// Returns null if the asset is missing — callers should fall back to their
    /// inspector defaults so the system stays useful even if Resources is empty.
    /// </summary>
    public static NetworkConfig Load()
    {
        if (_cached != null) return _cached;
        _cached = Resources.Load<NetworkConfig>("NetworkConfig");
        if (_cached == null)
            Debug.LogWarning("[NetworkConfig] Resources/NetworkConfig.asset not found — components will use their inspector default URLs.");
        return _cached;
    }
}
