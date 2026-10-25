using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MikuPlayerReplacer
{
    /// <summary>
    /// First-run notice. The mod ships no model of its own, so every character it
    /// shows is somebody else's work, each with its own terms. The player has to
    /// confirm that they will follow those terms before the mod replaces anything;
    /// declining quits the game.
    ///
    /// Two config entries carry the answer:
    ///   TermsAccepted      - "我同意" was clicked at least once. Until this is
    ///                        true the mod stays idle (MasterEnabled is false).
    ///   TermsDontShowAgain - the "不再显示" box was ticked in that same click.
    /// The notice is shown at every game start until both are true, which is
    /// exactly what the box promises.
    /// </summary>
    internal static class ConsentGate
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("MMS");

        private static ConfigEntry<bool> _accepted;
        private static ConfigEntry<bool> _dontShow;
        private static ConsentDialogHost _host;
        private static bool _answeredThisRun;

        internal static bool Agreed
        {
            get { return _accepted != null && _accepted.Value; }
        }

        internal static bool ShouldPrompt
        {
            get
            {
                return !(Agreed && _dontShow != null && _dontShow.Value);
            }
        }

        internal static void Bind(ConfigFile config)
        {
            _accepted = config.Bind("General", "TermsAccepted", false,
                "Written by the first-run notice: true once \"我同意\" has been " +
                "clicked. The mod does not replace the character until this is true.");
            _dontShow = config.Bind("General", "TermsDontShowAgain", false,
                "Written by the \"不再显示\" box of the first-run notice. Only " +
                "honoured together with TermsAccepted; with the box unticked the " +
                "notice is shown again at every game start.");
        }

        internal static void Accept(bool dontShow)
        {
            if (_accepted != null)
            {
                _accepted.Value = true;
            }
            if (_dontShow != null)
            {
                _dontShow.Value = dontShow;
            }
            try
            {
                if (_accepted != null && _accepted.ConfigFile != null)
                {
                    _accepted.ConfigFile.Save();
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not save the notice answer: " + e.Message);
            }
            Log.LogInfo("Notice accepted (dont show again: " + dontShow + ").");
            _answeredThisRun = true;
            CloseHost();
        }

        /// <summary>Called by the dialog's 我拒绝 button. Quits the game.</summary>
        internal static void Refuse()
        {
            Log.LogInfo("Notice declined: quitting the game.");
            CloseHost();
            try
            {
                Application.Quit();
            }
            catch (Exception e)
            {
                Log.LogWarning("Application.Quit failed: " + e.Message);
            }
        }

        /// <summary>
        /// Creates the notice when it still has to be answered. Called from the
        /// StartScreen / main menu patches, i.e. once the scene that destroys the
        /// BepInEx manager object in Single mode has loaded, so a
        /// DontDestroyOnLoad object created here survives into the game.
        /// </summary>
        internal static void EnsureHost()
        {
            // Showing it again after the player has already answered would mean
            // once per return to the main menu; the box promises once per game
            // start, so one answer per process run is enough.
            if (_host != null || !ShouldPrompt || _answeredThisRun)
            {
                return;
            }
            try
            {
                GameObject go = new GameObject("MMS_Consent");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _host = go.AddComponent<ConsentDialogHost>();
            }
            catch (Exception e)
            {
                _host = null;
                Log.LogWarning("Could not create the first-run notice: " + e.Message);
            }
        }

        private static void CloseHost()
        {
            ConsentDialogHost host = _host;
            _host = null;
            if (host != null)
            {
                try
                {
                    UnityEngine.Object.Destroy(host.gameObject);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    [HarmonyPatch(typeof(StartScreen), "Start")]
    internal static class StartScreenConsentPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            ConsentGate.EnsureHost();
        }
    }

    /// <summary>
    /// The notice itself, drawn with uGUI so it belongs to the same world as the
    /// game's own menus instead of the IMGUI console overlay. It is a small
    /// screen-space canvas above everything: dimmed backdrop that swallows
    /// clicks, a dark panel with the MMS logo and title, the terms text, the
    /// "不再显示" checkbox and the two answers. Built once, from StartScreen.Start.
    /// </summary>
    internal sealed class ConsentDialogHost : MonoBehaviour
    {
        private const string TitleText = "MMS 模组 · 使用须知";
        private const string SubtitleText = "MikuMikuSwim · Subnautica 主角模型替换";
        private const string BodyText =
            "本模组不附带任何模型。\n" +
            "使用任何模型前，请阅读并遵守该模型自带的 README 中的使用条款：\n" +
            "署名要求、可否修改、可否商用、禁止事项等。\n" +
            "模型的版权与使用许可归其作者所有。";
        private const string DontShowText = "不再显示（下次启动直接进入游戏）";

        /// <summary>Every glyph the notice can draw, prewarmed in the plugin Awake.</summary>
        internal const string PrewarmText = TitleText + SubtitleText + BodyText +
            DontShowText + "点击会直接退出游戏。";

        private static readonly Color Accent = new Color(0.35f, 0.85f, 0.87f);
        private static readonly Color BodyColor = new Color(0.85f, 0.92f, 0.94f);
        private static readonly Color DimColor = new Color(0.60f, 0.70f, 0.72f);

        private bool _dontShow;
        private EventSystem _eventSystem;
        private bool _navSaved;
        private bool _navWas;
        private bool _cursorSaved;
        private CursorLockMode _savedLock;
        private bool _savedVisible;
        private FPSInputModule _module;
        private uGUI_InputGroup _savedGroup;
        private bool _groupHeld;
        private BaseInputModule _blockedModule;
        private bool _blockedWasEnabled;

        private Image _agreeImage;
        private Image _refuseImage;
        private Image _checkImage;
        private Image _toggleHitImage;
        private RectTransform _agreeRect;
        private RectTransform _refuseRect;
        private RectTransform _toggleRow;
        private Color _agreeBase;
        private Color _refuseBase;
        private bool _hoverAgree;
        private bool _hoverRefuse;
        private bool _hoverToggle;

        private static FieldInfo _lastGroupField;
        private static bool _lastGroupLookupDone;

        private void Start()
        {
            BuildUi();
        }

        private void Update()
        {
            HoldPointer();
            HandlePointer();
        }

        private void OnDestroy()
        {
            RestoreInteraction();
        }

        // ---- construction ----------------------------------------------------

        private void BuildUi()
        {
            try
            {
                GameObject canvasGo = new GameObject("MMS_ConsentCanvas");
                canvasGo.transform.SetParent(transform, false);
                Canvas canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // The start menu's canvas is order 0; anything this high is drawn
                // over it and over the loading screen afterwards.
                canvas.sortingOrder = 30000;
                CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
                canvasGo.AddComponent<GraphicRaycaster>();

                TakeInteraction();

                // Dimmed backdrop. It stays a raycast target: if the game's own
                // input module gets a frame in before it is switched off, the
                // pointer stops here instead of reaching the menu below.
                RectTransform dim = NewRect("Dim", canvas.transform);
                Stretch(dim);
                Image dimImage = dim.gameObject.AddComponent<Image>();
                dimImage.sprite = PdaUiAssets.White;
                dimImage.color = new Color(0f, 0f, 0f, 0.76f);

                RectTransform panel = NewRect("Panel", canvas.transform);
                panel.anchorMin = new Vector2(0.5f, 0.5f);
                panel.anchorMax = new Vector2(0.5f, 0.5f);
                panel.pivot = new Vector2(0.5f, 0.5f);
                panel.anchoredPosition = Vector2.zero;
                panel.sizeDelta = new Vector2(760f, 440f);
                Image panelImage = panel.gameObject.AddComponent<Image>();
                panelImage.sprite = PdaUiAssets.White;
                panelImage.color = new Color(0.035f, 0.085f, 0.11f, 0.97f);
                Outline panelOutline = panel.gameObject.AddComponent<Outline>();
                panelOutline.effectColor = new Color(Accent.r, Accent.g, Accent.b, 0.55f);
                panelOutline.effectDistance = new Vector2(1.5f, -1.5f);

                // Thin accent bar along the top edge, like the game's panels.
                RectTransform bar = NewRect("Accent", panel);
                bar.anchorMin = new Vector2(0f, 1f);
                bar.anchorMax = new Vector2(1f, 1f);
                bar.pivot = new Vector2(0.5f, 1f);
                bar.anchoredPosition = new Vector2(0f, 0f);
                bar.sizeDelta = new Vector2(0f, 4f);
                Image barImage = bar.gameObject.AddComponent<Image>();
                barImage.sprite = PdaUiAssets.White;
                barImage.color = Accent;
                barImage.raycastTarget = false;

                // MMS logo + name: makes it obvious which mod is asking.
                Sprite logo = null;
                try
                {
                    logo = PdaUiAssets.TabIcon;
                }
                catch (Exception)
                {
                }
                float titleX = 30f;
                if (logo != null)
                {
                    RectTransform logoRect = NewRect("Logo", panel);
                    PlaceTopLeft(logoRect, 24f, 20f, 56f, 56f);
                    Image logoImage = logoRect.gameObject.AddComponent<Image>();
                    logoImage.sprite = logo;
                    logoImage.preserveAspect = true;
                    logoImage.raycastTarget = false;
                    titleX = 94f;
                }
                NewText("Title", panel, TitleText, 27, Color.white,
                    TextAnchor.UpperLeft, true, titleX, 24f, 620f, 40f);
                NewText("Subtitle", panel, SubtitleText, 15, DimColor,
                    TextAnchor.UpperLeft, false, titleX + 2f, 64f, 620f, 24f);

                RectTransform divider = NewRect("Divider", panel);
                PlaceTopLeft(divider, 28f, 100f, 704f, 2f);
                Image dividerImage = divider.gameObject.AddComponent<Image>();
                dividerImage.sprite = PdaUiAssets.White;
                dividerImage.color = new Color(Accent.r, Accent.g, Accent.b, 0.30f);
                dividerImage.raycastTarget = false;

                NewText("Body", panel, BodyText, 19, BodyColor,
                    TextAnchor.UpperLeft, false, 30f, 116f, 700f, 126f);

                BuildCheckbox(panel);
                BuildButton(panel, "我同意", 28f, new Color(0.075f, 0.30f, 0.34f),
                    new Color(0.75f, 0.95f, 0.97f), Accent, true);
                BuildButton(panel, "我拒绝", 392f, new Color(0.17f, 0.10f, 0.11f),
                    new Color(1f, 0.82f, 0.82f),
                    new Color(0.85f, 0.40f, 0.40f), false);

                NewText("Hint", panel, "点击“我拒绝”会直接退出游戏。", 14, DimColor,
                    TextAnchor.UpperLeft, false, 30f, 386f, 640f, 22f);
                NewText("Version", panel, "MMS v" + MikuPlayerReplacerPlugin.PluginVersion,
                    12, new Color(DimColor.r, DimColor.g, DimColor.b, 0.8f),
                    TextAnchor.UpperRight, false, 590f, 406f, 142f, 20f);
                LogInfo("First-run notice shown; the mod stays idle until it is accepted.");
            }
            catch (Exception e)
            {
                LogWarning("Could not build the first-run notice: " + e.Message);
            }
        }

        private void BuildCheckbox(RectTransform panel)
        {
            RectTransform row = NewRect("DontShow", panel);
            PlaceTopLeft(row, 26f, 254f, 700f, 40f);
            // Invisible hit area: clicking anywhere on the row toggles the box.
            Image hit = row.gameObject.AddComponent<Image>();
            hit.sprite = PdaUiAssets.White;
            hit.color = new Color(1f, 1f, 1f, 0f);

            RectTransform box = NewRect("Box", row);
            PlaceTopLeft(box, 2f, 6f, 28f, 28f);
            Image boxImage = box.gameObject.AddComponent<Image>();
            boxImage.sprite = PdaUiAssets.White;
            boxImage.color = new Color(0.05f, 0.13f, 0.16f, 1f);
            Outline boxOutline = box.gameObject.AddComponent<Outline>();
            boxOutline.effectColor = new Color(Accent.r, Accent.g, Accent.b, 0.8f);
            boxOutline.effectDistance = new Vector2(1.5f, -1.5f);

            RectTransform check = NewRect("Check", box);
            check.anchorMin = Vector2.zero;
            check.anchorMax = Vector2.one;
            check.offsetMin = new Vector2(6f, 6f);
            check.offsetMax = new Vector2(-6f, -6f);
            Image checkImage = check.gameObject.AddComponent<Image>();
            checkImage.sprite = PdaUiAssets.White;
            checkImage.color = new Color(Accent.r, Accent.g, Accent.b,
                _dontShow ? 1f : 0f);
            checkImage.raycastTarget = false;

            _toggleRow = row;
            _toggleHitImage = hit;
            _checkImage = checkImage;

            NewText("Label", row, DontShowText, 17, BodyColor, TextAnchor.MiddleLeft,
                false, 42f, 0f, 650f, 40f);
        }

        private void BuildButton(RectTransform panel, string label, float x,
                                 Color baseColor, Color textColor, Color outlineColor,
                                 bool agree)
        {
            RectTransform rt = NewRect(label, panel);
            PlaceTopLeft(rt, x, 318f, 340f, 54f);
            Image image = rt.gameObject.AddComponent<Image>();
            image.sprite = PdaUiAssets.White;
            image.color = baseColor;
            Outline outline = rt.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(outlineColor.r, outlineColor.g,
                outlineColor.b, 0.65f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            if (agree)
            {
                _agreeRect = rt;
                _agreeImage = image;
                _agreeBase = baseColor;
            }
            else
            {
                _refuseRect = rt;
                _refuseImage = image;
                _refuseBase = baseColor;
            }

            Text text = NewText("Label", rt, label, 20, textColor,
                TextAnchor.MiddleCenter, true, 0f, 0f, 340f, 54f);
            text.raycastTarget = false;
        }

        // ---- helpers ---------------------------------------------------------

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void PlaceTopLeft(RectTransform rt, float x, float y,
                                         float width, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(width, height);
        }

        private static Text NewText(string name, Transform parent, string value,
                                    int size, Color color, TextAnchor anchor,
                                    bool bold, float x, float y, float width,
                                    float height)
        {
            RectTransform rt = NewRect(name, parent);
            PlaceTopLeft(rt, x, y, width, height);
            Text text = rt.gameObject.AddComponent<Text>();
            text.font = MikuPlayerReplacerPlugin.UiFont;
            text.fontSize = size;
            text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            text.color = color;
            text.text = value;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            text.raycastTarget = false;
            return text;
        }

        private static void LogWarning(string message)
        {
            BepInEx.Logging.Logger.CreateLogSource("MMS").LogWarning(message);
        }

        private static void LogInfo(string message)
        {
            BepInEx.Logging.Logger.CreateLogSource("MMS").LogInfo(message);
        }

        // ---- modal behaviour -------------------------------------------------

        /// <summary>
        /// While the notice is up the game's menus must not act on keyboard or
        /// gamepad input behind it. Mouse clicks are swallowed by the dimmed
        /// backdrop once the notice is the only canvas the input module raycasts.
        /// Everything taken here is restored when the notice closes.
        /// </summary>
        private void TakeInteraction()
        {
            _eventSystem = EventSystem.current;
            if (_eventSystem != null)
            {
                try
                {
                    _navWas = _eventSystem.sendNavigationEvents;
                    _navSaved = true;
                    _eventSystem.sendNavigationEvents = false;
                }
                catch (Exception)
                {
                    _navSaved = false;
                }
            }
            try
            {
                _savedLock = Cursor.lockState;
                _savedVisible = Cursor.visible;
                _cursorSaved = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch (Exception)
            {
                _cursorSaved = false;
            }
            LogInfo("Notice is up; its clicks are handled directly.");
        }

        /// <summary>
        /// The notice is created at StartScreen.Start, before the menu scene
        /// builds its EventSystem and input module, so neither exists yet. Both
        /// are picked up here on the first frame they appear.
        ///
        /// The module is switched off while the notice is up: that stops every
        /// uGUI click from reaching the start menu underneath, and it makes the
        /// notice independent of Subnautica's group-scoped raycast (see
        /// SetLastGroup). The module is enabled again when the notice closes.
        /// </summary>
        private void HoldPointer()
        {
            if (_eventSystem == null)
            {
                _eventSystem = EventSystem.current;
            }
            if (_eventSystem != null && _blockedModule == null)
            {
                BaseInputModule module = _eventSystem.currentInputModule;
                if (module != null)
                {
                    _blockedModule = module;
                    _blockedWasEnabled = module.enabled;
                    LogInfo("Notice paused the game input module (" +
                        module.GetType().Name + ").");
                }
            }
            if (_blockedModule != null && _blockedModule.enabled)
            {
                _blockedModule.enabled = false;
            }
            KeepClicksOnNotice();
        }

        /// <summary>
        /// Subnautica's FPSInputModule does not raycast the whole UI while an
        /// input group is selected: GetMousePointerEventData calls
        /// uGUI_InputGroup.Raycast on that group's own canvas and only falls
        /// back to EventSystem.RaycastAll when lastGroup is null. And
        /// uGUI_InputGroup.Raycast returns true whenever its raycaster is
        /// active, hit or no hit, so the fallback never happens. Clearing
        /// lastGroup sends the module through RaycastAll, where the full-screen
        /// backdrop is on top and takes the click. The group's own selected
        /// state is not touched, so it is restored as it was when the notice
        /// closes.
        /// </summary>
        private void KeepClicksOnNotice()
        {
            if (_module == null)
            {
                _module = FPSInputModule.current;
                if (_module == null && _eventSystem != null)
                {
                    _module = _eventSystem.currentInputModule as FPSInputModule;
                }
                if (_module == null)
                {
                    return;
                }
                _groupHeld = true;
            }
            try
            {
                // The game may select a group again while the notice is up.
                uGUI_InputGroup group = _module.lastGroup;
                if (group != null)
                {
                    if (_savedGroup == null)
                    {
                        _savedGroup = group;
                    }
                    SetLastGroup(null);
                }
            }
            catch (Exception)
            {
            }
        }

        private void SetLastGroup(uGUI_InputGroup group)
        {
            if (!_lastGroupLookupDone)
            {
                _lastGroupLookupDone = true;
                _lastGroupField = typeof(FPSInputModule).GetField(
                    "<lastGroup>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (_lastGroupField == null)
                {
                    LogWarning("FPSInputModule.lastGroup not found; the notice " +
                        "may not receive clicks.");
                }
            }
            if (_lastGroupField != null && _module != null)
            {
                _lastGroupField.SetValue(_module, group);
            }
        }

        private void RestoreInteraction()
        {
            try
            {
                if (_groupHeld && _module != null && _module.lastGroup == null &&
                    _savedGroup != null && _savedGroup.selected)
                {
                    SetLastGroup(_savedGroup);
                }
            }
            catch (Exception)
            {
            }
            _groupHeld = false;
            _module = null;
            _savedGroup = null;
            try
            {
                if (_blockedModule != null && _blockedWasEnabled)
                {
                    _blockedModule.enabled = true;
                }
            }
            catch (Exception)
            {
            }
            _blockedModule = null;
            try
            {
                if (_navSaved && _eventSystem != null)
                {
                    _eventSystem.sendNavigationEvents = _navWas;
                }
            }
            catch (Exception)
            {
            }
            _navSaved = false;
            try
            {
                if (_cursorSaved)
                {
                    Cursor.lockState = _savedLock;
                    Cursor.visible = _savedVisible;
                }
            }
            catch (Exception)
            {
            }
            _cursorSaved = false;
        }

        // ---- direct pointer handling ----------------------------------------

        /// <summary>
        /// The notice reads the mouse itself instead of going through the
        /// EventSystem. At the start screen the game's pointer input is scoped
        /// to its own menu group and never reaches a separate canvas, and an
        /// IMGUI popup would be the old console-styled thing again. Polling
        /// Input and hit-testing the three interactive rectangles is a few
        /// lines and works with every input setup the game may use.
        /// </summary>
        private void HandlePointer()
        {
            try
            {
                Vector2 mouse = Input.mousePosition;
                bool overAgree = Contains(_agreeRect, mouse);
                bool overRefuse = Contains(_refuseRect, mouse);
                bool overToggle = Contains(_toggleRow, mouse);

                if (overAgree != _hoverAgree)
                {
                    _hoverAgree = overAgree;
                    if (_agreeImage != null)
                    {
                        _agreeImage.color = overAgree
                            ? Highlight(_agreeBase) : _agreeBase;
                    }
                }
                if (overRefuse != _hoverRefuse)
                {
                    _hoverRefuse = overRefuse;
                    if (_refuseImage != null)
                    {
                        _refuseImage.color = overRefuse
                            ? Highlight(_refuseBase) : _refuseBase;
                    }
                }
                if (overToggle != _hoverToggle)
                {
                    _hoverToggle = overToggle;
                    if (_toggleHitImage != null)
                    {
                        _toggleHitImage.color = new Color(1f, 1f, 1f,
                            overToggle ? 0.06f : 0f);
                    }
                }

                if (!Input.GetMouseButtonDown(0))
                {
                    return;
                }
                LogInfo("Notice click at " + mouse.x + "," + mouse.y + ".");
                if (overRefuse)
                {
                    ConsentGate.Refuse();
                }
                else if (overAgree)
                {
                    ConsentGate.Accept(_dontShow);
                }
                else if (overToggle)
                {
                    SetDontShow(!_dontShow);
                }
            }
            catch (Exception e)
            {
                LogWarning("Notice pointer error: " + e.Message);
            }
        }

        private void SetDontShow(bool value)
        {
            if (_dontShow == value)
            {
                return;
            }
            _dontShow = value;
            if (_checkImage != null)
            {
                _checkImage.color = new Color(Accent.r, Accent.g, Accent.b,
                    value ? 1f : 0f);
            }
            LogInfo("Notice checkbox: dont show again = " + value + ".");
        }

        private static bool Contains(RectTransform rt, Vector2 screenPoint)
        {
            // Screen-space overlay: no camera, so screen pixels map directly.
            return rt != null &&
                RectTransformUtility.RectangleContainsScreenPoint(
                    rt, screenPoint, null);
        }

        private static Color Highlight(Color color)
        {
            return new Color(Mathf.Min(color.r + 0.09f, 1f),
                Mathf.Min(color.g + 0.09f, 1f),
                Mathf.Min(color.b + 0.09f, 1f), color.a);
        }
    }
}
