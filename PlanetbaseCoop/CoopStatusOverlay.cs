using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlanetbaseCoop
{
    public static class CoopStatusOverlay
    {
        private static readonly object Lock = new object();
        private static readonly Queue<string> Pending = new Queue<string>();
        private static string Current;
        private static float CurrentUntil;
        private static bool ConnectionWarning;
        private static GUIStyle sPanelStyle;
        private static GUIStyle sTextStyle;
        private static GUIStyle sPingStyle;

        public static void Notify(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return;
            lock (Lock)
            {
                Pending.Enqueue(text.Trim());
                while (Pending.Count > 8) Pending.Dequeue();
            }
        }

        public static void SetConnectionWarning(bool lost)
        {
            lock (Lock)
            {
                // A healthy connection is intentionally silent. The user only
                // needs a visible panel while a peer is actually unreachable.
                ConnectionWarning = lost;
                if (lost) Pending.Enqueue("BAGLANTI SINYALI ALINAMIYOR");
                while (Pending.Count > 8) Pending.Dequeue();
            }
        }

        public static void OnGUI()
        {
            // Oturum kapandiktan sonra da son bildirim (or. neden ana menuye
            // donuldugu) okunabilir kalir; ping/baglanti paneli yalniz co-op'ta.
            bool fullMode = SyncManager.FullMode;
            bool warning;
            string text;
            lock (Lock)
            {
                if (Pending.Count > 0 && (Current == null || Time.realtimeSinceStartup >= CurrentUntil))
                {
                    Current = Pending.Dequeue();
                    CurrentUntil = Time.realtimeSinceStartup + 4.5f;
                }
                warning = fullMode && ConnectionWarning;
                text = Time.realtimeSinceStartup < CurrentUntil ? Current : null;
            }

            string message = CoopText.T(warning ? "BAGLANTI SINYALI ALINAMIYOR" : text);
            SyncManager sync = SyncManager.Instance;
            int pingMilliseconds = 0;
            int jitterMilliseconds = 0;
            bool hasPing = fullMode && sync != null &&
                sync.TryGetNetworkTiming(out pingMilliseconds, out jitterMilliseconds);
            bool connected = fullMode && sync != null && sync.IsPeerConnected();
            if (String.IsNullOrEmpty(message) && !connected) return;
            try
            {
                EnsureStyles();
                Color oldBackground = GUI.backgroundColor;
                Color oldColor = GUI.color;
                const float margin = 22f;
                if (!String.IsNullOrEmpty(message))
                {
                    float panelWidth = Mathf.Min(760f, Mathf.Max(440f, Screen.width * 0.38f));
                    float panelHeight = warning ? 118f : 96f;
                    float x = Mathf.Max(margin, Screen.width - panelWidth - margin);
                    // Keep transient errors beside the game view, below the HUD,
                    // so they remain readable without covering resource counters.
                    float y = Mathf.Clamp(Screen.height * 0.42f, 180f,
                        Screen.height - panelHeight - margin);
                    Rect panel = new Rect(x, y, panelWidth, panelHeight);

                    GUI.backgroundColor = warning
                        ? new Color(0.24f, 0.035f, 0.02f, 0.94f)
                        : new Color(0.025f, 0.055f, 0.09f, 0.92f);
                    GUI.Box(panel, GUIContent.none, sPanelStyle);
                    GUI.color = warning
                        ? new Color(1f, 0.45f, 0.28f, 1f)
                        : new Color(0.65f, 0.9f, 1f, 1f);
                    GUI.Label(new Rect(panel.x + 18f, panel.y + 12f,
                        panel.width - 36f, panel.height - 24f), message, sTextStyle);
                }

                if (connected)
                {
                    bool critical = hasPing &&
                        CoopNetworkQuality.IsCriticalLatency(pingMilliseconds);
                    bool high = hasPing &&
                        CoopNetworkQuality.IsHighLatency(pingMilliseconds);
                    string pingText = hasPing
                        ? "PING  " + pingMilliseconds + " MS" +
                            (jitterMilliseconds >= 10 ? "   ±" + jitterMilliseconds : "")
                        : CoopText.T("PING  ÖLÇÜLÜYOR");
                    Rect pingPanel = new Rect(
                        Screen.width - 252f - margin,
                        Screen.height - 54f - margin,
                        252f,
                        54f);
                    GUI.backgroundColor = critical
                        ? new Color(0.34f, 0.035f, 0.02f, 0.96f)
                        : (high
                            ? new Color(0.32f, 0.17f, 0.015f, 0.96f)
                            : new Color(0.025f, 0.18f, 0.11f, 0.94f));
                    GUI.Box(pingPanel, GUIContent.none, sPanelStyle);
                    GUI.color = critical
                        ? new Color(1f, 0.35f, 0.24f, 1f)
                        : (high
                            ? new Color(1f, 0.78f, 0.22f, 1f)
                            : new Color(0.52f, 1f, 0.72f, 1f));
                    GUI.Label(pingPanel, pingText, sPingStyle);
                }
                GUI.backgroundColor = oldBackground;
                GUI.color = oldColor;
            }
            catch (Exception) { }
        }

        private static void EnsureStyles()
        {
            if (sPanelStyle == null)
            {
                sPanelStyle = new GUIStyle(GUI.skin.box);
                sPanelStyle.padding = new RectOffset(10, 10, 8, 8);
            }
            if (sTextStyle == null)
            {
                sTextStyle = new GUIStyle(GUI.skin.label);
                sTextStyle.fontSize = 30;
                sTextStyle.fontStyle = FontStyle.Bold;
                sTextStyle.alignment = TextAnchor.MiddleCenter;
                sTextStyle.wordWrap = true;
                sTextStyle.normal.textColor = Color.white;
            }
            if (sPingStyle == null)
            {
                sPingStyle = new GUIStyle(GUI.skin.label);
                sPingStyle.fontSize = 22;
                sPingStyle.fontStyle = FontStyle.Bold;
                sPingStyle.alignment = TextAnchor.MiddleCenter;
                sPingStyle.normal.textColor = Color.white;
            }
        }
    }
}
