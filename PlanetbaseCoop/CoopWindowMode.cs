using UnityEngine;

namespace PlanetbaseCoop
{
    public static class CoopWindowMode
    {
        private static bool sApplied;

        public static void Apply()
        {
            if (sApplied || !SyncManager.FullMode || !SyncManager.Fullscreen)
                return;

            try
            {
                Resolution resolution = Screen.currentResolution;
                Screen.SetResolution(resolution.width, resolution.height, FullScreenMode.FullScreenWindow);
                sApplied = true;
                CoopPlugin.Log.LogInfo("[Coop] borderless tam ekran etkin: " + resolution.width + "x" + resolution.height);
            }
            catch (System.Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] tam ekran ayarlanamadi: " + ex.Message);
            }
        }
    }
}
