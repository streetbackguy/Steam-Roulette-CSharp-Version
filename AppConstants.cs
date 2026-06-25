namespace SteamRoulette;

public static class AppConstants
{
    public const int AnimationDurationMs = 7600;
    public const int FrameDelayMs = 16;
    public const double SlowdownFactor = 0.95;
    public const double MinSpeed = 5.0;
    public const int PreloadWorkers = 10;
    public const string ImageCacheSubdir = "image_cache";

    public static readonly HashSet<string> NonGameAppIds = new()
    {
        "228980",  // Steamworks Common Redistributables
        "250820",  // SteamVR
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - Soldier
        "1628350", // Steam Linux Runtime - Sniper
        "1493710", // Proton Experimental
        "1887720", // Proton 7.0
        "2348590", // Proton 8.0
        "1245040", // Proton 5.0
        "1420170", // Proton 5.13
        "1580130", // Proton 6.3
        "223850",  // 3DMark
        "365670",  // Blender
        "431960",  // Wallpaper Engine
        "3419430", // Bongo Cat
    };
}
