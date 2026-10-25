using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace MikuPlayerReplacer
{
    /// <summary>
    /// IMGUI console opened with F2 (rebindable). The BepInEx manager object is
    /// destroyed when the game leaves the preloader scene, so this cannot live on
    /// the plugin component itself. A plain GameObject is created the first time
    /// the plugin ticks during gameplay and recreated if a scene load destroys it.
    /// It is drawn with the legacy IMGUI path so it works independently of the
    /// game's uGUI screens, including while the PDA is open.
    /// </summary>
    internal static class MmsConsole
    {
        private static MmsConsoleHost _host;
        private static bool _open;
        private static CaptureMode _capture;

        private const float WindowWidth = 470f;
        private const float WindowHeight = 700f;
        private const int WindowId = 0x4D4D53;

        private enum CaptureMode
        {
            None,
            Menu,
            Console
        }

        internal static bool IsOpen
        {
            get { return _open; }
        }

        internal static bool IsCapturing
        {
            get { return _capture != CaptureMode.None; }
        }

        internal static void EnsureHost()
        {
            if (_host != null)
            {
                return;
            }
            // A destroyed host means the object did not survive a scene change.
            // Put the cursor back before building a new one.
            if (_open)
            {
                _open = false;
                _capture = CaptureMode.None;
                RestoreCursor();
            }
            try
            {
                GameObject go = new GameObject("MMS_Console");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _host = go.AddComponent<MmsConsoleHost>();
            }
            catch (Exception)
            {
                _host = null;
            }
        }

        internal static void Toggle()
        {
            if (_open)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        internal static void Open()
        {
            EnsureHost();
            if (_host == null)
            {
                return;
            }
            if (_open)
            {
                return;
            }
            _open = true;
            _capture = CaptureMode.None;
            _savedLock = Cursor.lockState;
            _savedVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        internal static void Close()
        {
            if (!_open)
            {
                return;
            }
            _open = false;
            _capture = CaptureMode.None;
            RestoreCursor();
        }

        private static CursorLockMode _savedLock = CursorLockMode.None;
        private static bool _savedVisible = true;

        private static void RestoreCursor()
        {
            try
            {
                Cursor.lockState = _savedLock;
                Cursor.visible = _savedVisible;
            }
            catch (Exception)
            {
            }
        }

        // ---- drawing ---------------------------------------------------------

        private static GUIStyle _windowStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _hintStyle;
        private static GUIStyle _titleStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _toggleStyle;
        private static Font _stylesFont;

        private static void EnsureStyles()
        {
            Font font = MikuPlayerReplacerPlugin.UiFont;
            if (_windowStyle != null && ReferenceEquals(_stylesFont, font))
            {
                return;
            }
            _stylesFont = font;
            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.font = font;
            _windowStyle.fontSize = 16;
            _windowStyle.padding = new RectOffset(14, 14, 26, 14);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.font = font;
            _labelStyle.fontSize = 15;
            _labelStyle.richText = false;

            _titleStyle = new GUIStyle(_labelStyle);
            _titleStyle.fontSize = 17;
            _titleStyle.fontStyle = FontStyle.Bold;

            _hintStyle = new GUIStyle(GUI.skin.label);
            _hintStyle.font = font;
            _hintStyle.fontSize = 13;
            _hintStyle.richText = false;
            _hintStyle.wordWrap = true;

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.font = font;
            _buttonStyle.fontSize = 15;

            _toggleStyle = new GUIStyle(GUI.skin.toggle);
            _toggleStyle.font = font;
            _toggleStyle.fontSize = 15;
        }

        internal static void Draw()
        {
            if (!_open)
            {
                return;
            }
            MikuPlayerReplacerPlugin plugin = MikuPlayerReplacerPlugin.InstanceBox;
            if (ReferenceEquals(plugin, null))
            {
                return;
            }
            try
            {
                EnsureStyles();
                // The game re-locks the cursor every frame during gameplay, so a
                // one-shot unlock at open time is undone immediately. Hold it
                // unlocked while the console is up or the mouse can never reach
                // the window. LookFreeze below stops the same mouse movement from
                // spinning the first-person view.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                // Read the key before the window handles controls, and consume it
                // so it cannot also activate a focused button.
                HandleCapture(plugin);

                GUI.depth = -1000;
                Color previous = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.35f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height),
                    Texture2D.whiteTexture);
                // An invisible full-screen button behind the window swallows clicks
                // that miss it, so they cannot reach the game underneath.
                GUI.Button(new Rect(0f, 0f, Screen.width, Screen.height),
                    "", GUIStyle.none);
                GUI.color = previous;

                float w = Mathf.Min(WindowWidth, Screen.width - 40f);
                float h = Mathf.Min(WindowHeight, Screen.height - 40f);
                Rect rect = new Rect((Screen.width - w) * 0.5f,
                    (Screen.height - h) * 0.5f, w, h);
                GUILayout.Window(WindowId, rect, DrawWindow,
                    "MMS 控制台  MikuMikuSwim", _windowStyle);
            }
            catch (Exception)
            {
                // A GUI exception must never escape into the game's event loop.
            }
        }

        private static void DrawWindow(int id)
        {
            MikuPlayerReplacerPlugin plugin = MikuPlayerReplacerPlugin.InstanceBox;
            if (ReferenceEquals(plugin, null))
            {
                return;
            }

            GUILayout.Label("MMS 总开关", _titleStyle);
            bool enabled = GUILayout.Toggle(plugin.MasterEnabled,
                "  启用模型替换（关闭后恢复游戏原角色）", _toggleStyle);
            if (enabled != plugin.MasterEnabled)
            {
                plugin.SetMasterEnabled(enabled);
            }
            GUILayout.Label(plugin.MasterEnabled
                ? "状态：已开启"
                : "状态：已关闭，原版角色已恢复", _hintStyle);

            string model = plugin.ActiveModelId;
            GUILayout.Label("当前模型：" +
                (string.IsNullOrEmpty(model) ? "未选择（Models 文件夹为空）" : model), _hintStyle);

            GUILayout.Space(8f);
            GUILayout.Label("按键设置", _titleStyle);
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("菜单键位（打开 PDA 模型页）：" +
                    KeyLabel(plugin.MenuKeyCode), _labelStyle);
                if (GUILayout.Button(_capture == CaptureMode.Menu ? "按下按键…" : "修改",
                        _buttonStyle, GUILayout.Width(96f)))
                {
                    _capture = CaptureMode.Menu;
                }
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("控制台键位（打开本窗口）：" +
                    KeyLabel(plugin.ConsoleKeyCode), _labelStyle);
                if (GUILayout.Button(_capture == CaptureMode.Console ? "按下按键…" : "修改",
                        _buttonStyle, GUILayout.Width(96f)))
                {
                    _capture = CaptureMode.Console;
                }
            }
            if (_capture != CaptureMode.None)
            {
                GUILayout.Label("请按下一个按键（Esc 取消）", _hintStyle);
            }

            GUILayout.Space(8f);
            GUILayout.Label("音效", _titleStyle);
            bool mute = plugin.MutePlayerSounds != null && plugin.MutePlayerSounds.Value;
            bool muteNew = GUILayout.Toggle(mute,
                "  屏蔽主角音效（受伤痛呼、跳跃喘气、水下换气）", _toggleStyle);
            if (muteNew != mute && plugin.MutePlayerSounds != null)
            {
                plugin.MutePlayerSounds.Value = muteNew;
                MikuPlayerReplacerPlugin.MutePlayerSoundsActive = muteNew;
                plugin.MarkConfigDirty();
                plugin.SaveConfigIfDirty();
            }

            GUILayout.Space(10f);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("打开模型页", _buttonStyle))
                {
                    Close();
                    plugin.OpenModelTab();
                }
                if (GUILayout.Button("重建当前模型", _buttonStyle))
                {
                    plugin.RebuildModel();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("关闭", _buttonStyle, GUILayout.Width(80f)))
                {
                    Close();
                }
            }
            GUILayout.Label("按 " + KeyLabel(plugin.ConsoleKeyCode) +
                " 打开或关闭控制台；菜单键位也可以在这里改。", _hintStyle);

            if (GUI.changed)
            {
                // Keeps a dragged window usable; no config is written by layout.
            }
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private static void HandleCapture(MikuPlayerReplacerPlugin plugin)
        {
            if (_capture == CaptureMode.None)
            {
                return;
            }
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown)
            {
                return;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                _capture = CaptureMode.None;
                e.Use();
                return;
            }
            if (!IsBindable(e.keyCode))
            {
                // Keep waiting on modifier-only keys.
                e.Use();
                return;
            }
            if (_capture == CaptureMode.Menu)
            {
                plugin.SetMenuKey(e.keyCode);
            }
            else
            {
                plugin.SetConsoleKey(e.keyCode);
            }
            _capture = CaptureMode.None;
            e.Use();
        }

        private static bool IsBindable(KeyCode code)
        {
            if (code == KeyCode.None)
            {
                return false;
            }
            switch (code)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                case KeyCode.LeftCommand:
                case KeyCode.RightCommand:
                    return false;
                default:
                    return true;
            }
        }

        private static string KeyLabel(KeyCode code)
        {
            // Unity's names are ASCII ("F2", "V"); a few are worth prettifying.
            switch (code)
            {
                case KeyCode.UpArrow:
                    return "↑";
                case KeyCode.DownArrow:
                    return "↓";
                case KeyCode.LeftArrow:
                    return "←";
                case KeyCode.RightArrow:
                    return "→";
                case KeyCode.Return:
                    return "Enter";
                case KeyCode.Escape:
                    return "Esc";
                case KeyCode.Space:
                    return "空格";
                default:
                    return code.ToString();
            }
        }
    }

    /// <summary>
    /// While the console is open the cursor is free, but the game does not know
    /// the overlay exists and would keep steering the first-person camera with
    /// the same mouse movement. Zero the look axes for the game only (the IMGUI
    /// window reads the cursor position, not the axes, and is unaffected).
    /// </summary>
    internal static class LookFreeze
    {
        private static bool Blocked(string axis)
        {
            return MmsConsole.IsOpen
                && (axis == "Mouse X" || axis == "Mouse Y");
        }

        [HarmonyPatch(typeof(Input), "GetAxis", new Type[] { typeof(string) })]
        private static class GetAxis
        {
            private static bool Prefix(string axisName, ref float __result)
            {
                if (!Blocked(axisName))
                {
                    return true;
                }
                __result = 0f;
                return false;
            }
        }

        [HarmonyPatch(typeof(Input), "GetAxisRaw", new Type[] { typeof(string) })]
        private static class GetAxisRaw
        {
            private static bool Prefix(string axisName, ref float __result)
            {
                if (!Blocked(axisName))
                {
                    return true;
                }
                __result = 0f;
                return false;
            }
        }
    }

    internal sealed class MmsConsoleHost : MonoBehaviour
    {
        private void OnGUI()
        {
            MmsConsole.Draw();
        }
    }
}
