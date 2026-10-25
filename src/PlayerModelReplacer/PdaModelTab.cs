using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MikuPlayerReplacer
{
    // ------------------------------------------------------------------
    // Shared reflection into the game's PDA. Every field is optional: if a
    // lookup fails the page simply does not install and the rest of the
    // game is untouched.
    // ------------------------------------------------------------------
    internal static class PdaReflection
    {
        internal static readonly FieldInfo RegularTabs =
            AccessTools.Field(typeof(uGUI_PDA), "regularTabs");
        internal static readonly FieldInfo Tabs =
            AccessTools.Field(typeof(uGUI_PDA), "tabs");
        internal static readonly FieldInfo CurrentTabs =
            AccessTools.Field(typeof(uGUI_PDA), "currentTabs");
        internal static readonly FieldInfo ToolbarTooltips =
            AccessTools.Field(typeof(uGUI_PDA), "toolbarTooltips");

        internal static List<PDATab> GetRegularTabs()
        {
            try
            {
                return RegularTabs != null ? RegularTabs.GetValue(null) as List<PDATab> : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static Dictionary<PDATab, uGUI_PDATab> GetTabs(uGUI_PDA pda)
        {
            try
            {
                return Tabs != null ? Tabs.GetValue(pda) as Dictionary<PDATab, uGUI_PDATab> : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static List<PDATab> GetCurrentTabs(uGUI_PDA pda)
        {
            try
            {
                return CurrentTabs != null ? CurrentTabs.GetValue(pda) as List<PDATab> : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static List<string> GetToolbarTooltips(uGUI_PDA pda)
        {
            try
            {
                return ToolbarTooltips != null
                    ? ToolbarTooltips.GetValue(pda) as List<string>
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // ------------------------------------------------------------------
    // Runtime sprites. The white square backs every uGUI graphic (they are
    // tinted per element); the tab icon is drawn once by hand because the
    // game's tab atlas has no "Tab8" entry.
    // ------------------------------------------------------------------
    internal static class PdaUiAssets
    {
        private static Sprite _white;
        private static Sprite _tabIcon;
        private static Texture2D _whiteTex;
        private static Texture2D _iconTex;

        internal static Sprite White
        {
            get
            {
                if (_white == null)
                {
                    _whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _whiteTex.hideFlags = HideFlags.HideAndDontSave;
                    _whiteTex.SetPixel(0, 0, Color.white);
                    _whiteTex.Apply();
                    _white = Sprite.Create(_whiteTex, new Rect(0f, 0f, 1f, 1f),
                        new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                    _white.hideFlags = HideFlags.HideAndDontSave;
                }
                return _white;
            }
        }

        internal static Sprite TabIcon
        {
            get
            {
                if (_tabIcon == null)
                {
                    BuildTabIcon();
                }
                return _tabIcon;
            }
        }

        /// <summary>File shipped next to the DLL; the PDA tab shows it as the logo.</summary>
        internal const string LogoFileName = "MMS_logo.png";

        private static void BuildTabIcon()
        {
            const int Size = 64;
            _tabIcon = null;
            _iconTex = null;
            try
            {
                if (!BuildLogoIcon(Size))
                {
                    BuildSilhouetteIcon(Size);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MMS] Could not load " + LogoFileName
                    + ", using the built-in icon: " + e.Message);
                if (_iconTex != null)
                {
                    UnityEngine.Object.Destroy(_iconTex);
                    _iconTex = null;
                }
                BuildSilhouetteIcon(Size);
            }
            _tabIcon = Sprite.Create(_iconTex, new Rect(0f, 0f, Size, Size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _tabIcon.hideFlags = HideFlags.HideAndDontSave;
        }

        /// <summary>
        /// Loads MMS_logo.png from the plugin folder, trims the transparent
        /// margin and letterboxes the artwork into a square icon so the
        /// toolbar's packed mesh (which stretches the sprite to the button
        /// rect) cannot distort it. Returns false when the file is missing.
        /// </summary>
        private static bool BuildLogoIcon(int size)
        {
            string dir = Path.GetDirectoryName(
                typeof(MikuPlayerReplacerPlugin).Assembly.Location);
            string path = string.IsNullOrEmpty(dir)
                ? LogoFileName : Path.Combine(dir, LogoFileName);
            if (!File.Exists(path))
            {
                return false;
            }

            Texture2D src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            src.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                if (!src.LoadImage(File.ReadAllBytes(path)))
                {
                    return false;
                }
                Color32[] px = src.GetPixels32();
                int w = src.width;
                int h = src.height;
                int minX = w;
                int minY = h;
                int maxX = -1;
                int maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        // Ignore the faint halo some PNG exporters leave around
                        // the artwork; only real pixels define the crop.
                        if (px[row + x].a <= 16)
                        {
                            continue;
                        }
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (maxX < minX || maxY < minY)
                {
                    return false;
                }

                int cw = maxX - minX + 1;
                int ch = maxY - minY + 1;
                const float pad = 3f;
                float avail = size - pad * 2f;
                float scale = Mathf.Min(avail / cw, avail / ch);
                float dw = cw * scale;
                float dh = ch * scale;
                float dx = (size - dw) * 0.5f;
                float dy = (size - dh) * 0.5f;

                Color32[] dst = new Color32[size * size];
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = new Color32(255, 255, 255, 0);
                }
                for (int y = 0; y < size; y++)
                {
                    float fy0 = (y - dy) / dh;
                    float fy1 = (y + 1 - dy) / dh;
                    if (fy0 < 0f || fy1 > 1f)
                    {
                        continue;
                    }
                    int sy0 = minY + Mathf.FloorToInt(fy0 * ch);
                    int sy1 = Mathf.Min(sy0 + Mathf.Max(1,
                        Mathf.RoundToInt((fy1 - fy0) * ch)), minY + ch);
                    for (int x = 0; x < size; x++)
                    {
                        float fx0 = (x - dx) / dw;
                        float fx1 = (x + 1 - dx) / dw;
                        if (fx0 < 0f || fx1 > 1f)
                        {
                            continue;
                        }
                        int sx0 = minX + Mathf.FloorToInt(fx0 * cw);
                        int sx1 = Mathf.Min(sx0 + Mathf.Max(1,
                            Mathf.RoundToInt((fx1 - fx0) * cw)), minX + cw);
                        // Box filter with alpha-weighted colour: averaging the
                        // raw RGB would drag the transparent black margin into
                        // the edges and leave a dark fringe.
                        float sr = 0f, sg = 0f, sb = 0f, sa = 0f;
                        int n = 0;
                        for (int yy = sy0; yy < sy1; yy++)
                        {
                            int row = yy * w;
                            for (int xx = sx0; xx < sx1; xx++)
                            {
                                Color32 c = px[row + xx];
                                float a = c.a;
                                if (a <= 0f)
                                {
                                    continue;
                                }
                                sr += c.r * a;
                                sg += c.g * a;
                                sb += c.b * a;
                                sa += a;
                                n++;
                            }
                        }
                        if (n == 0 || sa <= 0f)
                        {
                            continue;
                        }
                        dst[y * size + x] = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(sr / sa), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(sg / sa), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(sb / sa), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(sa / n), 0, 255));
                    }
                }

                _iconTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                _iconTex.hideFlags = HideFlags.HideAndDontSave;
                _iconTex.filterMode = FilterMode.Bilinear;
                _iconTex.wrapMode = TextureWrapMode.Clamp;
                _iconTex.SetPixels32(dst);
                _iconTex.Apply();
                return true;
            }
            finally
            {
                UnityEngine.Object.Destroy(src);
            }
        }

        private static void BuildSilhouetteIcon(int Size)
        {
            _iconTex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            _iconTex.hideFlags = HideFlags.HideAndDontSave;
            _iconTex.filterMode = FilterMode.Bilinear;
            _iconTex.wrapMode = TextureWrapMode.Clamp;

            Color clear = new Color(1f, 1f, 1f, 0f);
            Color solid = Color.white;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    bool on = false;

                    // Head.
                    float dx = px - 32f;
                    float dy = py - 43f;
                    if (dx * dx + dy * dy <= 9.5f * 9.5f)
                    {
                        on = true;
                    }

                    // Neck.
                    if (!on && px >= 28f && px <= 36f && py >= 30f && py <= 35f)
                    {
                        on = true;
                    }

                    // Shoulders and torso: wide at the top, tapering down.
                    if (!on && py >= 8f && py <= 32f)
                    {
                        float half = 21f - (32f - py) * 0.42f;
                        if (Mathf.Abs(px - 32f) <= half)
                        {
                            on = true;
                        }
                    }

                    _iconTex.SetPixel(x, y, on ? solid : clear);
                }
            }
            _iconTex.Apply();
        }
    }

    // ------------------------------------------------------------------
    // The model page itself.
    // ------------------------------------------------------------------
    internal sealed class PdaModelTab : uGUI_PDATab
    {
        internal const PDATab Tab = (PDATab)8;
        internal const int PreviewLayer = 31;

        // The SetTabs patch only injects the extra toolbar entry while this is
        // true. It stays false until both the SetTabs and Initialize patches
        // have been applied, and flips back off if attaching the page fails, so
        // the game can never end up with a tab in currentTabs that has no page.
        internal static bool Enabled;

        private static readonly Color PanelColor = new Color(0.03f, 0.12f, 0.15f, 0.55f);
        private static readonly Color SunkenColor = new Color(0f, 0.04f, 0.05f, 0.6f);
        private static readonly Color TextColor = new Color(0.87f, 0.97f, 0.99f, 1f);
        private static readonly Color DimColor = new Color(0.62f, 0.79f, 0.82f, 1f);
        private static readonly Color AccentColor = new Color(0.24f, 0.78f, 0.85f, 0.95f);
        private static readonly Color RowColor = new Color(0.08f, 0.25f, 0.29f, 0.65f);
        private static readonly Color RowActiveColor = new Color(0.16f, 0.55f, 0.62f, 0.9f);

        private uGUI_PDA _pdaUi;
        private CanvasGroup _group;
        private RectTransform _root;
        private Font _font;

        private RectTransform _modelContent;
        private RectTransform _materialContent;
        private RectTransform _tuneContent;
        private RawImage _previewImage;
        private int _previewUiProbeLeft = 2;
        private Text _statusText;
        private Toggle _tgHairPhysics;
        private Toggle _tgSkirtPhysics;
        private Toggle _tgClothesPhysics;
        private Toggle _tgAccessoryPhysics;
        private Toggle _tgSurfacePhysics;
        private Toggle _tgCollision;
        private Toggle _tgLimbCollision;
        private Toggle _tgShowHair;
        private Toggle _tgMutePlayer;
        private Slider _slFringe;
        private Slider _slStiffness;
        private Slider _slGravity;
        private Slider _slDamping;
        private Slider _slFlow;
        private Slider _slTwist;
        private Slider _slSkirtStiffness;
        private Slider _slSkirtLeash;
        private Slider _slSkirtClearance;
        private Slider _slAirGravity;
        private Slider _slAirDamping;
        private Slider _slGlow;
        private Slider _slHeadLook;
        private Slider _slIdleDelay;
        private Slider _slPoseTransition;
        private Text _valFringe;
        private Text _valStiffness;
        private Text _valGravity;
        private Text _valDamping;
        private Text _valFlow;
        private Text _valTwist;
        private Text _valSkirtStiffness;
        private Text _valSkirtLeash;
        private Text _valSkirtClearance;
        private Text _valAirGravity;
        private Text _valAirDamping;
        private Text _valGlow;
        private Text _valHeadLook;
        private Text _valIdleDelay;
        private Text _valPoseTransition;
        private readonly List<GameObject> _materialRows = new List<GameObject>();

        private readonly List<MaterialPart> _parts = new List<MaterialPart>();
        private int _lastGeneration = -1;
        private int _lastPreviewGeneration = -1;
        private int _lastAutoPreviewGen = -1;
        private string _lastModelId;
        private bool _open;

        private Camera _camera;
        private RenderTexture _rt;
        private float _rtAspect = -1f;
        private Light _light;

        // The preview is a static T-pose, so it only needs to be re-rendered
        // when something actually changed: the model, the camera orbit/zoom/pan,
        // or the material visibility. The worn model fallback is animated and
        // still renders every frame.
        private bool _previewDirty = true;
        private int _lastRenderPreviewGen = -1;
        private float _lastRenderYaw;
        private float _lastRenderPitch;
        private float _lastRenderZoom;
        private float _lastRenderPanX;
        private float _lastRenderPanY;

        // Turntable angles for the preview camera. They orbit the camera, not the
        // model: the mesh in the preview is the one worn in the world, so nothing
        // here may rotate it.
        private float _orbitYaw;
        private float _orbitPitch;
        // Wheel zoom factor (1 = framed) and the pan offset in screen pixels.
        private float _zoom = 1f;
        private float _panX;
        private float _panY;

        internal void OnPreviewDrag(Vector2 delta)
        {
            // Inverted on purpose: dragging the image to the right has to spin the
            // model to the right, which means orbiting the camera the other way.
            _orbitYaw = Mathf.Repeat(_orbitYaw + delta.x * 0.45f + 180f, 360f) - 180f;
            _orbitPitch = Mathf.Clamp(_orbitPitch - delta.y * 0.3f, -70f, 70f);
        }

        internal void OnPreviewPan(Vector2 delta)
        {
            // Right drag moves the framed point; the model follows the cursor.
            _panX += delta.x;
            _panY += delta.y;
            float limit = 3000f;
            _panX = Mathf.Clamp(_panX, -limit, limit);
            _panY = Mathf.Clamp(_panY, -limit, limit);
        }

        internal void OnPreviewScroll(float scroll)
        {
            // Wheel up (positive) zooms in, i.e. shortens the camera distance.
            _zoom = Mathf.Clamp(_zoom * Mathf.Pow(0.9f, scroll), 0.3f, 3f);
        }

        private void ResetPreviewView()
        {
            _orbitYaw = 0f;
            _orbitPitch = 0f;
            _zoom = 1f;
            _panX = 0f;
            _panY = 0f;
        }

        private static MikuPlayerReplacerPlugin Plugin
        {
            get { return MikuPlayerReplacerPlugin.InstanceBox; }
        }

        // ---- installation -------------------------------------------------

        /// <summary>
        /// Called from the Initialize postfix. Creates a page under the same parent
        /// as the regular tabs and registers it in the private tab table. Returns
        /// false when the game's layout could not be found.
        /// </summary>
        internal static bool Attach(uGUI_PDA pdaUi)
        {
            if (pdaUi == null || pdaUi.tabLog == null)
            {
                return false;
            }
            Dictionary<PDATab, uGUI_PDATab> tabs = PdaReflection.GetTabs(pdaUi);
            if (tabs == null)
            {
                return false;
            }
            if (tabs.ContainsKey(Tab))
            {
                return true;
            }

            Transform parent = pdaUi.tabLog.transform.parent;
            if (parent == null)
            {
                return false;
            }

            GameObject go = new GameObject("PdaModelTab", typeof(RectTransform));
            go.layer = pdaUi.tabLog.gameObject.layer;
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            RectTransform source = pdaUi.tabLog.transform as RectTransform;
            if (source != null)
            {
                rt.anchorMin = source.anchorMin;
                rt.anchorMax = source.anchorMax;
                rt.pivot = source.pivot;
                rt.anchoredPosition = source.anchoredPosition;
                rt.sizeDelta = source.sizeDelta;
                rt.localScale = source.localScale;
                rt.localRotation = source.localRotation;
            }
            rt.SetAsLastSibling();

            CanvasGroup group = go.AddComponent<CanvasGroup>();
            PdaModelTab tab = go.AddComponent<PdaModelTab>();
            tab.Register(pdaUi);
            tab.Setup(pdaUi, group);
            tab.Close();
            tabs[Tab] = tab;
            Enabled = true;

            // Initialize already rebuilt the toolbar before this page existed, so
            // the very first PDA open would otherwise show no model tab. Rebuild
            // it once here; every later OnClosePDA goes through the patched
            // SetTabs and picks the tab up automatically.
            try
            {
                List<PDATab> regular = PdaReflection.GetRegularTabs();
                if (regular != null && !regular.Contains(Tab))
                {
                    List<PDATab> withModel = new List<PDATab>(regular);
                    withModel.Add(Tab);
                    pdaUi.SetTabs(withModel);
                }
            }
            catch (Exception)
            {
            }

            // The preview renders after every pose script (LateRetargetHost uses
            // 32000 and FinalIK sits on its own components).
            PdaPreviewHost host = go.AddComponent<PdaPreviewHost>();
            host.Tab = tab;
            return true;
        }

        /// <summary>Used when Attach throws: rebuild the toolbar without the model
        /// tab so uGUI_PDA's own indexing stays consistent.</summary>
        internal static void Fallback(uGUI_PDA pdaUi)
        {
            try
            {
                List<PDATab> regular = PdaReflection.GetRegularTabs();
                if (pdaUi == null || regular == null)
                {
                    return;
                }
                pdaUi.SetTabs(new List<PDATab>(regular));
            }
            catch (Exception)
            {
            }
        }

        // ---- lifecycle ----------------------------------------------------

        internal void Setup(uGUI_PDA pdaUi, CanvasGroup group)
        {
            _pdaUi = pdaUi;
            _group = group;
            _font = MikuPlayerReplacerPlugin.UiFont;
            if (_font == null)
            {
                try
                {
                    _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
                catch (Exception)
                {
                    _font = null;
                }
            }
            _root = transform as RectTransform;
            ApplyPageRect();
            BuildUi();
            Show(false);
        }

        /// <summary>
        /// The regular pages are laid out over the whole PDA screen and the tab
        /// strip is drawn on top of them. Copying that rect as-is - which Attach
        /// does - put this page under the toolbar and hid the tab icons. Stretch
        /// over the parent instead and keep the top edge below the toolbar's
        /// bottom. Runs again on every open: the toolbar is laid out by the game
        /// after Attach, so the first measurement can be stale.
        /// </summary>
        private void ApplyPageRect()
        {
            if (_root == null)
            {
                return;
            }
            RectTransform parentRt = _root.parent as RectTransform;
            if (parentRt == null)
            {
                return;
            }

            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            // Bottom gap clears the PDA's quick-slot row, which is drawn over the
            // lower edge of every page.
            _root.offsetMin = new Vector2(24f, 64f);

            float top = 24f;
            RectTransform toolbarRt = null;
            try
            {
                if (_pdaUi != null && _pdaUi.toolbar != null)
                {
                    // The toolbar's own rect field is private; its transform is the
                    // same object.
                    toolbarRt = _pdaUi.toolbar.transform as RectTransform;
                }
            }
            catch (Exception)
            {
                toolbarRt = null;
            }
            if (toolbarRt != null && parentRt.rect.height > 1f)
            {
                Vector3[] corners = new Vector3[4];
                toolbarRt.GetWorldCorners(corners);
                float toolbarBottom = parentRt.InverseTransformPoint(corners[0]).y;
                float gap = parentRt.rect.yMax - toolbarBottom;
                if (gap > 0f && gap < parentRt.rect.height * 0.5f)
                {
                    top = gap + 12f;
                }
            }
            _root.offsetMax = new Vector2(-24f, -top);
        }

        public override void Open()
        {
            _open = true;
            ApplyPageRect();
            Show(true);
            EnsureRig();
            _previewDirty = true;
            MikuPlayerReplacerPlugin p = Plugin;
            _lastGeneration = !ReferenceEquals(p, null) ? p.BuildGeneration : -1;
            _lastModelId = !ReferenceEquals(p, null) ? p.ActiveModelId : null;
            if (!ReferenceEquals(p, null) && !p.PreviewBuilt && p.ModelCount > 0)
            {
                // First open: preload the model that is currently worn so the page
                // always starts with something on screen.
                int active = p.ActiveModelIndex;
                if (active >= 0)
                {
                    p.PreviewModelAt(active);
                }
            }
            _lastPreviewGeneration = !ReferenceEquals(p, null) ? p.PreviewGeneration : -1;
            BuildModelList();
            BuildMaterialList();
            UpdateStatus();
        }

        public override void Close()
        {
            _open = false;
            Show(false);
        }

        public override void OnClosePDA()
        {
            _open = false;
            Show(false);
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.SaveConfigIfDirty();
            }
        }

        public override void OnWarmUp()
        {
            // WarmUp is called once while the PDA is closed. Building the page is
            // cheap; the preview only runs while the page is actually visible.
        }

        private bool IsShowing
        {
            get
            {
                if (!_open || _pdaUi == null)
                {
                    return false;
                }
                Player player = Player.main;
                if (player == null)
                {
                    return false;
                }
                PDA pda = player.GetPDA();
                if (pda == null || !pda.isInUse)
                {
                    return false;
                }
                return _pdaUi.currentTabType == Tab;
            }
        }

        internal void HostLateUpdate()
        {
            if (!IsShowing)
            {
                return;
            }
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }

            if (p.BuildGeneration != _lastGeneration || p.ActiveModelId != _lastModelId ||
                p.PreviewGeneration != _lastPreviewGeneration)
            {
                _lastGeneration = p.BuildGeneration;
                _lastModelId = p.ActiveModelId;
                _lastPreviewGeneration = p.PreviewGeneration;
                BuildModelList();
                BuildMaterialList();
            }

            // Applying a model tears the preview down and leaves the page empty until
            // the worn model finishes building. Put a preview of the (now current)
            // model back as soon as that happens; it reuses the worn assets, so it is
            // cheap. Only once per build, so a model that cannot be previewed does not
            // retry every frame.
            if (!p.PreviewBuilt && p.IsModelBuilt && p.ModelCount > 0 &&
                p.BuildGeneration != _lastAutoPreviewGen)
            {
                _lastAutoPreviewGen = p.BuildGeneration;
                int active = p.ActiveModelIndex;
                if (active >= 0)
                {
                    p.PreviewModelAt(active);
                }
            }

            UpdateStatus();

            bool canRender = p.PreviewBuilt || (p.IsModelBuilt && p.ModelVisibleInWorld);
            if (_previewImage != null && _previewImage.enabled != canRender)
            {
                _previewImage.enabled = canRender;
            }
            if (!canRender)
            {
                return;
            }
            // A model was just clicked and is still parsing/decoding. The copy on
            // screen is the previous preview; leave its last frame alone (the view
            // was reset under it) until the clicked model's T-pose is ready.
            if (p.PreviewPending && p.PreviewBuilt)
            {
                return;
            }
            EnsureRig();
            if (_camera == null)
            {
                return;
            }

            // Keep the render texture at the frame's aspect. The rect is only
            // laid out once the page has been shown, so this can happen on the
            // first update after opening (or after a UI scale change).
            float aspect = PreviewAspect;
            if (_rt == null || Mathf.Abs(aspect - _rtAspect) > 0.02f)
            {
                CreateRenderTexture();
            }

            // The standalone preview is a static T-pose, so it only needs to be
            // drawn when the model, the camera or a visibility toggle changed.
            // The worn model fallback still animates, so it keeps rendering.
            bool standalone = p.PreviewBuilt;
            bool needsRender = _previewDirty || !standalone ||
                p.PreviewGeneration != _lastRenderPreviewGen ||
                _orbitYaw != _lastRenderYaw || _orbitPitch != _lastRenderPitch ||
                _zoom != _lastRenderZoom || _panX != _lastRenderPanX ||
                _panY != _lastRenderPanY;
            if (!needsRender)
            {
                return;
            }
            _previewDirty = false;
            _lastRenderPreviewGen = p.PreviewGeneration;
            _lastRenderYaw = _orbitYaw;
            _lastRenderPitch = _orbitPitch;
            _lastRenderZoom = _zoom;
            _lastRenderPanX = _panX;
            _lastRenderPanY = _panY;

            if (_light != null)
            {
                _light.transform.rotation =
                    Quaternion.Euler(35f, _camera.transform.eulerAngles.y + 25f, 0f);
            }
            if (_previewUiProbeLeft > 0)
            {
                // One-shot: says whether the white rectangle is the image holding a
                // real render texture or the image falling back to a null texture.
                _previewUiProbeLeft--;
                Texture tex = _previewImage != null ? _previewImage.texture : null;
                p.LogInfo(string.Format(
                    "Preview UI probe: image={0} enabled={1} tex={2} rtActive={3} camOn={4} imgColor={5}",
                    _previewImage != null,
                    _previewImage != null && _previewImage.enabled,
                    tex != null ? tex.name + "/" + tex.GetType().Name : "null",
                    RenderTexture.active != null ? RenderTexture.active.name : "null",
                    _camera != null && _camera.enabled,
                    _previewImage != null ? _previewImage.color : Color.clear));
            }
            p.TickPreview(_camera, _light, _orbitYaw, _orbitPitch, _zoom, _panX, _panY);
        }

        private void UpdateStatus()
        {
            if (_statusText == null)
            {
                return;
            }
            MikuPlayerReplacerPlugin p = Plugin;
            string message;
            if (!ReferenceEquals(p, null) && !p.MasterEnabled)
            {
                message = "MMS 已关闭（按 F2 打开控制台开启）";
            }
            else if (ReferenceEquals(p, null) || (!p.PreviewBuilt && !p.IsModelBuilt))
            {
                message = "正在加载模型…";
            }
            else
            {
                message = "";
            }
            if (_statusText.text != message)
            {
                _statusText.text = message;
            }
            _statusText.enabled = message.Length > 0;
        }

        private void Show(bool on)
        {
            if (_group != null)
            {
                _group.alpha = on ? 1f : 0f;
                _group.interactable = on;
                _group.blocksRaycasts = on;
            }
        }

        // ---- preview rig ---------------------------------------------------

        private void EnsureRig()
        {
            if (_camera != null)
            {
                return;
            }
            GameObject camGo = new GameObject("MikuPdaPreviewCamera");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            _camera = camGo.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            // Transparent clear: the rendered doll floats on the PDA page instead of
            // sitting on a solid rectangle.
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.fieldOfView = 30f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 60f;
            _camera.useOcclusionCulling = false;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.cullingMask = 1 << PreviewLayer;
            _camera.stereoTargetEye = StereoTargetEyeMask.None;

            CreateRenderTexture();

            GameObject lightGo = new GameObject("MikuPdaPreviewLight");
            lightGo.hideFlags = HideFlags.HideAndDontSave;
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Directional;
            _light.color = new Color(1f, 0.97f, 0.94f, 1f);
            _light.intensity = 1.1f;
            _light.shadows = LightShadows.None;
            _light.cullingMask = 1 << PreviewLayer;
            _light.enabled = false;
        }

        private float PreviewAspect
        {
            get
            {
                if (_previewImage != null)
                {
                    Rect rect = _previewImage.rectTransform.rect;
                    if (rect.width > 1f && rect.height > 1f)
                    {
                        return Mathf.Clamp(rect.width / rect.height, 0.4f, 4f);
                    }
                }
                return 16f / 9f;
            }
        }

        /// <summary>
        /// The image fills the whole preview frame, so the render texture (and
        /// with it the camera projection) is built at the frame's own aspect
        /// instead of a square. Height is fixed; the width follows the frame.
        /// </summary>
        private void CreateRenderTexture()
        {
            float aspect = PreviewAspect;
            const int height = 720;
            int width = Mathf.Clamp(Mathf.RoundToInt(height * aspect), 320, 2048);
            RenderTexture rt = new RenderTexture(width, height, 24,
                RenderTextureFormat.ARGB32);
            rt.name = "MikuPdaPreview";
            rt.antiAliasing = 1;
            rt.Create();

            if (_camera != null)
            {
                _camera.targetTexture = null;
                _camera.targetTexture = rt;
                _camera.aspect = (float)width / height;
            }
            if (_previewImage != null)
            {
                _previewImage.texture = rt;
            }
            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
            }
            _rt = rt;
            _rtAspect = aspect;
            _previewDirty = true;
        }

        private void OnDestroy()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
                Destroy(_camera.gameObject);
                _camera = null;
            }
            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
                _rt = null;
            }
            if (_light != null)
            {
                Destroy(_light.gameObject);
                _light = null;
            }
        }

        // ---- page construction ---------------------------------------------

        private void BuildUi()
        {
            if (_root == null)
            {
                return;
            }

            // Left column: the model list.
            RectTransform left = NewRect("ModelColumn", _root);
            left.anchorMin = new Vector2(0f, 0f);
            left.anchorMax = new Vector2(0f, 1f);
            left.pivot = new Vector2(0f, 0.5f);
            left.offsetMin = new Vector2(0f, 0f);
            left.offsetMax = new Vector2(300f, 0f);
            NewImage(left, PanelColor);

            NewHeader(left, "模型列表", 0f);
            ScrollRect modelScroll = NewScroll(left, "ModelScroll", out _modelContent);
            RectTransform modelRt = (RectTransform)modelScroll.transform;
            Stretch(modelRt, 0f, 40f, 0f, 40f);

            // Clicking a model only preloads it into the T-pose preview; this
            // button is what actually puts it on the character.
            Button apply = NewButton(left, "应用模型", AccentColor, 16);
            RectTransform applyRt = (RectTransform)apply.transform;
            applyRt.anchorMin = new Vector2(0f, 0f);
            applyRt.anchorMax = new Vector2(1f, 0f);
            applyRt.pivot = new Vector2(0.5f, 0f);
            applyRt.offsetMin = new Vector2(152f, 4f);
            applyRt.offsetMax = new Vector2(-4f, 34f);
            apply.onClick.AddListener(OnApplyClicked);

            Button refresh = NewButton(left, "刷新列表", RowColor, 16);
            RectTransform refreshRt = (RectTransform)refresh.transform;
            refreshRt.anchorMin = new Vector2(0f, 0f);
            refreshRt.anchorMax = new Vector2(1f, 0f);
            refreshRt.pivot = new Vector2(0.5f, 0f);
            refreshRt.offsetMin = new Vector2(4f, 4f);
            refreshRt.offsetMax = new Vector2(-152f, 34f);
            refresh.onClick.AddListener(OnRefreshClicked);

            // Right column.
            RectTransform right = NewRect("RightColumn", _root);
            right.anchorMin = new Vector2(0f, 0f);
            right.anchorMax = new Vector2(1f, 1f);
            right.offsetMin = new Vector2(312f, 0f);
            right.offsetMax = new Vector2(0f, 0f);

            RectTransform previewArea = NewRect("PreviewArea", right);
            previewArea.anchorMin = new Vector2(0f, 0.42f);
            previewArea.anchorMax = new Vector2(1f, 1f);
            previewArea.offsetMin = Vector2.zero;
            previewArea.offsetMax = Vector2.zero;
            NewHeader(previewArea, "预览（拖动旋转 · 滚轮缩放 · 右键平移）", 0f);

            RectTransform frame = NewRect("Frame", previewArea);
            Stretch(frame, 0f, 0f, 0f, 34f);
            // Light translucent panel: the preview itself is rendered with a
            // transparent background, so the page behind it shows through instead
            // of a solid rectangle.
            Image frameImage = NewImage(frame, new Color(0f, 0.05f, 0.06f, 0.35f));

            RectTransform rawGo = NewRect("Preview", frame);
            Stretch(rawGo, 4f, 4f, 4f, 4f);
            _previewImage = rawGo.gameObject.AddComponent<RawImage>();
            _previewImage.raycastTarget = false;
            _previewImage.enabled = false;
            // The render texture is built to the frame's own aspect (see
            // CreateRenderTexture), so the image can simply fill the frame
            // instead of being forced into a square.

            _statusText = NewText(frame, "正在加载模型…", 18, DimColor, TextAnchor.MiddleCenter);
            Stretch(_statusText.rectTransform, 8f, 8f, 8f, 8f);

            // Drag surface on top of the preview: dragging orbits the preview camera
            // around the character. The image is fully transparent but still catches
            // the pointer, so the page never has to touch the world model's transform.
            RectTransform drag = NewRect("PreviewDrag", frame);
            Stretch(drag, 4f, 4f, 4f, 4f);
            Image dragImage = drag.gameObject.AddComponent<Image>();
            dragImage.color = new Color(0f, 0f, 0f, 0f);
            dragImage.raycastTarget = true;
            PreviewDragHandler dragHandler = drag.gameObject.AddComponent<PreviewDragHandler>();
            dragHandler.Owner = this;

            RectTransform bottom = NewRect("Bottom", right);
            bottom.anchorMin = new Vector2(0f, 0f);
            bottom.anchorMax = new Vector2(1f, 0.42f);
            bottom.offsetMin = Vector2.zero;
            bottom.offsetMax = new Vector2(0f, -8f);

            RectTransform parts = NewRect("Parts", bottom);
            parts.anchorMin = new Vector2(0f, 0f);
            parts.anchorMax = new Vector2(0.5f, 1f);
            parts.offsetMin = new Vector2(0f, 0f);
            parts.offsetMax = new Vector2(-6f, 0f);
            NewImage(parts, PanelColor);

            NewHeader(parts, "部位隐藏（按材质）", 0f);
            Button showAll = NewButton(parts, "全部显示", RowColor, 15);
            RectTransform showAllRt = (RectTransform)showAll.transform;
            showAllRt.anchorMin = new Vector2(1f, 1f);
            showAllRt.anchorMax = new Vector2(1f, 1f);
            showAllRt.pivot = new Vector2(1f, 1f);
            showAllRt.anchoredPosition = new Vector2(-4f, -2f);
            showAllRt.sizeDelta = new Vector2(84f, 26f);
            showAll.onClick.AddListener(OnShowAllClicked);

            ScrollRect matScroll = NewScroll(parts, "PartScroll", out _materialContent);
            Stretch((RectTransform)matScroll.transform, 4f, 4f, 4f, 36f);

            RectTransform tune = NewRect("Tune", bottom);
            tune.anchorMin = new Vector2(0.5f, 0f);
            tune.anchorMax = new Vector2(1f, 1f);
            tune.offsetMin = new Vector2(6f, 0f);
            tune.offsetMax = new Vector2(0f, 0f);
            NewImage(tune, PanelColor);
            NewHeader(tune, "调整（拖动立即生效）", 0f);

            ScrollRect tuneScroll = NewScroll(tune, "TuneScroll", out _tuneContent);
            Stretch((RectTransform)tuneScroll.transform, 4f, 4f, 4f, 36f);

            BuildTunables();
        }

        /// <summary>One collapsible group in the tuning list. The rows stay direct
        /// children of the scroll content (so the existing layout group keeps
        /// working); collapsing simply deactivates them.</summary>
        private sealed class TuneSection
        {
            internal Text State;
            internal readonly List<GameObject> Rows = new List<GameObject>();
            internal bool Expanded;
        }

        private TuneSection NewSection(Transform parent, string title, bool expanded)
        {
            RectTransform header = NewRect("Section", parent);
            LayoutElement le = header.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 30f;
            le.minHeight = 30f;

            Image image = header.gameObject.AddComponent<Image>();
            image.sprite = PdaUiAssets.White;
            image.color = RowColor;
            image.raycastTarget = true;
            Button button = header.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            Text titleText = NewText(header, title, 17, TextColor, TextAnchor.MiddleLeft);
            Stretch(titleText.rectTransform, 10f, 0f, 76f, 0f);
            Text state = NewText(header, expanded ? "收起" : "展开", 14, DimColor,
                TextAnchor.MiddleRight);
            Stretch(state.rectTransform, 10f, 0f, 10f, 0f);

            TuneSection section = new TuneSection();
            section.State = state;
            section.Expanded = expanded;
            button.onClick.AddListener(delegate { SetSectionExpanded(section, !section.Expanded); });
            return section;
        }

        private static void AddRow(TuneSection section, GameObject row)
        {
            if (row == null)
            {
                return;
            }
            section.Rows.Add(row);
            row.SetActive(section.Expanded);
        }

        private static void SetSectionExpanded(TuneSection section, bool expanded)
        {
            section.Expanded = expanded;
            if (section.State != null)
            {
                section.State.text = expanded ? "收起" : "展开";
            }
            for (int i = 0; i < section.Rows.Count; i++)
            {
                if (section.Rows[i] != null)
                {
                    section.Rows[i].SetActive(expanded);
                }
            }
            if (section.Rows.Count > 0 && section.Rows[0] != null)
            {
                LayoutRebuilder.MarkLayoutForRebuild(
                    (RectTransform)section.Rows[0].transform.parent);
            }
        }

        private void BuildTunables()
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null) || _tuneContent == null)
            {
                return;
            }

            // Every group starts collapsed: there are enough knobs now that the
            // panel was an endless wall of rows.
            TuneSection hair = NewSection(_tuneContent, "头发", false);
            _tgHairPhysics = NewToggle(_tuneContent, "头发物理",
                p.HairPhysics.Value, OnHairPhysicsChanged);
            AddRow(hair, _tgHairPhysics.gameObject);
            _tgCollision = NewToggle(_tuneContent, "头发碰撞",
                p.HairCollision.Value, OnHairCollisionChanged);
            AddRow(hair, _tgCollision.gameObject);
            _tgLimbCollision = NewToggle(_tuneContent, "四肢碰撞",
                p.LimbCollision.Value, OnLimbCollisionChanged);
            AddRow(hair, _tgLimbCollision.gameObject);
            _tgShowHair = NewToggle(_tuneContent, "第一人称显示双马尾",
                p.ShowHair.Value, OnShowHairChanged);
            AddRow(hair, _tgShowHair.gameObject);
            _slFringe = NewSliderRow(_tuneContent, "刘海摆动", 0f, 2f,
                p.FringeSway.Value, out _valFringe, OnFringeChanged);
            AddRow(hair, _slFringe.gameObject);
            _slStiffness = NewSliderRow(_tuneContent, "头发刚度", 0f, 20f,
                p.HairStiffness.Value, out _valStiffness, OnStiffnessChanged);
            AddRow(hair, _slStiffness.gameObject);
            _slGravity = NewSliderRow(_tuneContent, "头发重力", 0f, 20f,
                p.HairGravity.Value, out _valGravity, OnGravityChanged);
            AddRow(hair, _slGravity.gameObject);
            _slDamping = NewSliderRow(_tuneContent, "水的阻力", 0f, 20f,
                p.HairDamping.Value, out _valDamping, OnDampingChanged);
            AddRow(hair, _slDamping.gameObject);
            _slFlow = NewSliderRow(_tuneContent, "暗流强度", 0f, 1f,
                p.HairFlow.Value, out _valFlow, OnFlowChanged);
            AddRow(hair, _slFlow.gameObject);
            _slTwist = NewSliderRow(_tuneContent, "扭转跟随", 0.3f, 20f,
                p.HairTwistFollow.Value, out _valTwist, OnTwistChanged);
            AddRow(hair, _slTwist.gameObject);

            TuneSection cloth = NewSection(_tuneContent, "裙子与衣服", false);
            _tgSkirtPhysics = NewToggle(_tuneContent, "裙子物理",
                p.SkirtPhysics.Value, OnSkirtPhysicsChanged);
            AddRow(cloth, _tgSkirtPhysics.gameObject);
            _tgClothesPhysics = NewToggle(_tuneContent, "衣服物理",
                p.ClothesPhysics.Value, OnClothesPhysicsChanged);
            AddRow(cloth, _tgClothesPhysics.gameObject);
            _slSkirtStiffness = NewSliderRow(_tuneContent, "裙子硬度", 0.1f, 3f,
                p.SkirtStiffness.Value, out _valSkirtStiffness, OnSkirtStiffnessChanged);
            AddRow(cloth, _slSkirtStiffness.gameObject);
            _slSkirtLeash = NewSliderRow(_tuneContent, "裙摆活动范围", 0f, 3f,
                p.SkirtLeash.Value, out _valSkirtLeash, OnSkirtLeashChanged);
            AddRow(cloth, _slSkirtLeash.gameObject);
            _slSkirtClearance = NewSliderRow(_tuneContent, "裙子与腿间隙", 0f, 0.08f,
                p.SkirtClearance.Value, out _valSkirtClearance, OnSkirtClearanceChanged);
            AddRow(cloth, _slSkirtClearance.gameObject);

            TuneSection accessory = NewSection(_tuneContent, "挂件", false);
            _tgAccessoryPhysics = NewToggle(_tuneContent, "挂件物理",
                p.AccessoryPhysics.Value, OnAccessoryPhysicsChanged);
            AddRow(accessory, _tgAccessoryPhysics.gameObject);

            TuneSection surface = NewSection(_tuneContent, "水面", false);
            _tgSurfacePhysics = NewToggle(_tuneContent, "水面物理",
                p.SurfacePhysics.Value, OnSurfacePhysicsChanged);
            AddRow(surface, _tgSurfacePhysics.gameObject);
            _slAirGravity = NewSliderRow(_tuneContent, "空气重力", 0f, 20f,
                p.AirGravity.Value, out _valAirGravity, OnAirGravityChanged);
            AddRow(surface, _slAirGravity.gameObject);
            _slAirDamping = NewSliderRow(_tuneContent, "空气阻力", 0f, 20f,
                p.AirDamping.Value, out _valAirDamping, OnAirDampingChanged);
            AddRow(surface, _slAirDamping.gameObject);

            TuneSection view = NewSection(_tuneContent, "视角", false);
            _slHeadLook = NewSliderRow(_tuneContent, "头部俯仰跟随", 0f, 1f,
                p.HeadLook.Value, out _valHeadLook, OnHeadLookChanged);
            AddRow(view, _slHeadLook.gameObject);
            _slIdleDelay = NewSliderRow(_tuneContent, "待机判定延时", 0f, 10f,
                p.IdleDelay.Value, out _valIdleDelay, OnIdleDelayChanged);
            AddRow(view, _slIdleDelay.gameObject);
            _slPoseTransition = NewSliderRow(_tuneContent, "动作衔接时长", 0.1f, 2f,
                p.PoseTransition.Value, out _valPoseTransition, OnPoseTransitionChanged);
            AddRow(view, _slPoseTransition.gameObject);

            TuneSection glow = NewSection(_tuneContent, "发光", false);
            _slGlow = NewSliderRow(_tuneContent, "发光强度", 0f, 3f,
                p.HighlightGlow.Value, out _valGlow, OnGlowChanged);
            AddRow(glow, _slGlow.gameObject);

            TuneSection audio = NewSection(_tuneContent, "音效", false);
            _tgMutePlayer = NewToggle(_tuneContent, "屏蔽主角音效",
                p.MutePlayerSounds.Value, OnMutePlayerSoundsChanged);
            AddRow(audio, _tgMutePlayer.gameObject);

            Button reset = NewButton(_tuneContent, "恢复默认值", RowColor, 16);
            LayoutElement le = reset.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 32f;
            le.minHeight = 32f;
            reset.onClick.AddListener(OnResetClicked);

            Text hint = NewText(_tuneContent, "关闭 PDA 时自动保存", 14, DimColor, TextAnchor.MiddleCenter);
            LayoutElement hintLe = hint.gameObject.AddComponent<LayoutElement>();
            hintLe.preferredHeight = 22f;
            hintLe.minHeight = 22f;
        }

        private void OnFringeChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.FringeSway.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnStiffnessChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HairStiffness.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnGravityChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HairGravity.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnDampingChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HairDamping.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnFlowChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HairFlow.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnTwistChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HairTwistFollow.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnSkirtStiffnessChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.SkirtStiffness.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnSkirtLeashChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.SkirtLeash.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnSkirtClearanceChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.SkirtClearance.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnAirGravityChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.AirGravity.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnAirDampingChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.AirDamping.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnHeadLookChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HeadLook.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnIdleDelayChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.IdleDelay.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnPoseTransitionChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.PoseTransition.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnGlowChanged(float value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HighlightGlow.Value = value;
                p.RefreshGlow();
                p.MarkConfigDirty();
            }
        }

        private void OnMutePlayerSoundsChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.MutePlayerSounds.Value = value;
                MikuPlayerReplacerPlugin.MutePlayerSoundsActive = value;
                p.MarkConfigDirty();
            }
        }

        private void OnHairPhysicsChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HairPhysics.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnSkirtPhysicsChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.SkirtPhysics.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnClothesPhysicsChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.ClothesPhysics.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnAccessoryPhysicsChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.AccessoryPhysics.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnSurfacePhysicsChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.SurfacePhysics.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnHairCollisionChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.HairCollision.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnLimbCollisionChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.LimbCollision.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnShowHairChanged(bool value)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (!ReferenceEquals(p, null))
            {
                p.ShowHair.Value = value;
                p.MarkConfigDirty();
            }
        }

        private void OnResetClicked()
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            p.ResetPhysicsDefaults();
            p.RefreshGlow();
            RefreshTunableValues();
        }

        private void RefreshTunableValues()
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            SetSlider(_slFringe, _valFringe, p.FringeSway.Value);
            SetSlider(_slStiffness, _valStiffness, p.HairStiffness.Value);
            SetSlider(_slGravity, _valGravity, p.HairGravity.Value);
            SetSlider(_slDamping, _valDamping, p.HairDamping.Value);
            SetSlider(_slFlow, _valFlow, p.HairFlow.Value);
            SetSlider(_slTwist, _valTwist, p.HairTwistFollow.Value);
            SetSlider(_slSkirtStiffness, _valSkirtStiffness, p.SkirtStiffness.Value);
            SetSlider(_slSkirtLeash, _valSkirtLeash, p.SkirtLeash.Value);
            SetSlider(_slSkirtClearance, _valSkirtClearance, p.SkirtClearance.Value);
            SetSlider(_slAirGravity, _valAirGravity, p.AirGravity.Value);
            SetSlider(_slAirDamping, _valAirDamping, p.AirDamping.Value);
            SetSlider(_slGlow, _valGlow, p.HighlightGlow.Value);
            SetSlider(_slHeadLook, _valHeadLook, p.HeadLook.Value);
            SetSlider(_slIdleDelay, _valIdleDelay, p.IdleDelay.Value);
            SetSlider(_slPoseTransition, _valPoseTransition, p.PoseTransition.Value);
            SetToggle(_tgHairPhysics, p.HairPhysics.Value);
            SetToggle(_tgSkirtPhysics, p.SkirtPhysics.Value);
            SetToggle(_tgClothesPhysics, p.ClothesPhysics.Value);
            SetToggle(_tgAccessoryPhysics, p.AccessoryPhysics.Value);
            SetToggle(_tgSurfacePhysics, p.SurfacePhysics.Value);
            SetToggle(_tgCollision, p.HairCollision.Value);
            SetToggle(_tgLimbCollision, p.LimbCollision.Value);
            SetToggle(_tgShowHair, p.ShowHair.Value);
            SetToggle(_tgMutePlayer, p.MutePlayerSounds.Value);
        }

        private static void SetSlider(Slider slider, Text valueText, float value)
        {
            if (slider == null)
            {
                return;
            }
            slider.SetValueWithoutNotify(value);
            if (valueText != null)
            {
                valueText.text = value.ToString("0.00");
            }
        }

        private static void SetToggle(Toggle toggle, bool value)
        {
            if (toggle == null)
            {
                return;
            }
            toggle.SetIsOnWithoutNotify(value);
            // The checkmark is managed by hand (see NewToggle).
            Transform checkTransform = toggle.transform.Find("Box/Check");
            if (checkTransform != null)
            {
                Image img = checkTransform.GetComponent<Image>();
                if (img != null)
                {
                    img.enabled = value;
                }
            }
        }

        // ---- model list ------------------------------------------------------

        private void OnRefreshClicked()
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            p.RefreshCatalog();
            BuildModelList();
            BuildMaterialList();
        }

        private void BuildModelList()
        {
            if (_modelContent == null)
            {
                return;
            }
            ClearChildren(_modelContent);
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            if (p.ModelCount == 0)
            {
                // The mod no longer ships a model, so an empty list is a normal
                // first-run state and has to say what to do about it.
                Text empty = NewText(_modelContent,
                    "没有找到模型。\n请把模型文件夹放进\nBepInEx\\plugins\\" +
                    "PlayerModelReplacer\\Models\\\n然后点下面的“刷新列表”。",
                    14, DimColor, TextAnchor.UpperLeft);
                LayoutElement emptyLayout = empty.gameObject.AddComponent<LayoutElement>();
                emptyLayout.preferredHeight = 96f;
                emptyLayout.minHeight = 96f;
                return;
            }
            for (int i = 0; i < p.ModelCount; i++)
            {
                int index = i;
                bool worn = p.IsActiveModel(i);
                bool highlighted = p.PreviewModelIndex == i;
                string label = (highlighted ? "● " : "○ ") + p.ModelLabelAt(i);
                if (worn)
                {
                    label += "（已穿戴）";
                }
                Button b = NewButton(_modelContent, label,
                    highlighted ? RowActiveColor : RowColor, 16);
                LayoutElement le = b.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 34f;
                le.minHeight = 34f;
                b.onClick.AddListener(delegate { OnModelClicked(index); });
            }
        }

        /// <summary>
        /// A click preloads and previews the model in its T-pose; it does not touch
        /// the worn character. "应用模型" commits it.
        /// </summary>
        private void OnModelClicked(int index)
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            p.PreviewModelAt(index);
            ResetPreviewView();
            BuildModelList();
            BuildMaterialList();
            UpdateStatus();
        }

        private void OnApplyClicked()
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            p.ApplyPreviewModel();
            BuildModelList();
            UpdateStatus();
        }

        private void BuildMaterialList()
        {
            if (_materialContent == null)
            {
                return;
            }
            ClearChildren(_materialContent);
            _materialRows.Clear();
            _parts.Clear();
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            p.CollectParts(_parts);
            for (int i = 0; i < _parts.Count; i++)
            {
                MaterialPart part = _parts[i];
                string label = string.IsNullOrEmpty(part.Name) ? "未命名材质" : part.Name;
                if (!string.IsNullOrEmpty(part.Texture))
                {
                    label += "  (" + part.Texture + ")";
                }
                NewMaterialRow(label, part.Name, p);
            }
        }

        /// <summary>One material line: the checkbox on the left hides the part,
        /// the "发光" checkbox on the right makes it glow like the built-in
        /// model's highlights. Both are stored per model.</summary>
        private void NewMaterialRow(string label, string materialName, MikuPlayerReplacerPlugin p)
        {
            RectTransform row = NewRect("MaterialRow", _materialContent);
            LayoutElement le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 26f;
            le.minHeight = 26f;
            _materialRows.Add(row.gameObject);

            RectTransform hideRt = NewRect("Hide", row);
            Stretch(hideRt, 0f, 0f, 78f, 0f);
            // Long material names stop at the glow cell instead of running
            // underneath it.
            hideRt.gameObject.AddComponent<RectMask2D>();
            BuildToggleParts(hideRt, label, p.IsPartHidden(materialName),
                delegate(bool hidden)
                {
                    MikuPlayerReplacerPlugin plugin = Plugin;
                    if (!ReferenceEquals(plugin, null))
                    {
                        plugin.SetPartHidden(materialName, hidden);
                    }
                    _previewDirty = true;
                }, 15);

            RectTransform glowRt = NewRect("Glow", row);
            glowRt.anchorMin = new Vector2(1f, 0f);
            glowRt.anchorMax = new Vector2(1f, 1f);
            glowRt.pivot = new Vector2(1f, 0.5f);
            glowRt.sizeDelta = new Vector2(74f, 0f);
            glowRt.anchoredPosition = Vector2.zero;
            BuildToggleParts(glowRt, "发光", p.IsPartGlowing(materialName),
                delegate(bool glow)
                {
                    MikuPlayerReplacerPlugin plugin = Plugin;
                    if (!ReferenceEquals(plugin, null))
                    {
                        plugin.SetPartGlow(materialName, glow);
                    }
                    _previewDirty = true;
                }, 15);
        }

        private void OnShowAllClicked()
        {
            MikuPlayerReplacerPlugin p = Plugin;
            if (ReferenceEquals(p, null))
            {
                return;
            }
            p.ShowAllParts();
            _previewDirty = true;
            // Rebuild so every checkbox shows the restored state.
            BuildMaterialList();
        }

        private static void ClearChildren(RectTransform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        // ---- UI helpers ------------------------------------------------------

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        private static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static Image NewImage(Transform parent, Color color)
        {
            RectTransform rt = NewRect("Image", parent);
            Stretch(rt, 0f, 0f, 0f, 0f);
            Image image = rt.gameObject.AddComponent<Image>();
            image.sprite = PdaUiAssets.White;
            image.type = Image.Type.Simple;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private Text NewText(Transform parent, string text, int size, Color color, TextAnchor anchor)
        {
            RectTransform rt = NewRect("Text", parent);
            Text component = rt.gameObject.AddComponent<Text>();
            component.font = _font;
            component.fontSize = size;
            component.color = color;
            component.alignment = anchor;
            component.raycastTarget = false;
            component.supportRichText = false;
            component.horizontalOverflow = HorizontalWrapMode.Wrap;
            component.verticalOverflow = VerticalWrapMode.Truncate;
            component.text = text;
            return component;
        }

        private Text NewHeader(Transform parent, string text, float rightPadding)
        {
            Text header = NewText(parent, text, 18, AccentColor, TextAnchor.MiddleLeft);
            RectTransform rt = header.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(8f, -32f);
            rt.offsetMax = new Vector2(-8f - rightPadding, 0f);
            return header;
        }

        private Button NewButton(Transform parent, string label, Color color, int size)
        {
            RectTransform rt = NewRect("Button", parent);
            Image image = rt.gameObject.AddComponent<Image>();
            image.sprite = PdaUiAssets.White;
            image.color = color;
            Button button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            button.colors = colors;

            Text text = NewText(rt, label, size, TextColor, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, 6f, 2f, 6f, 2f);
            return button;
        }

        private Toggle NewToggle(Transform parent, string label, bool value, Action<bool> onChanged)
        {
            RectTransform row = NewRect("Toggle", parent);
            LayoutElement le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 26f;
            le.minHeight = 26f;
            return BuildToggleParts(row, label, value, onChanged, 16);
        }

        /// <summary>Builds a checkbox on an existing rect so two of them can share
        /// one row (hide / glow per material).</summary>
        private Toggle BuildToggleParts(RectTransform row, string label, bool value,
            Action<bool> onChanged, int fontSize)
        {
            // Transparent hit area so the whole cell can be clicked, not just the
            // 18 px box.
            RectTransform hit = NewRect("Hit", row);
            Stretch(hit, 0f, 0f, 0f, 0f);
            Image hitImage = hit.gameObject.AddComponent<Image>();
            hitImage.sprite = PdaUiAssets.White;
            hitImage.color = new Color(0f, 0f, 0f, 0f);
            hitImage.raycastTarget = true;

            RectTransform box = NewRect("Box", row);
            box.anchorMin = new Vector2(0f, 0.5f);
            box.anchorMax = new Vector2(0f, 0.5f);
            box.pivot = new Vector2(0f, 0.5f);
            box.anchoredPosition = new Vector2(4f, 0f);
            box.sizeDelta = new Vector2(18f, 18f);
            Image boxImage = box.gameObject.AddComponent<Image>();
            boxImage.sprite = PdaUiAssets.White;
            boxImage.color = SunkenColor;
            boxImage.raycastTarget = true;

            RectTransform check = NewRect("Check", box);
            Stretch(check, 3f, 3f, 3f, 3f);
            Image checkImage = check.gameObject.AddComponent<Image>();
            checkImage.sprite = PdaUiAssets.White;
            checkImage.color = AccentColor;
            checkImage.raycastTarget = false;
            checkImage.enabled = value;

            Text text = NewText(row, label, fontSize, TextColor, TextAnchor.MiddleLeft);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            Stretch(text.rectTransform, 28f, 0f, 2f, 0f);

            Toggle toggle = row.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = boxImage;
            toggle.graphic = null;
            toggle.isOn = value;
            toggle.onValueChanged.AddListener(delegate(bool on)
            {
                checkImage.enabled = on;
                if (onChanged != null)
                {
                    onChanged(on);
                }
            });
            return toggle;
        }

        private Slider NewSliderRow(Transform parent, string label, float min, float max,
            float value, out Text valueText, Action<float> onChanged)
        {
            RectTransform row = NewRect("SliderRow", parent);
            LayoutElement le = row.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 30f;
            le.minHeight = 30f;

            Text labelText = NewText(row, label, 16, DimColor, TextAnchor.MiddleLeft);
            RectTransform labelRt = labelText.rectTransform;
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(0f, 1f);
            labelRt.pivot = new Vector2(0f, 0.5f);
            labelRt.offsetMin = new Vector2(6f, 0f);
            labelRt.offsetMax = new Vector2(124f, 0f);

            Text valueLabel = NewText(row, value.ToString("0.00"), 15, TextColor, TextAnchor.MiddleRight);
            valueText = valueLabel;
            RectTransform valueRt = valueLabel.rectTransform;
            valueRt.anchorMin = new Vector2(1f, 0f);
            valueRt.anchorMax = new Vector2(1f, 1f);
            valueRt.pivot = new Vector2(1f, 0.5f);
            valueRt.offsetMin = new Vector2(-46f, 0f);
            valueRt.offsetMax = new Vector2(-6f, 0f);

            RectTransform sliderRt = NewRect("Slider", row);
            sliderRt.anchorMin = new Vector2(0f, 0f);
            sliderRt.anchorMax = new Vector2(1f, 1f);
            sliderRt.offsetMin = new Vector2(128f, 9f);
            sliderRt.offsetMax = new Vector2(-50f, -9f);

            Slider slider = sliderRt.gameObject.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;

            RectTransform bg = NewRect("Background", sliderRt);
            bg.anchorMin = new Vector2(0f, 0.35f);
            bg.anchorMax = new Vector2(1f, 0.65f);
            bg.offsetMin = Vector2.zero;
            bg.offsetMax = Vector2.zero;
            Image bgImage = bg.gameObject.AddComponent<Image>();
            bgImage.sprite = PdaUiAssets.White;
            bgImage.color = new Color(0f, 0f, 0f, 0.55f);
            bgImage.raycastTarget = true;

            RectTransform fillArea = NewRect("Fill Area", sliderRt);
            fillArea.anchorMin = new Vector2(0f, 0.35f);
            fillArea.anchorMax = new Vector2(1f, 0.65f);
            fillArea.offsetMin = new Vector2(2f, 0f);
            fillArea.offsetMax = new Vector2(-2f, 0f);
            RectTransform fill = NewRect("Fill", fillArea);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = Vector2.one;
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
            Image fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.sprite = PdaUiAssets.White;
            fillImage.color = AccentColor;
            fillImage.raycastTarget = false;

            RectTransform handleArea = NewRect("Handle Slide Area", sliderRt);
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(8f, 0f);
            handleArea.offsetMax = new Vector2(-8f, 0f);
            RectTransform handle = NewRect("Handle", handleArea);
            handle.anchorMin = new Vector2(0f, 0f);
            handle.anchorMax = new Vector2(1f, 1f);
            handle.sizeDelta = new Vector2(14f, 0f);
            Image handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.sprite = PdaUiAssets.White;
            handleImage.color = TextColor;
            handleImage.raycastTarget = true;

            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));
            slider.onValueChanged.AddListener(delegate(float v)
            {
                if (valueLabel != null)
                {
                    valueLabel.text = v.ToString("0.00");
                }
                if (onChanged != null)
                {
                    onChanged(v);
                }
            });
            return slider;
        }

        private ScrollRect NewScroll(Transform parent, string name, out RectTransform content)
        {
            RectTransform root = NewRect(name, parent);
            Image background = root.gameObject.AddComponent<Image>();
            background.sprite = PdaUiAssets.White;
            background.color = SunkenColor;
            background.raycastTarget = true;

            ScrollRect scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            RectTransform viewport = NewRect("Viewport", root);
            Stretch(viewport, 2f, 2f, 2f, 2f);
            Image viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.sprite = PdaUiAssets.White;
            viewportImage.color = new Color(1f, 1f, 1f, 0.02f);
            viewportImage.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;

            content = NewRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            // Preferred heights come from the LayoutElements on each row; without
            // control the layout would fall back to the RectTransform's default
            // 100 px and every row would be huge.
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 4f;
            layout.padding = new RectOffset(4, 4, 4, 4);

            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = content;
            return scroll;
        }
    }

    // ------------------------------------------------------------------
    // Renders the preview after the pose has been finalised for the frame.
    // LateRetargetHost runs at 32000, FinalIK later still, so the preview
    // camera is the last thing to look at the model.
    // ------------------------------------------------------------------
    [DefaultExecutionOrder(33000)]
    internal sealed class PdaPreviewHost : MonoBehaviour
    {
        internal PdaModelTab Tab;

        private void LateUpdate()
        {
            if (Tab != null)
            {
                Tab.HostLateUpdate();
            }
        }
    }

    /// <summary>
    /// Drags over the preview box are turned into camera orbit. Kept as a
    /// component so uGUI's event system routes the drags here; the page itself
    /// only needs to accumulate the angles.
    /// </summary>
    internal sealed class PreviewDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler,
        IScrollHandler
    {
        internal PdaModelTab Owner;

        public void OnBeginDrag(PointerEventData eventData)
        {
            // Nothing to prepare - implementing the interface is what claims the drag.
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Owner == null)
            {
                return;
            }
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                Owner.OnPreviewPan(new Vector2(eventData.delta.x, eventData.delta.y));
            }
            else
            {
                Owner.OnPreviewDrag(eventData.delta);
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (Owner != null)
            {
                Owner.OnPreviewScroll(eventData.scrollDelta.y);
            }
        }
    }

    // ------------------------------------------------------------------
    // Harmony patches. These run inside the game every time a PDA is
    // created, so every path is wrapped: a failure logs once and leaves the
    // vanilla PDA working.
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds the model tab to every toolbar rebuild. The tab is injected only for
    /// the duration of the call and only when the game is passing its own
    /// regularTabs list, so tabPrev and the game's localization check never see
    /// the custom value.
    /// </summary>
    [HarmonyPatch(typeof(uGUI_PDA), "SetTabs")]
    internal static class PdaSetTabsPatch
    {
        private static bool _injected;

        [HarmonyPrefix]
        private static void Prefix(List<PDATab> tabs)
        {
            _injected = false;
            try
            {
                if (!PdaModelTab.Enabled)
                {
                    return;
                }
                List<PDATab> regular = PdaReflection.GetRegularTabs();
                if (tabs == null || regular == null || !ReferenceEquals(tabs, regular))
                {
                    return;
                }
                if (tabs.Contains(PdaModelTab.Tab))
                {
                    return;
                }
                tabs.Add(PdaModelTab.Tab);
                _injected = true;
            }
            catch (Exception)
            {
                _injected = false;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(List<PDATab> tabs)
        {
            if (!_injected)
            {
                return;
            }
            _injected = false;
            try
            {
                if (tabs != null)
                {
                    tabs.Remove(PdaModelTab.Tab);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Creates the page after the game has built its tab table.</summary>
    [HarmonyPatch(typeof(uGUI_PDA), "Initialize")]
    internal static class PdaInitializePatch
    {
        private static bool _reported;

        [HarmonyPostfix]
        private static void Postfix(uGUI_PDA __instance)
        {
            try
            {
                if (PdaModelTab.Attach(__instance))
                {
                    if (!_reported)
                    {
                        _reported = true;
                        MikuPlayerReplacerPlugin p = MikuPlayerReplacerPlugin.InstanceBox;
                        if (!ReferenceEquals(p, null))
                        {
                            p.LogInfo("PDA model page installed.");
                        }
                    }
                }
                else
                {
                    PdaModelTab.Enabled = false;
                    PdaModelTab.Fallback(__instance);
                }
            }
            catch (Exception e)
            {
                PdaModelTab.Enabled = false;
                try
                {
                    MikuPlayerReplacerPlugin p = MikuPlayerReplacerPlugin.InstanceBox;
                    if (!ReferenceEquals(p, null))
                    {
                        p.LogWarning("Could not install the PDA model page: " + e);
                    }
                }
                catch (Exception)
                {
                }
                PdaModelTab.Fallback(__instance);
            }
        }
    }

    /// <summary>Replaces the tooltip the game would look up as "Tab8".</summary>
    [HarmonyPatch(typeof(uGUI_PDA), "CacheToolbarTooltips")]
    internal static class PdaTooltipPatch
    {
        [HarmonyPostfix]
        private static void Postfix(uGUI_PDA __instance)
        {
            try
            {
                List<PDATab> currentTabs = PdaReflection.GetCurrentTabs(__instance);
                List<string> tooltips = PdaReflection.GetToolbarTooltips(__instance);
                if (currentTabs == null || tooltips == null)
                {
                    return;
                }
                int index = currentTabs.IndexOf(PdaModelTab.Tab);
                if (index >= 0 && index < tooltips.Count)
                {
                    tooltips[index] = "MMS";
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Supplies the toolbar sprite for the custom tab value.</summary>
    [HarmonyPatch(typeof(SpriteManager), "Get")]
    [HarmonyPatch(new Type[] { typeof(SpriteManager.Group), typeof(string), typeof(Sprite) })]
    internal static class PdaTabSpritePatch
    {
        private const string IconName = "Tab8";

        [HarmonyPostfix]
        private static void Postfix(SpriteManager.Group group, string name, ref Sprite __result)
        {
            if (group == SpriteManager.Group.Tab && name == IconName && __result == null)
            {
                try
                {
                    __result = PdaUiAssets.TabIcon;
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
