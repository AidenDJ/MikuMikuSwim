using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace MikuPlayerReplacer
{
    internal sealed class BoneData
    {
        public string Name;
        public int Parent;
        public Vector3 LocalPos;
        public Quaternion LocalRot;
        // PMX inherit-rotation link, remapped to the imported bone order.
        public bool InheritsRotation;
        public int InheritParent = -1;
        public float InheritRatio;
    }

    internal sealed class MaterialData
    {
        public string Name;
        public string Texture;
        public int Cull;
    }

    // What the PDA page shows for one toggleable material.
    internal sealed class MaterialPart
    {
        public string Name;
        public string Texture;
    }

    internal sealed class MeshData
    {
        public const int RoleBody = 0;
        public const int RoleHead = 1;

        public string Name;
        public int Role;
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] Uvs;
        public BoneWeight[] Weights;
        public int[] SubMeshMaterials;
        public int[][] Indices;
    }

    internal sealed class ModelData
    {
        public readonly List<BoneData> Bones = new List<BoneData>();
        public readonly List<MaterialData> Materials = new List<MaterialData>();
        public readonly List<MeshData> Meshes = new List<MeshData>();

        // Imported models get their arm rest pose rotated onto the game's bind pose.
        // That bakes into the vertex data, so it may only happen once per model.
        public bool ArmPoseBaked;

        public static ModelData Load(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader br = new BinaryReader(fs, Encoding.UTF8))
            {
                string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (magic != "MKU1")
                {
                    throw new InvalidDataException("Bad magic: " + magic);
                }

                int version = br.ReadInt32();
                if (version != 1)
                {
                    throw new InvalidDataException("Unsupported model version: " + version);
                }

                ModelData model = new ModelData();

                int boneCount = br.ReadInt32();
                for (int i = 0; i < boneCount; i++)
                {
                    BoneData b = new BoneData();
                    b.Name = ReadString(br);
                    b.Parent = br.ReadInt32();
                    b.LocalPos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    b.LocalRot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    model.Bones.Add(b);
                }

                int materialCount = br.ReadInt32();
                for (int i = 0; i < materialCount; i++)
                {
                    MaterialData m = new MaterialData();
                    m.Name = ReadString(br);
                    m.Texture = ReadString(br);
                    m.Cull = br.ReadInt32();
                    model.Materials.Add(m);
                }

                int meshCount = br.ReadInt32();
                for (int mi = 0; mi < meshCount; mi++)
                {
                    MeshData md = new MeshData();
                    md.Name = ReadString(br);
                    int vertexCount = br.ReadInt32();
                    md.Vertices = new Vector3[vertexCount];
                    md.Normals = new Vector3[vertexCount];
                    md.Uvs = new Vector2[vertexCount];
                    md.Weights = new BoneWeight[vertexCount];

                    for (int v = 0; v < vertexCount; v++)
                    {
                        md.Vertices[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        md.Normals[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        md.Uvs[v] = new Vector2(br.ReadSingle(), br.ReadSingle());

                        BoneWeight bw = new BoneWeight();
                        bw.boneIndex0 = br.ReadInt32();
                        bw.boneIndex1 = br.ReadInt32();
                        bw.boneIndex2 = br.ReadInt32();
                        bw.boneIndex3 = br.ReadInt32();
                        bw.weight0 = br.ReadSingle();
                        bw.weight1 = br.ReadSingle();
                        bw.weight2 = br.ReadSingle();
                        bw.weight3 = br.ReadSingle();
                        md.Weights[v] = bw;
                    }

                    int subMeshCount = br.ReadInt32();
                    md.SubMeshMaterials = new int[subMeshCount];
                    md.Indices = new int[subMeshCount][];
                    for (int s = 0; s < subMeshCount; s++)
                    {
                        md.SubMeshMaterials[s] = br.ReadInt32();
                        int indexCount = br.ReadInt32();
                        int[] indices = new int[indexCount];
                        for (int k = 0; k < indexCount; k++)
                        {
                            indices[k] = br.ReadInt32();
                        }
                        md.Indices[s] = indices;
                    }

                    md.Role = string.Equals(md.Name, "Miku_Head", StringComparison.OrdinalIgnoreCase)
                        ? MeshData.RoleHead
                        : MeshData.RoleBody;
                    model.Meshes.Add(md);
                }

                return model;
            }
        }

        private static string ReadString(BinaryReader br)
        {
            int length = br.ReadInt32();
            if (length <= 0)
            {
                return string.Empty;
            }
            byte[] bytes = br.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class MikuPlayerReplacerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.miku.subnautica.playerreplacer";
        // MMS = MikuMikuSwim: the mod's short name, also the PDA tab label.
        public const string PluginName = "MMS";
        public const string PluginVersion = "0.1.7";

        // ---- configuration ----
        private ConfigEntry<float> _scale;
        // Body-size fit measured at build: the ratio that makes the model's
        // head-to-toe bone chain as long as the vanilla suit's. Multiplied on
        // top of the Scale config so the model approaches the vanilla body size.
        private float _scaleFit = 1f;
        private ConfigEntry<float> _yaw;
        private ConfigEntry<float> _offsetX;
        private ConfigEntry<float> _offsetY;
        private ConfigEntry<float> _offsetZ;
        private ConfigEntry<float> _headHideDistance;
        private ConfigEntry<bool> _hideHead;
        private ConfigEntry<bool> _hideGameBody;
        private ConfigEntry<bool> _hideTopFrontTails;
        private ConfigEntry<bool> _hideSleeveLining;
        private ConfigEntry<bool> _tightenCuffs;
        private ConfigEntry<bool> _hideHeadsetMic;
        private ConfigEntry<bool> _useGameShader;
        private ConfigEntry<float> _emissionFill;
        private ConfigEntry<float> _highlightGlow;
        private ConfigEntry<string> _glowMaterials;
        private ConfigEntry<float> _decalOffset;
        private ConfigEntry<bool> _alignHands;
        // How a held quick-slot item is carried. "Model" (default) lets the
        // model keep its own arm proportions and natural wrist and moves the
        // item into the model's palm; "Game" keeps the legacy game-hand IK;
        // "Off" leaves the arm and the item exactly as retargeted.
        private ConfigEntry<string> _holdMode;
        private ConfigEntry<bool> _itemToModelHand;
        private ConfigEntry<bool> _holdPoseMode;
        private ConfigEntry<bool> _holdAim;
        private ConfigEntry<string> _holdIkMode;
        private ConfigEntry<bool> _holdIkLog;
        private ConfigEntry<float> _holdIkMaxWrist;
        private ConfigEntry<float> _holdIkMaxPose;
        private ConfigEntry<bool> _hairPhysics;
        private ConfigEntry<bool> _hairCollision;
        private ConfigEntry<bool> _limbCollision;
        private ConfigEntry<bool> _skirtPhysics;
        private ConfigEntry<bool> _clothesPhysics;
        private ConfigEntry<bool> _accessoryPhysics;
        private ConfigEntry<bool> _surfacePhysics;
        private ConfigEntry<float> _airGravity;
        private ConfigEntry<float> _airDamping;
        // One-shot migration marker: configs created before the dry-air settling
        // fix still carry AirDamping = 1.2, which leaves the hair swinging for
        // seconds after the player stops. Without the marker those configs would
        // never see the new default (BepInEx keeps the stored value).
        private ConfigEntry<bool> _airSettleMigrated;
        private ConfigEntry<string> _glowParts;
        // The skirt cloth hangs a couple of centimetres inside its bone chain, so a
        // capsule that merely matches the skin is already inside the cloth. The
        // legs push the skirt from this much further out.
        private ConfigEntry<float> _skirtClearance;
        private ConfigEntry<float> _skirtStiffness;
        // Multiplier on the skirt's MaxLag leash (how far the hem may travel from
        // the authored pose before being pulled back).
        private ConfigEntry<float> _skirtLeash;
        private ConfigEntry<float> _hairStiffness;
        private ConfigEntry<float> _hairGravity;
        private ConfigEntry<float> _hairDamping;
        private ConfigEntry<float> _hairFlow;
        private ConfigEntry<float> _hairTwistFollow;
        private ConfigEntry<float> _fringeSway;
        private static Font _uiFont;
        private ConfigEntry<bool> _showHair;
        // Read by the Harmony prefixes that silence the pain, jump and breathing
        // sounds, which can run before or after this component is destroyed, so
        // it lives in a plain static.
        internal static bool MutePlayerSoundsActive;
        private ConfigEntry<bool> _mutePlayerSounds;
        private ConfigEntry<bool> _verbose;
        private ConfigEntry<bool> _glowTest;
        private ConfigEntry<float> _showcase;
        private ConfigEntry<float> _thirdPersonDistance;
        private ConfigEntry<float> _thirdPersonHeight;
        private ConfigEntry<float> _thirdPersonSide;
        private ConfigEntry<bool> _faceMovement;
        private ConfigEntry<float> _headLook;
        private ConfigEntry<float> _idleDelay;

        // ---- loaded assets ----
        private bool _glowLogged;
        private bool _blendStateLogged;
        private ModelData _data;
        private string _modelDir;
        private Dictionary<string, string> _textureFiles;
        // PMX references that miss (wrong folder, wrong extension, moved file) fall
        // back to these indexes, built from the same scan.
        private Dictionary<string, List<string>> _textureNames;
        private Dictionary<string, List<string>> _textureStems;
        // Folder the PMX lives in. MMD resolves a texture reference against it,
        // including "..\shared\..." that leaves the model folder.
        private string _textureRoot;
        private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        // Textures this plugin derived from a source texture at load. None are
        // created today, but the list stays as the ownership boundary: anything
        // added here is destroyed on every model switch along with _textures.
        private readonly List<Texture2D> _derivedTextures = new List<Texture2D>();
        // Parsed + converted model data, kept pristine per catalog entry. BuildModel
        // rewrites vertices and bone weights in place, so every switch hands it a deep
        // clone; switching back then skips the PMX parse entirely.
        private readonly Dictionary<string, ModelData> _modelCache =
            new Dictionary<string, ModelData>(StringComparer.OrdinalIgnoreCase);
        private List<Material> _materials;
        private Shader _shader;
        private Material _suitBaseMat;
        // Materials whose texture paints them semi-transparent. Indexed like
        // _data.Materials; true means "draw with alpha blending, not a hard cutout".
        private bool[] _blendFlags;

        // ---- model catalog ----
        // True while the active model was converted from a PMX at runtime, which turns
        // on the generic hair rig and a few import-only material behaviours.
        private bool _imported;
        private bool _textureRepeat;
        private ConfigEntry<string> _activeModel;
        private readonly List<ModelEntry> _modelCatalog = new List<ModelEntry>();

        private sealed class ModelEntry
        {
            public string Id;        // folder name under Models/
            public string Label;
            public string Folder;
            public string PmxPath;
        }

        // One drawable sub-mesh of the current model. The triangles captured here are
        // the final ones written into the Mesh (after first-person clothing filtering
        // and the head/twintail split), so hiding and restoring is lossless.
        private sealed class MeshPart
        {
            public Mesh Mesh;
            public int SubMesh;
            public int[] Triangles;
            public string Material;
            public string Texture;
        }

        // ---- PDA model page and console ----
        // F2 opens the MMS console; the menu key (V by default) opens the game's PDA
        // on the model page. The page itself lives in PdaModelTab.cs.
        // The game's GUIController.Update also listens for F6 (and cycles its HUD
        // hide phases on every press), so the shortcuts moved to free keys.
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _menuKey;
        private KeyCode _menuKeyCode = KeyCode.V;
        private ConfigEntry<string> _consoleKey;
        private KeyCode _consoleKeyCode = KeyCode.F2;
        // Per model (folder id, "" = built-in) material names whose sub-meshes are
        // hidden. Serialised into one config entry by the PDA page.
        private ConfigEntry<string> _hiddenMaterials;
        private readonly Dictionary<string, HashSet<string>> _hiddenByModel =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Every sub-mesh of the current model, so a material toggle can clear it and
        // put the original triangle list back without rebuilding the model.
        private readonly List<MeshPart> _meshParts = new List<MeshPart>();
        // Per model material names that glow on top of the faint EmissionFill,
        // serialised like the hidden set. Seeded for the built-in model from the
        // original GlowMaterials list so existing configs keep their highlights.
        private readonly Dictionary<string, HashSet<string>> _glowByModel =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private string _currentModelId = "";
        private int _buildGeneration;
        private bool _configDirty;
        private bool _pdaKeyErrorLogged;
        // Renderers of the current model, cached for the PDA preview camera.
        private Renderer[] _previewRenderers;
        private int[] _previewLayers;
        private ShadowCastingMode[] _previewShadows;

        // Standalone preview of the model highlighted in the PDA list. It is built
        // from the parsed mesh data in its rest (T) pose and never touches the worn
        // character, so clicking a list entry only preloads it; "apply" is separate.
        private Transform _previewRoot;
        private List<Material> _previewMaterials;
        // Blend flags that belong to _previewMaterials. Handed to the worn build
        // together with the materials when the player clicks "apply".
        private bool[] _previewBlendFlags;
        // Baked textures (the printed "01" sticker) created while building the
        // preview. They belong to the preview's materials, so they must live and
        // die with it, and follow the materials when the model is applied.
        private readonly List<Texture2D> _previewDerivedTextures = new List<Texture2D>();
        private List<Texture2D> _pendingDerivedTextures;
        // PMX material names parallel to _previewMaterials, needed to apply the
        // glow selection to a preview that owns its own materials.
        private List<string> _previewMaterialNames;
        // Model id that the materials currently being created belong to. The
        // preview temporarily builds another model's materials while _data holds
        // that model, so IsGlowMaterial must not look at the worn id.
        private string _materialModelId = "";
        private Dictionary<string, Texture2D> _previewTextures =
            new Dictionary<string, Texture2D>();
        private Renderer[] _previewModelRenderers;
        // The preview borrows the worn model's materials/textures instead of its own.
        private bool _previewSharedAssets;
        // Assets handed from a released preview to the build that is about to
        // start, so the switch does not decode or create the same files twice.
        private Dictionary<string, Texture2D> _pendingTextures;
        private List<Material> _pendingMaterials;
        private bool[] _pendingBlendFlags;
        private string _pendingTextureModel;
        private readonly List<MeshPart> _previewParts = new List<MeshPart>();
        private string _previewModelId;
        private int _previewModelIndex = -1;
        private int _previewGeneration;
        // Blend classification is deterministic for a given model, and the alpha
        // scan reads every texture. Cache it per model id so switching back to a
        // model does not pay for the scan again.
        private readonly Dictionary<string, bool[]> _blendCache =
            new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
        // Set when the alpha scan threw: the all-cutout result of a failed scan
        // must not be cached, or every later switch to that model inherits it.
        private bool _blendScanFailed;
        // A preview can be waiting on a background PMX parse and/or asynchronous
        // texture decodes. While it is pending the page shows "正在加载模型…".
        private sealed class PreviewJob
        {
            public int Index;
            public string Id;
            public ModelEntry Entry;
            public ModelData Data;
            public Task<ModelData> ParseTask;
            public float Since;
            public bool PrefetchStarted;
        }
        private PreviewJob _pendingPreview;

        // ---- runtime objects ----
        private Transform _container;
        private Transform _armature;
        private Transform _playerView;
        private Transform _maleGeo;
        private Transform _headBone;
        // Parent of the head bone, when the model has one (MMD 首). The head look
        // splits its pitch between this and the head so the neck does not snap.
        private Transform _neckBone;
        private Transform _headSourceBone;
        private Animator _animator;
        private SkinnedMeshRenderer _bodyRenderer;
        private SkinnedMeshRenderer _headRenderer;
        // Kept as a List so the periodic refresh can reuse the same buffer
        // (GetComponentsInChildren(bool, List)) instead of allocating a new
        // array four times per second.
        private readonly List<Renderer> _gameBodyRenderers = new List<Renderer>();
        private float _nextBodyCheck;
        private float _nextTry;
        private bool _built;
        private bool _missingLogged;
        private bool _errorLogged;
        private bool _loggedPlayerFound;
        private bool _missingViewLogged;
        private bool _loggedTry;
        private float _nextDiag;
        private bool _loggedUpdate;
        private bool _loggedView;
        private float _nextHeartbeat;

        // pose-tracking diagnostics
        private Transform[] _diagBones;
        private Dictionary<string, int> _diagIndexByName;
        private SourceMap _diagSources;
        private float _nextPoseCheck;
        private int _poseSamples;
        private Vector3 _hipsRefWorld;
        private float _nextRigLog;
        private int _rigSamples;
        private bool _renderersLogged;
        private float _nextToolLog;
        private int _toolLogSamples;
        private float _nextToolTrace;
        private float _nextPitchDiag;

        // Late-pass bookkeeping: the second retarget runs from LateRetargetHost, after
        // RootMotion FinalIK has written the arm bones.
        private bool _latePass;
        private Vector3 _handAtPlayerLate;
        private int _handAtPlayerLateFrame = -1;
        private float _nextLagLog;
        private float _nextArmLog;
        private float _nextIkLogL;
        private float _nextIkLogR;
        private float _nextAimLogL;
        private float _nextAimLogR;

        // Last elbow bend direction per arm, in model space. Carried across frames so
        // the IK cannot flip the elbow to the other side of the arm line in one frame.
        private Vector3 _poleL;
        private Vector3 _poleR;
        private bool _hasPoleL;
        private bool _hasPoleR;
        // True while the arm IK ran last frame on each side. Used to re-pick the elbow
        // side when a new held-item pose starts.
        private bool _ikLeftActive;
        private bool _ikRightActive;
        // How much of the IK correction is applied, 0..1. Picking an item up or
        // holstering it used to flip the arm between "pulled onto the game hand"
        // and "plain retarget" in one frame, which reads as the arm snapping while
        // swimming. Ease the correction in and out instead.
        private float _ikWeightL;
        private float _ikWeightR;
        // Weight of the rigid vanilla-hand aim used while a tool is held
        // (HoldPoseMode). It fades like the IK so picking up or holstering a
        // tool does not snap the arm between the body pose and the aim.
        private float _aimWeightL;
        private float _aimWeightR;
        private float _nextPoleLog;

        // Grip-anchored IK (HoldIkMode = Grip). Separate weights from the old
        // wrist-to-wrist IK so the two never blend into each other; the grip
        // weight fades on equip/holster exactly like _ikWeight did.
        private float _gripWeightL;
        private float _gripWeightR;
        private bool _gripActiveL;
        private bool _gripActiveR;
        private float _nextGripLogL;
        private float _nextGripLogR;
        // Last target frame, kept for the fade-out after holstering: the hand
        // eases off the last grip instead of snapping to the swim pose.
        private readonly Vector3[] _gripLastPoint = new Vector3[2];
        private readonly Vector3[] _gripLastAxis = new Vector3[2];
        private readonly Vector3[] _gripLastNormal = new Vector3[2];
        private readonly float[] _gripLastTime = new float[2];
        private bool _gripLastValidL;
        private bool _gripLastValidR;
        // Finger-bone lookup cache, keyed by hand transform instance id. The
        // fingers only change when the rig is rebuilt, so searching the
        // hierarchy every frame would be wasted work.
        private sealed class GripBoneSet
        {
            public Transform[] Bones;
            public bool Searched;
            public bool Valid;
        }

        private readonly Dictionary<int, GripBoneSet> _gripBonesByHand =
            new Dictionary<int, GripBoneSet>();

        // Held-item adaptation: instead of bending the arm onto the game's hand,
        // the tool is moved so its grip sits in the model's palm. The item stays
        // parented to the game's hand socket, so its authored local transform is
        // kept as the base and the animated parent rebuilds the authored world
        // pose every frame; the palm-to-palm delta is then applied on top. The
        // base is re-read whenever the game writes the socket again (equip,
        // tool switch), which is detected by the local transform changing away
        // from the value written last frame.
        private GameObject _movedItem;
        private Transform _movedItemParent;
        private bool _movedItemBaseValid;
        private Vector3 _movedItemBasePos;
        private Quaternion _movedItemBaseRot;
        private Vector3 _movedItemBaseScale;
        private Vector3 _movedItemWrittenPos;
        private Quaternion _movedItemWrittenRot;
        private bool _movedItemLogged;
        // Model-led carry: the item position is low-pass filtered a little so a
        // single noisy palm frame (pose crossfade, FinalIK step) cannot make the
        // tool jump; the rotation is the authored one and needs no filter.
        private Vector3 _itemCarryPos;
        private Vector3 _itemCarryLastPos;
        private bool _itemCarrySmoothValid;
        private GameObject _itemCarryItem;
        private float _itemJitterMax;
        private float _nextModelGripLog;
        // Hybrid item pull + arm IK (Model mode). The item is pulled towards
        // the character a little, then the arms solve onto the pulled grips.
        // The pull starts only after the draw settles, so equip/holster
        // animations keep the authored item pose.
        private ConfigEntry<float> _itemPull;
        private ConfigEntry<bool> _holdArmIk;
        private float _itemPullWeight;
        private Vector3 _itemPullVec;
        private Vector3 _itemPullSmooth;
        private bool _itemPullValid;
        private GameObject _itemPullItem;
        private float _itemPullSince;
        private bool _itemPullHolding;
        // The handle point in the ITEM's local space, sampled from the game
        // palm while the item is still in its authored pose. The arm IK target
        // is item.TransformPoint(this), so the hand follows the item through
        // movement and camera pitch instead of chasing the body hand.
        private Vector3 _itemGripLocalR;
        private Vector3 _itemGripLocalL;
        private bool _itemGripValid;
        private GameObject _itemGripItem;
        // Carry weight for the left support arm solve, and the last target it
        // used so the fade-out does not snap back to the retargeted stroke.
        private float _supportWeightL;
        private Vector3 _supportTargetL;
        private bool _supportTargetValidL;
        // Model-mode arm solve: position-only IK onto the game hands, with the
        // model's retargeted hand rotation kept. This puts the model's hands
        // where the game's hands (and the authored tool) are without the old
        // wrist pin/twist redistribution.
        private float _modelArmWeightR;
        private float _modelArmWeightL;
        private Vector3 _modelArmTargetR;
        private Vector3 _modelArmTargetL;
        private bool _modelArmTargetValidR;
        private bool _modelArmTargetValidL;
        private float _modelHandRollR;
        private float _modelHandRollL;
        private float _handAlignWeightR;
        private float _handAlignWeightL;
        // Smoothed targets for the model arm solve. The game hand target is
        // animated by FinalIK and can carry a little one-frame noise; feeding
        // it straight into the solver made the knife/builder hand buzz.
        private Vector3 _modelArmSmoothR;
        private Vector3 _modelArmSmoothL;
        private bool _modelArmSmoothValidR;
        private bool _modelArmSmoothValidL;
        private GameObject _modelArmSmoothItem;
        private Vector3 _modelArmLastR;
        private Vector3 _modelArmLastL;
        private bool _modelArmLastValidR;
        private bool _modelArmLastValidL;
        private float _modelArmJitterMax;
        private float _nextModelArmLog;

        // The left-hand decision only depends on the held item, but it used to run
        // GetComponent twice every frame. Recompute it only when the item changes.
        private InventoryItem _heldDecisionItem;
        private bool _heldDecisionLeftHand;
        private bool _heldDecisionSeaglide;

        // Camera view mode cycled by F5: 0 = first person, 1 = shoulder/third person
        // (behind), 2 = second person (in front, looking back at the character). The
        // camera is moved at the very end of the frame; the character model itself
        // never moves.
        private int _viewMode;
        // Shoulder view: the model keeps its own heading instead of turning with the
        // camera. _modelYaw is that heading in world degrees (where the model is
        // actually looking, i.e. the movement direction); _faceDetach fades that
        // detached heading in and out so entering/leaving the view or picking up a
        // tool does not snap the body around.
        private float _modelYaw;
        private bool _modelYawValid;
        private float _faceDetach;
        // Third-person extras. _headLookFade scales the added head yaw/pitch and
        // fades out only while a cinematic owns the animation. _suppressViewAnim
        // asks the ArmsController patch to zero the camera-driven view_turn/
        // view_pitch inputs while the player stands still empty-handed (with an
        // item the body must track the camera, so the game pose stays untouched).
        private float _headLookFade = 1f;
        private bool _viewIdle;
        private bool _viewIdleLogged;
        private bool _viewCinematic;
        private bool _suppressViewAnim;
        private float _viewSuppressWeight;
        // While the player keeps gliding after the stick is released (the normal
        // way to swim with the seaglide) the input reads zero although the body
        // is still travelling. Idle only starts after this time, so the idle
        // freeze does not cut the head follow off mid-glide.
        private float _idleHoldUntil;
        // Last position and its age, for the "is the player actually travelling"
        // test above.
        private Vector3 _idleRefPos;
        private float _idleRefTime;
        // What the head is actually turned by, in degrees. The clamped target
        // flips from +60 to -60 when the view crosses the body's 180 degree line
        // (the head cannot turn any further, so it has to come around the other
        // side); applying the flips straight made the head teleport. These follow
        // the target instead, so the turn plays out over a tenth of a second or so.
        private float _headYawShown;
        private float _headPitchShown;
        // Throttle for the temporary idle-pose diagnostic in FrameLate.
        private bool _camAdjusted;
        private float _camAdjustLogAt;
        // Horizontal heading of travel, smoothed as a unit vector. In water the
        // motor writes PlayerController.velocity from the rigidbody, i.e. once per
        // physics tick, so using that direction directly made the model turn in
        // 50 Hz steps. A little easing on the direction (and on the yaw) keeps the
        // turn continuous at any frame rate.
        private Vector3 _moveDir;
        private bool _moveDirValid;
        // How hard the model itself is turning, for the bank/skid of the
        // third-person turn.
        private float _modelYawPrev;
        private bool _modelYawPrevValid;
        private float _modelTurnRate;
        private float _lean;
        // The diving-mask overlay in front of the camera; hidden outside first
        // person. Renderers rather than the object, so the game keeps updating it.
        private PlayerMask _playerMask;
        private Renderer[] _maskRenderers;
        private bool _maskHidden;
        private float _maskScanAt;
        // The mode currently being blended; lagging behind _viewMode lets a mode change
        // fade out to first person first instead of snapping through it.
        private int _viewApplied;
        private float _viewBlend;
        private bool _camMoved;
        private Transform _camMovedT;
        private Vector3 _camBaseLocalPos;
        private Quaternion _camBaseLocalRot;
        // Local rotation this plugin last wrote to the camera in second person. If the
        // game wrote a different one since, _camBaseLocalRot is refreshed from it, so
        // leaving the view restores the player's actual current look direction.
        private Quaternion _camAppliedLocalRot;
        private readonly RaycastHit[] _camHits = new RaycastHit[16];
        // Stand-in transform handed to the player motors while second person has the
        // real camera turned around. See SecondPersonForward().
        private Transform _viewProbe;
        // Camera rotation borrowed for the duration of one movement or animation
        // update. See BeginSecondPersonCameraSwap().
        private Transform _swapCamT;
        private Quaternion _swapSavedRot;
        // Camera rotation borrowed for one ArmsController.Update while the shoulder
        // view has detached the heading. See BeginSwimFacingSwap().
        private Transform _swimCamT;
        private Quaternion _swimSavedRot;

        // Simulated cloth chain (twintails, skirt panels): a pinned root plus a few
        // bones that follow the root with some lag.
        private sealed class ClothChain
        {
            public string Name;
            public Transform Root;         // transform the chain is pinned to
            public Transform[] Bones;
            public Vector3[] RestLocal;    // joints in root-bone local space
            public Vector3[] RestDirLocal; // rest forward of each bone in its own space
            public Vector3[] RestDirWorld; // rest forward of each segment in bind world space
            public Vector3[] RestUpWorld;  // twist-free roll reference per bone, bind world space
            public Quaternion[] RestRotWorld; // bone rotation in bind world space
            public Quaternion[] RestBend;  // rest turn from segment k to k+1, in root space
            public float[] Length;         // world segment lengths
            public Vector3[] Points;       // simulated joints in world space
            public Vector3[] Prev;
            public Vector3[] Vel;          // displacement this frame, before constraints
            public float StiffnessScale = 1f;
            public float FlowScale = 1f;   // share of the ambient water current
            public float FlowPhase;        // so the two tails do not sway in lockstep
            public float GravityScale = 1f;
            public float DampingScale = 1f;
            // Extra gravity/damping that only applies to the dry part of the chain
            // (weight 1 when a joint is above the water line). Long hair uses it to
            // read as heavy: no pendulum ringing after leaving the water, and it
            // hangs straight down instead of drifting like kelp at the surface.
            public float AirGravityScale = 1f;
            public float AirDampScale = 1f;
            public bool Light;             // fringe / sideburns: amplitude follows FringeSway
            // Fringe hair is leashed to the rigidly posed strand: any joint further
            // than this from where the head alone would put it is pulled back. Long
            // tails leave this at zero and swing freely.
            public float MaxLag;
            // Root rotation at build time. Light strands are posed rigidly relative
            // to it, so the authored curl and, more importantly, the authored roll
            // of every flat hair card survive the simulation.
            public Quaternion RestRootRot = Quaternion.identity;
            // Total rigid lean of a light strand this frame (fringe: a few degrees).
            public Quaternion Swing = Quaternion.identity;
            public float MaxSwing;
            public Vector3 LastRoot;
            public bool HasLastRoot;
            // Orientation the authored rest curl is expressed in. It is the head's
            // rotation with a short lag, so the tail's shape follows the body without
            // copying every per-stroke twitch of the swim animation. Bones are posed
            // against the frozen bind pose (RestDirWorld/RestRotWorld), not this
            // frame: rolling the ribbons with the head is what made the tips circle.
            public Quaternion SheetFrame;
            public bool HasSheetFrame;
            // Per-segment roll of a hanging accessory (necktie, strap). Unlike
            // hair, whose cards should turn with the head immediately, a tie has
            // to be turned by its knot: the segment at the chest catches the body
            // yaw first and each segment below follows it a little later. The
            // arrays remember last frame's roll reference and aim so the roll can
            // be carried along the segment and then eased towards the body frame.
            public Vector3[] RollUp;
            public Vector3[] RollAim;
            public bool HasRoll;
            // Hair chains are the ones the [Physics] HairPhysics switch owns. Skirt
            // chains ride on SkirtPhysics, and the two toggles must not trip over
            // each other now that the rig is always built.
            public bool Hair;
            // A PMX skirt/petticoat chain: simulated when SkirtPhysics is on, and
            // collides against the legs instead of only the torso.
            public bool Skirt;
            // A cape / coat / mantle chain. It is cloth like the skirt, but it hangs
            // from the shoulders and has its own ClothesPhysics switch.
            public bool Clothes;
            // A necktie / ribbon / scarf / strap chain: swings like hair but is not
            // head geometry, and has its own AccessoryPhysics switch.
            public bool Accessory;
            // Skirt or another garment. These take the cloth padding profile (thick
            // shell, leg clearance, stiff hem) instead of the hair one.
            public bool Cloth;
            // Extra world-space clearance added to every body capsule for this
            // chain. Skirts need it because the cloth surface sits inside the
            // bone line; hair and neckties use zero.
            public float Margin;
            // Measured cloth thickness around each joint, in world metres. The
            // solver pushes joints, but the mesh is a shell around them: without
            // this the cloth still shows roughly half a tail's thickness sunk into
            // the body. Style checks beside the strand line; skirts the panel.
            public float[] Padding;
            // Where the shell's centre sits relative to each joint, in that
            // bone's space (a tie ribbon hangs in front of its bones, a skirt
            // panel outside them). Collision acts on the shell's centre.
            public Vector3[] Shell;
            // Bind local rotation of every joint, so the chain can be put back to the
            // authored pose the moment hair physics is switched off.
            public Quaternion[] RestLocalRot;
        }

        private readonly List<ClothChain> _clothChains = new List<ClothChain>();
        private SkinnedMeshRenderer _hairRenderer;
        // Imported models: vertices that BuildHairRig moved onto the simulated hair
        // chain. The head mesh is split along this mask so the tails can keep drawing
        // while the head is hidden in first person.
        private bool[] _clothVertexMask;
        // Hair vertices that should keep drawing while the head is hidden in first
        // person. Wider than the simulated chain: a model whose hair never got a
        // chain still needs its long hair split off the head renderer, otherwise the
        // whole hairstyle disappears as soon as the camera is in the head.
        private bool[] _hairVisibleMask;
        private float _nextHairLog;
        private int _clothResets;

        private static object _instanceBox;

        // ---- asynchronous texture prefetch ----
        //
        // Texture2D.LoadImage decodes on the calling (main) thread, which is what
        // made every model switch and every PDA preview stall for hundreds of
        // milliseconds. Unity's native image loader can decode on a worker thread,
        // so PNG/JPG files are requested with UnityWebRequestTexture as soon as
        // their model is known (at start-up for the worn model, on list click for a
        // preview). LoadTexture consumes the finished texture from this cache and
        // only falls back to the synchronous decoder for BMP/TGA or failed files.
        // The list/dictionary are static because they must survive the BepInEx
        // manager object being destroyed between scenes.
        private sealed class PrefetchJob
        {
            public string Path;
            public string ModelId;
            public UnityWebRequest Request;
        }
        private static readonly List<PrefetchJob> _prefetchJobs = new List<PrefetchJob>();
        private static readonly Dictionary<string, Texture2D> _prefetchedTextures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _prefetchOrder = new List<string>();
        private static readonly HashSet<string> _prefetchRequested =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Pump()
        {
            object box = _instanceBox;
            if (box == null)
            {
                return;
            }
            MikuPlayerReplacerPlugin plugin = (MikuPlayerReplacerPlugin)box;
            plugin.Tick();
            plugin.FrameLate();
        }

        // Second retarget, called from LateRetargetHost at the very end of LateUpdate.
        // Subnautica aims the first-person arms with RootMotion FinalIK, whose solvers
        // write the hand/elbow bone rotations in their own LateUpdate - i.e. after the
        // Player.LateUpdate hook above. Reading the bones there means the model's arms
        // are one frame behind the game's, while a held tool (parented to the game's
        // hand_R) is rendered at the final pose; the item then visibly slides out of the
        // model's hand as soon as the view pitch changes.
        public static void PumpLate()
        {
            object box = _instanceBox;
            if (box == null)
            {
                return;
            }
            ((MikuPlayerReplacerPlugin)box).FrameLateLate();
        }

        private void FrameLateLate()
        {
            if (_container == null)
            {
                RestoreMovedItem();
                return;
            }
            _latePass = true;
            try
            {
                TryCaptureHoldPose();
                FrameLate();
                // The model pose is final here (both retarget passes are done and
                // the game's own LateUpdate has already animated the tool socket),
                // so the held tool can be rebuilt from the model's hand.
                AdjustMovedItem();
                UpdateAdaptiveHeldIk();
            }
            finally
            {
                _latePass = false;
            }
        }

        /// <summary>
        /// Model-led grip: carries the held tool into the model's right palm
        /// instead of bending the model's arm onto the game's hand. The tool
        /// stays parented to the game's socket: its authored local transform
        /// (captured before the first correction) rebuilds the authored world
        /// pose from the animated parent every frame, and then only its
        /// POSITION is moved by the game-to-model palm offset. The rotation is
        /// left exactly as the game authored it, so the tool keeps following
        /// the camera when the player looks up or down; rotating it onto the
        /// model's palm frame was what made the knife point away from the view.
        ///
        /// A two-handed tool is not pulled towards the left palm: the right
        /// hand owns the socket and stays the anchor, and the left arm is
        /// solved onto the carried item separately (UpdateSupportArm). Averaging
        /// both palms made the item crawl between the hands and the seaglide
        /// rock back and forth.
        /// </summary>
        private void AdjustMovedItem()
        {
            if (AdaptiveMode()) { RestoreMovedItem(); return; }
            bool modelMode = ModelGripModeActive();
            if (!_itemGlueOn)
            {
                // Off/Game mode: the tool keeps exactly the pose the game
                // authored (position, rotation and scale); a Game-mode solve
                // puts the model's hand on it instead. Drop any tracked base
                // without writing, so a later mode switch restarts from the
                // game's current pose.
                _movedItem = null;
                _movedItemParent = null;
                _movedItemBaseValid = false;
                _movedItemLogged = false;
                _itemCarrySmoothValid = false;
                _itemCarryItem = null;
                UpdateSupportArm(false, Vector3.zero);
                return;
            }
            // The PDA is held with both hands and solved by the IK; a quick-slot
            // item can still be equipped underneath it, and moving it while the
            // PDA owns the hands would fight that solve.
            if (PdaInUse())
            {
                RestoreMovedItem();
                UpdateSupportArm(false, Vector3.zero);
                return;
            }
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            GameObject item = held != null && held.item != null
                ? held.item.gameObject : null;
            if (item == null)
            {
                RestoreMovedItem();
                UpdateSupportArm(false, Vector3.zero);
                return;
            }

            if (!ReferenceEquals(item, _movedItem))
            {
                RestoreMovedItem();
                _movedItem = item;
                _movedItemBaseValid = false;
                _movedItemLogged = false;
                _itemCarrySmoothValid = false;
                _itemCarryItem = null;
                _supportTargetValidL = false;
                _itemJitterMax = 0f;
            }

            Transform track = _movedItem.transform;
            Transform parent = track.parent;
            if (parent == null)
            {
                RestoreMovedItem();
                UpdateSupportArm(false, Vector3.zero);
                return;
            }

            // The base is the pose the game authored for this socket. It is
            // re-read whenever the local transform is not the one written here
            // last frame (the game attached or animated the tool), or when the
            // game reparented it (draw from the container, holster and redraw).
            if (_movedItemBaseValid
                && (parent != _movedItemParent
                    || Vector3.Distance(track.localPosition, _movedItemWrittenPos) > 1e-4f
                    || Quaternion.Angle(track.localRotation, _movedItemWrittenRot) > 0.02f))
            {
                _movedItemBaseValid = false;
            }
            if (!_movedItemBaseValid)
            {
                _movedItemParent = parent;
                _movedItemBasePos = track.localPosition;
                _movedItemBaseRot = track.localRotation;
                _movedItemBaseScale = track.localScale;
                _movedItemBaseValid = true;
            }

            bool firstPersonRig = ItemInFirstPersonRig(parent);
            // Model mode: pull the item towards the character only after the
            // draw settles. Rotation stays authored; the arm IK targets these
            // pulled grips.
            if (modelMode)
            {
                Vector3 basePos = parent.TransformPoint(_movedItemBasePos);
                Quaternion baseRot = parent.rotation * _movedItemBaseRot;
                Vector3 pull = _itemPullValid ? _itemPullVec : Vector3.zero;
                track.position = basePos + pull;
                track.rotation = baseRot;
                track.localScale = _movedItemBaseScale;
                _movedItemWrittenPos = track.localPosition;
                _movedItemWrittenRot = track.localRotation;
                UpdateSupportArm(false, Vector3.zero);
                if (!_movedItemLogged)
                {
                    _movedItemLogged = true;
                    Logger.LogInfo("Item pull: " + item.name
                        + " move=" + pull.magnitude.ToString("F3") + " m"
                        + " twoHand=" + HeldItemUsesLeftHand()
                        + " rig=" + (firstPersonRig ? "fp" : "body"));
                }
                return;
            }

            Quaternion deltaRot;
            Vector3 deltaPos;
            // Legacy item glue still maps the palm frame.
            bool alignRotation = !modelMode;
            if (!_itemGlueOn
                || !HoldPalmDelta(firstPersonRig, alignRotation,
                    out deltaRot, out deltaPos))
            {
                RestoreMovedItem();
                UpdateSupportArm(false, Vector3.zero);
                return;
            }

            Vector3 baseWorldPos = parent.TransformPoint(_movedItemBasePos);
            Quaternion baseWorldRot = parent.rotation * _movedItemBaseRot;
            Vector3 wantedPos = deltaRot * baseWorldPos + deltaPos;
            Quaternion wantedRot = deltaRot * baseWorldRot;

            if (modelMode)
            {
                // The authored rotation is kept; only the position is filtered.
                // A 35 ms low-pass removes the one-frame noise a pose crossfade
                // or a FinalIK step injects into the palm without visibly
                // lagging the tool behind the hand.
                if (!_itemCarrySmoothValid || !ReferenceEquals(_itemCarryItem, item))
                {
                    _itemCarryPos = wantedPos;
                    _itemCarrySmoothValid = true;
                    _itemCarryItem = item;
                    _itemCarryLastPos = wantedPos;
                }
                else
                {
                    float dt = Time.unscaledDeltaTime;
                    float k = dt > 0f ? 1f - Mathf.Exp(-dt / 0.035f) : 1f;
                    _itemCarryPos = Vector3.Lerp(_itemCarryPos, wantedPos, k);
                    float step = Vector3.Distance(_itemCarryPos, _itemCarryLastPos);
                    _itemCarryLastPos = _itemCarryPos;
                    if (dt > 0f)
                    {
                        _itemJitterMax = Mathf.Max(_itemJitterMax, step);
                    }
                }
                track.position = _itemCarryPos;
                track.rotation = baseWorldRot;
            }
            else
            {
                track.position = wantedPos;
                track.rotation = wantedRot;
            }
            track.localScale = _movedItemBaseScale;
            _movedItemWrittenPos = track.localPosition;
            _movedItemWrittenRot = track.localRotation;

            // Left support arm: the left hand is not the socket owner, but on a
            // two-handed tool it must still hold its part. The target is the
            // carried game left wrist/knuckle (the same translation that moved
            // the item), solved as a position-only two-bone IK so the left wrist
            // keeps its retargeted orientation and does not wind up.
            if (modelMode && HeldItemUsesLeftHand())
            {
                // Use the same carried (FP-rig aware) anchor as the right
                // hand, so the left target is measured in the rig the item is
                // authored in. deltaPos then moves it with the item.
                PalmAnchor gameLeftAnchor, modelLeftAnchor;
                bool hasLeftAnchor = TryPalmAnchor("L", firstPersonRig,
                    out gameLeftAnchor, out modelLeftAnchor);
                Transform gameLeft = SourceTransform("hand_L");
                Transform gameLeftKnuckle = SourceTransform("hand_L_midl_base");
                Transform modelLeft = ModelTransform("hand_L");
                Transform modelLeftKnuckle = ModelTransform("hand_L_midl_base");
                Vector3 targetKnuckle;
                if (hasLeftAnchor)
                {
                    targetKnuckle = gameLeftAnchor.Point + deltaPos;
                }
                else if (gameLeftKnuckle != null)
                {
                    targetKnuckle = gameLeftKnuckle.position + deltaPos;
                }
                else if (gameLeft != null)
                {
                    targetKnuckle = gameLeft.position + deltaPos;
                }
                else
                {
                    targetKnuckle = Vector3.zero;
                }
                Vector3 targetWrist = targetKnuckle;
                if (modelLeft != null && modelLeftKnuckle != null)
                {
                    targetWrist = targetKnuckle
                        - modelLeft.rotation
                        * (modelLeftKnuckle.position - modelLeft.position);
                }
                if (hasLeftAnchor || gameLeft != null)
                {
                    UpdateSupportArm(true, targetWrist);
                }
                else
                {
                    UpdateSupportArm(false, Vector3.zero);
                }
            }
            else
            {
                UpdateSupportArm(false, Vector3.zero);
            }

            float movedDist = Vector3.Distance(track.position, baseWorldPos);
            float turned = Quaternion.Angle(track.rotation, baseWorldRot);
            if (!_movedItemLogged)
            {
                _movedItemLogged = true;
                // The socket's relation to the retarget hand says whether the
                // authored pose is hand-driven (the normal case) or camera-
                // driven (the tool still ends up rigidly in the model hand, but
                // the captured base can change while the game aims).
                Transform gameHand = SourceTransform("hand_R");
                Transform modelHand = ModelTransform("hand_R");
                string rel = gameHand == null ? "no-hand"
                    : parent == gameHand ? "hand"
                    : parent.IsChildOf(gameHand) ? "under-hand" : "other";
                float handGap = gameHand != null && modelHand != null
                    ? Vector3.Distance(gameHand.position, modelHand.position) : -1f;
                Logger.LogInfo("Item adapt: " + item.name
                    + " mode=" + (_holdMode != null ? _holdMode.Value : "?")
                    + " parent=" + GetPath(parent, null) + " (" + rel + ")"
                    + " rig=" + (firstPersonRig ? "fp" : "body")
                    + " move=" + movedDist.ToString("F3") + " m"
                    + " turn=" + turned.ToString("F0") + " deg"
                    + " handGap=" + handGap.ToString("F3"));
            }

            if (modelMode && Time.unscaledTime >= _nextModelGripLog)
            {
                _nextModelGripLog = Time.unscaledTime + 0.5f;
                LogModelGrip(item, track, parent, baseWorldPos, baseWorldRot,
                    firstPersonRig, deltaPos);
                _itemJitterMax = 0f;
            }
        }

        /// <summary>
        /// True for the model-led hold pipeline (the default). Anything that is
        /// not an explicit Game/Off value falls back to Model.
        /// </summary>
        private bool ModelGripModeActive()
        {
            if (_holdMode == null)
            {
                return true;
            }
            string v = _holdMode.Value;
            return !string.Equals(v, "VanillaAdaptive", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, "Game", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Model-mode item pull: after the draw has settled, move the item a few
        /// centimetres towards the character so the short model arm only needs a
        /// small IK correction. During the draw (first 0.45 s) and while
        /// holstering the weight is 0, so the authored equip animation is never
        /// disturbed. The same _itemPull vector is added to both palm targets.
        /// </summary>
        private void UpdateModelItemPull(bool holding)
        {
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            GameObject item = held != null && held.item != null
                ? held.item.gameObject : null;
            if (!ReferenceEquals(item, _itemPullItem))
            {
                _itemPullItem = item;
                _itemPullWeight = 0f;
                _itemPullValid = false;
                _itemPullSince = Time.unscaledTime;
            }
            if (holding && !_itemPullHolding)
            {
                // Just drawn: restart the settle delay.
                _itemPullSince = Time.unscaledTime;
            }
            _itemPullHolding = holding;
            if (!holding || item == null)
            {
                _itemPullWeight = 0f;
                _itemPullValid = false;
                return;
            }
            float dt = Time.unscaledDeltaTime;
            bool ready = Time.unscaledTime - _itemPullSince >= 0.45f;
            _itemPullWeight = Mathf.MoveTowards(_itemPullWeight,
                ready ? 1f : 0f, dt > 0f ? dt / 0.20f : 0f);
            if (_itemPullWeight <= 0.0001f)
            {
                _itemPullValid = false;
                return;
            }
            // Pull straight BACK towards the character, not towards the head
            // point: the head is above the grip, so using it lifted the weapon
            // out of the hands. Backwards is also the axis the short model arm
            // cannot cover.
            Vector3 dir = _container != null ? -_container.forward : Vector3.back;
            if (dir.sqrMagnitude < 1e-6f)
            {
                dir = Vector3.back;
            }
            float max = _itemPull != null
                ? Mathf.Clamp(_itemPull.Value, 0f, 0.2f) : 0.06f;
            Vector3 target = dir.normalized * (max * _itemPullWeight);
            if (!_itemPullValid)
            {
                _itemPullSmooth = target;
                _itemPullValid = true;
            }
            else
            {
                float k = dt > 0f ? 1f - Mathf.Exp(-dt / 0.05f) : 1f;
                _itemPullSmooth = Vector3.Lerp(_itemPullSmooth, target, k);
            }
            _itemPullVec = _itemPullSmooth;
        }

        /// <summary>
        /// Fades the left support solve in/out and runs it. Called once per frame
        /// from AdjustMovedItem (the final pass), after the item has been placed.
        /// </summary>
        private void UpdateSupportArm(bool active, Vector3 target)
        {
            float dt = Time.unscaledDeltaTime;
            float step = dt > 0f ? dt / 0.22f : 0f;
            _supportWeightL = Mathf.MoveTowards(_supportWeightL,
                active ? 1f : 0f, step);
            if (active)
            {
                _supportTargetL = target;
                _supportTargetValidL = true;
            }
            if (_supportWeightL <= 0.0001f || !_supportTargetValidL)
            {
                return;
            }
            AimArmAt("L", _supportTargetL, _supportWeightL, true);
        }

        /// <summary>
        /// Model mode arm solve: position-only two-bone IK for the gripping
        /// arm(s) onto the game hands, with the retargeted hand rotation kept.
        /// The item is NOT moved, so it keeps the authored world pose and
        /// follows the camera; the model hand reaches the hand the item is
        /// parented to instead. This is the part the old SolveArm did with a
        /// hand-orientation pin and twist redistribution, which is what wound
        /// the wrist up.
        /// </summary>
        private void UpdateModelArms(bool activeRight, bool activeLeft,
            Vector3 gamePalmRight, Vector3 gamePalmLeft)
        {
            float dt = Time.unscaledDeltaTime;
            float step = dt > 0f ? dt / 0.22f : 0f;
            // A new held item: restart the solve from the pure retargeted pose
            // instead of snapping the arm from the previous item's target.
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            GameObject heldObject = held != null && held.item != null
                ? held.item.gameObject : null;
            if (!ReferenceEquals(heldObject, _modelArmSmoothItem))
            {
                _modelArmSmoothItem = heldObject;
                _modelArmSmoothValidR = false;
                _modelArmSmoothValidL = false;
                _modelArmWeightR = 0f;
                _modelArmWeightL = 0f;
                _modelArmLastValidR = false;
                _modelArmLastValidL = false;
                _modelArmJitterMax = 0f;
            }
            _modelArmWeightR = Mathf.MoveTowards(_modelArmWeightR,
                activeRight ? 1f : 0f, step);
            _modelArmWeightL = Mathf.MoveTowards(_modelArmWeightL,
                activeLeft ? 1f : 0f, step);
            if (activeRight)
            {
                Vector3 target = WristForPalm("R", gamePalmRight);
                _modelArmTargetR = target;
                _modelArmTargetValidR = true;
                _modelArmSmoothR = _modelArmSmoothValidR
                    ? Vector3.Lerp(_modelArmSmoothR, target,
                        dt > 0f ? 1f - Mathf.Exp(-dt / 0.03f) : 1f)
                    : target;
                _modelArmSmoothValidR = true;
            }
            if (activeLeft)
            {
                Vector3 target = WristForPalm("L", gamePalmLeft);
                _modelArmTargetL = target;
                _modelArmTargetValidL = true;
                _modelArmSmoothL = _modelArmSmoothValidL
                    ? Vector3.Lerp(_modelArmSmoothL, target,
                        dt > 0f ? 1f - Mathf.Exp(-dt / 0.03f) : 1f)
                    : target;
                _modelArmSmoothValidL = true;
            }
            if (_modelArmWeightR > 0.0001f && _modelArmTargetValidR)
            {
                SolveHeldArm("R", _modelArmSmoothR, _modelArmWeightR);
            }
            if (_modelArmWeightL > 0.0001f && _modelArmTargetValidL)
            {
                SolveHeldArm("L", _modelArmSmoothL, _modelArmWeightL);
            }
            MeasureModelArmJitter();
            bool logging = activeRight || activeLeft
                || _modelArmWeightR > 0.001f || _modelArmWeightL > 0.001f;
            if (logging && Time.unscaledTime >= _nextModelArmLog)
            {
                _nextModelArmLog = Time.unscaledTime + 0.5f;
                LogModelArms(gamePalmRight, gamePalmLeft);
                _modelArmJitterMax = 0f;
            }
        }

        /// <summary>
        /// Middle of the palm (between the middle finger's base and first
        /// joint), live on the model or on the game skeleton. This is the
        /// point the handle sits on; aiming the WRIST at the game wrist left
        /// the model's palm centre on top of the handle (knife/builder clipped
        /// into the palm, seaglide hands inside the grips).
        /// </summary>
        private Vector3 PalmPoint(string side, bool model)
        {
            Transform k = model
                ? ModelTransform("hand_" + side + "_midl_base")
                : SourceTransform("hand_" + side + "_midl_base");
            Transform m = model
                ? ModelTransform("hand_" + side + "_midl_mid")
                : SourceTransform("hand_" + side + "_midl_mid");
            if (k != null && m != null)
            {
                return (k.position + m.position) * 0.5f;
            }
            Transform h = model
                ? ModelTransform("hand_" + side) : SourceTransform("hand_" + side);
            return h != null ? h.position : Vector3.zero;
        }

        /// <summary>
        /// Wrist position that puts the model's palm centre on the game palm
        /// point, using the model hand's current (retargeted) rotation. The
        /// hand itself is not rotated, so no wrist twist is introduced.
        /// </summary>
        private Vector3 WristForPalm(string side, Vector3 gamePalm)
        {
            Transform hand = ModelTransform("hand_" + side);
            if (hand == null)
            {
                return gamePalm;
            }
            Vector3 palm = PalmPoint(side, true);
            return gamePalm - hand.rotation * (palm - hand.position);
        }

        /// <summary>
        /// Rotate the gripping hand onto the game palm frame (PalmFix) without
        /// moving the arm. The hand bone's bind is ~179 deg from the suit's, so
        /// the retargeted palm faces the wrong way: the knife/builder handle
        /// ends up inside the palm and the seaglide hands sit inside the grips.
        /// This only changes the hand rotation; the arm position is left to
        /// the fitted bone lengths. Slerped in/out over 0.22 s so it cannot
        /// snap.
        /// </summary>
        private void UpdateHandAlignment(bool activeRight, bool activeLeft)
        {
            float dt = Time.unscaledDeltaTime;
            float step = dt > 0f ? dt / 0.22f : 0f;
            _handAlignWeightR = Mathf.MoveTowards(_handAlignWeightR,
                activeRight ? 1f : 0f, step);
            _handAlignWeightL = Mathf.MoveTowards(_handAlignWeightL,
                activeLeft ? 1f : 0f, step);
            AlignHandToGamePalm("R", _handAlignWeightR);
            AlignHandToGamePalm("L", _handAlignWeightL);
        }

        private void AlignHandToGamePalm(string side, float weight)
        {
            if (weight <= 0.0001f)
            {
                return;
            }
            ArmRest rest = _armRest[side == "L" ? 0 : 1];
            if (rest == null || !rest.HasPalm)
            {
                return;
            }
            Transform end = ModelTransform("hand_" + side);
            Transform mid = ModelTransform("elbow_" + side);
            Transform game = SourceTransform("hand_" + side);
            if (end == null || game == null)
            {
                return;
            }
            Quaternion want = game.rotation * rest.PalmFix;
            // Split the palm correction: roll the FOREARM by the twist part so
            // the hand and fingers rotate rigidly (no finger shear), then put
            // only the configured share of the remaining swing on the hand
            // itself. Rotating the hand by the full 145 deg is what stretched
            // the fingers.
            if (mid != null)
            {
                Vector3 fore = end.position - mid.position;
                if (fore.sqrMagnitude > 1e-8f)
                {
                    fore.Normalize();
                    Quaternion delta = want * Quaternion.Inverse(end.rotation);
                    float twist = SignedTwist(delta, fore);
                    if (Mathf.Abs(twist) > 0.05f)
                    {
                        mid.rotation = Quaternion.AngleAxis(twist * weight, fore)
                            * mid.rotation;
                    }
                }
            }
            float handShare = 0.5f;
            if (handShare > 0.0001f)
            {
                end.rotation = Quaternion.Slerp(end.rotation, want,
                    weight * handShare);
            }
        }

        /// <summary>
        /// Model arm solve used by the held-item pipeline. Position-only: the
        /// upper arm and forearm are aimed at a (smoothed) game-hand target and
        /// the hand keeps its retargeted rotation. Deliberately does NOT share
        /// the swing to the clavicle and does NOT slide the arm root, and puts
        /// the elbow on a fixed down/outward pole. Those three are what folded
        /// the upper arm into the chest on the seaglide/rifle; without them the
        /// arm keeps its shape, at the cost of the hand falling a few
        /// centimetres short when the model arm cannot reach.
        /// </summary>
        private void SolveHeldArm(string side, Vector3 target, float weight)
        {
            Transform upper = ModelTransform("shoulder_" + side);
            Transform mid = ModelTransform("elbow_" + side);
            Transform end = ModelTransform("hand_" + side);
            if (upper == null || mid == null || end == null)
            {
                return;
            }
            float w = Mathf.Clamp01(weight);
            if (w <= 0.0001f)
            {
                return;
            }
            float l1 = Vector3.Distance(upper.position, mid.position);
            float l2 = Vector3.Distance(mid.position, end.position);
            if (l1 < 1e-4f || l2 < 1e-4f)
            {
                return;
            }
            Vector3 a = upper.position;
            Vector3 b = mid.position;
            Vector3 want = target - a;
            float dist = want.magnitude;
            if (dist < 1e-5f)
            {
                return;
            }
            Vector3 tDir = want / dist;
            float reach = l1 + l2 - 0.002f;
            float dd = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 0.002f, reach);
            Vector3 pole = StablePole(side, a, b, tDir);
            float cosA = (l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd);
            float angA = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            Vector3 newB = a + (tDir * Mathf.Cos(angA) + pole * Mathf.Sin(angA)) * l1;

            Vector3 upperNow = b - a;
            if (upperNow.sqrMagnitude > 1e-8f && (newB - a).sqrMagnitude > 1e-8f)
            {
                upper.rotation = Quaternion.Slerp(Quaternion.identity,
                    Quaternion.FromToRotation(upperNow, newB - a), w) * upper.rotation;
            }
            // Forearm: point it at the target. When the target is reachable
            // this puts the wrist exactly there; when it is not, the hand ends
            // up short along the same line instead of the arm folding.
            Vector3 b2 = mid.position;
            Vector3 aim = target - b2;
            Vector3 foreNow = end.position - b2;
            if (aim.sqrMagnitude > 1e-8f && foreNow.sqrMagnitude > 1e-8f)
            {
                mid.rotation = Quaternion.Slerp(Quaternion.identity,
                    Quaternion.FromToRotation(foreNow, aim), w) * mid.rotation;
            }
            // Hand rotation intentionally unchanged: it already follows the
            // game hand from the retarget, and rotating it here is what used
            // to fold the wrist / make the knife buzz.
        }

        /// <summary>
        /// Fixed elbow direction for a held item: down and outward from the
        /// torso. A fixed pole cannot flip between frames (no jitter) and keeps
        /// the elbow outside the body when the hands are pulled in close.
        /// </summary>
        private Vector3 SafeElbowPole(string side, Vector3 tDir)
        {
            Vector3 down = _container != null ? -_container.up : Vector3.down;
            Vector3 outDir = _container != null ? _container.right : Vector3.right;
            if (side == "L")
            {
                outDir = -outDir;
            }
            Vector3 desired = down * 0.8f + outDir * 0.45f;
            desired -= Vector3.Dot(desired, tDir) * tDir;
            if (desired.sqrMagnitude < 1e-6f)
            {
                Vector3 fwd = _container != null ? _container.forward : Vector3.forward;
                desired = Vector3.Cross(tDir, fwd);
                desired -= Vector3.Dot(desired, tDir) * tDir;
            }
            if (desired.sqrMagnitude < 1e-8f)
            {
                desired = down;
            }
            return desired.normalized;
        }

        /// <summary>
        /// Max per-frame hand displacement for the diagnostic line; a buzzing
        /// hand shows up here even when the target itself looks smooth.
        /// </summary>
        private void MeasureModelArmJitter()
        {
            Transform r = ModelTransform("hand_R");
            if (r != null)
            {
                if (_modelArmLastValidR)
                {
                    _modelArmJitterMax = Mathf.Max(_modelArmJitterMax,
                        Vector3.Distance(r.position, _modelArmLastR));
                }
                _modelArmLastR = r.position;
                _modelArmLastValidR = true;
            }
            Transform l = ModelTransform("hand_L");
            if (l != null)
            {
                if (_modelArmLastValidL)
                {
                    _modelArmJitterMax = Mathf.Max(_modelArmJitterMax,
                        Vector3.Distance(l.position, _modelArmLastL));
                }
                _modelArmLastL = l.position;
                _modelArmLastValidL = true;
            }
        }

        /// <summary>
        /// Rolls the forearm around its own axis so the model's palm faces the
        /// way the game's palm does, without changing the wrist joint. The
        /// model's hand bone bind is ~179 deg from the suit's (its bind wrist
        /// local is 0 deg while the suit's is 179.2), so the retargeted hand is
        /// palm-up while the authored tool expects a palm-down grip. Rotating
        /// the HAND to fix that is what used to fold the wrist; rolling the
        /// FOREARM (pronation/supination) flips the hand and everything below
        /// it while the wrist local stays exactly as retargeted.
        ///
        /// Only the twist component of the palm correction is used: the
        /// remaining swing would need wrist flexion and is left to the natural
        /// wrist. The angle is measured live from (game hand * PalmFix) and the
        /// current hand, then applied around the elbow-to-wrist axis.
        /// </summary>
        private void CorrectModelHandRoll(string side, float weight)
        {
            if (weight <= 0.0001f)
            {
                return;
            }
            ArmRest rest = _armRest[side == "L" ? 0 : 1];
            if (rest == null || !rest.HasPalm || !rest.HasWrist)
            {
                return;
            }
            Transform mid = ModelTransform("elbow_" + side);
            Transform end = ModelTransform("hand_" + side);
            Transform game = SourceTransform("hand_" + side);
            if (mid == null || end == null || game == null)
            {
                return;
            }
            Quaternion desired = game.rotation * rest.PalmFix;
            Quaternion delta = desired * Quaternion.Inverse(end.rotation);
            Vector3 fore = end.position - mid.position;
            if (fore.sqrMagnitude < 1e-8f)
            {
                return;
            }
            fore.Normalize();
            float twist = SignedTwist(delta, fore) * weight;
            // Slew-limit the per-frame roll. On the seaglide the palm
            // correction can be >100 deg; applying it in one frame made the
            // forearm snap (the "鬼畜" report). 1200 deg/s rolls it in over a
            // few frames and never lets a sign flip jump the sleeve.
            float dt = Time.unscaledDeltaTime;
            float maxStep = dt > 0f ? 1200f * dt : 180f;
            twist = Mathf.Clamp(twist, -maxStep, maxStep);
            if (side == "L")
            {
                _modelHandRollL = twist;
            }
            else
            {
                _modelHandRollR = twist;
            }
            if (Mathf.Abs(twist) < 0.05f)
            {
                return;
            }
            mid.rotation = Quaternion.AngleAxis(twist, fore) * mid.rotation;
        }

        /// <summary>
        /// Diagnostic line for the model arm solve: the remaining position /
        /// rotation error at each hand, the weights, and whether the held item
        /// is on screen. This is what tells whether the tool is actually in
        /// front of the camera or behind it.
        /// </summary>
        private void LogModelArms(Vector3 gamePalmRight, Vector3 gamePalmLeft)
        {
            Transform modelR = ModelTransform("hand_R");
            Transform gameR = SourceTransform("hand_R");
            Transform modelL = ModelTransform("hand_L");
            Transform gameL = SourceTransform("hand_L");
            float errR = modelR != null
                ? Vector3.Distance(PalmPoint("R", true), gamePalmRight) : -1f;
            float errL = -1f;
            float rotR = -1f;
            float rotL = -1f;
            if (modelL != null && _modelArmWeightL > 0.001f)
            {
                errL = Vector3.Distance(PalmPoint("L", true), gamePalmLeft);
            }
            if (modelR != null && gameR != null)
            {
                rotR = Quaternion.Angle(modelR.rotation, gameR.rotation);
            }
            if (modelL != null && gameL != null)
            {
                rotL = Quaternion.Angle(modelL.rotation, gameL.rotation);
            }
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            Transform track = held != null && held.item != null
                ? held.item.transform : null;
            string item = track != null ? track.name : "-";
            Vector3 screen = Vector3.zero;
            bool visible = false;
            float camAngle = -1f;
            Camera cam = Camera.main;
            if (track != null)
            {
                if (cam != null)
                {
                    screen = cam.WorldToScreenPoint(track.position);
                    camAngle = Vector3.Angle(track.forward, cam.transform.forward);
                }
                Renderer[] renderers = track.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null && renderers[i].enabled
                        && renderers[i].gameObject.activeInHierarchy
                        && renderers[i].isVisible)
                    {
                        visible = true;
                        break;
                    }
                }
            }
            Logger.LogInfo(string.Format(
                "ModelArm R: err={0:F3} rot={1:F0} w={2:F2} | "
                + "L: err={3:F3} rot={4:F0} w={5:F2} | jitter={6:F3} item={7} "
                + "screen=({8:F0},{9:F0},{10:F1}) vis={11} camAngle={12:F0}",
                errR, rotR, _modelArmWeightR,
                errL, rotL, _modelArmWeightL, _modelArmJitterMax,
                item, screen.x, screen.y, screen.z, visible, camAngle));
        }

        /// <summary>
        /// One diagnostic line per half second for the model-led carry: how far
        /// the item actually moved from the authored pose, whether it is on
        /// screen, the two palm gaps, and the left support error. The raw
        /// transform translation is an affine offset (rotation about the world
        /// origin), so its magnitude is meaningless; move is the real thing.
        /// </summary>
        private void LogModelGrip(GameObject item, Transform track, Transform parent,
            Vector3 baseWorldPos, Quaternion baseWorldRot, bool firstPersonRig,
            Vector3 deltaPos)
        {
            Transform gameKnuckle = SourceTransform("hand_R_midl_base");
            Transform modelKnuckle = ModelTransform("hand_R_midl_base");
            Transform gameLeft = SourceTransform("hand_L_midl_base");
            Transform modelLeft = ModelTransform("hand_L_midl_base");
            float rightGap = gameKnuckle != null && modelKnuckle != null
                ? Vector3.Distance(modelKnuckle.position, gameKnuckle.position) : -1f;
            float leftGap = -1f;
            if (gameLeft != null && modelLeft != null && HeldItemUsesLeftHand())
            {
                leftGap = Vector3.Distance(modelLeft.position,
                    gameLeft.position + deltaPos);
            }
            Camera cam = Camera.main;
            Vector3 screen = cam != null
                ? cam.WorldToScreenPoint(track.position) : Vector3.zero;
            float camAngle = cam != null
                ? Vector3.Angle(track.forward, cam.transform.forward) : -1f;
            bool visible = false;
            Renderer[] renderers = item.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled
                    && renderers[i].gameObject.activeInHierarchy
                    && renderers[i].isVisible)
                {
                    visible = true;
                    break;
                }
            }
            Logger.LogInfo(string.Format(
                "ModelGrip {0}: move={1:F3} turn={2:F0} jitter={3:F3} "
                + "Rgap={4:F3} Lgap={5:F3} support={6:F2} rig={7} "
                + "screen=({8:F0},{9:F0},{10:F1}) visible={11} camAngle={12:F0}",
                item.name,
                Vector3.Distance(track.position, baseWorldPos),
                Quaternion.Angle(track.rotation, baseWorldRot),
                _itemJitterMax,
                rightGap, leftGap, _supportWeightL,
                firstPersonRig ? "fp" : "body",
                screen.x, screen.y, screen.z, visible, camAngle));
        }

        /// <summary>
        /// One hand's grip anchor for carrying the held item: the middle knuckle
        /// (the authored handle sits in the palm, just behind it) and the live
        /// palm frame (fingers forward, thumb side). World space, both sides.
        /// </summary>
        private struct PalmAnchor
        {
            public Vector3 Point;
            public Quaternion Frame;
        }

        /// <summary>
        /// True when the item's socket lives in the game's first-person arms rig
        /// rather than on the body skeleton. The item's authored pose is
        /// expressed in that rig, so its palm anchor has to be measured there
        /// too: the body hand's palm is carried across by the hand-to-hand
        /// delta instead of mixing an anchor from one rig with a pose from the
        /// other.
        /// </summary>
        private static bool ItemInFirstPersonRig(Transform itemParent)
        {
            Player player = Player.main;
            ArmsController arms = player != null ? player.armsController : null;
            if (arms == null || itemParent == null)
            {
                return false;
            }
            Transform right = arms.rightHand;
            Transform left = arms.leftHand;
            return (right != null
                    && (itemParent == right || itemParent.IsChildOf(right)))
                || (left != null
                    && (itemParent == left || itemParent.IsChildOf(left)));
        }

        private bool TryPalmAnchor(string side, bool useFirstPerson,
            out PalmAnchor game, out PalmAnchor model)
        {
            game = default(PalmAnchor);
            model = default(PalmAnchor);
            Transform gameHand = SourceTransform("hand_" + side);
            Transform gameKnuckle = SourceTransform("hand_" + side + "_midl_base");
            Transform gameThumb = SourceTransform("hand_" + side + "_thumb_base");
            Transform modelHand = ModelTransform("hand_" + side);
            Transform modelKnuckle = ModelTransform("hand_" + side + "_midl_base");
            Transform modelThumb = ModelTransform("hand_" + side + "_thumb_base");
            Quaternion gameFrame, modelFrame;
            if (!PalmFrame(gameHand, gameKnuckle, gameThumb, out gameFrame)
                || !PalmFrame(modelHand, modelKnuckle, modelThumb, out modelFrame))
            {
                return false;
            }
            game.Point = gameKnuckle.position;
            game.Frame = gameFrame;
            if (useFirstPerson)
            {
                // Same palm frame, expressed on the FP hand: the body hand's
                // knuckle moved across by the hand-to-hand delta. The FP hand
                // has no finger bones of its own, so the body hand's palm is
                // the only measurement of the grip available.
                Player player = Player.main;
                ArmsController arms = player != null ? player.armsController : null;
                Transform fp = arms != null
                    ? (side == "L" ? arms.leftHand : arms.rightHand) : null;
                if (fp != null && gameHand != null)
                {
                    Quaternion carry = fp.rotation
                        * Quaternion.Inverse(gameHand.rotation);
                    game.Point = fp.position
                        + carry * (game.Point - gameHand.position);
                    game.Frame = carry * game.Frame;
                }
            }
            model.Point = modelKnuckle.position;
            model.Frame = modelFrame;
            return true;
        }

        /// <summary>
        /// Transform that carries a held item from the game's right palm to the
        /// model's right palm. The right hand owns the socket, so it is the only
        /// anchor used: letting a two-handed tool's left hand pull the item too
        /// made the item chase both palms (and made the seaglide rock between
        /// them). The left hand is solved onto the carried item separately.
        ///
        /// With <paramref name="alignRotation"/> false the rotation is identity:
        /// the item keeps the world rotation the game authored, which is what
        /// makes it keep following the camera. Only its position moves, by the
        /// offset between the game and model knuckles. With it true (the legacy
        /// MoveHeldItemToModelHand path) the palm frames are also mapped, which
        /// aligns the tool's roll with the model's palm but costs the camera
        /// orientation on models whose palm bind differs a lot.
        /// </summary>
        private bool HoldPalmDelta(bool useFirstPerson, bool alignRotation,
            out Quaternion rot, out Vector3 pos)
        {
            rot = Quaternion.identity;
            pos = Vector3.zero;
            PalmAnchor gameR, modelR;
            if (!TryPalmAnchor("R", useFirstPerson, out gameR, out modelR))
            {
                return false;
            }
            if (alignRotation)
            {
                rot = modelR.Frame * Quaternion.Inverse(gameR.Frame);
                pos = modelR.Point - rot * gameR.Point;
            }
            else
            {
                pos = modelR.Point - gameR.Point;
            }
            return true;
        }

        /// <summary>
        /// Puts the tool's local transform back where the game authored it and
        /// stops tracking. Only restores while the tool still has the parent it
        /// was captured under; after a holster the game may have reparented it,
        /// and writing the old local there would move an inventory object.
        /// </summary>
        private void RestoreMovedItem()
        {
            if (_movedItem != null && _movedItemBaseValid)
            {
                Transform track = _movedItem.transform;
                if (track != null && track.parent == _movedItemParent)
                {
                    track.localPosition = _movedItemBasePos;
                    track.localRotation = _movedItemBaseRot;
                    track.localScale = _movedItemBaseScale;
                }
            }
            _movedItem = null;
            _movedItemParent = null;
            _movedItemBaseValid = false;
            _movedItemLogged = false;
        }

        /// <summary>
        /// The hold-pose class of a held item. The knife gets its own pose;
        /// every other one-handed quick-slot item shares the generic tool grip.
        /// </summary>
        private static string HoldPoseClass(InventoryItem held)
        {
            if (held != null && held.item != null
                && held.item.GetTechType() == TechType.Knife)
            {
                return "knife";
            }
            return "tool";
        }

        private static bool PlayerSwimming()
        {
            try
            {
                Player player = Player.main;
                return player != null && player.IsSwimming();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Captures the vanilla grip pose for the held one-handed item and solves
        /// the model's right arm onto it once. Runs in the final late pass, when
        /// the game's FinalIK has already written the hand bones, and only after
        /// the equip crossfade has settled, so the captured frame is the true
        /// grip pose and not a draw-animation or swim-stroke frame.
        /// </summary>
        private void TryCaptureHoldPose()
        {
            if (!_holdCapturePending || !_holdPoseMode.Value || _container == null
                || _data == null || _diagSources == null || _diagIndexByName == null
                || _diagBones == null)
            {
                return;
            }
            if (!HoldingItem() || PdaInUse())
            {
                HoldPending("not holding or PDA in use");
                return;
            }
            InventoryItem held = Inventory.main.quickSlots.heldItem;
            if (held == null || held.item == null)
            {
                _holdCapturePending = false;
                return;
            }
            if (HeldItemUsesLeftHand())
            {
                // Two-handed grips keep the old IK; nothing to capture.
                _holdCapturePending = false;
                return;
            }
            string cls = HoldPoseClass(held);
            if (_holdPoses.ContainsKey(cls))
            {
                _holdCapturePending = false;
                return;
            }
            // The draw animation and the pose crossfade are still playing, or the
            // hand is mid-stroke/mid-swing: the captured frame would not be the
            // settled grip.
            if (_poseTransition > 0f || PlayerSwimming())
            {
                HoldPending("transition=" + _poseTransition.ToString("F2")
                    + " swim=" + PlayerSwimming());
                return;
            }
            PlayerTool tool = held.item.GetComponent<PlayerTool>();
            if (tool != null && tool.isInUse)
            {
                HoldPending("tool isInUse");
                return;
            }

            SourceBone srcUpper, srcMid, srcHand;
            if (!_diagSources.Bones.TryGetValue("shoulder_R", out srcUpper)
                || !_diagSources.Bones.TryGetValue("elbow_R", out srcMid)
                || !_diagSources.Bones.TryGetValue("hand_R", out srcHand)
                || srcUpper.Bone == null || srcMid.Bone == null || srcHand.Bone == null
                || srcUpper.SmrTransform == null || srcMid.SmrTransform == null
                || srcHand.SmrTransform == null)
            {
                HoldPending("missing source arm bones");
                return;
            }
            ArmRest rest = _armRest[1];
            if (rest == null)
            {
                HoldPending("no arm rest for R");
                return;
            }
            Transform up = ModelTransform("shoulder_R");
            Transform mid = ModelTransform("elbow_R");
            Transform hand = ModelTransform("hand_R");
            if (up == null || mid == null || hand == null)
            {
                HoldPending("missing model arm bones");
                return;
            }

            // The twist bone is the elbow's parent; solve the chain with it at
            // its authored rest so the captured rotations and the runtime replay
            // (which also holds it at rest) agree on where the elbow sits.
            Transform twist = null;
            if (rest.ArmTwist != null)
            {
                int twistIndex = ModelIndex("arm_twist_R");
                if (twistIndex >= 0 && twistIndex < _data.Bones.Count)
                {
                    twist = rest.ArmTwist;
                    twist.localRotation = _data.Bones[twistIndex].LocalRot;
                }
            }

            Vector3 s = up.position;
            Vector3 e = mid.position;
            Vector3 w = hand.position;
            float l1 = Vector3.Distance(s, e);
            float l2 = Vector3.Distance(e, w);
            if (l1 < 1e-4f || l2 < 1e-4f)
            {
                HoldPending("bad arm lengths l1=" + l1.ToString("F4")
                    + " l2=" + l2.ToString("F4"));
                return;
            }
            // The grip target is the game hand itself, in the container's space:
            // the palm alignment carries the game hand's rotation onto the
            // model palm, exactly as the old IK did, but solved only once.
            Vector3 target = _container.InverseTransformPoint(srcHand.Bone.position);
            Transform modelKnuckle = ModelTransform("hand_R_midl_base");
            Transform gameKnuckle = SourceTransform("hand_R_midl_base");
            Transform modelThumb = ModelTransform("hand_R_thumb_base");
            Transform gameThumb = SourceTransform("hand_R_thumb_base");
            Quaternion handWorld;
            Quaternion gamePalmLive = Quaternion.identity;
            Quaternion modelPalmLive = Quaternion.identity;
            bool palmLive = modelKnuckle != null && gameKnuckle != null
                && modelThumb != null && gameThumb != null
                && PalmFrame(srcHand.Bone, gameKnuckle, gameThumb, out gamePalmLive)
                && PalmFrame(hand, modelKnuckle, modelThumb, out modelPalmLive);
            if (palmLive)
            {
                // Live palm frames: both hands are mid-grip with the fingers
                // curled around the handle, so the curl is part of both frames
                // and cancels in the mapping. The rest-pose calibration reads
                // that curl as a roll error (the knife sat ~30 degrees off).
                handWorld = gamePalmLive * Quaternion.Inverse(modelPalmLive)
                    * hand.rotation;
            }
            else if (rest.HasPalm)
            {
                handWorld = srcHand.Bone.rotation * rest.PalmFix;
            }
            else
            {
                _holdCapturePending = false;
                Logger.LogWarning("Hold pose (" + cls + "): no palm alignment, "
                    + "keeping the arm IK for this item");
                return;
            }
            Vector3 t = _container.TransformPoint(target);
            Vector3 at = t - s;
            float dist = at.magnitude;
            if (dist < 1e-4f)
            {
                return;
            }
            float reach = l1 + l2;
            // The model's hand bone is the wrist, while a held item is gripped
            // in the middle of the palm, and the MMD palm is shorter than the
            // suit glove's: targeting the game wrist leaves the handle beyond
            // the model's fingertips. Aim the wrist so the model's knuckle
            // lands on the game's knuckle instead.
            if (modelKnuckle != null && gameKnuckle != null)
            {
                float palmLength = Vector3.Distance(modelKnuckle.position, hand.position);
                Vector3 gameHandDir = gameKnuckle.position - srcHand.Bone.position;
                if (palmLength > 0.02f * reach && palmLength < 0.5f * l1
                    && gameHandDir.sqrMagnitude > 1e-8f)
                {
                    Vector3 grip = gameKnuckle.position
                        - gameHandDir.normalized * palmLength;
                    if (Vector3.Distance(grip, t) < 0.35f * l2)
                    {
                        t = grip;
                        at = t - s;
                        dist = at.magnitude;
                        target = _container.InverseTransformPoint(t);
                    }
                }
            }
            float dd = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 0.002f, reach - 0.002f);
            Vector3 tDir = at / dist;
            Vector3 perp = -_container.up;
            perp -= Vector3.Dot(perp, tDir) * tDir;
            if (perp.sqrMagnitude < 1e-6f)
            {
                perp = Vector3.Cross(tDir, _container.right);
            }
            if (perp.sqrMagnitude < 1e-6f)
            {
                perp = Vector3.Cross(tDir, Vector3.forward);
            }
            perp.Normalize();
            float cosA = (l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd);
            float angA = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            Vector3 elbow = s + (tDir * Mathf.Cos(angA) + perp * Mathf.Sin(angA)) * l1;

            Quaternion upperSolved = Quaternion.FromToRotation(e - s, elbow - s)
                * up.rotation;
            Quaternion midSolved;
            if (rest.HasWrist)
            {
                Quaternion desiredMid = handWorld * Quaternion.Inverse(rest.WristLocal);
                Vector3 dirNow = w - elbow;
                Vector3 aim = t - elbow;
                midSolved = dirNow.sqrMagnitude > 1e-8f && aim.sqrMagnitude > 1e-8f
                    ? Quaternion.FromToRotation(dirNow, aim) * desiredMid
                    : desiredMid;
            }
            else
            {
                Vector3 dirNow = w - elbow;
                Vector3 aim = t - elbow;
                midSolved = dirNow.sqrMagnitude > 1e-8f && aim.sqrMagnitude > 1e-8f
                    ? Quaternion.FromToRotation(dirNow, aim) * mid.rotation
                    : mid.rotation;
            }

            Quaternion containerInv = Quaternion.Inverse(_container.rotation);
            HoldPoseData pose = new HoldPoseData();
            pose.Class = cls;
            pose.LocalUpper = containerInv * upperSolved;
            pose.LocalMid = containerInv * midSolved;
            pose.LocalHand = containerInv * handWorld;
            pose.SrcUpper0 = Quaternion.Inverse(srcUpper.SmrTransform.rotation)
                * srcUpper.Bone.rotation;
            pose.SrcMid0 = Quaternion.Inverse(srcMid.SmrTransform.rotation)
                * srcMid.Bone.rotation;
            pose.SrcHand0 = Quaternion.Inverse(srcHand.SmrTransform.rotation)
                * srcHand.Bone.rotation;
            pose.TwistRestLocal = twist != null ? twist.localRotation : Quaternion.identity;
            Transform heldTrack = held != null && held.item != null
                ? held.item.transform : null;
            if (heldTrack != null
                && ComputeGripCylinder(heldTrack, out pose.CylAxisLocal,
                    out pose.CylCenterLocal, out pose.CylRadius))
            {
                pose.HasCylinder = true;
            }
            pose.Valid = true;
            _holdPoses[cls] = pose;
            _holdCapturePending = false;
            _holdPendingNextLog = 0f;

            Logger.LogInfo("Hold pose (" + cls + "): grip="
                + target.ToString("F3") + " dist=" + dist.ToString("F3")
                + "/" + reach.ToString("F3")
                + (dist > reach ? " (out of reach by " + (dist - reach).ToString("F3") + ")" : "")
                + " palm=" + (palmLive ? "live"
                    : Quaternion.Angle(Quaternion.identity, rest.PalmFix).ToString("F0") + "deg-fix")
                + " cyl=" + (pose.HasCylinder
                    ? pose.CylRadius.ToString("F4") + "m" : "none"));
            LogGripAnalysis(cls, held);
        }

        /// <summary>
        /// Rate-limited trace of why a pending hold-pose capture has not fired.
        /// The capture gates return silently by design; without this, a stuck
        /// gate is indistinguishable from an unheld item.
        /// </summary>
        private void HoldPending(string reason)
        {
            if (Time.unscaledTime < _holdPendingNextLog)
            {
                return;
            }
            _holdPendingNextLog = Time.unscaledTime + 2f;
            Logger.LogInfo("Hold pose pending: " + reason);
        }

        /// <summary>
        /// Reads the vanilla arm animation every frame and solves it onto the
        /// model's own proportions: the hand target is the vanilla hand's live
        /// position (the reach deficit is ~3 percent, so the elbow only
        /// straightens a hair), the bend plane comes from the vanilla elbow's
        /// live direction, and the hand rotation is the vanilla hand's own
        /// orientation mapped through the live palm frames. Swings, aim and
        /// use actions are the game's motion, proportion-adapted. Only
        /// rotations are written: the sleeve shape cannot change.
        /// </summary>
        private void ApplyHoldPose(string side, float weight, HoldPoseData pose)
        {
            SourceBone srcUpper, srcMid, srcHand;
            if (_diagSources == null || pose == null || !pose.Valid
                || !_diagSources.Bones.TryGetValue("shoulder_" + side, out srcUpper)
                || !_diagSources.Bones.TryGetValue("elbow_" + side, out srcMid)
                || !_diagSources.Bones.TryGetValue("hand_" + side, out srcHand)
                || srcUpper.Bone == null || srcMid.Bone == null || srcHand.Bone == null)
            {
                return;
            }
            Transform up = ModelTransform("shoulder_" + side);
            Transform mid = ModelTransform("elbow_" + side);
            Transform hand = ModelTransform("hand_" + side);
            if (up == null || mid == null || hand == null)
            {
                return;
            }
            // The pose never writes positions; put the roots back to bind so a
            // slide left by the old IK on an earlier frame cannot persist.
            ResetArmRoot(side);
            ArmRest rest = _armRest[side == "L" ? 0 : 1];
            if (rest != null && rest.ArmTwist != null)
            {
                rest.ArmTwist.localRotation = pose.TwistRestLocal;
            }

            Quaternion retUpper = up.rotation;
            Quaternion retMid = mid.rotation;
            Quaternion retHand = hand.rotation;
            Vector3 s = up.position;
            Vector3 t = srcHand.Bone.position;
            Vector3 at = t - s;
            float dist = at.magnitude;
            if (dist < 1e-4f)
            {
                return;
            }
            float l1 = Vector3.Distance(s, mid.position);
            float l2 = Vector3.Distance(mid.position, hand.position);
            if (l1 < 1e-4f || l2 < 1e-4f)
            {
                return;
            }
            float dd = Mathf.Clamp(dist,
                Mathf.Abs(l1 - l2) + 0.002f, l1 + l2 - 0.002f);
            Vector3 tDir = at / dist;
            // The vanilla elbow picks the bend side and the bend plane, so the
            // model's elbow follows the game's animation instead of folding
            // backwards or snapping across the arm.
            Vector3 perp = srcMid.Bone.position - s;
            if (perp.sqrMagnitude < 1e-6f)
            {
                perp = _container != null ? -_container.up : Vector3.down;
            }
            perp -= Vector3.Dot(perp, tDir) * tDir;
            if (perp.sqrMagnitude < 1e-6f)
            {
                perp = Vector3.Cross(tDir,
                    _container != null ? _container.right : Vector3.right);
            }
            perp.Normalize();
            float cosA = (l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd);
            float angA = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            Vector3 elbow = s
                + (tDir * Mathf.Cos(angA) + perp * Mathf.Sin(angA)) * l1;
            Quaternion upperSolved = Quaternion.FromToRotation(
                mid.position - s, elbow - s) * retUpper;
            Vector3 dirNow = hand.position - elbow;
            Vector3 aim = t - elbow;
            Quaternion midSolved = dirNow.sqrMagnitude > 1e-8f
                && aim.sqrMagnitude > 1e-8f
                ? Quaternion.FromToRotation(dirNow, aim) * retMid
                : retMid;
            // The vanilla hand's own orientation, mapped onto the model palm
            // through the live palm frames: the finger curl is part of both
            // frames and cancels, so no rest-pose calibration twist leaks in.
            Quaternion handTarget = retHand;
            if (side == "R")
            {
                Transform modelKnuckle = ModelTransform("hand_R_midl_base");
                Transform gameKnuckle = SourceTransform("hand_R_midl_base");
                Transform modelThumb = ModelTransform("hand_R_thumb_base");
                Transform gameThumb = SourceTransform("hand_R_thumb_base");
                Quaternion gamePalm, modelPalm;
                if (modelKnuckle != null && gameKnuckle != null
                    && modelThumb != null && gameThumb != null
                    && PalmFrame(srcHand.Bone, gameKnuckle, gameThumb,
                        out gamePalm)
                    && PalmFrame(hand, modelKnuckle, modelThumb,
                        out modelPalm))
                {
                    handTarget = gamePalm * Quaternion.Inverse(modelPalm)
                        * retHand;
                }
            }
            up.rotation = Quaternion.Slerp(retUpper, upperSolved, weight);
            mid.rotation = Quaternion.Slerp(retMid, midSolved, weight);
            hand.rotation = Quaternion.Slerp(retHand, handTarget, weight);
            FitGripFingers(pose, weight);
            if (side == "R" && weight > 0.99f
                && !_holdGripErrLogged.ContainsKey(pose.Class))
            {
                _holdGripErrLogged[pose.Class] = true;
                LogModelGripError(pose.Class);
            }
        }

        private static readonly string[] GripProbeBones =
        {
            "hand_R_midl_base", "hand_R_midl_1", "hand_R_midl_2", "hand_R_midl_tip",
            "hand_R_thumb_base", "hand_R_thumb_1", "hand_R_thumb_2", "hand_R_thumb_tip",
            "hand_R_ring_base", "hand_R_ring_1", "hand_R_ring_2", "hand_R_ring_tip",
            "hand_R_pinky_base", "hand_R_pinky_1", "hand_R_pinky_2", "hand_R_pinky_tip",
            "hand_R_index_base", "hand_R_index_1", "hand_R_index_2", "hand_R_index_tip"
        };

        /// <summary>
        /// Fits the grip cylinder to the game's own curled fingers, in the
        /// tool's local space: the socket axis the fingertips wrap at the most
        /// constant distance is the handle, and that distance is its radius.
        /// Falls back to the palm-lateral axis (across the knuckles) when the
        /// game skeleton has no finger tips to measure.
        /// </summary>
        private bool ComputeGripCylinder(Transform track, out Vector3 axisLocal,
            out Vector3 centerLocal, out float radius)
        {
            axisLocal = Vector3.right;
            centerLocal = Vector3.zero;
            radius = 0.02f;
            if (track == null)
            {
                return false;
            }
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            List<Vector3> tips = new List<Vector3>();
            for (int i = 0; i < GripProbeBones.Length; i++)
            {
                string n = GripProbeBones[i];
                if (!n.EndsWith("_tip"))
                {
                    continue;
                }
                Transform bone = SourceTransform(n);
                if (bone != null)
                {
                    tips.Add(track.InverseTransformPoint(bone.position));
                }
            }
            int best = -1;
            float bestSd = float.MaxValue;
            float bestMean = 0f;
            for (int a = 0; a < 3; a++)
            {
                float sum = 0f;
                float sum2 = 0f;
                for (int t = 0; t < tips.Count; t++)
                {
                    Vector3 radial = tips[t] - axes[a]
                        * Vector3.Dot(tips[t], axes[a]);
                    float d = radial.magnitude;
                    sum += d;
                    sum2 += d * d;
                }
                if (tips.Count == 0)
                {
                    break;
                }
                float mean = sum / tips.Count;
                float sd = Mathf.Sqrt(Mathf.Max(0f, sum2 / tips.Count - mean * mean));
                if (sd < bestSd)
                {
                    bestSd = sd;
                    best = a;
                    bestMean = mean;
                }
            }
            if (best < 0)
            {
                // No finger tips on the game skeleton: use the socket axis
                // nearest the palm lateral (across the knuckles) instead.
                Transform gameHand = SourceTransform("hand_R");
                Transform gameKnuckle = SourceTransform("hand_R_midl_base");
                Transform gameThumb = SourceTransform("hand_R_thumb_base");
                if (gameHand == null || gameKnuckle == null || gameThumb == null)
                {
                    return false;
                }
                Vector3 palmDir = gameKnuckle.position - gameHand.position;
                if (palmDir.sqrMagnitude <= 1e-8f)
                {
                    return false;
                }
                palmDir.Normalize();
                Vector3 thumbDir = gameThumb.position - gameHand.position;
                Vector3 lateral = thumbDir
                    - palmDir * Vector3.Dot(thumbDir, palmDir);
                if (lateral.sqrMagnitude <= 1e-8f)
                {
                    return false;
                }
                lateral.Normalize();
                Vector3 latLocal = track.InverseTransformDirection(lateral);
                float bestDot = -1f;
                for (int a = 0; a < 3; a++)
                {
                    float d = Mathf.Abs(Vector3.Dot(axes[a], latLocal));
                    if (d > bestDot)
                    {
                        bestDot = d;
                        best = a;
                    }
                }
                Vector3 ax = axes[best];
                Vector3 kn = track.InverseTransformPoint(gameKnuckle.position);
                centerLocal = ax * Vector3.Dot(kn, ax);
                Vector3 tb = track.InverseTransformPoint(gameThumb.position);
                Vector3 radial = kn - ax * Vector3.Dot(kn, ax);
                Vector3 tbRadial = tb - ax * Vector3.Dot(tb, ax);
                radius = Mathf.Max(0.008f,
                    0.5f * Mathf.Min(radial.magnitude, tbRadial.magnitude));
                axisLocal = ax;
                return true;
            }
            Vector3 axis = axes[best];
            float projSum = 0f;
            for (int t = 0; t < tips.Count; t++)
            {
                projSum += Vector3.Dot(tips[t], axis);
            }
            centerLocal = axis * (projSum / tips.Count);
            axisLocal = axis;
            radius = Mathf.Max(0.005f, bestMean);
            return true;
        }

        /// <summary>
        /// One-shot geometric analysis of the vanilla grip, logged at capture:
        /// the socket transform's pose in game-hand space, every game finger
        /// bone's position in socket space, and the socket axis the fingertips
        /// wrap at the most constant distance (the handle-axis candidate with
        /// its radius). Purely diagnostic; feeds the grip-cylinder fit.
        /// </summary>
        private void LogGripAnalysis(string cls, InventoryItem held)
        {
            Transform track = held != null && held.item != null
                ? held.item.transform : null;
            if (track == null)
            {
                return;
            }
            Logger.LogInfo("Grip analyze (" + cls + "): item=" + track.name
                + " worldScale=" + track.lossyScale.ToString("F3")
                + " parent=" + (track.parent != null
                    ? GetPath(track.parent, null) : "(none)"));
            Transform gameHand = SourceTransform("hand_R");
            if (gameHand == null)
            {
                return;
            }
            Logger.LogInfo("Grip analyze: socketInHand pos="
                + gameHand.InverseTransformPoint(track.position).ToString("F4")
                + " rot=" + (Quaternion.Inverse(gameHand.rotation)
                    * track.rotation).eulerAngles.ToString("F1"));
            Transform gameKnuckle = SourceTransform("hand_R_midl_base");
            Transform gameThumb = SourceTransform("hand_R_thumb_base");
            if (gameKnuckle != null && gameThumb != null)
            {
                Vector3 palmDir = gameKnuckle.position - gameHand.position;
                if (palmDir.sqrMagnitude > 1e-8f)
                {
                    palmDir.Normalize();
                    Vector3 thumbDir = gameThumb.position - gameHand.position;
                    Vector3 lateral = thumbDir
                        - palmDir * Vector3.Dot(thumbDir, palmDir);
                    if (lateral.sqrMagnitude > 1e-8f)
                    {
                        lateral.Normalize();
                        Vector3 up = Vector3.Cross(palmDir, lateral);
                        Logger.LogInfo("Grip analyze: palmAxesInSocket fwd="
                            + track.InverseTransformDirection(palmDir).ToString("F3")
                            + " lat="
                            + track.InverseTransformDirection(lateral).ToString("F3")
                            + " up="
                            + track.InverseTransformDirection(up).ToString("F3"));
                    }
                }
            }
            Renderer rend = track.GetComponentInChildren<Renderer>();
            if (rend != null)
            {
                Bounds b = rend.bounds;
                Logger.LogInfo("Grip analyze: bounds center="
                    + track.InverseTransformPoint(b.center).ToString("F3")
                    + " size=" + b.size.ToString("F3"));
            }
            List<Vector3> tips = new List<Vector3>();
            for (int i = 0; i < GripProbeBones.Length; i++)
            {
                string n = GripProbeBones[i];
                Transform bone = SourceTransform(n);
                if (bone == null)
                {
                    Logger.LogInfo("Grip analyze: " + n + " MISSING");
                    continue;
                }
                Vector3 p = track.InverseTransformPoint(bone.position);
                Logger.LogInfo("Grip analyze: " + n + "=" + p.ToString("F4"));
                if (n.EndsWith("_tip"))
                {
                    tips.Add(p);
                }
            }
            // Handle-axis candidates: the socket axis the fingertips wrap at
            // the most constant distance is the handle; its mean distance is
            // the handle radius.
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            string[] axisNames = { "X", "Y", "Z" };
            for (int a = 0; a < 3; a++)
            {
                float sum = 0f;
                float sum2 = 0f;
                int cnt = 0;
                for (int t = 0; t < tips.Count; t++)
                {
                    Vector3 radial = tips[t] - axes[a]
                        * Vector3.Dot(tips[t], axes[a]);
                    float d = radial.magnitude;
                    sum += d;
                    sum2 += d * d;
                    cnt++;
                }
                if (cnt > 0)
                {
                    float mean = sum / cnt;
                    float sd = Mathf.Sqrt(Mathf.Max(0f, sum2 / cnt - mean * mean));
                    Logger.LogInfo("Grip analyze: axis " + axisNames[a]
                        + " tipCount=" + cnt + " meanDist="
                        + mean.ToString("F4") + " sd=" + sd.ToString("F4"));
                }
            }
        }

        /// <summary>
        /// One-shot per hold-pose class, logged once the solved pose is fully
        /// applied: the model's hand bones and palm axes in the same vanilla
        /// socket frame used by LogGripAnalysis at capture, so the two blocks
        /// can be compared line by line; the difference is the grip error.
        /// </summary>
        private void LogModelGripError(string cls)
        {
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            Transform track = held != null && held.item != null
                ? held.item.transform : null;
            if (track == null)
            {
                return;
            }
            Transform modelHand = ModelTransform("hand_R");
            Transform modelKnuckle = ModelTransform("hand_R_midl_base");
            Transform modelThumb = ModelTransform("hand_R_thumb_base");
            if (modelHand != null && modelKnuckle != null && modelThumb != null)
            {
                Vector3 palmDir = modelKnuckle.position - modelHand.position;
                if (palmDir.sqrMagnitude > 1e-8f)
                {
                    palmDir.Normalize();
                    Vector3 thumbDir = modelThumb.position - modelHand.position;
                    Vector3 lateral = thumbDir
                        - palmDir * Vector3.Dot(thumbDir, palmDir);
                    if (lateral.sqrMagnitude > 1e-8f)
                    {
                        lateral.Normalize();
                        Vector3 up = Vector3.Cross(palmDir, lateral);
                        Logger.LogInfo("Grip model (" + cls + "): palmAxesInSocket fwd="
                            + track.InverseTransformDirection(palmDir).ToString("F3")
                            + " lat="
                            + track.InverseTransformDirection(lateral).ToString("F3")
                            + " up="
                            + track.InverseTransformDirection(up).ToString("F3"));
                    }
                }
            }
            for (int i = 0; i < GripProbeBones.Length; i++)
            {
                string n = GripProbeBones[i];
                Transform bone = ModelTransform(n);
                if (bone == null)
                {
                    continue;
                }
                Logger.LogInfo("Grip model (" + cls + "): " + n + "="
                    + track.InverseTransformPoint(bone.position).ToString("F4"));
            }
        }

        /// <summary>
        /// Snaps the model's fingertips onto the grip cylinder of the held
        /// tool (fit to the game's own grip at capture). Each finger chain is
        /// solved as two bones onto the closest point of the cylinder surface
        /// and blended with the retargeted curl by the same weight as the arm
        /// pose. Rotations only, never positions, so the sleeve and palm shape
        /// cannot change.
        /// </summary>
        private void FitGripFingers(HoldPoseData pose, float weight)
        {
            if (pose == null || !pose.HasCylinder || weight <= 0.0001f)
            {
                return;
            }
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            Transform track = held != null && held.item != null
                ? held.item.transform : null;
            if (track == null)
            {
                return;
            }
            Vector3 axis = track.rotation * pose.CylAxisLocal;
            float scale = (Mathf.Abs(track.lossyScale.x)
                + Mathf.Abs(track.lossyScale.y)
                + Mathf.Abs(track.lossyScale.z)) / 3f;
            Vector3 center = track.TransformPoint(pose.CylCenterLocal);
            float radius = pose.CylRadius * Mathf.Max(0.01f, scale);
            string[] prefixes = { "hand_R_midl", "hand_R_ring",
                "hand_R_pinky", "hand_R_index", "hand_R_thumb" };
            for (int pf = 0; pf < prefixes.Length; pf++)
            {
                // Gather the chain bones that exist for this finger, in
                // hierarchy order (the probe list is ordered root to tip).
                List<Transform> chain = new List<Transform>();
                for (int i = 0; i < GripProbeBones.Length; i++)
                {
                    string n = GripProbeBones[i];
                    if (!n.StartsWith(prefixes[pf]))
                    {
                        continue;
                    }
                    Transform bone = ModelTransform(n);
                    if (bone != null)
                    {
                        chain.Add(bone);
                    }
                }
                if (chain.Count < 3)
                {
                    continue;
                }
                Transform b0 = chain[0];
                Transform b1 = chain[chain.Count - 2];
                Transform tip = chain[chain.Count - 1];
                // Closest point on the cylinder surface to the current tip.
                Vector3 tipPos = tip.position;
                Vector3 along = axis * Vector3.Dot(tipPos - center, axis);
                Vector3 radial = tipPos - center - along;
                if (radial.sqrMagnitude < 1e-8f)
                {
                    radial = tipPos - b0.position;
                    radial -= axis * Vector3.Dot(radial, axis);
                    if (radial.sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }
                }
                Vector3 target = center + along + radial.normalized * radius;
                SolveFingerChain(b0, b1, tip, target, weight);
            }
        }

        /// <summary>
        /// Closed-form two-bone solve for one finger: rotates the base and the
        /// joint before the tip so the tip lands on the target, bending in the
        /// plane of the current curl. Mirrors the arm's SolveArm math.
        /// </summary>
        private static void SolveFingerChain(Transform b0, Transform b1,
            Transform tip, Vector3 target, float weight)
        {
            Vector3 s = b0.position;
            Vector3 e = b1.position;
            Vector3 w = tip.position;
            float l1 = Vector3.Distance(s, e);
            float l2 = Vector3.Distance(e, w);
            if (l1 < 1e-5f || l2 < 1e-5f)
            {
                return;
            }
            Vector3 at = target - s;
            float dist = at.magnitude;
            if (dist < 1e-5f)
            {
                return;
            }
            float dd = Mathf.Clamp(dist,
                Mathf.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-4f);
            Vector3 tDir = at / dist;
            Vector3 perp = e - s;
            perp -= Vector3.Dot(perp, tDir) * tDir;
            if (perp.sqrMagnitude < 1e-6f)
            {
                perp = Vector3.Cross(tDir, Vector3.up);
                if (perp.sqrMagnitude < 1e-6f)
                {
                    perp = Vector3.Cross(tDir, Vector3.right);
                }
            }
            perp.Normalize();
            float cosA = (l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd);
            float angA = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            Vector3 elbow = s
                + (tDir * Mathf.Cos(angA) + perp * Mathf.Sin(angA)) * l1;
            Quaternion r0 = Quaternion.FromToRotation(e - s, elbow - s)
                * b0.rotation;
            Vector3 dirNow = w - elbow;
            Vector3 aim = target - elbow;
            Quaternion r1 = dirNow.sqrMagnitude > 1e-8f
                && aim.sqrMagnitude > 1e-8f
                ? Quaternion.FromToRotation(dirNow, aim) * b1.rotation
                : b1.rotation;
            b0.rotation = Quaternion.Slerp(b0.rotation, r0, weight);
            b1.rotation = Quaternion.Slerp(b1.rotation, r1, weight);
        }

        private static Quaternion MeshDelta(SourceBone bone, Quaternion mesh0)
        {
            if (bone == null || bone.Bone == null || bone.SmrTransform == null)
            {
                return Quaternion.identity;
            }
            Quaternion now = Quaternion.Inverse(bone.SmrTransform.rotation)
                * bone.Bone.rotation;
            return now * Quaternion.Inverse(mesh0);
        }

        private Transform SourceTransform(string name)
        {
            SourceBone sb;
            if (_diagSources != null && _diagSources.Bones.TryGetValue(name, out sb))
            {
                return sb.Bone;
            }
            return null;
        }

        /// <summary>
        /// The direction the player is looking, independent of the view mode.
        /// Third/second person move the camera and, in front view, turn it around
        /// to face the character, so Camera.main.forward is not the look direction
        /// there; the game's own camera orientation is.
        /// </summary>
        private Vector3 ViewForward()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return Vector3.zero;
            }
            Transform t = cam.transform;
            if (_viewApplied != 0 && _camMoved && _camMovedT == t)
            {
                Transform parent = t.parent;
                Quaternion baseRot = parent != null
                    ? parent.rotation * _camBaseLocalRot
                    : _camBaseLocalRot;
                return baseRot * Vector3.forward;
            }
            return t.forward;
        }

        /// <summary>
        /// Starts (or restarts) the pose crossfade used by the retarget pass.
        /// The pose the model currently shows is captured on the first retarget
        /// frame after this call, so a switch that is noticed before the game
        /// has actually changed the animation still blends from the old pose.
        /// <paramref name="freeSide"/> (1 = left, 2 = right) names an arm that
        /// must skip the crossfade: taking out or holstering a one-handed item
        /// changes the grip arm, while the other arm keeps playing the game's
        /// own animation, and blending that arm towards a captured pose made it
        /// stall for the whole fade. <paramref name="gripSide"/> is the same for
        /// the arm that grips a held item (3 = both arms, for two-handed tools):
        /// it holds the vanilla tool, so it must stay on the game's live
        /// animation too, or the hand drifts off the tool for the whole fade.
        /// </summary>
        private void StartPoseTransition(int freeSide = 0, int gripSide = 0)
        {
            float length = _poseTime != null
                ? Mathf.Clamp(_poseTime.Value, 0.1f, 2f) : 0.6f;
            _poseTransition = length;
            _poseTransitionLength = length;
            _poseFromValid = false;
            _poseFreeSide = freeSide;
            _poseGripSide = gripSide;
        }

        /// <summary>
        /// The side of the hand currently gripping a one-handed item, or 0 when
        /// nothing is held (or the item uses the left hand). The gripping hand
        /// must skip the pose crossfade so it always plays the game's live
        /// animation and stays on the tool.
        /// </summary>
        private int GripSideNow()
        {
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            if (held == null)
            {
                return 0;
            }
            // 3 = two-handed grip (both arms on the tool), 2 = right hand only.
            return HeldItemUsesLeftHand() ? 3 : 2;
        }

        /// <summary>
        /// Bones that snap around as part of ordinary animation: finger and toe
        /// curls, the hand state that opens and closes with tools, facial bones
        /// and the leg bones that swing through each walk/swim cycle. They are
        /// never used as evidence of a pose switch, because a real switch always
        /// turns the pelvis and spine as well. Used only by the jump detector.
        /// </summary>
        private static bool IsDistalSourceBone(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            string n = name.ToLowerInvariant();
            return n.Contains("toe") || n.Contains("foot") || n.Contains("heel")
                || n.Contains("ankle") || n.Contains("calf") || n.Contains("thigh")
                || n.Contains("hand") || n.Contains("thumb") || n.Contains("index")
                || n.Contains("midl") || n.Contains("ring") || n.Contains("pinky")
                || n.Contains("point") || n.Contains("finger")
                || n.Contains("eye") || n.Contains("jaw") || n.Contains("tongue")
                || n.Contains("ear") || n.Contains("hair");
        }

        /// <summary>
        /// Which arm a source bone belongs to: 1 for the left chain (clavicle,
        /// upper arm, elbow, hand and everything under the hand), 2 for the
        /// right, 0 for the rest of the body. Used to keep the free arm out of
        /// the pose crossfade so it plays the game's animation unchanged.
        /// </summary>
        private static int SourceArmSide(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return 0;
            }
            if (name.StartsWith("clav_L", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("shoulder_L", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("elbow_L", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("hand_L", System.StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (name.StartsWith("clav_R", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("shoulder_R", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("elbow_R", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("hand_R", System.StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            return 0;
        }

        /// <summary>
        /// Turns the head toward the view. The vanilla skeleton never does this
        /// (nobody can see the body in first person), which reads as a stiff neck
        /// in the third/second-person views. The yaw is what lets the character
        /// look where the camera looks while the body faces its movement
        /// direction; the pitch follows looking up/down. Both are shared by the
        /// neck and the head and capped to a human range so the neck cannot break.
        /// </summary>
        private void ApplyHeadLook()
        {
            // Only in the third/second-person views. In first person the camera
            // sits at a fixed point inside the model's head: rotating the skull
            // around it swings the twintail roots into view, and the player cannot
            // see the head anyway. The view blend fades the effect in/out so the
            // transition does not snap. _headLookFade removes the follow while
            // the player is idle (so only the camera moves and nothing on the
            // model does) and during cinematics, where the animation's own head
            // pose is what should play.
            float blend = _viewApplied != 0 ? _viewBlend : 0f;
            // Smoothstep, not linear: the fade in/out is 0.3/0.8 s, and easing
            // both ends makes the head leave and re-enter the idle pose with no
            // visible step at either end of the transition.
            float fade = Mathf.Clamp01(_headLookFade);
            fade = fade * fade * (3f - 2f * fade);
            float amount = (_headLook != null ? _headLook.Value : 0f) * blend * fade;
            if (amount <= 0.001f || _headBone == null)
            {
                return;
            }
            Vector3 forward = ViewForward();
            if (forward.sqrMagnitude < 1e-6f)
            {
                return;
            }
            float pitchUp = Mathf.Asin(Mathf.Clamp(forward.normalized.y, -1f, 1f))
                * Mathf.Rad2Deg;

            // The seaglide's own aim pose already points the head at the camera,
            // so adding the camera pitch on top made the head fall back far too
            // much. Other quick-slot items do not supply that pitch, so they
            // keep following (the user's stated split: pitch off only for the
            // seaglide; everything else follows like the empty hand).
            float pitchAmount = HoldingSeaglide() ? 0f : amount;
            // Unity pitches down for a positive rotation about the local right
            // axis, so looking up needs the negative angle.
            float angle = Mathf.Clamp(-pitchUp * pitchAmount, -55f, 55f);

            // Yaw: the body keeps its own heading in the shoulder view, so the
            // head has to turn the rest of the way towards the view direction.
            // With the heading following the camera (holding a tool, first
            // person, second person) the two already agree and this is ~0.
            float yawAngle = 0f;
            if (_container != null)
            {
                Vector3 viewDir = forward;
                viewDir.y = 0f;
                Vector3 bodyDir = _container.forward;
                bodyDir.y = 0f;
                if (viewDir.sqrMagnitude > 1e-6f && bodyDir.sqrMagnitude > 1e-6f)
                {
                    float bodyYaw = Mathf.Atan2(bodyDir.x, bodyDir.z) * Mathf.Rad2Deg;
                    float viewYaw = Mathf.Atan2(viewDir.x, viewDir.z) * Mathf.Rad2Deg;
                    yawAngle = Mathf.Clamp(
                        Mathf.DeltaAngle(bodyYaw, viewYaw) * amount, -60f, 60f);
                }
            }

            // The clamp is what a neck can physically do, but its input is the
            // *signed* body-to-view angle. Crossing the body's 180 degree line
            // flips that sign, so the clamped value jumps from +60 to -60 and the
            // head teleported. Ease the applied angles towards the targets instead
            // of writing them: the head then swings around through the front over
            // a beat, which is also what the real neck has to do. Updated once per
            // frame (both late passes would otherwise double the rate).
            if (!_latePass)
            {
                float lookDt = Time.deltaTime;
                if (lookDt > 0f)
                {
                    float follow = 1f - Mathf.Exp(-lookDt / 0.10f);
                    _headYawShown = Mathf.LerpAngle(_headYawShown, yawAngle, follow);
                    _headPitchShown = Mathf.Lerp(_headPitchShown, angle, follow);
                }
            }
            yawAngle = _headYawShown;
            angle = _headPitchShown;

            float neckShare = _neckBone != null ? 0.35f : 0f;
            Vector3 upAxis = _headBone.up;
            Vector3 rightAxis = _headBone.right;
            if (neckShare > 0f)
            {
                Quaternion neckTurn = Quaternion.AngleAxis(yawAngle * neckShare, upAxis)
                    * Quaternion.AngleAxis(angle * neckShare, rightAxis);
                _neckBone.rotation = neckTurn * _neckBone.rotation;
            }
            // Setting the neck's world rotation already carried the head with it;
            // the head only adds the remaining share.
            Quaternion headTurn = Quaternion.AngleAxis(yawAngle * (1f - neckShare), upAxis)
                * Quaternion.AngleAxis(angle * (1f - neckShare), rightAxis);
            _headBone.rotation = headTurn * _headBone.rotation;
        }

        // ---- retargeting ----
        private BonePair[] _pairs;
        private Quaternion[] _deltas;
        // Pose crossfade. Equipping or holstering a tool switches the game's
        // animation state in a single frame, so copying the new pose straight
        // onto the model snapped the whole body (most obvious with the seaglide).
        // For [View] PoseTransition seconds the retarget blends from the pose the
        // model showed when the switch happened to the new (live) pose, with an
        // ease-in-out curve, so the body settles into the new pose instead of
        // whipping over in the last frames.
        // A state switch (equip, holster, cinematic cut, teleport) turns the
        // whole body in a single frame. A fast animation frame turns one limb.
        // So a switch is only believed when PoseJumpVotes of the body's
        // proximal bones jump by more than PoseJumpAngle at once. The old test
        // fired on any single bone above 35 degrees, which toes, fingers,
        // shoulders and elbows routinely exceed during swim strokes; each hit
        // restarted the crossfade from the pose of the previous trigger, so the
        // model kept being dragged back towards a stale pose and the animation
        // read as slow motion.
        private const float PoseJumpAngle = 60f;
        private const int PoseJumpVotes = 2;
        private float _poseJumpLock;         // detector lockout after a trigger
        private int _poseFreeSide;           // 1 = left / 2 = right arm excluded from the crossfade
        private int _poseGripSide;           // gripping hand(s) excluded from the crossfade (3 = both, two-handed tools)
        private float _poseTransition;       // seconds left in the current crossfade
        private float _poseTransitionLength; // total seconds of the current crossfade
        private Quaternion[] _poseFromRot;   // pose shown when the crossfade started
        private bool _poseFromValid;
        private Quaternion[] _lastBaseRot;   // last frame's retargeted pose, before look/IK
        private bool _lastBaseValid;
        private Quaternion[] _prevPoseRot;
        private bool _prevSourceValid;
        private InventoryItem _poseHeldItem;
        private bool _poseCinematicPrev;
        private ConfigEntry<float> _poseTime;
        private Vector3 _baseLocalPos;
        private SourceBone _hipsSource;
        private Quaternion _hipsRestLocalRot;

        private sealed class SourceBone
        {
            public Transform Bone;
            public Transform SmrTransform;
            public Quaternion RestLocalRot;
            public Vector3 RestLocalPos;
        }

        private struct BonePair
        {
            public Transform Source;
            public Transform SmrTransform;
            public Quaternion SourceRestLocalInverse;
            public Transform Target;
            // Model bone rest rotation expressed in the container's local space,
            // so the container's own rotation (which follows player_view) is not
            // double-counted when we write absolute world rotations every frame.
            public Quaternion TargetRestRot;
            public int Depth;
        }

        /// <summary>
        /// The model's own bind frame for one arm, in the container's local space.
        /// The IK re-aims the upper arm and forearm at the game's hand, and doing
        /// that from the retargeted rotations carried the source skeleton's roll
        /// into joints whose bind frames face in a different direction - the elbow
        /// then looked wrung around its own axis (obvious on the near-straight
        /// seaglide pose). Rebuilding each bone from its rest frame plus the swing
        /// that its parent received keeps the model's own roll and only bends the
        /// joint where the IK needs it.
        /// </summary>
        private sealed class ArmRest
        {
            // The clavicle (肩) and its child (肩C) carry the game's shoulder
            // lift. Their retargeted roll is what twisted the skin at the arm
            // root, so the IK rebuilds them from rest as well, keeping only
            // their aim direction.
            public bool HasClav;
            public Quaternion ClavParentLocal;
            public Quaternion ClavLocal;
            public Vector3 ClavDir;
            // 肩C's own rest direction to the upper arm (container-rest space).
            // Its swing carries the game's aim lift, so the arm-root rebuild
            // maps this direction onto the live upper-arm direction instead of
            // flattening the aim bone to the clavicle's swing.
            public bool HasClavC;
            public Vector3 ClavCDir;
            public Quaternion ParentLocal;
            public Quaternion UpperLocal;
            public Quaternion MidLocal;
            public Vector3 UpperDir;
            public Vector3 MidDir;
            // Grip calibration. The retargeted wrist roll lives in the suit's
            // bind frame, which is not the model's, so the palm ended up facing
            // the wrong way round the held tool. Each hand's palm frame (fingers
            // forward, thumb side) is built from its own thumb and middle-finger
            // bones; PalmFix is the constant that carries the game hand's
            // rotation onto the model hand's so both palms line up.
            public bool HasPalm;
            public Quaternion PalmFix;
            // Wrist/elbow calibration. These are the model's local joint
            // rotations in its bind pose. The chain is rebuilt from the hand
            // (which is pinned to the game hand) through these constants, so
            // both joints keep their natural angle instead of absorbing the
            // bind-frame roll difference. GameWristLocal / GameElbowLocal are
            // the same quantities on the suit at build, for the IK diagnostics.
            public bool HasWrist;
            public Quaternion WristLocal;
            public Quaternion ElbowLocal;
            public bool HasGameWrist;
            public Quaternion GameWristLocal;
            // The same wrist angle on the suit at *bind*, i.e. the game's rest
            // wrist. The retarget copies the handheld arm as a world rotation
            // delta, which also copies the suit's bind frame; subtracting this
            // constant leaves just the suit's wrist articulation, which is what
            // can safely be applied to the model's wrist.
            public Quaternion GameWristRestLocal;
            public bool HasGameElbow;
            public Quaternion GameElbowLocal;
            // MMD twist group for the upper arm: 腕捩 (arm_twist_*) sits between
            // the upper arm and the elbow, and 腕捩1/2/3 are separate children
            // that copy 25/50/75% of its rotation (PMX inherit rotation). The
            // IK drives the main bone to take the elbow's local roll, and the
            // helpers spread it over the upper arm so the skin twists in a
            // gradient instead of bunching at the elbow joint.
            public Transform ArmTwist;
            public Transform[] ArmTwistHelpers;
            public float[] ArmTwistRatios;
            // Grip-anchored IK frame, in the hand bone's own local space. The
            // old solve pinned the wrists together, which forced the model's
            // shorter palm and different bind wrist to make up the difference -
            // the wrist had to bend (and roll) far more than a hand holding a
            // handle ever does. A held item is constrained at the handle: the
            // palm point, the knuckle line (index-to-pinky) and the palm normal
            // are what have to line up. Storing those in hand-local space lets
            // the solver rotate the hand about the grip and put the wrist where
            // that rotation requires, instead of the other way round.
            public bool HasGripFrame;
            public Vector3 GripPointLocal;   // middle of the palm / handle point
            public Vector3 GripAxisLocal;    // knuckle line, point -> pinky
            public Vector3 GripNormalLocal;  // palm normal (out of the palm)
            // Wrist -> elbow direction in hand-local space. Turning it by the
            // solved hand rotation gives the elbow position that leaves the
            // model's wrist at its bind angle, i.e. the natural elbow pole.
            public Vector3 ForearmDirLocal;
            public float PalmLen;
            public float L1;
            public float L2;
            public float ClavLen;
        }

        private readonly ArmRest[] _armRest = new ArmRest[2];

        // ---- static hold pose (capture-based) ----
        // The held-item grip for one-handed tools is captured from the game's own
        // hand pose once the item has settled after being drawn, solved once onto
        // the model's arm proportions, and replayed every frame with the vanilla
        // arm's mesh-space motion deltas overlaid. Nothing is solved per frame and
        // no bone position is ever written, so the sleeves keep their authored
        // shape instead of being stretched by reach corrections.
        private readonly Dictionary<string, HoldPoseData> _holdPoses =
            new Dictionary<string, HoldPoseData>();
        private bool _holdCapturePending;
        private float _holdPendingNextLog;
        // One grip-error block per hold-pose class; see LogModelGripError.
        private readonly Dictionary<string, bool> _holdGripErrLogged =
            new Dictionary<string, bool>();
        // The pose of the item that was held most recently, kept while the IK
        // weight fades out after a holster so the release blends smoothly into
        // the game's holster animation instead of snapping to the retarget.
        private HoldPoseData _holdPoseLast;
        // Whether the held item is carried into the model hand this frame.
        // Set by the FrameLate gate: only the legacy MoveHeldItemToModelHand
        // mode glues the tool. The hold-pose mode never rewrites the tool;
        // TryCaptureHoldPose solves the hand onto its vanilla grip instead.
        private bool _itemGlueOn;

        /// <summary>
        /// One captured hold pose: the vanilla hand frame at capture time and the
        /// model chain rotations that put the model hand there, all in the model
        /// container's local space so the pose rides with the container.
        /// </summary>
        private sealed class HoldPoseData
        {
            public bool Valid;
            public string Class;            // "knife" | "tool"
            // Model chain rotations in container-local space, solved at capture.
            public Quaternion LocalUpper;   // shoulder
            public Quaternion LocalMid;     // elbow
            public Quaternion LocalHand;    // hand
            // Vanilla arm rotations at capture, in the game rig's mesh space.
            // The per-frame delta from these is overlaid on the model pose, so
            // swings, aim and sway all come from the vanilla animation.
            public Quaternion SrcUpper0;
            public Quaternion SrcMid0;
            public Quaternion SrcHand0;
            // The MMD arm twist bone is written by the retarget; the pose holds
            // it at its authored rest so the upper-arm skin does not shear.
            public Quaternion TwistRestLocal;
            // Grip cylinder fit to the game's own curled fingers at capture,
            // in the tool's local space; FitGripFingers snaps the model's
            // fingertips onto its surface.
            public bool HasCylinder;
            public Vector3 CylAxisLocal;
            public Vector3 CylCenterLocal;
            public float CylRadius;
        }

        private void Awake()
        {
            // The PDA sliders write their ConfigEntry on every drag frame. Letting
            // BepInEx flush the whole file each time would hitch the page, so writes
            // stay in memory and the page saves explicitly when the PDA closes (and
            // when a model is picked).
            Config.SaveOnConfigSet = false;

            // The first-run notice needs its two entries before anything else reads
            // MasterEnabled, which stays false until the notice is accepted.
            ConsentGate.Bind(Config);

            _enabled = Config.Bind("General", "Enabled", true,
                "MMS master switch. When off the game's original character is shown " +
                "again and the model, its clothes, physics and PDA preview are " +
                "disabled. Toggle it in game from the MMS console (F2) without " +
                "restarting or editing this file.");
            _scale = Config.Bind("Model", "Scale", 1.095f,
                "Uniform scale of the Miku model relative to the player skeleton.");
            _activeModel = Config.Bind("Model", "ActiveModel", "",
                "Model to wear, as listed in the PDA page. A folder name selects " +
                "that folder's default PMX (model.pmx if present, otherwise the " +
                "first file). When a folder holds several PMX files, the others " +
                "are written as \"folder/file\" (for example \"sour暄/White\"). " +
                "Empty uses the first model found. Press the menu key in game " +
                "(default V) to pick from the list instead.");
            _menuKey = Config.Bind("Interface", "MenuKey", "V",
                "Key that opens the PDA directly on the model page. The default is V; " +
                "F6 is the game's own HUD hide-phase key and F9/F12 are often taken by " +
                "screenshot tools, so pick something free. Can be rebound in game from " +
                "the MMS console (F2).");
            _consoleKey = Config.Bind("Interface", "ConsoleKey", "F2",
                "Key that opens the MMS console overlay (master switch, key bindings " +
                "and the sound toggle).");
            // Old builds had one key (PickerKey, default F2) that opened the model
            // page. The page has moved to V and F2 now opens the console. Carry a
            // custom old binding over to MenuKey, but turn the old default F2 into
            // V so the default layout follows this version.
            ConfigEntry<string> legacyPicker;
            if (Config.TryGetEntry<string>("Interface", "PickerKey", out legacyPicker))
            {
                bool menuKeyOnDisk = ConfigFileContains("MenuKey");
                if (!menuKeyOnDisk)
                {
                    string legacy = legacyPicker.Value != null
                        ? legacyPicker.Value.Trim() : "";
                    _menuKey.Value = legacy.Length == 0 ||
                        string.Equals(legacy, "F2", StringComparison.OrdinalIgnoreCase)
                        ? "V" : legacy;
                }
                Config.Remove(legacyPicker.Definition);
                _configDirty = true;
            }
            _menuKeyCode = ParseKey(_menuKey.Value, KeyCode.V);
            _consoleKeyCode = ParseKey(_consoleKey.Value, KeyCode.F2);
            _yaw = Config.Bind("Model", "Yaw", 0f,
                "Extra yaw (degrees) applied to the model.");
            _offsetX = Config.Bind("Model", "OffsetX", 0f, "Extra local X offset.");
            _offsetY = Config.Bind("Model", "OffsetY", 0f, "Extra local Y offset.");
            _offsetZ = Config.Bind("Model", "OffsetZ", 0f, "Extra local Z offset.");
            _holdMode = Config.Bind("Model", "HoldMode", "VanillaAdaptive",
                "VanillaAdaptive (default): independent palm-constrained IK, vanilla item trajectory. " +
                "Model: during the draw " +
                "the item keeps the game-authored pose; after it settles the item is " +
                "pulled towards the character by ItemPull and the arms are solved onto " +
                "the pulled grips (position only, hand rotation untouched). This closes " +
                "the short-arm gap without rotating the hand/fingers. Game: the old grip " +
                "IK (more contact, can twist the wrist/deform). Off: pure retarget.");
            AdaptiveSelfTest();
            Logger.LogInfo("Hold mode: " + _holdMode.Value
                + " (Model = item pull + position IK, Game = old hand IK, "
                + "Off = plain retarget)");
            _itemPull = Config.Bind("Model", "ItemPull", 0.08f,
                "Metres the held item is pulled towards the character once the draw has " +
                "settled (Model mode). Moving the grip closer means the short model arm " +
                "needs only a small IK correction. 0 disables the pull. Default 0.08.");
            _holdArmIk = Config.Bind("Model", "HoldArmIk", true,
                "Solve the model's arm POSITION onto the pulled grip in Model mode. Only " +
                "upper/forearm position is solved; the hand rotation, fingers and " +
                "clavicle are untouched, so this cannot stretch fingers. Turn off to keep " +
                "pure retarget (the item pull alone then leaves a gap).");
            _alignHands = Config.Bind("Model", "AlignHandsToGameHands", true,
                "Two-bone arm IK for the PDA (the only thing it still drives; quick-slot " +
                "items follow HoldMode). The PDA is not a quick-slot item and has no " +
                "palms to carry it, so its two hands are solved onto the game's hand " +
                "bones. Turn off to leave the arms purely retargeted while the PDA is open.");
            _itemToModelHand = Config.Bind("Model", "MoveHeldItemToModelHand", false,
                "Off by default: the held tool keeps the game's original animation and sits " +
                "in the authoring socket, and the arm IK bends the model's arm onto it. Turn " +
                "on to invert the adaptation: the model keeps its retargeted arm pose and the " +
                "tool is carried into the model's right palm every frame, so its final world " +
                "pose is rebuilt from the model hand instead of the game's. The PDA always " +
                "uses the hand IK because it is not a quick-slot item.");
            _holdPoseMode = Config.Bind("Model", "HoldPoseMode", true,
                "Master switch for held-tool arm handling. On (default): the grip " +
                "solver selected by HoldIkMode handles the gripping arm(s), and the " +
                "tool itself is never moved. Off: fall back to the legacy " +
                "wrist-to-wrist arm IK. HoldIkMode = Off with this On gives the " +
                "previous direct-vanilla behaviour (pure retarget, no solve).");
            _holdIkMode = Config.Bind("Model", "HoldIkMode", "Grip",
                "Grip (default): the hand is rotated by the constant bind-palm offset " +
                "needed to match the game's live grip frame, and the wrist is moved so " +
                "the model's palm point meets the item's grip point. The arm keeps its " +
                "authored rolls (minimal-arc solves, no twist redistribution), so the " +
                "correction can no longer wind the sleeve up. Position: leave the hand " +
                "rotation exactly as animated and solve only the wrist position (zero " +
                "hand correction, but the palm can stay rotated off the handle). Off: " +
                "plain retargeted vanilla arm animation, no solve. The tool is never " +
                "moved in any mode.");
            _holdIkLog = Config.Bind("Model", "HoldIkLog", false,
                "One line per second while the grip IK is active: target source, " +
                "distance versus reach, shoulder assist, elbow/wrist angles, and how " +
                "far the model's palm is from the item's grip. Turn off once tuned.");
            _holdIkMaxWrist = Config.Bind("Model", "HoldIkMaxWrist", 45f,
                "Maximum total wrist bend (degrees) the grip solver will use, " +
                "measured from the model's bind wrist. The palm-point target is " +
                "rebuilt after clamping, so a smaller value keeps a more natural " +
                "wrist at the cost of the palm angle on the handle.");
            _holdIkMaxPose = Config.Bind("Model", "HoldIkMaxPose", 75f,
                "Safety cap (degrees) on how far the grip IK may rotate the hand away " +
                "from the retargeted hand pose. If the game/model grip frames disagree " +
                "by more than this, the hand is blended back towards the animation " +
                "instead of snapping the palm around the handle.");
            _holdAim = Config.Bind("Model", "HoldAim", false,
                "Off by default. During a vanilla hold the body skeleton the model copies " +
                "does not pitch with the view while the first-person arms rig (and the tool " +
                "in it) does, so the model's hand sits a few centimetres from the tool. This " +
                "aims the gripping arm at that hand to close the gap. It costs the wrist: " +
                "putting the hand exactly on the tool needs 60-100 deg of wrist bend the " +
                "model's proportions cannot give, which reads as a twisted wrist and a " +
                "palm-down hand. Leave it off to keep the arm, wrist and shoulder exactly " +
                "as the vanilla animation has them.");
            _hideHead = Config.Bind("Visibility", "HideHeadInFirstPerson", true,
                "Hide the model's head mesh when the camera is inside it.");
            _headHideDistance = Config.Bind("Visibility", "HeadHideDistance", 0.75f,
                "Camera distance below which the head is hidden.");
            _hideGameBody = Config.Bind("Visibility", "HideGamePlayerBody", true,
                "Disable the game's own player suit renderers while the model is active.");
            _hideHeadsetMic = Config.Bind("Visibility", "HideHeadsetMic", true,
                "Hide the headset microphone. It is part of the body mesh rather than the " +
                "head mesh, so hiding the head alone leaves it floating in front of the " +
                "first-person camera as two small blocks.");
            _hideTopFrontTails = Config.Bind("Visibility", "HideTopFrontTails", false,
                "Optional: hide the two long front flaps of the white top. Off by default, " +
                "because it leaves the belly uncovered.");
            _hideSleeveLining = Config.Bind("Visibility", "HideSleeveLining", false,
                "Optional: hide the inner lining of the wide bell sleeves. Off by default.");
            _showHair = Config.Bind("Visibility", "ShowTwintailsInFirstPerson", true,
                "Keep the long twintails visible in first person. The head itself is still " +
                "hidden so it does not block the view, but the tails hang behind the " +
                "shoulders and show the hair physics. Turn this off to hide them with the head.");
            _hiddenMaterials = Config.Bind("Visibility", "HiddenMaterials", "",
                "Materials hidden per model, managed by the model page in the PDA " +
                "(tick a material to hide that part). Stored as model|name,name;model|name.");
            _hairPhysics = Config.Bind("Physics", "HairPhysics", true,
                "Give the hair secondary motion. The built-in twintails and an " +
                "imported model's long hair bone chains follow the body with a little " +
                "lag and sway when the player turns, swims or moves. Fringe and " +
                "sideburns only drift slightly; a hair cap glued to the scalp stays " +
                "rigid. Neckties and other accessories have their own switch " +
                "(AccessoryPhysics), capes and coats ClothesPhysics.");
            _hairCollision = Config.Bind("Physics", "HairCollision", true,
                "Keep simulated hair, neckties and hanging ornaments out of the head " +
                "and body. The body volume is measured from the model's own mesh (a " +
                "stack of rings fitted along every body bone), and the collision acts " +
                "on the visible cloth surface rather than the bone line inside it, so " +
                "strands rest on the skin instead of cutting through it. Turn this " +
                "off to get the old free-swinging look.");
            _limbCollision = Config.Bind("Physics", "LimbCollision", true,
                "Also keep simulated hair and cloth out of the arms and legs. " +
                "Colliders are fitted along the clavicle, upper arm, forearm, hand, " +
                "thigh, calf, ankle and foot bones from the model's own mesh, so a " +
                "turning twintail no longer passes through a raised arm and the " +
                "skirt and coat hems ride over the knees and feet. This switch is " +
                "independent of HairCollision: with it off the head and torso " +
                "colliders stay, but the arms and legs are ignored.");
            _skirtPhysics = Config.Bind("Physics", "SkirtPhysics", true,
                "Give the skirt secondary motion: the hem swings and settles when the " +
                "player turns, runs or swims instead of being welded to the hips. " +
                "The model's own skirt bone chains (スカート / skirt / 裙 ...) are used " +
                "for this.");
            _clothesPhysics = Config.Bind("Physics", "ClothesPhysics", true,
                "Give garments other than the skirt secondary motion: capes, coats, " +
                "mantles and robes (ケープ / cape / マント / コート / robe ...). These " +
                "chains hang from the shoulders, so they are simulated as cloth with " +
                "their own weight and swing. Turn off to keep them in the authored pose.");
            _accessoryPhysics = Config.Bind("Physics", "AccessoryPhysics", true,
                "Give hanging accessories secondary motion: neckties, ribbons, " +
                "scarves, stoles, necklaces and straps (ネクタイ / リボン / マフラー / " +
                "ストラップ ...). They swing like hair, but they are not hidden with the " +
                "head in first person. Turn off to keep them in the authored pose.");
            _surfacePhysics = Config.Bind("Physics", "SurfacePhysics", true,
                "Separate the physics above and below the water surface. Cloth that " +
                "rises out of the water behaves like dry fabric: gravity pulls it down " +
                "and the air barely damps it, while the submerged part keeps floating " +
                "with the heavy water drag. Inside a base the whole model is treated as " +
                "dry. Turn off to simulate everything as underwater (the old behaviour).");
            _airGravity = Config.Bind("Physics", "AirGravity", 6.0f,
                new ConfigDescription(
                    "Downward acceleration on cloth that is above the water line, in " +
                    "m/s^2 (only used when SurfacePhysics is on). Dry hair falls with " +
                    "real gravity, so this is much larger than HairGravity. Raise it " +
                    "for a heavier hang, lower it for hair that floats up in the air.",
                    new AcceptableValueRange<float>(0f, 20f)));
            _airDamping = Config.Bind("Physics", "AirDamping", 3.0f,
                new ConfigDescription(
                    "Air resistance on cloth that is above the water line, in 1/s " +
                    "(only used when SurfacePhysics is on). Air damps far less than " +
                    "water, so wet-looking motion stops above the surface and the cloth " +
                    "swings freely instead. 2.5-4 stops a walking swing within about a " +
                    "second and lets the hair settle into its natural hang; lower values " +
                    "keep it moving for several seconds.",
                    new AcceptableValueRange<float>(0f, 20f)));
            _airSettleMigrated = Config.Bind("Physics", "AirSettleMigrated", false,
                "Internal: one-time flag, raised the AirDamping default from 1.2 to 3.0 " +
                "so dry hair settles instead of swinging for seconds. Leave it alone.");
            _glowParts = Config.Bind("Rendering", "GlowPartsByModel", "",
                "Per-model glow material names, written by the PDA model page (same " +
                "format as HiddenMaterials). Each material row in the page has a 发光 " +
                "toggle; the names selected there are stored here.");
            _skirtClearance = Config.Bind("Physics", "SkirtClearance", 0.02f,
                new ConfigDescription(
                    "Minimum gap kept between the legs and the skirt, in metres. The " +
                    "cloth thickness around its own bone chain is measured from the " +
                    "mesh; this is the floor applied on top of it, so the panel cannot " +
                    "sink into the thighs no matter how thin it is. Raise it if the " +
                    "legs still poke through, lower it if the skirt looks puffed out " +
                    "around the hips.",
                    new AcceptableValueRange<float>(0f, 0.08f)));
            _skirtStiffness = Config.Bind("Physics", "SkirtStiffness", 1.0f,
                new ConfigDescription(
                    "Multiplier on the skirt and coat bending stiffness. 1.0 keeps the " +
                    "current cloth behaviour (HairStiffness scaled up for fabric); lower " +
                    "values make the hem softer and more flowing, higher values keep it " +
                    "closer to the authored shape and stop it flaring in the current. " +
                    "Can be dragged live in the PDA model page.",
                    new AcceptableValueRange<float>(0.1f, 3f)));
            _skirtLeash = Config.Bind("Physics", "SkirtLeash", 1.0f,
                new ConfigDescription(
                    "Multiplier on the invisible leash that limits how far the skirt " +
                    "hem may travel from the shape its own bones produce. 1.0 keeps " +
                    "the current behaviour; 0 pins the panel to the authored pose so " +
                    "it can no longer fold up over the waist (it still turns with the " +
                    "hips); 2-3 lets the hem lift and fold much further on turns and " +
                    "in the current. Combines with the automatic hardness " +
                    "compensation, so pair a low value with high SkirtStiffness for a " +
                    "stiff skirt that never folds. Can be dragged live in the PDA " +
                    "model page.",
                    new AcceptableValueRange<float>(0f, 3f)));
            _hairStiffness = Config.Bind("Physics", "HairStiffness", 5.0f,
                new ConfigDescription(
                    "Bending stiffness of the hair (1/s): how strongly each joint resists " +
                    "bending away from the model's authored curve. Low values let the tail " +
                    "hang and stream freely, high values snap it back to the modelled shape " +
                    "like a rubber band. Around 3-6 keeps the shape while the tips still " +
                    "straighten in the current.",
                    new AcceptableValueRange<float>(0f, 50f)));
            _hairGravity = Config.Bind("Physics", "HairGravity", 0.8f,
                new ConfigDescription(
                    "Downward acceleration on the hair in m/s^2. Wet hair is close to " +
                    "neutrally buoyant, so the default is almost weightless; raise it for " +
                    "a heavier, sinking look, set 0 for fully floating.",
                    new AcceptableValueRange<float>(0f, 20f)));
            _hairDamping = Config.Bind("Physics", "HairDamping", 10.0f,
                new ConfigDescription(
                    "Water resistance (1/s). Water is far thicker than air: higher values " +
                    "stop the hair almost as soon as the player stops and make it follow " +
                    "the body heavily, lower values let it swing and wobble afterwards. " +
                    "10-14 looks like wet hair; below 6 it starts to feel springy.",
                    new AcceptableValueRange<float>(0f, 20f)));
            _hairFlow = Config.Bind("Physics", "HairFlow", 0.03f,
                new ConfigDescription(
                    "Strength of the slow ambient current that ripples along the hair while " +
                    "the player is still, so it keeps floating instead of freezing. " +
                    "0 stops all idle motion.",
                    new AcceptableValueRange<float>(0f, 1f)));
            _hairTwistFollow = Config.Bind("Physics", "HairTwistFollow", 2.0f,
                new ConfigDescription(
                    "How quickly the tail's authored curl follows the body when you turn " +
                    "(1/s). The bones are posed against their bind pose and the roll is " +
                    "carried along the chain, so this no longer affects rolling: it only " +
                    "sets how fast the rest shape swings round with a turn. It is also " +
                    "the base rate at which a hanging accessory (necktie, strap) twists " +
                    "after the chest, fastest at the knot and half that at the tip. " +
                    "Lower is looser and heavier, higher follows the body more exactly.",
                    new AcceptableValueRange<float>(0.3f, 20f)));
            _fringeSway = Config.Bind("Physics", "FringeSway", 0.35f,
                new ConfigDescription(
                    "Sway amplitude of the fringe and sideburns (前髪 / もみあげ / 側髪). " +
                    "Each light strand leans rigidly about its root on a short leash, which " +
                    "scales both the leash length and the idle current on it: the authored " +
                    "shape, the gaps between the strands and each card's roll never change. " +
                    "0 keeps the authored pose exactly, 1 is the old full drift, above 1 " +
                    "exaggerates it. Can be " +
                    "dragged live in the in-game list (F2).",
                    new AcceptableValueRange<float>(0f, 2f)));
            _tightenCuffs = Config.Bind("Visibility", "TightenSleeveCuffs", false,
                "Optional: pull the mouth of each bell sleeve in to the wrist so it stops " +
                "hiding the hand in first person. Off by default, which keeps the sleeve " +
                "exactly as the model was authored; turn it on if the wide cuff is in the way.");
            _useGameShader = Config.Bind("Rendering", "UseGameSuitShader", true,
                "Build the model materials from the game's own suit material (MarmosetUBER). " +
                "This is the recommended path: the game shader follows Subnautica's ambient " +
                "light, so surfaces out of direct sunlight do not turn black.");
            _emissionFill = Config.Bind("Rendering", "EmissionFill", 0.12f,
                new ConfigDescription(
                    "Faint glow over the whole model: feeds this fraction of the albedo " +
                    "texture through the shader's glow channel on every material, so the " +
                    "body, clothes and hair all keep a soft self-lit look in dim water " +
                    "instead of going black. 0.10-0.15 is a faint glow; 0.3 and above washes " +
                    "the colours out and makes the model look flat.",
                    new AcceptableValueRange<float>(0f, 1f)));
            _glowMaterials = Config.Bind("Rendering", "GlowMaterials", "",
                "Legacy list from versions that bundled a model of their own; it no " +
                "longer applies to anything. Glow is now chosen per material in the PDA " +
                "model page (right-hand checkbox) and saved per model in GlowPartsByModel.");
            _highlightGlow = Config.Bind("Rendering", "HighlightGlow", 1.0f,
                new ConfigDescription(
                    "How strongly the GlowMaterials emissive parts light up. 0 turns the " +
                    "effect off; 1 makes the accents read as full-bright, above 1 they " +
                    "start to bloom.",
                    new AcceptableValueRange<float>(0f, 3f)));
            _mutePlayerSounds = Config.Bind("Audio", "MutePlayerSounds", false,
                "Silence the player character's own voice: the pain grunts when " +
                "taking damage (with and without the oxygen tank, the light pain " +
                "sound, the smoke cough and the maskless gasp), the exertion grunt " +
                "when jumping, and the underwater breathing loop. Footsteps, " +
                "splashes, bubbles, the PDA voice and all damage, health and red " +
                "flash effects are unaffected. Can be toggled live in the PDA model " +
                "page (音效 group).");
            MutePlayerSoundsActive = _mutePlayerSounds.Value;
            // Carry the old pain-only setting over and drop the stale entry. Skip the
            // copy when the new key already exists in the file (the player has since
            // toggled it), so a leftover old line can never override the new choice.
            ConfigEntry<bool> legacyMute;
            if (Config.TryGetEntry<bool>("Audio", "MuteDamageSounds", out legacyMute))
            {
                bool newKeyOnDisk = false;
                try
                {
                    newKeyOnDisk = File.Exists(Config.ConfigFilePath)
                        && File.ReadAllText(Config.ConfigFilePath)
                            .IndexOf("MutePlayerSounds", StringComparison.Ordinal) >= 0;
                }
                catch (Exception)
                {
                }
                if (!newKeyOnDisk)
                {
                    _mutePlayerSounds.Value = legacyMute.Value;
                    MutePlayerSoundsActive = legacyMute.Value;
                }
                Config.Remove(legacyMute.Definition);
                _configDirty = true;
            }
            _decalOffset = Config.Bind("Rendering", "DecalOffset", 0f,
                new ConfigDescription(
                    "Extra push (metres) of decal materials (No01) along their normals. The " +
                    "decal is skinned with the weights of the arm around it, so 0 keeps it " +
                    "exactly where the source model put it; raise only if it z-fights.",
                    new AcceptableValueRange<float>(0f, 0.02f)));
            _verbose = Config.Bind("Debug", "VerboseLogging", false,
                "Log bone mapping and material details.");
            _glowTest = Config.Bind("Debug", "GlowTest", false,
                "Tint every model material with red emission to verify the mesh is visible.");
            _showcase = Config.Bind("Debug", "ShowcaseDistance", 0f,
                "Debug: push the model this many metres in front of the camera so it can be inspected.");
            _thirdPersonDistance = Config.Bind("View", "ThirdPersonDistance", 3.0f,
                new ConfigDescription(
                    "How far from the player the camera sits in third person (the shoulder " +
                    "view) and second person (in front). Press F5 in game to cycle first " +
                    "person -> shoulder -> second person. In the shoulder view, Numpad + / - " +
                    "change it live.",
                    new AcceptableValueRange<float>(1f, 8f)));
            _thirdPersonHeight = Config.Bind("View", "ThirdPersonHeight", 0.45f,
                new ConfigDescription(
                    "How far above the player's head the third- and second-person camera " +
                    "sits (metres). In the shoulder view, Numpad 8 / 2 change it live.",
                    new AcceptableValueRange<float>(-1f, 2f)));
            _thirdPersonSide = Config.Bind("View", "ThirdPersonSide", 0.45f,
                new ConfigDescription(
                    "Sideways offset of the third-person shoulder camera, in metres. " +
                    "Positive puts it over the right shoulder (the character sits left of " +
                    "centre). Numpad 4 / 6 change it live, Numpad 5 resets all three " +
                    "shoulder-camera offsets.",
                    new AcceptableValueRange<float>(-2f, 2f)));
            _faceMovement = Config.Bind("View", "FaceMovement", true,
                "In third person the model faces the direction it is moving instead of " +
                "the camera, like a shoulder-cam mod: moving the mouse orbits the camera " +
                "around a character that keeps its own heading, and pressing a movement " +
                "key turns the model towards it. The game's own aiming, controls and " +
                "collision are untouched. While a quick-slot item is held the model " +
                "faces the camera again, standing still included, so the body stays " +
                "aligned with the item (the seaglide points where the camera looks).");
            _headLook = Config.Bind("View", "HeadLook", 0.7f,
                new ConfigDescription(
                    "How much of the camera's up/down look the model's head follows " +
                    "(0 = the vanilla rig, where the head never pitches; 1 = the head " +
                    "points exactly where you look). The camera pitch is added on top " +
                    "of the animation for every item except the seaglide: its own aim " +
                    "pose already pitches the head with the camera, so adding it again " +
                    "made the head fall back far too much. The angle is split between " +
                    "the neck and the head bone and capped at 55 degrees either way; " +
                    "the sideways turn (capped at 60) is eased towards its target, so " +
                    "crossing the 180 degree line behind the body swings the head " +
                    "around instead of teleporting it. While standing still, or " +
                    "during cinematics and interactions, the whole follow fades out so " +
                    "turning the camera moves nothing on the model. 0.6-0.8 reads as a natural glance; can be dragged live in " +
                    "the PDA model page (视角 group).",
                    new AcceptableValueRange<float>(0f, 1f)));
            _idleDelay = Config.Bind("View", "IdleDelay", 3f,
                new ConfigDescription(
                    "How many seconds after the player stops moving the third-person " +
                    "idle freeze kicks in (0 = immediately). Until it does, the head " +
                    "keeps following the camera and the swim pose stays live, so a " +
                    "brief pause still looks alive; after it the body, head, hair and " +
                    "clothes ignore the camera completely. Can be dragged live in the " +
                    "PDA model page (视角 group).",
                    new AcceptableValueRange<float>(0f, 10f)));
            _poseTime = Config.Bind("View", "PoseTransition", 0.6f,
                new ConfigDescription(
                    "Seconds the model takes to ease into a new animation pose when " +
                    "taking out or holstering a quick-slot item (or when a cinematic " +
                    "starts or ends). The game switches those animation states in a " +
                    "single frame; this crossfade blends from the pose the model was " +
                    "showing to the new one with an ease-in-out curve, so the body " +
                    "settles instead of snapping. Longer is softer; too long starts to " +
                    "read as slow motion. Can be dragged live in the PDA model page " +
                    "(视角 group).",
                    new AcceptableValueRange<float>(0.1f, 2f)));

            string dir = null;
            try
            {
                if (Info != null && !string.IsNullOrEmpty(Info.Location))
                {
                    dir = Path.GetDirectoryName(Info.Location);
                }
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Paths.PluginPath, "PlayerModelReplacer");
            }

            _modelDir = dir;
            LoadHiddenMaterials();
            LoadGlowParts();
            MigrateAirSettle();
            ScanModels();
            if (_modelCatalog.Count == 0)
            {
                Logger.LogInfo("No models installed. Put a model folder (PMX + textures) in "
                    + Path.Combine(_modelDir, "Models") + " and press 刷新列表 in the PDA.");
            }
            else if (!LoadModel(FindActiveModel(), false))
            {
                Logger.LogWarning("The active model could not be loaded; pick another one in the PDA.");
            }

            // Decode the worn model's PNG/JPG textures on Unity's worker threads now,
            // while the main menu and the loading screen are up. By the time the
            // player spawns and the model is built, LoadTexture finds them ready and
            // the frame that used to freeze no longer pays for the decode.
            if (MasterEnabled && _data != null)
            {
                try
                {
                    PrefetchModelTextures(_data, _currentModelId ?? "");
                }
                catch (Exception e)
                {
                    // A prefetch failure must never take the rest of Awake (the
                    // Harmony patches) down with it; LoadTexture decodes instead.
                    Logger.LogWarning("Could not start texture prefetch: " + e.Message);
                }
            }

            // Build the CJK font and its glyph atlas now rather than on the first PDA
            // model page. The page is pure uGUI, so the same font is reused there.
            PrewarmUiFont();

            // Subnautica destroys BepInEx's manager object (and anything else in the
            // DontDestroyOnLoad scene) when it loads StartScreen in Single mode, so this
            // component never receives Update/LateUpdate. Keep ourselves reachable with a
            // static reference and pump from a Harmony patch on Player.LateUpdate.
            _instanceBox = this;
            Harmony harmony = new Harmony(PluginGuid);
            bool setTabs = TryPatch(harmony, typeof(PlayerLateUpdatePatch), "Player.LateUpdate");
            bool tabList = TryPatch(harmony, typeof(PdaSetTabsPatch), "uGUI_PDA.SetTabs");
            bool tabPage = TryPatch(harmony, typeof(PdaInitializePatch), "uGUI_PDA.Initialize");
            TryPatch(harmony, typeof(PdaTooltipPatch), "uGUI_PDA.CacheToolbarTooltips");
            TryPatch(harmony, typeof(PdaTabSpritePatch), "SpriteManager.Get");
            TryPatch(harmony, typeof(PlayerForwardReferencePatch), "PlayerController.forwardReference");
            bool movePatch = TryPatch(harmony, typeof(GroundMotorSecondPersonPatch), "GroundMotor.UpdateMove")
                & TryPatch(harmony, typeof(UnderwaterMotorSecondPersonPatch), "UnderwaterMotor.UpdateMove");
            TryPatch(harmony, typeof(ArmsControllerSecondPersonPatch), "ArmsController.Update");
            TryPatch(harmony, typeof(PlayerDamageSoundsPatch), "PlayerDamageSounds.OnTakeDamage");
            TryPatch(harmony, typeof(PlayerJumpSoundPatch), "Utils.PlayFMODAsset");
            TryPatch(harmony, typeof(BreathingSoundPatch), "BreathingSound.UpdateSound");
            // The first-run notice is created from the start screen, which is the
            // first scene that survives; the plugin component itself is gone by then.
            TryPatch(harmony, typeof(StartScreenConsentPatch), "StartScreen.Start");
            // Without both the toolbar entry and the page the PDA would index a
            // tab that does not exist, so the extra entry stays off unless both
            // halves went in.
            PdaModelTab.Enabled = setTabs && tabList && tabPage;
            Logger.LogInfo("Harmony patches applied (Player.LateUpdate=" + setTabs
                + ", PDA tab=" + (tabList && tabPage)
                + ", second-person movement=" + movePatch + ").");

            // Persist key/config migrations now instead of waiting for the first
            // PDA close. Saves stay off for the high-frequency slider writes.
            SaveConfigIfDirty();
        }

        private bool TryPatch(Harmony harmony, Type type, string what)
        {
            try
            {
                harmony.PatchAll(type);
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not patch " + what + ": " + e.Message);
                return false;
            }
        }

        private void BuildTextureIndex(string root, bool recursive)
        {
            _textureFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _textureNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _textureStems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _textureRoot = null;
            if (!Directory.Exists(root))
            {
                Logger.LogWarning("Texture directory not found: " + root);
                return;
            }

            string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
            _textureRoot = rootFull;
            string[] files = Directory.GetFiles(root, "*", recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" &&
                    ext != ".bmp" && ext != ".tga" && ext != ".dds")
                {
                    continue;
                }
                // Key by the path relative to the model folder, so
                // "tex/hair.png" and "spa/hair.png" stay distinct.
                string full = Path.GetFullPath(files[i]);
                string rel = full.Length > rootFull.Length
                    ? full.Substring(rootFull.Length).TrimStart('\\', '/').Replace('\\', '/')
                    : Path.GetFileName(full);
                if (rel.Length == 0)
                {
                    rel = Path.GetFileName(full);
                }
                if (!_textureFiles.ContainsKey(rel))
                {
                    _textureFiles[rel] = full;
                }
                AddTextureKey(_textureNames, TexturePaths.BaseName(rel), rel);
                AddTextureKey(_textureStems, Path.GetFileNameWithoutExtension(rel), rel);
            }
            Logger.LogInfo("Indexed " + _textureFiles.Count + " texture files under " + root);
        }

        private static void AddTextureKey(Dictionary<string, List<string>> index,
            string key, string rel)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            List<string> list;
            if (!index.TryGetValue(key, out list))
            {
                list = new List<string>();
                index[key] = list;
            }
            list.Add(rel);
        }

        // Resolve a PMX texture reference to the files that could satisfy it, in
        // the order MMD would try them:
        //   1. the reference as written: an absolute path, or a path relative to
        //      the PMX (this also covers "..\shared\tex\foo.png" leaving the
        //      model folder),
        //   2. the same relative path inside the scanned model folder,
        //   3. the same file name anywhere under the model folder,
        //   4. the same stem with another extension (001.tga vs 001.png).
        // A reference may name a file that is not on disk (a toon ramp, a texture
        // the author forgot to pack), so the list can be empty.
        private List<string> TextureCandidates(string file)
        {
            List<string> hits = new List<string>();
            if (string.IsNullOrEmpty(file))
            {
                return hits;
            }

            string raw = file.Trim().Trim('"').Replace('\\', '/');
            try
            {
                if (raw.Length > 2 && raw[1] == ':')
                {
                    // PMX exported on another machine: a drive-qualified path still
                    // works when the model was copied with its folders.
                    if (File.Exists(raw))
                    {
                        AddTextureFile(hits, raw);
                    }
                }
                else if (!Path.IsPathRooted(raw) && !string.IsNullOrEmpty(_textureRoot))
                {
                    string direct = Path.GetFullPath(Path.Combine(_textureRoot, raw));
                    if (File.Exists(direct))
                    {
                        AddTextureFile(hits, direct);
                    }
                }
            }
            catch (Exception)
            {
            }

            string key = TexturePaths.Normalize(file);
            string path;
            if (key.Length > 0 && _textureFiles != null &&
                _textureFiles.TryGetValue(key, out path))
            {
                AddTextureFile(hits, path);
            }
            string name = TexturePaths.BaseName(key.Length > 0 ? key : file);
            AddTextureFiles(hits, _textureNames, name);
            AddTextureFiles(hits, _textureStems, Path.GetFileNameWithoutExtension(name));
            return hits;
        }

        private void AddTextureFiles(List<string> hits,
            Dictionary<string, List<string>> index, string key)
        {
            if (index == null || string.IsNullOrEmpty(key) || _textureFiles == null)
            {
                return;
            }
            List<string> list;
            if (!index.TryGetValue(key, out list) || list.Count == 0)
            {
                return;
            }
            List<string> found = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                string path;
                if (_textureFiles.TryGetValue(list[i], out path))
                {
                    AddTextureFile(found, path);
                }
            }
            // Several same-named files: prefer the shallowest one, then a stable
            // order, so the choice does not depend on the directory scan order.
            found.Sort(CompareTexturePaths);
            for (int i = 0; i < found.Count; i++)
            {
                AddTextureFile(hits, found[i]);
            }
        }

        private static int CompareTexturePaths(string a, string b)
        {
            if (a.Length != b.Length)
            {
                return a.Length.CompareTo(b.Length);
            }
            return string.CompareOrdinal(a, b);
        }

        private static void AddTextureFile(List<string> hits, string path)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (string.Equals(hits[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            hits.Add(path);
        }

        // ------------------------------------------------------------------
        // model catalog: <plugin>/Models/<folder>/<anything>.pmx
        // ------------------------------------------------------------------

        private void ScanModels()
        {
            // The mod ships no model of its own: everything comes from the user's
            // Models folder. An empty catalog means the mod simply does nothing.
            _modelCatalog.Clear();
            string root = Path.Combine(_modelDir, "Models");
            if (!Directory.Exists(root))
            {
                return;
            }

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(root);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not scan Models/: " + e.Message);
                return;
            }
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < dirs.Length; i++)
            {
                string[] pmx;
                try
                {
                    pmx = Directory.GetFiles(dirs[i], "*.pmx", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue;
                }
                if (pmx.Length == 0)
                {
                    continue;
                }
                Array.Sort(pmx, StringComparer.OrdinalIgnoreCase);

                // A folder can hold more than one model (e.g. a Black and a White
                // variant of the same character). Each PMX becomes its own list
                // entry so it can be previewed and worn independently. The entry
                // for the folder's default file keeps the bare folder name as its
                // id, so existing configs (ActiveModel, hidden materials) still
                // point at the same model they always did.
                string folderName = Path.GetFileName(dirs[i]);
                int primary = 0;
                for (int k = 0; k < pmx.Length; k++)
                {
                    if (string.Equals(Path.GetFileName(pmx[k]), "model.pmx",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        primary = k;
                        break;
                    }
                }
                bool multi = pmx.Length > 1;
                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                AddModelEntry(folderName,
                    multi ? folderName + " · " +
                        Path.GetFileNameWithoutExtension(pmx[primary]) : folderName,
                    pmx[primary], used);

                for (int k = 0; k < pmx.Length; k++)
                {
                    if (k == primary)
                    {
                        continue;
                    }
                    string stem = Path.GetFileNameWithoutExtension(pmx[k]);
                    AddModelEntry(MakeUniqueId(used, folderName + "/" + stem),
                        folderName + " · " + stem, pmx[k], used);
                }
            }

            Logger.LogInfo("Model catalog: " + _modelCatalog.Count + " entries ("
                + root + ")");
            for (int i = 0; i < _modelCatalog.Count; i++)
            {
                ModelEntry entry = _modelCatalog[i];
                Logger.LogInfo("  [" + i + "] " + entry.Id + "  ->  " + entry.Label);
            }
        }

        private void AddModelEntry(string id, string label, string pmxPath,
            HashSet<string> used)
        {
            ModelEntry e = new ModelEntry();
            e.Id = id;
            e.Label = label;
            e.Folder = Path.GetDirectoryName(pmxPath);
            e.PmxPath = pmxPath;
            _modelCatalog.Add(e);
            if (used != null)
            {
                used.Add(id);
            }
        }

        private static string MakeUniqueId(HashSet<string> used, string baseId)
        {
            if (used == null || used.Add(baseId))
            {
                return baseId;
            }
            // Two subfolders can hold files with the same name; keep the ids
            // distinct so their per-model settings stay separate.
            for (int n = 2; ; n++)
            {
                string id = baseId + " (" + n + ")";
                if (used.Add(id))
                {
                    return id;
                }
            }
        }

        private ModelEntry FindActiveModel()
        {
            if (_modelCatalog.Count == 0)
            {
                return null;
            }
            string id = _activeModel != null ? _activeModel.Value : "";
            for (int i = 0; i < _modelCatalog.Count; i++)
            {
                if (_modelCatalog[i].Id == id)
                {
                    return _modelCatalog[i];
                }
            }
            // Empty (or a model that was deleted) now means "the first model in the
            // folder" - there is no shipped model to fall back to any more.
            return _modelCatalog[0];
        }

        /// <summary>
        /// Load one catalog entry into _data and point the texture index at it. When
        /// <paramref name="live"/> is set the running model is torn down so the next
        /// Tick rebuilds with the new data.
        /// </summary>
        /// <summary>
        /// Parses a catalog entry once and keeps the pristine result. Both the worn
        /// model and the PDA preview build from this cache, so highlighting a model
        /// that is already worn costs no second parse.
        /// </summary>
        private ModelData GetPristineModel(ModelEntry entry)
        {
            if (entry == null)
            {
                return null;
            }
            string cacheKey = entry.PmxPath;
            ModelData pristine;
            if (_modelCache.TryGetValue(cacheKey, out pristine))
            {
                return pristine;
            }
            try
            {
                PmxData pmx = PmxReader.Read(entry.PmxPath);
                pristine = PmxConverter.Convert(pmx, entry.Id);
                Logger.LogInfo(string.Format("Imported {0}: PMX {1} bones / {2} verts -> " +
                    "{3} bones, {4} materials, {5} meshes", entry.PmxPath,
                    pmx.Bones.Count, pmx.Positions.Length, pristine.Bones.Count,
                    pristine.Materials.Count, pristine.Meshes.Count));
                _modelCache[cacheKey] = pristine;
            }
            catch (Exception e)
            {
                Logger.LogError("Could not load model " + entry.PmxPath + ": " + e);
                return null;
            }
            return pristine;
        }

        private bool LoadModel(ModelEntry entry, bool live)
        {
            if (entry == null)
            {
                return false;
            }

            // Parse (or reuse the cached parse) of this catalog entry. The cache stores
            // the pristine result; BuildModel rewrites vertices, normals and bone weights
            // in place, so it always gets a deep copy.
            ModelData pristine = GetPristineModel(entry);
            if (pristine == null)
            {
                return false;
            }

            ModelData data = CloneModel(pristine);
            _data = data;
            _currentModelId = entry.Id;
            _imported = true;
            _textureRepeat = _imported;
            BuildTextureIndex(entry.Folder, true);

            if (live)
            {
                // Destroy the old model's materials, decoded textures, baked textures and
                // meshes before the textures dictionary is repopulated for the new model.
                ReleaseModelAssets();
                if (_pendingTextures != null)
                {
                    if (_pendingTextureModel == _currentModelId)
                    {
                        // Apply: reuse the preview's decode pass.
                        _textures.Clear();
                        foreach (KeyValuePair<string, Texture2D> pair in _pendingTextures)
                        {
                            _textures[pair.Key] = pair.Value;
                        }
                        _pendingTextures.Clear();
                        // The preview also created the materials and classified
                        // their alpha. Adopting them means EnsureMaterials has
                        // nothing left to do for this switch.
                        if (_pendingMaterials != null)
                        {
                            _materials = _pendingMaterials;
                            _blendFlags = _pendingBlendFlags;
                            _materialModelId = _currentModelId ?? "";
                        }
                        _pendingMaterials = null;
                        _pendingBlendFlags = null;
                        if (_pendingDerivedTextures != null)
                        {
                            // The preview's baked decals belong to its materials.
                            _derivedTextures.AddRange(_pendingDerivedTextures);
                            _pendingDerivedTextures = null;
                        }
                    }
                    else
                    {
                        foreach (Texture2D tex in _pendingTextures.Values)
                        {
                            if (tex != null)
                            {
                                Destroy(tex);
                            }
                        }
                        if (_pendingMaterials != null)
                        {
                            for (int i = 0; i < _pendingMaterials.Count; i++)
                            {
                                if (_pendingMaterials[i] != null)
                                {
                                    Destroy(_pendingMaterials[i]);
                                }
                            }
                        }
                        _pendingMaterials = null;
                        _pendingBlendFlags = null;
                        if (_pendingDerivedTextures != null)
                        {
                            for (int i = 0; i < _pendingDerivedTextures.Count; i++)
                            {
                                if (_pendingDerivedTextures[i] != null)
                                {
                                    Destroy(_pendingDerivedTextures[i]);
                                }
                            }
                            _pendingDerivedTextures = null;
                        }
                    }
                    _pendingTextures = null;
                    _pendingTextureModel = null;
                }
                _built = false;
                _nextTry = 0f;
            }
            return true;
        }

        /// <summary>
        /// Deep-copies the arrays BuildModel edits. Bones and materials are only read, so
        /// the cached lists are shared. Sub-mesh index arrays are rebuilt into new arrays
        /// during the build as well, so those are shared too.
        /// </summary>
        private static ModelData CloneModel(ModelData src)
        {
            ModelData dst = new ModelData();
            dst.ArmPoseBaked = src.ArmPoseBaked;
            for (int i = 0; i < src.Bones.Count; i++)
            {
                dst.Bones.Add(src.Bones[i]);
            }
            for (int i = 0; i < src.Materials.Count; i++)
            {
                dst.Materials.Add(src.Materials[i]);
            }
            for (int i = 0; i < src.Meshes.Count; i++)
            {
                MeshData s = src.Meshes[i];
                MeshData d = new MeshData();
                d.Name = s.Name;
                d.Role = s.Role;
                d.Vertices = s.Vertices != null ? (Vector3[])s.Vertices.Clone() : null;
                d.Normals = s.Normals != null ? (Vector3[])s.Normals.Clone() : null;
                d.Uvs = s.Uvs != null ? (Vector2[])s.Uvs.Clone() : null;
                d.Weights = s.Weights != null ? (BoneWeight[])s.Weights.Clone() : null;
                d.SubMeshMaterials = s.SubMeshMaterials;
                d.Indices = s.Indices;
                dst.Meshes.Add(d);
            }
            return dst;
        }

        /// <summary>
        /// Destroys everything the current model owns apart from the parsed data: the
        /// container and its meshes, the materials, and every texture decoded or baked
        /// for it. The old code leaked all of this on each switch (several hundred MB of
        /// VRAM per imported model), which caused the growing stutter and the occasional
        /// crash after a few model changes.
        /// </summary>
        private void ReleaseModelAssets()
        {
            // A preview of the worn model holds the very materials and textures that
            // are about to be destroyed; drop it first so nothing dangles.
            if (_previewSharedAssets)
            {
                ReleasePreviewModel();
            }
            if (_container != null)
            {
                // Destroy the generated meshes too; the container alone does not own them.
                SkinnedMeshRenderer[] smrs =
                    _container.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < smrs.Length; i++)
                {
                    SkinnedMeshRenderer smr = smrs[i];
                    if (smr == null || smr.sharedMesh == null)
                    {
                        continue;
                    }
                    Mesh mesh = smr.sharedMesh;
                    smr.sharedMesh = null;
                    Destroy(mesh);
                }
                Destroy(_container.gameObject);
                _container = null;
            }
            // The tool may still be offset onto the model hand; put it back
            // before the model that was holding it disappears.
            RestoreMovedItem();

            if (_materials != null)
            {
                for (int i = 0; i < _materials.Count; i++)
                {
                    if (_materials[i] != null)
                    {
                        Destroy(_materials[i]);
                    }
                }
                _materials = null;
            }
            _blendFlags = null;

            foreach (Texture2D tex in _textures.Values)
            {
                if (tex != null)
                {
                    Destroy(tex);
                }
            }
            _textures.Clear();

            for (int i = 0; i < _derivedTextures.Count; i++)
            {
                if (_derivedTextures[i] != null)
                {
                    Destroy(_derivedTextures[i]);
                }
            }
            _derivedTextures.Clear();

            _armature = null;
            _headBone = null;
            _bodyRenderer = null;
            _headRenderer = null;
            _hairRenderer = null;
            _clothChains.Clear();
            _colliders.Clear();
            _bodySlices.Clear();
            _collidersLogged = false;
            // Meshes below are gone; the preview record and the sub-mesh triangles
            // point into the destroyed model.
            _meshParts.Clear();
            _previewRenderers = null;
            _previewLayers = null;
            _previewShadows = null;
        }

        private void SelectModel(ModelEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            string id = entry.Id;
            string current = _activeModel != null ? _activeModel.Value : "";
            if (id == current && _data != null)
            {
                return;
            }
            if (!LoadModel(entry, true))
            {
                return;
            }
            if (_activeModel != null)
            {
                _activeModel.Value = id;
                try
                {
                    Config.Save();
                }
                catch (Exception e)
                {
                    Logger.LogWarning("Could not save the config: " + e.Message);
                }
            }
            Logger.LogInfo("Model switched to " + entry.Id);
        }

        // ---- standalone T-pose preview -------------------------------------
        //
        // The PDA list used to switch the worn model on click, and the preview was
        // just a camera aimed at the animated character. Clicking now only builds
        // this static copy: the PMX vertices are authored in the rest pose, so a
        // plain mesh (no bones, no skinning) is already the T-pose the page shows.
        // The material factory reads the plugin's texture index and cache, so the
        // build swaps those for the previewed model's and restores them afterwards;
        // it all happens inside one synchronous UI click.

        internal bool PreviewBuilt
        {
            get
            {
                return _previewRoot != null && _previewModelRenderers != null &&
                    _previewModelRenderers.Length > 0;
            }
        }

        /// <summary>True while a newly clicked model is parsing/decoding. The
        /// preview on screen still belongs to the previously clicked model, so
        /// the page freezes that frame instead of re-framing the old copy.</summary>
        internal bool PreviewPending
        {
            get { return _pendingPreview != null; }
        }

        internal int PreviewModelIndex
        {
            get { return _previewModelIndex; }
        }

        internal string PreviewModelId
        {
            get { return _previewModelId ?? ""; }
        }

        internal int PreviewGeneration
        {
            get { return _previewGeneration; }
        }

        internal int ActiveModelIndex
        {
            get
            {
                for (int i = 0; i < _modelCatalog.Count; i++)
                {
                    if (IsActiveModel(i))
                    {
                        return i;
                    }
                }
                return -1;
            }
        }

        internal void PreviewModelAt(int index)
        {
            if (index < 0 || index >= _modelCatalog.Count || !MasterEnabled)
            {
                return;
            }
            ModelEntry entry = _modelCatalog[index];
            string id = ModelId(entry);
            if (_previewRoot != null && id == (_previewModelId ?? ""))
            {
                // Already built; just move the highlight. Clicking back to the
                // model that is on screen also cancels a build that is still in
                // flight for the one clicked in between.
                _pendingPreview = null;
                _previewModelIndex = index;
                return;
            }
            if (_pendingPreview != null && _pendingPreview.Id == id)
            {
                // Already parsing/decoding; keep the highlight where the player
                // clicked and let the pending job finish.
                _pendingPreview.Index = index;
                _previewModelIndex = index;
                return;
            }

            // The preview that is on screen is deliberately NOT released here.
            // Doing so left nothing to draw until the clicked model finished
            // parsing/decoding, so TickPreview fell back to the worn model, which
            // is animated: the player saw the current model in its current pose
            // flash before the new T-pose appeared. Keeping the old preview also
            // gives the swap a single-frame hand-over (see UpdatePendingPreview).
            // The job it belonged to is abandoned; a parse already running on a
            // worker thread simply finishes and its result is dropped.
            if (_previewRoot != null)
            {
                Logger.LogInfo("Preview swap: keeping " + (_previewModelId ?? "?") +
                    " on screen while " + id + " loads");
            }
            _pendingPreview = null;

            PreviewJob job = new PreviewJob();
            job.Index = index;
            job.Id = id;
            job.Entry = entry;
            job.Since = Time.unscaledTime;
            _pendingPreview = job;
            // The list highlight moves immediately; the preview already on screen
            // stays until the clicked model's T-pose replaces it.
            _previewModelIndex = index;

            string cacheKey = entry.PmxPath;
            ModelData cached;
            if (_modelCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                job.Data = cached;
                Logger.LogInfo("Preloading preview of " + entry.Id);
                StartPreviewPrefetch(job);
                return;
            }

            // First preview of this model: PMX parse + conversion is pure CPU and
            // can take a noticeable moment for 60k-100k vertex models. Run it on a
            // worker thread; the click returns to the UI immediately.
            Logger.LogInfo("Parsing " + entry.Id + " for the preview...");
            Task<ModelData> parse = Task.Run(() => ParseModel(entry));
            job.ParseTask = parse;
            // Observe a fault even if the player clicks another model first, so a
            // failed parse never surfaces as an UnobservedTaskException.
            parse.ContinueWith(delegate(Task<ModelData> t)
            {
                Exception ignored = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>Background-thread half of the preview parse. Uses only plain
        /// .NET and math structs, never Unity objects.</summary>
        private static ModelData ParseModel(ModelEntry entry)
        {
            PmxData pmx = PmxReader.Read(entry.PmxPath);
            return PmxConverter.Convert(pmx, entry.Id);
        }

        // Called once per Tick while a preview is parsing or its textures are
        // decoding. A parse that finishes is cached and its textures are requested;
        // once those are done the static copy is built.
        private void UpdatePendingPreview()
        {
            PreviewJob job = _pendingPreview;
            if (job == null)
            {
                return;
            }

            if (job.ParseTask != null)
            {
                if (!job.ParseTask.IsCompleted)
                {
                    if (Time.unscaledTime - job.Since > 30f)
                    {
                        Logger.LogWarning("Preview parse timed out: " + job.Id);
                        _pendingPreview = null;
                        _previewModelIndex = -1;
                        _previewGeneration++;
                    }
                    return;
                }
                Task<ModelData> task = job.ParseTask;
                job.ParseTask = null;
                ModelData data = null;
                try
                {
                    data = task.Result;
                }
                catch (Exception e)
                {
                    Logger.LogError("Could not parse " + job.Id + " for the preview: " + e);
                }
                if (data == null)
                {
                    _pendingPreview = null;
                    _previewModelIndex = -1;
                    _previewGeneration++;
                    return;
                }
                job.Data = data;
                _modelCache[job.Entry.PmxPath] = data;
                Logger.LogInfo(string.Format(
                    "Preloaded model {0}: {1} bones, {2} materials, {3} meshes",
                    job.Id, data.Bones.Count, data.Materials.Count, data.Meshes.Count));
                StartPreviewPrefetch(job);
                return;
            }

            if (!MasterEnabled)
            {
                ReleasePreviewModel();
                return;
            }

            // Wait for the asynchronous decodes, but never forever: anything that
            // failed is filled in by the synchronous decoder during the build.
            if (PendingTextureCount(job.Id) > 0 && Time.unscaledTime - job.Since < 10f)
            {
                return;
            }
            _pendingPreview = null;
            // Hand-over in one frame: the previous preview stayed on screen for
            // the whole parse/decode wait above and is only freed here, right
            // before the new copy is built and drawn in this same frame. That
            // way no frame ever falls back to the worn model in between.
            ReleasePreviewModel();
            BuildPreviewNow(job);
        }

        /// <summary>
        /// Points the texture resolver at the previewed model for one pass and
        /// starts the background PNG/JPG decodes. The worn model's index is put
        /// back immediately; the decoded results are keyed by absolute path and
        /// are therefore independent of which index is active when they arrive.
        /// </summary>
        private void StartPreviewPrefetch(PreviewJob job)
        {
            if (job == null || job.Data == null)
            {
                return;
            }
            Dictionary<string, string> savedFiles = _textureFiles;
            Dictionary<string, List<string>> savedNames = _textureNames;
            Dictionary<string, List<string>> savedStems = _textureStems;
            string savedTextureRoot = _textureRoot;
            bool savedImported = _imported;
            bool savedRepeat = _textureRepeat;
            ModelData savedData = _data;
            Dictionary<string, Texture2D> savedTextures =
                new Dictionary<string, Texture2D>(_textures);
            try
            {
                _textures.Clear();
                _imported = true;
                _textureRepeat = _imported;
                _data = job.Data;
                _materialModelId = job.Id;
                BuildTextureIndex(job.Entry.Folder, true);
                PrefetchModelTextures(job.Data, job.Id);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not start preview texture prefetch: " + e.Message);
            }
            finally
            {
                _textures.Clear();
                foreach (KeyValuePair<string, Texture2D> pair in savedTextures)
                {
                    _textures[pair.Key] = pair.Value;
                }
                _textureFiles = savedFiles;
                _textureNames = savedNames;
                _textureStems = savedStems;
                _textureRoot = savedTextureRoot;
                _imported = savedImported;
                _textureRepeat = savedRepeat;
                _data = savedData;
                _materialModelId = _currentModelId ?? "";
            }
            job.PrefetchStarted = true;
            job.Since = Time.unscaledTime;
        }

        private void BuildPreviewNow(PreviewJob job)
        {
            ModelEntry entry = job.Entry;
            string id = job.Id;
            int index = job.Index;
            ModelData data = job.Data;
            if (entry == null || data == null)
            {
                return;
            }
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            Dictionary<string, string> savedFiles = _textureFiles;
            Dictionary<string, List<string>> savedNames = _textureNames;
            Dictionary<string, List<string>> savedStems = _textureStems;
            string savedTextureRoot = _textureRoot;
            bool savedImported = _imported;
            bool savedRepeat = _textureRepeat;
            ModelData savedData = _data;
            Dictionary<string, Texture2D> savedTextures =
                new Dictionary<string, Texture2D>(_textures);

            // Previewing the model that is already worn: the materials and decoded
            // textures in memory are exactly this model's, so borrowing them makes
            // opening the page instant instead of decoding everything a second time.
            bool shareWorn = _built && _materials != null && _data != null &&
                _materials.Count == data.Materials.Count && id == ActiveModelId;
            // CreateMaterial can derive textures (the list is currently empty, but
            // the bookkeeping stays for any future bake). Derived ones belong to
            // this preview, not to the worn model, so they are moved out of the
            // plugin-owned list once the build succeeds.
            int derivedStart = _derivedTextures.Count;

            List<Material> materials = null;
            bool[] blend = null;
            Transform root = null;
            try
            {
                if (shareWorn)
                {
                    materials = _materials;
                    _previewMaterialNames = null;
                    blend = _blendFlags;
                }
                else
                {
                    _textures.Clear();
                    _imported = true;
                    _textureRepeat = _imported;
                    _data = data;
                    // The materials of the previewed model must read that model's
                    // glow selection, not the worn one (which _data temporarily
                    // points away from here).
                    _materialModelId = id;
                    BuildTextureIndex(entry.Folder, true);

                    blend = GetBlendFlags(id, data);
                    materials = new List<Material>(data.Materials.Count);
                    List<string> materialNames = new List<string>(data.Materials.Count);
                    for (int i = 0; i < data.Materials.Count; i++)
                    {
                        bool blended = blend != null && i < blend.Length && blend[i];
                        materials.Add(CreateMaterial(data.Materials[i], blended));
                        materialNames.Add(data.Materials[i].Name ?? "");
                    }
                    _previewMaterialNames = materialNames;
                    _blendStateLogged = false;
                    LogBlendState(materials, blend, id);
                }
                _previewBlendFlags = blend;

                GameObject rootGo = new GameObject("Miku_PreviewModel");
                rootGo.layer = PdaModelTab.PreviewLayer;
                rootGo.transform.position = Vector3.zero;
                rootGo.transform.rotation = Quaternion.identity;
                root = rootGo.transform;

                List<Renderer> built = new List<Renderer>();
                for (int mi = 0; mi < data.Meshes.Count; mi++)
                {
                    MeshData md = data.Meshes[mi];
                    if (md == null || md.Vertices == null || md.Vertices.Length == 0)
                    {
                        continue;
                    }
                    GameObject meshGo = new GameObject(string.IsNullOrEmpty(md.Name)
                        ? "PreviewMesh" + mi : md.Name);
                    meshGo.layer = PdaModelTab.PreviewLayer;
                    meshGo.transform.SetParent(root, false);

                    Mesh mesh = new Mesh();
                    mesh.name = md.Name;
                    mesh.indexFormat = IndexFormat.UInt32;
                    mesh.vertices = md.Vertices;
                    mesh.normals = md.Normals;
                    mesh.uv = md.Uvs;
                    int subCount = md.Indices != null ? md.Indices.Length : 0;
                    mesh.subMeshCount = subCount;
                    Material[] meshMaterials = new Material[subCount];
                    for (int s = 0; s < subCount; s++)
                    {
                        int matIndex = md.SubMeshMaterials[s];
                        meshMaterials[s] = matIndex >= 0 && matIndex < materials.Count
                            ? materials[matIndex]
                            : null;
                        MeshPart part = new MeshPart();
                        part.Mesh = mesh;
                        part.SubMesh = s;
                        part.Triangles = md.Indices[s];
                        if (matIndex >= 0 && matIndex < data.Materials.Count)
                        {
                            part.Material = data.Materials[matIndex].Name ?? "";
                            part.Texture = data.Materials[matIndex].Texture;
                        }
                        else
                        {
                            part.Material = "";
                        }
                        _previewParts.Add(part);
                        mesh.SetTriangles(md.Indices[s], s);
                    }
                    mesh.RecalculateBounds();

                    MeshFilter filter = meshGo.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;
                    MeshRenderer renderer = meshGo.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = meshMaterials;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    // Off outside the manual preview render: the main camera must
                    // never see this copy, whatever its culling mask includes.
                    renderer.enabled = false;
                    built.Add(renderer);
                }

                // PMX vertices are authored in MMD units, where a 1.5 m character is
                // about 19 units tall. Fit the copy to a person-sized box so the
                // framing, the zoom limits and the near plane all behave the same
                // for every model.
                if (built.Count > 0)
                {
                    Bounds pb = built[0].bounds;
                    for (int i = 1; i < built.Count; i++)
                    {
                        pb.Encapsulate(built[i].bounds);
                    }
                    if (pb.size.y > 0.001f)
                    {
                        float s = 1.7f / pb.size.y;
                        root.localScale = new Vector3(s, s, s);
                    }
                }

                if (!shareWorn && _derivedTextures.Count > derivedStart)
                {
                    int added = _derivedTextures.Count - derivedStart;
                    _previewDerivedTextures.AddRange(
                        _derivedTextures.GetRange(derivedStart, added));
                    _derivedTextures.RemoveRange(derivedStart, added);
                }

                // Borrowed materials and textures belong to the worn model: the
                // preview must never destroy them.
                _previewMaterials = shareWorn ? null : materials;
                _previewSharedAssets = shareWorn;
                _previewRoot = root;
                _previewModelRenderers = built.ToArray();
                _previewModelId = id;
                _previewModelIndex = index;
                _previewTextures = shareWorn
                    ? new Dictionary<string, Texture2D>()
                    : new Dictionary<string, Texture2D>(_textures);
                ApplyPreviewVisibility();
                // Apply the stored 发光 selection to the preview's own materials
                // right away; CreateMaterial only bakes the model whose id is
                // current when it runs, and a preview can outlive that pass.
                ApplyPreviewGlow();
                _previewGeneration++;
                Logger.LogInfo(string.Format(
                    "Preview built: {0} mesh(es), {1} material(s){2} in {3} ms",
                    built.Count, materials.Count, shareWorn ? " (worn assets reused)" : "",
                    watch.ElapsedMilliseconds));
            }
            catch (Exception e)
            {
                Logger.LogError("Could not build the preview: " + e);
                if (root != null)
                {
                    Destroy(root.gameObject);
                }
                if (!shareWorn && materials != null)
                {
                    for (int i = 0; i < materials.Count; i++)
                    {
                        if (materials[i] != null)
                        {
                            Destroy(materials[i]);
                        }
                    }
                }
                if (!shareWorn)
                {
                    // The textures loaded for this attempt are still in _textures while
                    // the state swap is in effect.
                    foreach (Texture2D tex in _textures.Values)
                    {
                        if (tex != null)
                        {
                            Destroy(tex);
                        }
                    }
                    if (_derivedTextures.Count > derivedStart)
                    {
                        for (int i = derivedStart; i < _derivedTextures.Count; i++)
                        {
                            if (_derivedTextures[i] != null)
                            {
                                Destroy(_derivedTextures[i]);
                            }
                        }
                        _derivedTextures.RemoveRange(derivedStart,
                            _derivedTextures.Count - derivedStart);
                    }
                }
                _previewParts.Clear();
                _previewMaterials = null;
                _previewMaterialNames = null;
                _previewBlendFlags = null;
                _previewSharedAssets = false;
                _previewTextures.Clear();
                _previewRoot = null;
                _previewModelRenderers = null;
                _previewModelId = null;
                _previewModelIndex = -1;
                _previewGeneration++;
            }
            finally
            {
                // Put the worn model's texture state back.
                _textures.Clear();
                foreach (KeyValuePair<string, Texture2D> pair in savedTextures)
                {
                    _textures[pair.Key] = pair.Value;
                }
                _textureFiles = savedFiles;
                _textureNames = savedNames;
                _textureStems = savedStems;
                _textureRoot = savedTextureRoot;
                _imported = savedImported;
                _textureRepeat = savedRepeat;
                _data = savedData;
                _materialModelId = _currentModelId ?? "";
            }
        }

        internal void ApplyPreviewModel()
        {
            // Read the target before touching the preview state: ReleasePreviewModel
            // resets both fields, and reading them afterwards made every apply select
            // index -1 and silently do nothing.
            int index = _previewModelIndex;
            if (index < 0 || index >= _modelCatalog.Count)
            {
                return;
            }
            if (_previewSharedAssets || _previewRoot == null)
            {
                // Already the worn model: nothing to hand over.
                return;
            }

            // The preview already decoded every texture of this model and created
            // its materials. Hand both to the worn build instead of destroying them
            // and doing the same work again one frame later - that second pass is
            // most of the "apply" stutter.
            Dictionary<string, Texture2D> donated = _previewTextures;
            _previewTextures = new Dictionary<string, Texture2D>();
            List<Material> donatedMaterials = _previewMaterials;
            _previewMaterials = null;
            bool[] donatedBlend = _previewBlendFlags;
            _previewBlendFlags = null;
            List<Texture2D> donatedDerived =
                new List<Texture2D>(_previewDerivedTextures);
            _previewDerivedTextures.Clear();
            string donatedModel = _previewModelId;
            ReleasePreviewModel();
            _pendingTextures = donated;
            _pendingMaterials = donatedMaterials;
            _pendingBlendFlags = donatedBlend;
            _pendingDerivedTextures = donatedDerived;
            _pendingTextureModel = donatedModel;
            SelectModelAt(index);
            if (_pendingTextures != null)
            {
                // The switch did not happen (same model, bad data): give the textures
                // back to the preview so the page keeps drawing them. The materials
                // are no longer attached to anything, so destroy them.
                if (_pendingMaterials != null)
                {
                    for (int i = 0; i < _pendingMaterials.Count; i++)
                    {
                        if (_pendingMaterials[i] != null)
                        {
                            Destroy(_pendingMaterials[i]);
                        }
                    }
                }
                _pendingMaterials = null;
                _pendingBlendFlags = null;
                if (_pendingDerivedTextures != null)
                {
                    for (int i = 0; i < _pendingDerivedTextures.Count; i++)
                    {
                        if (_pendingDerivedTextures[i] != null)
                        {
                            Destroy(_pendingDerivedTextures[i]);
                        }
                    }
                    _pendingDerivedTextures = null;
                }
                _previewTextures = _pendingTextures;
                _pendingTextures = null;
                _pendingTextureModel = null;
                _previewGeneration++;
            }
        }

        internal void ReleasePreviewModel()
        {
            if (_previewRoot != null)
            {
                MeshFilter[] filters = _previewRoot.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < filters.Length; i++)
                {
                    if (filters[i] == null || filters[i].sharedMesh == null)
                    {
                        continue;
                    }
                    Mesh mesh = filters[i].sharedMesh;
                    filters[i].sharedMesh = null;
                    Destroy(mesh);
                }
                Destroy(_previewRoot.gameObject);
                _previewRoot = null;
            }
            if (_previewMaterials != null)
            {
                for (int i = 0; i < _previewMaterials.Count; i++)
                {
                    if (_previewMaterials[i] != null)
                    {
                        Destroy(_previewMaterials[i]);
                    }
                }
                _previewMaterials = null;
            }
            for (int i = 0; i < _previewDerivedTextures.Count; i++)
            {
                if (_previewDerivedTextures[i] != null)
                {
                    Destroy(_previewDerivedTextures[i]);
                }
            }
            _previewDerivedTextures.Clear();
            _previewMaterialNames = null;
            foreach (Texture2D tex in _previewTextures.Values)
            {
                if (tex != null)
                {
                    Destroy(tex);
                }
            }
            _previewTextures.Clear();
            _previewModelRenderers = null;
            _previewParts.Clear();
            _previewModelId = null;
            _previewModelIndex = -1;
            _previewSharedAssets = false;
            _previewBlendFlags = null;
            // Drop a parse/decode job that has not produced a preview yet. A parse
            // already running on a worker thread simply finishes and its result is
            // ignored (the fault continuation observes any exception).
            _pendingPreview = null;
            _previewGeneration++;
        }

        /// <summary>Applies the hidden-material set of the previewed model to the
        /// preview meshes. The worn model keeps its own copy of the same hide state
        /// through <see cref="ApplyPartVisibility"/>.</summary>
        private void ApplyPreviewVisibility()
        {
            HashSet<string> set;
            if (!_hiddenByModel.TryGetValue(_previewModelId ?? "", out set))
            {
                set = null;
            }
            for (int i = 0; i < _previewParts.Count; i++)
            {
                MeshPart part = _previewParts[i];
                if (part.Mesh == null)
                {
                    continue;
                }
                bool hide = set != null && set.Contains(part.Material ?? "");
                try
                {
                    part.Mesh.SetTriangles(hide ? NoTriangles : part.Triangles, part.SubMesh);
                }
                catch (Exception)
                {
                }
            }
        }

        // ---- PDA model page ----

        /// <summary>
        /// Opens the game's PDA on the model page, or switches to it when the PDA is
        /// already up. Everything else (cursor, pause, viewmodel) goes through the
        /// game's own flow, so the page behaves like any other tab.
        /// </summary>
        internal void OpenModelTab()
        {
            if (Player.main == null)
            {
                return;
            }
            PDA pda = Player.main.GetPDA();
            if (pda == null)
            {
                return;
            }
            if (pda.isInUse)
            {
                uGUI_PDA ui = pda.ui;
                if (ui == null)
                {
                    return;
                }
                // The time-capsule view empties the toolbar on purpose; there is no
                // model tab to switch to while it is showing.
                if (ui.currentTabType == PDATab.TimeCapsule)
                {
                    return;
                }
                if (ui.currentTabType != PdaModelTab.Tab)
                {
                    ui.OpenTab(PdaModelTab.Tab);
                }
                return;
            }
            // Do not steal the key from a locker, the builder or the pause menu.
            FPSInputModule input = FPSInputModule.current;
            if (input != null && input.lastGroup != null)
            {
                return;
            }
            pda.Open(PdaModelTab.Tab);
        }

        /// <summary>
        /// The PDA page draws Chinese labels with Unity's legacy Text, which needs a
        /// dynamic OS font: the game's own font assets do not ship CJK glyphs. Creating
        /// it lazily on the first PDA open made that frame hitch, so it is built and
        /// rasterised during start-up instead.
        /// </summary>
        private void EnsureUiFont()
        {
            if (_uiFont != null)
            {
                return;
            }
            try
            {
                _uiFont = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial Unicode MS" },
                    16);
            }
            catch (Exception)
            {
                _uiFont = null;
            }
        }

        private void PrewarmUiFont()
        {
            EnsureUiFont();
            if (_uiFont == null)
            {
                return;
            }
            try
            {
                // Every fixed label the model page draws, so the first open does not
                // rasterise a few hundred glyphs while the game is running. The
                // dynamic font rasterises per size, so both sizes used by the page
                // are requested.
                const string Text =
                    "模型列表预览部位隐藏按材质全部显示调整刷新当前模型内置初音" +
                    "刘海鬓角摆动头发刚度重力水的阻力暗流强度扭转跟随生理" +
                    "第一人称显示双马尾恢复默认值关闭时自动保存正在加载" +
                    "在当前视角不可见未命名材质勾选隐藏拖动立即生效页" +
                    "物理与身体碰撞空无已开启关闭号" +
                    "MMS控制台总开启用替换后恢复游戏原角色状态已原版" +
                    "按键设置菜单键位打窗口修改按下请一个取消音效屏蔽主角" +
                    "受伤痛呼跳跃喘气水下换重建当打开或在这也可以改" +
                    "使用须知本模组不附带任何模型前阅读并遵守该自带的" +
                    "中条款署名要求否修改商用禁止等事项版权与许可归其作者" +
                    "不再显示下次启动直接进入游戏同意拒绝我点击会退出" +
                    ConsentDialogHost.PrewarmText +
                    "，。：；！？（）【】《》、·—…“”‘’0123456789.%/+- F";
                // The dynamic font rasterises per size: the model page uses 16/20,
                // the console 13/15/16/17 and the first-run notice 12-27. Reserve
                // every size up front so the first press of F2 or the notice does
                // not stall on glyph generation.
                int[] sizes = new int[] { 12, 13, 14, 15, 16, 17, 18, 19, 20, 27 };
                for (int si = 0; si < sizes.Length; si++)
                {
                    _uiFont.RequestCharactersInTexture(Text, sizes[si], FontStyle.Normal);
                }
                // The notice's title and buttons are bold; dynamic fonts keep a
                // separate set of glyphs per style.
                _uiFont.RequestCharactersInTexture(ConsentDialogHost.PrewarmText, 20,
                    FontStyle.Bold);
                _uiFont.RequestCharactersInTexture(ConsentDialogHost.PrewarmText, 27,
                    FontStyle.Bold);
                // Model names come from folder names, so they vary; reserve them too.
                for (int i = 0; i < _modelCatalog.Count; i++)
                {
                    string label = _modelCatalog[i].Label;
                    if (!string.IsNullOrEmpty(label))
                    {
                        for (int si = 0; si < sizes.Length; si++)
                        {
                            _uiFont.RequestCharactersInTexture(label, sizes[si],
                                FontStyle.Normal);
                        }
                    }
                }
                Logger.LogInfo("PDA page font prewarmed (" + _uiFont.name + ").");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not prewarm the PDA page font: " + e.Message);
            }
        }

        private static KeyCode ParseKey(string text, KeyCode fallback)
        {
            KeyCode code;
            if (!string.IsNullOrEmpty(text) &&
                Enum.TryParse<KeyCode>(text.Trim(), true, out code) &&
                code != KeyCode.None)
            {
                return code;
            }
            return fallback;
        }

        // True when the config file already has a line for this key. Used by the
        // migrations so an old entry can only seed a new one that does not exist
        // yet; once the player has changed the new setting, the old line must
        // never override it on the next start.
        private bool ConfigFileContains(string key)
        {
            try
            {
                return File.Exists(Config.ConfigFilePath) &&
                    File.ReadAllText(Config.ConfigFilePath)
                        .IndexOf(key, StringComparison.Ordinal) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- what the PDA page is allowed to see ----

        internal static MikuPlayerReplacerPlugin InstanceBox
        {
            get { return _instanceBox as MikuPlayerReplacerPlugin; }
        }

        // The logger itself is protected on BaseUnityPlugin, so the PDA page goes
        // through these instead of reaching for it directly.
        internal void LogInfo(string message)
        {
            Logger.LogInfo(message);
        }

        internal void LogWarning(string message)
        {
            Logger.LogWarning(message);
        }

        internal static Font UiFont
        {
            get { return _uiFont; }
        }

        internal bool MasterEnabled
        {
            // ConsentGate.Agreed gates the whole mod: before the first-run notice
            // has been accepted the player still gets the vanilla character.
            get { return (_enabled == null || _enabled.Value) && ConsentGate.Agreed; }
        }

        internal ConfigEntry<bool> EnabledSetting
        {
            get { return _enabled; }
        }

        internal KeyCode MenuKeyCode
        {
            get { return _menuKeyCode; }
        }

        internal KeyCode ConsoleKeyCode
        {
            get { return _consoleKeyCode; }
        }

        internal void SetMenuKey(KeyCode code)
        {
            _menuKeyCode = code;
            if (_menuKey != null)
            {
                _menuKey.Value = code.ToString();
                _configDirty = true;
                SaveConfigIfDirty();
            }
        }

        internal void SetConsoleKey(KeyCode code)
        {
            _consoleKeyCode = code;
            if (_consoleKey != null)
            {
                _consoleKey.Value = code.ToString();
                _configDirty = true;
                SaveConfigIfDirty();
            }
        }

        /// <summary>
        /// Master switch. Off tears the worn model down without freeing its decoded
        /// textures/materials (so switching back on is quick) and re-enables the
        /// game's own suit renderers. On lets the next Tick build the model again.
        /// </summary>
        internal void SetMasterEnabled(bool on)
        {
            if (_enabled == null || _enabled.Value == on)
            {
                return;
            }
            _enabled.Value = on;
            _configDirty = true;
            SaveConfigIfDirty();

            if (!on)
            {
                ReleasePreviewModel();
                TearDownContainer();
                _built = false;
                RestoreGameBody();
                Logger.LogInfo("MMS disabled: game character restored.");
            }
            else
            {
                _built = false;
                _nextTry = 0f;
                Logger.LogInfo("MMS enabled: rebuilding " +
                    (string.IsNullOrEmpty(_currentModelId) ? "(no model)" : _currentModelId));
            }
        }

        internal void ToggleMasterEnabled()
        {
            SetMasterEnabled(!MasterEnabled);
        }

        /// <summary>Rebuilds the worn model from a fresh copy of its parsed data.
        /// Used by the console after changing settings that are baked at build
        /// time; LoadModel re-clones because BuildModel edits the model in place.</summary>
        internal void RebuildModel()
        {
            if (!MasterEnabled || _data == null)
            {
                return;
            }
            ReleasePreviewModel();
            ModelEntry entry = FindActiveModel();
            if (entry == null || !LoadModel(entry, true))
            {
                return;
            }
            PrefetchModelTextures(_data, _currentModelId ?? "");
        }

        // Destroys the visible model but keeps _materials/_textures around. The
        // master switch uses this: re-enabling can then rebuild without decoding
        // every texture again.
        private void TearDownContainer()
        {
            if (_container != null)
            {
                SkinnedMeshRenderer[] smrs =
                    _container.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < smrs.Length; i++)
                {
                    SkinnedMeshRenderer smr = smrs[i];
                    if (smr == null || smr.sharedMesh == null)
                    {
                        continue;
                    }
                    Mesh mesh = smr.sharedMesh;
                    smr.sharedMesh = null;
                    Destroy(mesh);
                }
                Destroy(_container.gameObject);
                _container = null;
            }
            RestoreMovedItem();
            _pairs = null;
            _prevPoseRot = null;
            _prevSourceValid = false;
            _poseJumpLock = 0f;
            _poseTransition = 0f;
            _poseTransitionLength = 0f;
            _poseFromRot = null;
            _poseFromValid = false;
            _lastBaseRot = null;
            _lastBaseValid = false;
            _armature = null;
            _adaptiveHands[0] = null;
            _adaptiveHands[1] = null;
            _armRest[0] = null;
            _armRest[1] = null;
            _holdPoses.Clear();
            _holdCapturePending = false;
            _holdPoseLast = null;
            _headBone = null;
            _neckBone = null;
            _bodyRenderer = null;
            _headRenderer = null;
            _hairRenderer = null;
            _clothChains.Clear();
            _colliders.Clear();
            _bodySlices.Clear();
            _meshParts.Clear();
            _clothVertexMask = null;
            _hairVisibleMask = null;
        }

        private void RestoreGameBody()
        {
            RestoreMask();
            if (_gameBodyRenderers.Count == 0 && _maleGeo != null)
            {
                _maleGeo.GetComponentsInChildren(true, _gameBodyRenderers);
            }
            for (int i = 0; i < _gameBodyRenderers.Count; i++)
            {
                Renderer r = _gameBodyRenderers[i];
                if (r != null && !r.enabled)
                {
                    r.enabled = true;
                }
            }
        }

        internal void RefreshCatalog()
        {
            ScanModels();
            // A model folder may have been replaced: do not keep alpha results
            // computed from the old files.
            _blendCache.Clear();
            // The game started with an empty Models folder. Adopt the first model
            // that has just been added, so "刷新列表" alone brings the mod to life
            // (there is no shipped model to fall back to any more).
            if (_data == null && _modelCatalog.Count > 0)
            {
                LoadModel(_modelCatalog[0], false);
                _built = false;
            }
        }

        internal int ModelCount
        {
            get { return _modelCatalog.Count; }
        }

        internal string ModelLabelAt(int index)
        {
            return index >= 0 && index < _modelCatalog.Count ? _modelCatalog[index].Label : "";
        }

        internal string ActiveModelId
        {
            get { return _activeModel != null ? _activeModel.Value : ""; }
        }

        internal bool IsActiveModel(int index)
        {
            return index >= 0 && index < _modelCatalog.Count &&
                ModelId(_modelCatalog[index]) == ActiveModelId;
        }

        internal void SelectModelAt(int index)
        {
            if (index < 0 || index >= _modelCatalog.Count)
            {
                return;
            }
            SelectModel(_modelCatalog[index]);
        }

        private static string ModelId(ModelEntry entry)
        {
            return entry == null ? "" : (entry.Id ?? "");
        }

        internal bool IsModelBuilt
        {
            get { return _built; }
        }

        internal int BuildGeneration
        {
            get { return _buildGeneration; }
        }

        internal bool ModelVisibleInWorld
        {
            get { return _container != null && _container.gameObject.activeInHierarchy; }
        }

        internal ConfigEntry<float> FringeSway { get { return _fringeSway; } }
        internal ConfigEntry<float> HeadLook { get { return _headLook; } }
        internal ConfigEntry<float> IdleDelay { get { return _idleDelay; } }

        internal ConfigEntry<float> PoseTransition { get { return _poseTime; } }
        internal ConfigEntry<float> HairStiffness { get { return _hairStiffness; } }
        internal ConfigEntry<float> HairGravity { get { return _hairGravity; } }
        internal ConfigEntry<float> HairDamping { get { return _hairDamping; } }
        internal ConfigEntry<float> HairFlow { get { return _hairFlow; } }
        internal ConfigEntry<float> HairTwistFollow { get { return _hairTwistFollow; } }
        internal ConfigEntry<bool> HairPhysics { get { return _hairPhysics; } }
        internal ConfigEntry<bool> HairCollision { get { return _hairCollision; } }
        internal ConfigEntry<bool> LimbCollision { get { return _limbCollision; } }
        internal ConfigEntry<bool> SkirtPhysics { get { return _skirtPhysics; } }
        internal ConfigEntry<bool> ClothesPhysics { get { return _clothesPhysics; } }
        internal ConfigEntry<bool> AccessoryPhysics { get { return _accessoryPhysics; } }
        internal ConfigEntry<bool> SurfacePhysics { get { return _surfacePhysics; } }
        internal ConfigEntry<bool> ShowHair { get { return _showHair; } }
        internal ConfigEntry<float> SkirtStiffness { get { return _skirtStiffness; } }
        internal ConfigEntry<float> SkirtLeash { get { return _skirtLeash; } }
        internal ConfigEntry<float> SkirtClearance { get { return _skirtClearance; } }
        internal ConfigEntry<float> AirGravity { get { return _airGravity; } }
        internal ConfigEntry<float> AirDamping { get { return _airDamping; } }
        internal ConfigEntry<float> HighlightGlow { get { return _highlightGlow; } }
        internal ConfigEntry<bool> MutePlayerSounds { get { return _mutePlayerSounds; } }

        internal void MarkConfigDirty()
        {
            _configDirty = true;
        }

        internal void ResetPhysicsDefaults()
        {
            _fringeSway.Value = 0.35f;
            _hairStiffness.Value = 5f;
            _hairGravity.Value = 0.8f;
            _hairDamping.Value = 10f;
            _hairFlow.Value = 0.03f;
            _hairTwistFollow.Value = 2f;
            _hairPhysics.Value = true;
            _hairCollision.Value = true;
            _limbCollision.Value = true;
            _skirtPhysics.Value = true;
            _skirtClearance.Value = 0.02f;
            _skirtStiffness.Value = 1f;
            _skirtLeash.Value = 1f;
            _clothesPhysics.Value = true;
            _accessoryPhysics.Value = true;
            _surfacePhysics.Value = true;
            _airGravity.Value = 6f;
            _airDamping.Value = 3f;
            if (_highlightGlow != null)
            {
                _highlightGlow.Value = 1f;
            }
            if (_headLook != null)
            {
                _headLook.Value = 0.7f;
            }
            if (_idleDelay != null)
            {
                _idleDelay.Value = 3f;
            }
            if (_poseTime != null)
            {
                _poseTime.Value = 0.6f;
            }
            _showHair.Value = true;
            if (_mutePlayerSounds != null)
            {
                _mutePlayerSounds.Value = false;
                MutePlayerSoundsActive = false;
            }
            _configDirty = true;
        }

        internal void SaveConfigIfDirty()
        {
            if (!_configDirty)
            {
                return;
            }
            _configDirty = false;
            if (_hiddenMaterials != null)
            {
                _hiddenMaterials.Value = SerializeHiddenMaterials();
            }
            if (_glowParts != null)
            {
                _glowParts.Value = SerializeGlowParts();
            }
            try
            {
                Config.Save();
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not save the config: " + e.Message);
            }
        }

        // ---- per-material part hiding ----

        private static readonly int[] NoTriangles = new int[0];

        /// <summary>
        /// Captures the final triangle list of one sub-mesh so the PDA page can hide
        /// and restore it later. The array is the same one written into the Mesh.
        /// </summary>
        private void RecordPart(Mesh mesh, int submesh, int[] triangles, int materialIndex)
        {
            if (mesh == null || triangles == null)
            {
                return;
            }
            MeshPart part = new MeshPart();
            part.Mesh = mesh;
            part.SubMesh = submesh;
            part.Triangles = triangles;
            part.Material = MaterialName(materialIndex);
            if (_data != null && materialIndex >= 0 && materialIndex < _data.Materials.Count)
            {
                part.Texture = _data.Materials[materialIndex].Texture;
            }
            _meshParts.Add(part);
        }

        /// <summary>
        /// Fills the material list for the PDA page: unique names in PMX order, each
        /// with the file name of its texture so two materials with the same name stay
        /// distinguishable to the player.
        /// </summary>
        internal void CollectParts(List<MaterialPart> into)
        {
            into.Clear();
            // The page edits the model on screen, which is the T-pose preview when
            // one is built; the worn model's parts are only the fallback.
            List<MeshPart> source = _previewParts.Count > 0 ? _previewParts : _meshParts;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < source.Count; i++)
            {
                MeshPart part = source[i];
                string name = part.Material ?? "";
                if (!seen.Add(name))
                {
                    continue;
                }
                MaterialPart mp = new MaterialPart();
                mp.Name = name;
                if (!string.IsNullOrEmpty(part.Texture))
                {
                    int slash = part.Texture.LastIndexOfAny(new char[] { '\\', '/' });
                    mp.Texture = slash >= 0 ? part.Texture.Substring(slash + 1) : part.Texture;
                }
                else
                {
                    mp.Texture = "";
                }
                into.Add(mp);
            }
        }

        /// <summary>Hide state is stored per model. The page edits the model it
        /// shows, so the previewed model's key wins while a preview is built.</summary>
        private string PartModelId
        {
            get { return _previewRoot != null ? (_previewModelId ?? "") : (_currentModelId ?? ""); }
        }

        internal bool IsPartHidden(string material)
        {
            HashSet<string> set;
            return _hiddenByModel.TryGetValue(PartModelId, out set) &&
                set.Contains(material ?? "");
        }

        internal void SetPartHidden(string material, bool hidden)
        {
            material = material ?? "";
            string id = PartModelId;
            HashSet<string> set;
            if (!_hiddenByModel.TryGetValue(id, out set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _hiddenByModel[id] = set;
            }
            bool changed = hidden ? set.Add(material) : set.Remove(material);
            if (!changed)
            {
                return;
            }
            ApplyPartVisibility();
            ApplyPreviewVisibility();
            _configDirty = true;
        }

        internal void ShowAllParts()
        {
            HashSet<string> set;
            string id = PartModelId;
            if (!_hiddenByModel.TryGetValue(id, out set) || set.Count == 0)
            {
                return;
            }
            set.Clear();
            ApplyPartVisibility();
            ApplyPreviewVisibility();
            _configDirty = true;
        }

        /// <summary>
        /// Hides a sub-mesh by clearing its triangle list; the cached original array
        /// brings it back. Bounds are deliberately not recalculated: the preview
        /// framing and the game's culling stay stable while parts come and go.
        /// </summary>
        private void ApplyPartVisibility()
        {
            HashSet<string> set;
            if (!_hiddenByModel.TryGetValue(_currentModelId ?? "", out set))
            {
                set = null;
            }
            for (int i = 0; i < _meshParts.Count; i++)
            {
                MeshPart part = _meshParts[i];
                if (part.Mesh == null)
                {
                    continue;
                }
                bool hide = set != null && set.Contains(part.Material ?? "");
                try
                {
                    part.Mesh.SetTriangles(hide ? NoTriangles : part.Triangles, part.SubMesh);
                }
                catch (Exception e)
                {
                    if (!_partErrorLogged)
                    {
                        _partErrorLogged = true;
                        Logger.LogWarning("Could not hide material \"" + part.Material + "\": " + e.Message);
                    }
                }
            }
        }

        private bool _partErrorLogged;

        private void LoadHiddenMaterials()
        {
            _hiddenByModel.Clear();
            if (_hiddenMaterials == null || string.IsNullOrEmpty(_hiddenMaterials.Value))
            {
                return;
            }
            string[] models = _hiddenMaterials.Value.Split(';');
            for (int i = 0; i < models.Length; i++)
            {
                string entry = models[i];
                if (entry.Length == 0)
                {
                    continue;
                }
                int bar = entry.IndexOf('|');
                if (bar < 0)
                {
                    continue;
                }
                string model = UnescapePart(entry.Substring(0, bar));
                HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
                string list = entry.Substring(bar + 1);
                if (list.Length > 0)
                {
                    string[] names = list.Split(',');
                    for (int n = 0; n < names.Length; n++)
                    {
                        if (names[n].Length > 0)
                        {
                            set.Add(UnescapePart(names[n]));
                        }
                    }
                }
                if (set.Count > 0)
                {
                    _hiddenByModel[model] = set;
                }
            }
        }

        private string SerializeHiddenMaterials()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, HashSet<string>> pair in _hiddenByModel)
            {
                if (pair.Value == null || pair.Value.Count == 0)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(';');
                }
                sb.Append(EscapePart(pair.Key));
                sb.Append('|');
                bool first = true;
                foreach (string name in pair.Value)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(EscapePart(name));
                }
            }
            return sb.ToString();
        }

        private static string EscapePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            return value.Replace("%", "%25").Replace(";", "%3B")
                .Replace("|", "%7C").Replace(",", "%2C");
        }

        private static string UnescapePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            return value.Replace("%3B", ";").Replace("%7C", "|")
                .Replace("%2C", ",").Replace("%25", "%");
        }

        // ---- per-material glow ----
        // The built-in model paints its highlights into dedicated materials. The
        // same effect is now selectable for any imported model: the page stores
        // the chosen material names per model, and the emissive properties are
        // rewritten live, so a toggle takes effect without rebuilding the model.

        private void LoadGlowParts()
        {
            _glowByModel.Clear();
            if (_glowParts != null && !string.IsNullOrEmpty(_glowParts.Value))
            {
                string[] models = _glowParts.Value.Split(';');
                for (int i = 0; i < models.Length; i++)
                {
                    string entry = models[i];
                    if (entry.Length == 0)
                    {
                        continue;
                    }
                    int bar = entry.IndexOf('|');
                    if (bar < 0)
                    {
                        continue;
                    }
                    string model = UnescapePart(entry.Substring(0, bar));
                    HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string list = entry.Substring(bar + 1);
                    if (list.Length > 0)
                    {
                        string[] names = list.Split(',');
                        for (int n = 0; n < names.Length; n++)
                        {
                            string name = UnescapePart(names[n]).Trim();
                            if (name.Length > 0)
                            {
                                set.Add(name);
                            }
                        }
                    }
                    // Keep empty entries too: "model|" means the user turned every
                    // glow checkbox off, and that must not fall back to the
                    // GlowMaterials default for the built-in model.
                    _glowByModel[model] = set;
                }
            }
            // Existing installs only have the old single GlowMaterials list. It is
            // the built-in model's selection; seed it unless the page already
            // wrote a per-model entry for the built-in model.
            if (!_glowByModel.ContainsKey("") && _glowMaterials != null &&
                !string.IsNullOrEmpty(_glowMaterials.Value))
            {
                HashSet<string> seed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] names = _glowMaterials.Value.Split(',');
                for (int i = 0; i < names.Length; i++)
                {
                    string name = names[i].Trim();
                    if (name.Length > 0)
                    {
                        seed.Add(name);
                    }
                }
                if (seed.Count > 0)
                {
                    _glowByModel[""] = seed;
                }
            }
        }

        private string SerializeGlowParts()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, HashSet<string>> pair in _glowByModel)
            {
                if (pair.Value == null)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(';');
                }
                sb.Append(EscapePart(pair.Key));
                sb.Append('|');
                bool first = true;
                foreach (string name in pair.Value)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(EscapePart(name));
                }
            }
            return sb.ToString();
        }

        /// <summary>Glow selection of one model, or null when nothing glows.</summary>
        private HashSet<string> GlowSetFor(string modelId)
        {
            HashSet<string> set;
            return _glowByModel.TryGetValue(modelId ?? "", out set) ? set : null;
        }

        internal bool IsPartGlowing(string material)
        {
            HashSet<string> set = GlowSetFor(PartModelId);
            return set != null && set.Contains((material ?? "").Trim());
        }

        internal void SetPartGlow(string material, bool glow)
        {
            // IsGlowMaterial trims the name it looks up, so store it trimmed too;
            // otherwise a material with padding could be checked but not glow.
            material = (material ?? "").Trim();
            string id = PartModelId;
            HashSet<string> set;
            if (!_glowByModel.TryGetValue(id, out set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _glowByModel[id] = set;
            }
            bool changed = glow ? set.Add(material) : set.Remove(material);
            if (!changed)
            {
                return;
            }
            ApplyGlow();
            _configDirty = true;
        }

        /// <summary>Re-applies the glow selection after the highlight strength
        /// slider moved. Materials are rewritten in place, no rebuild.</summary>
        internal void RefreshGlow()
        {
            ApplyGlow();
        }

        /// <summary>Rewrite the emissive properties of both material sets after the
        /// selection or the highlight strength changed. Existing materials keep
        /// their textures; only the glow strength/keyword move.</summary>
        private void ApplyGlow()
        {
            if (_materials != null && _data != null)
            {
                string id = _currentModelId ?? "";
                for (int i = 0; i < _materials.Count && i < _data.Materials.Count; i++)
                {
                    UpdateMaterialGlow(_materials[i], _data.Materials[i].Name, id);
                }
            }
            ApplyPreviewGlow();
        }

        /// <summary>Glow for the preview's own materials only. Separate from
        /// ApplyGlow because a preview build temporarily points _data at the
        /// previewed model while _materials still belongs to the worn one.</summary>
        private void ApplyPreviewGlow()
        {
            if (_previewMaterials == null || _previewMaterialNames == null)
            {
                return;
            }
            string id = _previewModelId ?? "";
            for (int i = 0; i < _previewMaterials.Count
                && i < _previewMaterialNames.Count; i++)
            {
                UpdateMaterialGlow(_previewMaterials[i], _previewMaterialNames[i], id);
            }
        }

        private void UpdateMaterialGlow(Material m, string name, string modelId)
        {
            if (m == null || (_glowTest != null && _glowTest.Value))
            {
                return;
            }
            bool glow = IsGlowMaterial(name, modelId);
            float fill = _emissionFill != null ? Mathf.Max(0f, _emissionFill.Value) : 0f;
            float highlight = glow && _highlightGlow != null
                ? Mathf.Max(0f, _highlightGlow.Value) : 0f;
            float strength = fill + highlight;
            Texture tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            if (m.HasProperty("_GlowStrength"))
            {
                // Game suit shader (MarmosetUBER): the glow channel multiplies the
                // _Illum texture by _GlowStrength, so pointing _Illum at the albedo
                // keeps each part glowing in its own painted colour.
                if (tex != null && strength > 0f)
                {
                    if (m.HasProperty("_EnableGlow"))
                    {
                        m.SetFloat("_EnableGlow", 1f);
                    }
                    if (m.HasProperty("_Illum"))
                    {
                        m.SetTexture("_Illum", tex);
                    }
                    if (m.HasProperty("_GlowColor"))
                    {
                        m.SetColor("_GlowColor", Color.white);
                    }
                    m.SetFloat("_GlowStrength", strength);
                    if (m.HasProperty("_GlowStrengthNight"))
                    {
                        m.SetFloat("_GlowStrengthNight", strength);
                    }
                    if (m.HasProperty("_GlowUVfromVC"))
                    {
                        m.SetFloat("_GlowUVfromVC", 0f);
                    }
                    m.EnableKeyword("MARMO_EMISSION");
                }
                else
                {
                    m.SetFloat("_GlowStrength", 0f);
                    if (m.HasProperty("_GlowStrengthNight"))
                    {
                        m.SetFloat("_GlowStrengthNight", 0f);
                    }
                    m.DisableKeyword("MARMO_EMISSION");
                }
                return;
            }
            // Standard shader fallback: emission map x colour.
            if (m.HasProperty("_EmissionColor"))
            {
                if (tex != null && strength > 0f)
                {
                    if (m.HasProperty("_EmissionMap"))
                    {
                        m.SetTexture("_EmissionMap", tex);
                    }
                    m.SetColor("_EmissionColor", new Color(strength, strength, strength));
                    m.EnableKeyword("_EMISSION");
                }
                else
                {
                    m.SetColor("_EmissionColor", Color.black);
                    m.DisableKeyword("_EMISSION");
                }
            }
        }

        private void MigrateAirSettle()
        {
            try
            {
                if (_airSettleMigrated == null || _airSettleMigrated.Value)
                {
                    return;
                }
                if (_airDamping != null && Mathf.Abs(_airDamping.Value - 1.2f) < 0.001f)
                {
                    _airDamping.Value = 3f;
                    Logger.LogInfo("Raised the old AirDamping default (1.2) to 3.0: "
                        + "dry hair and cloth now settle instead of swaying for seconds.");
                }
                _airSettleMigrated.Value = true;
                Config.Save();
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not migrate AirDamping: " + e.Message);
            }
        }

        // ---- live preview ----

        /// <summary>
        /// Renders the worn model into the page's RenderTexture. The model's own
        /// renderers are moved to a private layer for the duration of one manual
        /// render and restored in <c>finally</c>, so the main camera never sees the
        /// swap. The head is forced to cast shadows because first person sets it to
        /// ShadowsOnly, which would otherwise leave the preview headless.
        /// </summary>
        internal void TickPreview(Camera cam, Light fill, float orbitYaw = 0f,
            float orbitPitch = 0f, float zoom = 1f, float panX = 0f, float panY = 0f)
        {
            if (cam == null)
            {
                return;
            }

            // The standalone T-pose copy is what the page shows once a model has
            // been preloaded; the worn model is only the fallback.
            bool standalone = _previewRoot != null && _previewModelRenderers != null &&
                _previewModelRenderers.Length > 0;
            Renderer[] renderers = standalone ? _previewModelRenderers : _previewRenderers;
            if (renderers == null || renderers.Length == 0)
            {
                return;
            }
            if (!standalone && (!_built || _container == null ||
                !_container.gameObject.activeInHierarchy))
            {
                return;
            }

            bool any = false;
            Bounds bounds = new Bounds();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.gameObject.activeInHierarchy)
                {
                    continue;
                }
                // Standalone preview renderers are disabled except while they are
                // being drawn, so their enabled flag says nothing about visibility.
                if (!standalone && !r.enabled)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
            if (!any)
            {
                return;
            }

            Transform t = standalone ? _previewRoot : _container;
            Vector3 up = t.up;
            if (up.sqrMagnitude < 0.0001f)
            {
                up = Vector3.up;
            }
            up.Normalize();

            Vector3 forward = t.forward;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            // Turntable orbit. Nothing here rotates the mesh: the camera walks
            // around the model, which keeps the preview independent of the world
            // character's transform.
            Quaternion yawRot = Quaternion.AngleAxis(orbitYaw, up);
            Vector3 dir = yawRot * forward;
            if (Mathf.Abs(orbitPitch) > 0.01f)
            {
                // The base camera looks back along -forward, so its right axis is
                // the cross of the view direction and up. Rotating the camera
                // position about the negative axis raises it for a positive pitch.
                Vector3 rightAxis = yawRot * Vector3.Cross(-forward, up);
                dir = Quaternion.AngleAxis(-orbitPitch, rightAxis) * dir;
            }

            float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float dist;
            if (standalone)
            {
                // The preview frame is wide, so fit the eight bounding-box
                // corners into it for this orbit angle instead of fitting the
                // bounding sphere: the T-pose then fills the frame instead of
                // floating inside a mostly empty one. Both the vertical and
                // horizontal limits are honoured, so wide models still fit.
                float aspect = cam.aspect > 0.01f ? cam.aspect : 1f;
                Quaternion fitRot = Quaternion.LookRotation(-dir, up);
                Vector3 rightAxis = fitRot * Vector3.right;
                Vector3 upAxis = fitRot * Vector3.up;
                Vector3 ext = bounds.extents;
                dist = 0f;
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 corner = new Vector3(ext.x * sx,
                                ext.y * sy, ext.z * sz);
                            float toward = Vector3.Dot(corner, dir);
                            float x = Mathf.Abs(Vector3.Dot(corner, rightAxis));
                            float y = Mathf.Abs(Vector3.Dot(corner, upAxis));
                            float need = toward + Mathf.Max(y / tanV,
                                x / (tanV * aspect));
                            if (need > dist)
                            {
                                dist = need;
                            }
                        }
                    }
                }
                dist *= 1.04f;
            }
            else
            {
                // The worn model is animated, so frame its bounding sphere to
                // keep the fallback view from pumping with every pose.
                dist = radius / tanV * 1.08f;
            }
            // The limits have to scale with the model: a fixed 8 m cap put the
            // camera inside anything bigger than a person, which is how a raw PMX
            // (about 20 units tall) ended up as a white close-up of the torso.
            dist = Mathf.Clamp(dist, radius * 1.2f, radius * 12f)
                * Mathf.Clamp(zoom, 0.3f, 3f);

            Vector3 target = bounds.center;
            Vector3 camPos = target + dir * dist;
            Quaternion camRot = Quaternion.LookRotation(target - camPos, up);

            // Pan slides the framed point across the view plane; the scale is tied
            // to the distance so it feels the same zoomed in or out.
            if (Mathf.Abs(panX) > 0.01f || Mathf.Abs(panY) > 0.01f)
            {
                float k = dist * 0.0016f;
                Vector3 shift = camRot * new Vector3(-panX, -panY, 0f) * k;
                target += shift;
                camPos += shift;
                camRot = Quaternion.LookRotation(target - camPos, up);
            }

            cam.transform.position = camPos;
            cam.transform.rotation = camRot;
            cam.cullingMask = 1 << PdaModelTab.PreviewLayer;
            // Re-assert the transparent clear every frame: the preview must never
            // fall back to a solid or skybox background.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
            if (fill != null)
            {
                // Key light from over the camera's shoulder so the face is never
                // lost in its own shadow.
                fill.transform.rotation =
                    Quaternion.Euler(35f, cam.transform.eulerAngles.y + 25f, 0f);
            }

            int count = renderers.Length;
            if (_previewLayers == null || _previewLayers.Length != count)
            {
                _previewLayers = new int[count];
            }
            if (_previewShadows == null || _previewShadows.Length != count)
            {
                _previewShadows = new ShadowCastingMode[count];
            }

            // Save everything before touching anything: two renderers can share a
            // GameObject, and saving after the first swap would record layer 31.
            for (int i = 0; i < count; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                {
                    continue;
                }
                _previewLayers[i] = r.gameObject.layer;
                _previewShadows[i] = r.shadowCastingMode;
            }

            bool fog = RenderSettings.fog;
            for (int i = 0; i < count; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                {
                    continue;
                }
                r.gameObject.layer = PdaModelTab.PreviewLayer;
                if (standalone)
                {
                    r.enabled = true;
                }
                else
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }
            RenderSettings.fog = false;
            if (fill != null)
            {
                fill.enabled = true;
            }
            RenderTexture previousTarget = RenderTexture.active;
            try
            {
                // A clear in advance guarantees the uncovered pixels are fully
                // transparent, whatever the PDA image behind them looks like.
                RenderTexture rt = cam.targetTexture;
                if (rt != null)
                {
                    RenderTexture.active = rt;
                    GL.Clear(true, true, Color.clear);
                }
                cam.Render();
                ProbePreviewTarget(cam, rt);
            }
            catch (Exception e)
            {
                if (!_previewErrorLogged)
                {
                    _previewErrorLogged = true;
                    Logger.LogWarning("Preview render failed: " + e.Message);
                }
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (fill != null)
                {
                    fill.enabled = false;
                }
                RenderSettings.fog = fog;
                for (int i = 0; i < count; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null)
                    {
                        continue;
                    }
                    r.gameObject.layer = _previewLayers[i];
                    r.shadowCastingMode = _previewShadows[i];
                    if (standalone)
                    {
                        r.enabled = false;
                    }
                }
            }
        }

        private int _previewProbeLeft = 2;

        /// <summary>Reads one pixel of the preview target a couple of times and logs
        /// it, so a transparent clear can be told apart from a white one.</summary>
        private void ProbePreviewTarget(Camera cam, RenderTexture rt)
        {
            if (_previewProbeLeft <= 0 || rt == null || !rt.IsCreated())
            {
                return;
            }
            _previewProbeLeft--;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                int w = rt.width;
                int h = rt.height;
                // Corners say what the background really is; the centre says whether
                // the model itself is blown out white. Both are needed to tell the
                // "white rectangle" symptom apart.
                int[] xs = { 2, w - 3, w / 2, 2, w - 3 };
                int[] ys = { 2, 2, h / 2, h - 3, h - 3 };
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < xs.Length; i++)
                {
                    probe.ReadPixels(new Rect(xs[i], ys[i], 1, 1), 0, 0);
                    probe.Apply();
                    Color c = probe.GetPixel(0, 0);
                    sb.AppendFormat("{0}:({1:F2},{2:F2},{3:F2},{4:F2}) ", i,
                        c.r, c.g, c.b, c.a);
                }
                Logger.LogInfo(string.Format(
                    "Preview probe: {0}clear={1} bg={2} fmt={3} size={4}x{5} srgb={6}",
                    sb.ToString(), cam.clearFlags, cam.backgroundColor, rt.format, w, h,
                    rt.sRGB));
            }
            catch (Exception e)
            {
                Logger.LogWarning("Preview probe failed: " + e.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(probe);
            }
        }

        private bool _previewErrorLogged;

        public void Tick()
        {
            if (!_loggedUpdate)
            {
                _loggedUpdate = true;
                Logger.LogInfo("Update is running. data=" + (_data != null));
            }

            // Finish any background texture loads. This happens before the model
            // build so LoadTexture can take the decoded textures instead of
            // decoding them synchronously.
            PumpTexturePrefetches();

            // The preview objects live in the scene; a scene load destroys them but
            // leaves the materials and textures cached here. Free them. A preview
            // that is still parsing/prefetching has no root yet but must not be
            // cancelled here.
            if (_previewRoot == null && _previewModelIndex != -1 && _pendingPreview == null)
            {
                ReleasePreviewModel();
            }

            // F2 opens the console; V opens the PDA model page. Checked before the
            // data guard so a broken model can be swapped out without restarting.
            try
            {
                MmsConsole.EnsureHost();
                if (!MmsConsole.IsCapturing)
                {
                    if (Input.GetKeyDown(_consoleKeyCode))
                    {
                        MmsConsole.Toggle();
                    }
                    else if (!MmsConsole.IsOpen && Input.GetKeyDown(_menuKeyCode))
                    {
                        OpenModelTab();
                    }
                }
            }
            catch (Exception e)
            {
                if (!_pdaKeyErrorLogged)
                {
                    _pdaKeyErrorLogged = true;
                    Logger.LogWarning("Could not open the MMS console or PDA model page: "
                        + e.Message);
                }
            }

            // Master switch off: the game's own character must be visible again and
            // nothing of the model may run (no build attempts, no physics).
            if (!MasterEnabled)
            {
                if (Time.unscaledTime >= _nextBodyCheck)
                {
                    _nextBodyCheck = Time.unscaledTime + 0.5f;
                    RestoreGameBody();
                }
                return;
            }

            UpdatePendingPreview();

            // Idle / cinematic state, seaglide thrust and the head-follow fade.
            // Runs before the facing update so both agree about the pose.
            UpdatePoseState(Time.deltaTime);

            // Shoulder view: decide where the model is heading this frame. Runs
            // once per frame, before either retarget pass.
            UpdateModelFacing(Time.deltaTime);

            if (_verbose.Value && Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + 5f;
                bool hasPlayer = false;
                try
                {
                    hasPlayer = Player.main != null;
                }
                catch (Exception e)
                {
                    Logger.LogError("Player.main access failed: " + e.GetType().Name + ": " + e.Message);
                }
                Logger.LogInfo("Heartbeat t=" + Time.time.ToString("F1")
                    + " data=" + (_data != null) + " player=" + hasPlayer);
            }

            if (_data == null)
            {
                return;
            }

            if (Player.main == null)
            {
                if (Time.unscaledTime >= _nextDiag)
                {
                    _nextDiag = Time.unscaledTime + 10f;
                    Logger.LogInfo("Waiting for Player.main (Unity time " + Time.time.ToString("F1") + ")");
                }
                return;
            }

            if (!_loggedPlayerFound)
            {
                _loggedPlayerFound = true;
                Logger.LogInfo("Player found: " + Player.main.name + " at " + Player.main.transform.position);
                DumpHierarchy(Player.main.transform, 0, 3);
            }

            Transform pv = null;
            try
            {
                Transform root = Player.main.transform;
                pv = root.Find("body/player_view");
                if (pv == null)
                {
                    pv = FindDeep(root, "player_view");
                }
            }
            catch (Exception e)
            {
                if (!_missingViewLogged)
                {
                    _missingViewLogged = true;
                    Logger.LogError("Error while looking for player_view: " + e);
                }
            }

            if (pv == null)
            {
                if (!_missingViewLogged)
                {
                    _missingViewLogged = true;
                    Logger.LogWarning("player_view not found; player hierarchy follows:");
                    DumpHierarchy(Player.main.transform, 0, 4);
                }
                _built = false;
                return;
            }

            if (!_built || _container == null || _container.parent != pv)
            {
                if (Time.unscaledTime < _nextTry)
                {
                    return;
                }
                _nextTry = Time.unscaledTime + 0.5f;
                TryBuild(pv);
            }
        }

        private void TryBuild(Transform pv)
        {
            try
            {
                if (!_loggedTry)
                {
                    _loggedTry = true;
                    Logger.LogInfo("TryBuild under " + pv.name + " (" + pv.GetType().Name + ")");
                    DumpHierarchy(pv, 0, 3);
                    DumpComponents(pv, 0, 6, "player_view");
                    Animator[] anims = Player.main.GetComponentsInChildren<Animator>(true);
                    Logger.LogInfo("Animators under Player: " + anims.Length);
                    for (int ai = 0; ai < anims.Length; ai++)
                    {
                        Animator a = anims[ai];
                        Logger.LogInfo("Animator " + ai + ": path=" + GetPath(a.transform, pv)
                            + " enabled=" + a.enabled + " active=" + a.gameObject.activeInHierarchy
                            + " ctrl=" + (a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "none")
                            + " avatar=" + (a.avatar != null ? a.avatar.name : "none")
                            + " speed=" + a.speed);
                    }
                }
                Transform male = FindDeep(pv, "male_geo");
                if (male == null)
                {
                    male = FindDeep(pv, "female_geo");
                }

                if (male == null)
                {
                    if (!_missingLogged)
                    {
                        Logger.LogWarning("Player body object (male_geo/female_geo) not found yet.");
                        _missingLogged = true;
                    }
                    return;
                }
                _missingLogged = false;

                if (_container != null)
                {
                    Destroy(_container.gameObject);
                    _container = null;
                }
                _built = false;

                SourceMap sources = GatherSources(pv, male);
                if (sources == null)
                {
                    Logger.LogWarning("No usable suit SkinnedMeshRenderer with bindposes found yet.");
                    return;
                }

                System.Diagnostics.Stopwatch buildWatch =
                    System.Diagnostics.Stopwatch.StartNew();
                EnsureMaterials(sources);
                if (_materials == null)
                {
                    return;
                }
                // Materials may come from the preview (donated, so EnsureMaterials
                // did nothing) or from an older build of this model. Rewrite the
                // stored per-material glow selection once here so the highlight
                // settings in the config are live on the very first frame, not only
                // after the page's 发光 checkbox is toggled.
                ApplyGlow();
                long materialMs = buildWatch.ElapsedMilliseconds;

                BuildModel(pv, male, sources);
                _built = true;
                _nextTry = 0f;
                Logger.LogInfo("Miku player model attached (" + materialMs
                    + " ms materials, " + buildWatch.ElapsedMilliseconds
                    + " ms total).");
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to build the Miku player model: " + e);
                if (_container != null)
                {
                    Destroy(_container.gameObject);
                    _container = null;
                }
                _built = false;
            }
        }

        // ------------------------------------------------------------------
        // source skeleton
        // ------------------------------------------------------------------

        private sealed class SourceMap
        {
            public readonly Dictionary<string, SourceBone> Bones =
                new Dictionary<string, SourceBone>();
            public SourceBone Head;
            public SourceBone Hips;
            public readonly List<SkinnedMeshRenderer> SuitRenderers =
                new List<SkinnedMeshRenderer>();
            public readonly List<Renderer> GameBodyRenderers = new List<Renderer>();
        }

        private SourceMap GatherSources(Transform pv, Transform male)
        {
            SourceMap map = new SourceMap();
            male.GetComponentsInChildren(true, map.GameBodyRenderers);

            SkinnedMeshRenderer[] all = pv.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            List<SkinnedMeshRenderer> valid = new List<SkinnedMeshRenderer>();

            for (int i = 0; i < all.Length; i++)
            {
                SkinnedMeshRenderer r = all[i];
                if (r == null)
                {
                    continue;
                }

                // never treat our own replacement meshes as the source
                if (r.name == "Miku_Body" || r.name == "Miku_Head")
                {
                    continue;
                }
                if (r.rootBone != null && r.rootBone.name.StartsWith("miku_", StringComparison.Ordinal))
                {
                    continue;
                }

                Mesh mesh = r.sharedMesh;
                if (mesh == null || r.bones == null || r.bones.Length == 0)
                {
                    continue;
                }

                Matrix4x4[] bindposes = mesh.bindposes;
                if (bindposes == null || bindposes.Length != r.bones.Length)
                {
                    continue;
                }

                valid.Add(r);
                map.SuitRenderers.Add(r);
            }

            if (valid.Count == 0)
            {
                return null;
            }

            // Prefer the hierarchy with the most bones so finger/head bones are covered.
            valid.Sort((a, b) => b.bones.Length.CompareTo(a.bones.Length));

            for (int ri = 0; ri < valid.Count; ri++)
            {
                SkinnedMeshRenderer r = valid[ri];
                Matrix4x4[] bindposes = r.sharedMesh.bindposes;
                for (int i = 0; i < r.bones.Length; i++)
                {
                    Transform bt = r.bones[i];
                    if (bt == null)
                    {
                        continue;
                    }

                    string name = bt.name;
                    if (map.Bones.ContainsKey(name))
                    {
                        continue;
                    }

                    Matrix4x4 inv = bindposes[i].inverse;
                    SourceBone sb = new SourceBone();
                    sb.Bone = bt;
                    sb.SmrTransform = r.transform;
                    sb.RestLocalRot = inv.rotation;
                    sb.RestLocalPos = new Vector3(inv.m03, inv.m13, inv.m23);
                    map.Bones[name] = sb;
                }
            }

            // The game's arm chain is clav_L -> clav_L_aim -> shoulder_L, and the
            // shoulder lift (ArmsController's AimIK) lives on clav_L_aim. No suit
            // mesh references that helper bone, so it is not in the bind poses and
            // the loop above never sees it. Register it next to its clavicle so the
            // 肩C mapping in BaseMap actually resolves; without this the model's
            // clav/shoulder stayed at the bind pose while the IK folded the arm at
            // the elbow.
            AddAimSource(map, "clav_L", "clav_L_aim");
            AddAimSource(map, "clav_R", "clav_R_aim");

            SourceBone tmp;
            if (map.Bones.TryGetValue("head_rig", out tmp))
            {
                map.Head = tmp;
            }
            if (map.Bones.TryGetValue("hips", out tmp))
            {
                map.Hips = tmp;
            }

            if (_verbose.Value)
            {
                Logger.LogInfo(string.Format("Source skeleton: {0} suit renderers, {1} bones, largest {2} bones ({3})",
                    valid.Count, map.Bones.Count, valid[0].bones.Length, valid[0].name));

                List<string> names = new List<string>(map.Bones.Keys);
                names.Sort(StringComparer.Ordinal);
                Logger.LogInfo("Source bone names: " + string.Join(", ", names.ToArray()));
            }

            return map;
        }

        // Rest local rotation of the game's clav_*_aim helper bones in the
        // player_view prefab. The helper sits at the shoulder end of the clavicle
        // and is what the game's AimIK rotates to raise/aim the arm; both sides
        // use the same local offset (~25 degrees).
        private static readonly Quaternion AimBoneRestLocalRot =
            new Quaternion(-0.0353f, 0.2199f, 0.0047f, 0.9749f);

        /// <summary>
        /// Registers one of the game's clav_*_aim helper bones as a retarget source.
        /// The helpers are not referenced by any suit SkinnedMeshRenderer, so they
        /// are not in the bind poses GatherSources reads. Rebuild the bind rotation
        /// from the parent clavicle's bind rotation and the helper's rest local
        /// rotation (both fixed in the player_view prefab).
        /// </summary>
        private void AddAimSource(SourceMap map, string clavName, string aimName)
        {
            if (map == null || map.Bones.ContainsKey(aimName))
            {
                return;
            }

            SourceBone clav;
            if (!map.Bones.TryGetValue(clavName, out clav) || clav.Bone == null)
            {
                return;
            }

            Transform aim = FindDescendant(clav.Bone, aimName);
            if (_verbose.Value)
            {
                Logger.LogInfo("Aim source " + aimName + ": " + (aim != null ? "found" : "MISSING"));
            }
            if (aim == null)
            {
                return;
            }

            SourceBone sb = new SourceBone();
            sb.Bone = aim;
            sb.SmrTransform = clav.SmrTransform;
            sb.RestLocalRot = clav.RestLocalRot * AimBoneRestLocalRot;
            // RestLocalPos is only read for the shoulder/elbow arm-pose bake.
            sb.RestLocalPos = clav.RestLocalPos;
            map.Bones[aimName] = sb;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }
            if (root.name == name)
            {
                return root;
            }
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDescendant(root.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // materials / textures
        // ------------------------------------------------------------------

        private void EnsureMaterials(SourceMap sources)
        {
            if (_materials != null)
            {
                return;
            }

            Material baseMat = null;
            for (int i = 0; i < sources.SuitRenderers.Count; i++)
            {
                Material m = sources.SuitRenderers[i].sharedMaterial;
                if (m != null)
                {
                    baseMat = m;
                    break;
                }
            }

            Shader standard = Shader.Find("Standard");
            Shader chosen = null;

            if (_useGameShader.Value && baseMat != null)
            {
                chosen = baseMat.shader;
            }
            if (chosen == null)
            {
                chosen = standard;
            }
            if (chosen == null && baseMat != null)
            {
                chosen = baseMat.shader;
            }
            if (chosen == null)
            {
                chosen = Shader.Find("Legacy Shaders/Diffuse");
            }
            if (chosen == null)
            {
                Logger.LogError("No usable shader found (Standard, game suit, Diffuse all missing).");
                return;
            }

            _shader = chosen;
            _suitBaseMat = baseMat;
            Logger.LogInfo(string.Format("Shader: {0} (Standard found={1}, game suit shader={2})",
                chosen.name, standard != null, baseMat != null ? baseMat.shader.name : "n/a"));

            if (_verbose.Value)
            {
                LogShaderTextures(chosen);
                DumpMaterial(baseMat);
                DumpCutoutSamples();
            }

            _blendFlags = GetBlendFlags(_currentModelId ?? "", _data);
            // IsGlowMaterial reads the per-model selection; materials are created
            // once per model, so remember whose materials these are.
            _materialModelId = _currentModelId ?? "";
            _materials = new List<Material>(_data.Materials.Count);
            for (int i = 0; i < _data.Materials.Count; i++)
            {
                bool blend = _blendFlags != null && i < _blendFlags.Length && _blendFlags[i];
                _materials.Add(CreateMaterial(_data.Materials[i], blend));
            }
            _blendStateLogged = false;
            LogBlendState(_materials, _blendFlags, _materialModelId);
        }

        /// <summary>
        /// Logs the render state of the first alpha-blended material of a model.
        /// Classification says what the material should be; this says what the
        /// engine actually got, which is the other half of "the soft texture is
        /// drawn as black-and-white speckle".
        /// </summary>
        private void LogBlendState(List<Material> mats, bool[] flags, string modelId)
        {
            if (_blendStateLogged || mats == null || flags == null)
            {
                return;
            }
            _blendStateLogged = true;
            bool gotBlend = false;
            bool gotCutout = false;
            for (int i = 0; i < mats.Count && i < flags.Length; i++)
            {
                Material m = mats[i];
                if (m == null)
                {
                    continue;
                }
                bool blended = flags[i];
                if (blended ? gotBlend : gotCutout)
                {
                    continue;
                }
                if (blended)
                {
                    gotBlend = true;
                }
                else
                {
                    gotCutout = true;
                }
                Logger.LogInfo(string.Format(
                    "Blend state ({0}){1}: '{2}' queue={3} src={4} dst={5} zwrite={6} cutOff={7} dither={8} keys=[{9}]",
                    modelId, blended ? "" : " cutout", m.name, m.renderQueue,
                    m.HasProperty("_SrcBlend") ? m.GetFloat("_SrcBlend") : -1f,
                    m.HasProperty("_DstBlend") ? m.GetFloat("_DstBlend") : -1f,
                    m.HasProperty("_ZWrite") ? m.GetFloat("_ZWrite") : -1f,
                    m.HasProperty("_EnableCutOff") ? m.GetFloat("_EnableCutOff") : -1f,
                    m.HasProperty("_EnableDitherAlpha") ? m.GetFloat("_EnableDitherAlpha") : -1f,
                    string.Join(" ", m.shaderKeywords)));
                if (gotBlend && gotCutout)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Reads an alpha-downsampled copy of a texture. Textures decoded by the
        /// synchronous path carry a mip chain, so a small level can be read directly.
        /// Textures decoded by UnityWebRequestTexture have no mips; their level 0 is
        /// averaged down on the CPU (one temporary allocation per texture, and only
        /// the small copy is kept). A GPU blit into a render target is the last
        /// resort for textures whose pixels cannot be read on the CPU at all.
        /// </summary>
        private static Color32[] GetAlphaPixels(Texture2D tex, int maxSize,
            out int width, out int height)
        {
            width = 0;
            height = 0;
            if (tex == null || !tex.isReadable)
            {
                return null;
            }
            int mip = 0;
            int mw = tex.width;
            int mh = tex.height;
            while ((mw > maxSize || mh > maxSize) && mip < 12)
            {
                mw = Mathf.Max(1, mw / 2);
                mh = Mathf.Max(1, mh / 2);
                mip++;
            }
            if (tex.mipmapCount > 1)
            {
                for (int level = mip; level >= 0; level--)
                {
                    try
                    {
                        Color32[] candidate = tex.GetPixels32(level);
                        int cw = Mathf.Max(1, tex.width >> level);
                        int ch = Mathf.Max(1, tex.height >> level);
                        if (candidate != null && candidate.Length >= cw * ch)
                        {
                            width = cw;
                            height = ch;
                            return candidate;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            // No mip chain: the background image loader hands back a level-0-only
            // texture. Average that level down on the CPU into the same kind of
            // filtered copy a mip level gives, so a texture classifies the same
            // whether it arrived through the preview loader or the decoder. Every
            // transparency decision is made from these pixels, and the blit that
            // used to produce this copy could hand back a plausible but wrong
            // (fully opaque) alpha channel.
            //
            // The averaging matters, not just the size. q3.png of the 10th model is
            // 70% fully transparent and 26% semi-transparent, and those two groups
            // sit on separate parts of the sheet: picking one texel in 32 read 73%
            // transparent / 23% semi, which misses the "semi-transparent area is
            // larger than the opaque one" test below and downgrades the panels to a
            // hard cutout. The same area averaged reads 33% semi and classifies
            // correctly. The small copy is what the caller keeps, so the cost is one
            // temporary allocation per texture.
            try
            {
                Color32[] full = tex.GetPixels32(0);
                int tw2 = tex.width;
                int th2 = tex.height;
                if (full != null && full.Length >= tw2 * th2)
                {
                    int stride = Mathf.Max(1, Mathf.CeilToInt(
                        (float)Mathf.Max(tw2, th2) / Mathf.Max(1, maxSize)));
                    int w = Mathf.Max(1, tw2 / stride);
                    int h = Mathf.Max(1, th2 / stride);
                    Color32[] small = new Color32[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        int sy0 = y * stride;
                        int sy1 = Mathf.Min(th2, sy0 + stride);
                        int orow = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int sx0 = x * stride;
                            int sx1 = Mathf.Min(tw2, sx0 + stride);
                            int r = 0;
                            int g = 0;
                            int b = 0;
                            int a = 0;
                            int n = 0;
                            for (int sy = sy0; sy < sy1; sy++)
                            {
                                int row = sy * tw2;
                                for (int sx = sx0; sx < sx1; sx++)
                                {
                                    Color32 c = full[row + sx];
                                    r += c.r;
                                    g += c.g;
                                    b += c.b;
                                    a += c.a;
                                    n++;
                                }
                            }
                            if (n == 0)
                            {
                                n = 1;
                            }
                            small[orow + x] = new Color32(
                                (byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                        }
                    }
                    width = w;
                    height = h;
                    return small;
                }
            }
            catch (Exception)
            {
            }
            // Last resort for textures whose pixels cannot be read on the CPU.
            RenderTexture rt = null;
            Texture2D probe = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                int w = Mathf.Max(1, mw);
                int h = Mathf.Max(1, mh);
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                probe = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                probe.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                probe.Apply(false, false);
                Color32[] px = probe.GetPixels32();
                if (px != null && px.Length >= w * h && !SameAlpha(px))
                {
                    width = w;
                    height = h;
                    return px;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                RenderTexture.active = previous;
                if (probe != null)
                {
                    Destroy(probe);
                }
                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            return null;
        }

        /// <summary>
        /// True when every pixel carries the same alpha. A real sheet is never that
        /// flat, while a blit that dropped the alpha channel comes back exactly like
        /// this, so the caller can tell "opaque" from "the read failed".
        /// </summary>
        private static bool SameAlpha(Color32[] px)
        {
            if (px == null || px.Length == 0)
            {
                return true;
            }
            byte a = px[0].a;
            for (int i = 1; i < px.Length; i++)
            {
                if (px[i].a != a)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns the translucent-material classification for a model, reusing the
        /// result of an earlier scan. The scan reads texture pixels, so repeating it
        /// on every switch back to the same model was pure repeated cost.
        /// </summary>
        private bool[] GetBlendFlags(string modelId, ModelData data)
        {
            if (data == null)
            {
                return null;
            }
            string key = modelId ?? "";
            bool[] cached;
            if (_blendCache.TryGetValue(key, out cached) && cached != null &&
                cached.Length == data.Materials.Count)
            {
                return cached;
            }
            bool[] blend = ClassifyTranslucentMaterials();
            if (!_blendScanFailed)
            {
                _blendCache[key] = blend;
            }
            return blend;
        }

        /// <summary>
        /// Flags the materials whose own texture area is painted semi-transparent, so
        /// they can be drawn with alpha blending instead of a 0.5 cutout. MMD models
        /// put transparency in the alpha channel of the diffuse map, and cutting that
        /// at 0.5 turned every soft sleeve or skirt panel into a ragged, noisy hole
        /// pattern. The alpha is sampled at the UVs of each material's own triangles:
        /// atlases pack translucent cloth right next to fully opaque parts, so a
        /// texture-wide average would make the opaque parts translucent as well.
        /// </summary>
        private bool[] ClassifyTranslucentMaterials()
        {
            int count = _data.Materials.Count;
            bool[] blend = new bool[count];
            _blendScanFailed = false;
            try
            {
                if (count == 0 || _data.Meshes.Count == 0)
                {
                    return blend;
                }
                int[][] hist = new int[count][];
                int[] taken = new int[count];
                for (int i = 0; i < count; i++)
                {
                    hist[i] = new int[256];
                }
                // Alpha is read from a mip level of at most 512 px: a copy of a 4K
                // texture would cost 64 MB and a visible hitch on every model switch,
                // while the downsampled alpha says exactly the same thing.
                Dictionary<Texture2D, Color32[]> levelPixels = new Dictionary<Texture2D, Color32[]>();
                Dictionary<Texture2D, Vector2Int> levelSize = new Dictionary<Texture2D, Vector2Int>();

                for (int mi = 0; mi < _data.Meshes.Count; mi++)
                {
                    MeshData md = _data.Meshes[mi];
                    if (md.Uvs == null || md.Indices == null || md.SubMeshMaterials == null)
                    {
                        continue;
                    }
                    int subs = Mathf.Min(md.Indices.Length, md.SubMeshMaterials.Length);
                    for (int s = 0; s < subs; s++)
                    {
                        int mat = md.SubMeshMaterials[s];
                        if (mat < 0 || mat >= count)
                        {
                            continue;
                        }
                        int[] ind = md.Indices[s];
                        if (ind == null || ind.Length < 3)
                        {
                            continue;
                        }
                        Texture2D tex = LoadTexture(_data.Materials[mat].Texture);
                        if (tex == null || !tex.isReadable)
                        {
                            continue;
                        }
                        Color32[] pixels;
                        Vector2Int size;
                        if (!levelPixels.TryGetValue(tex, out pixels))
                        {
                            int pw;
                            int ph;
                            pixels = GetAlphaPixels(tex, 512, out pw, out ph);
                            if (pixels == null)
                            {
                                continue;
                            }
                            size = new Vector2Int(pw, ph);
                            levelPixels[tex] = pixels;
                            levelSize[tex] = size;
                        }
                        else
                        {
                            size = levelSize[tex];
                        }
                        int tw = size.x;
                        int th = size.y;
                        if (tw <= 0 || th <= 0 || pixels.Length < tw * th)
                        {
                            continue;
                        }
                        int[] h = hist[mat];
                        int tris = ind.Length / 3;
                        // A few thousand triangles per submesh describe the material
                        // well enough; scanning every triangle of a 4K model is not
                        // worth the load time.
                        int step = tris > 2000 ? tris / 2000 : 1;
                        for (int t = 0; t < tris; t += step)
                        {
                            int i0 = ind[t * 3];
                            int i1 = ind[t * 3 + 1];
                            int i2 = ind[t * 3 + 2];
                            if (i0 < 0 || i1 < 0 || i2 < 0 ||
                                i0 >= md.Uvs.Length || i1 >= md.Uvs.Length || i2 >= md.Uvs.Length)
                            {
                                continue;
                            }
                            Vector2 a0 = md.Uvs[i0];
                            Vector2 a1 = md.Uvs[i1];
                            Vector2 a2 = md.Uvs[i2];
                            Vector2 c = (a0 + a1 + a2) / 3f;
                            for (int k = 0; k < 4; k++)
                            {
                                Vector2 uv = k == 0 ? c : (k == 1 ? a0 : (k == 2 ? a1 : a2));
                                int ix = Mathf.Clamp((int)((uv.x - Mathf.Floor(uv.x)) * tw), 0, tw - 1);
                                int iy = Mathf.Clamp((int)((uv.y - Mathf.Floor(uv.y)) * th), 0, th - 1);
                                h[pixels[iy * tw + ix].a]++;
                                taken[mat]++;
                            }
                        }
                    }
                }

                List<string> blended = new List<string>();
                List<string> fallback = new List<string>();
                // Per-texture cache: the same sheet is shared by several materials.
                Dictionary<string, bool> softTexture = new Dictionary<string, bool>();
                for (int i = 0; i < count; i++)
                {
                    string file = _data.Materials[i].Texture;
                    bool soft = false;
                    if (!string.IsNullOrEmpty(file))
                    {
                        if (!softTexture.TryGetValue(file, out soft))
                        {
                            soft = TextureMostlyTranslucent(file);
                            softTexture[file] = soft;
                        }
                    }
                    if (taken[i] < 64)
                    {
                        // A decal or a hair card can be a handful of triangles, which
                        // says nothing about the texture behind it. Defaulting those to
                        // cutout turned soft-alpha sheets into hard-edged, noisy
                        // silhouettes. Fall back to the texture-wide alpha mix.
                        if (soft)
                        {
                            blend[i] = true;
                            blended.Add(_data.Materials[i].Name);
                        }
                        // Logged at info level: these are the materials that only the
                        // sheet-wide test can classify, and getting that test wrong is
                        // what makes a translucent panel render as black-and-white
                        // speckle. One line per model keeps the answer in the log.
                        fallback.Add(string.Format("{0}[{1} samples={2} soft={3} -> {4}]",
                            _data.Materials[i].Name, file, taken[i], soft,
                            blend[i] ? "blend" : "cutout"));
                        continue;
                    }
                    int partial = 0;
                    int peak = 0;
                    int peakCount = -1;
                    for (int a = 0; a < 256; a++)
                    {
                        if (a >= 16 && a <= 240)
                        {
                            partial += hist[i][a];
                            // The mode has to be taken inside the translucent band.
                            // Over the whole range the winner is often alpha=0 - the
                            // unused corners of an atlas - and that made a material
                            // with a wide semi-transparent area look like a cutout
                            // again (the skirt panels on the 10th model).
                            if (hist[i][a] > peakCount)
                            {
                                peakCount = hist[i][a];
                                peak = a;
                            }
                        }
                    }
                    float frac = (float)partial / taken[i];
                    if (_verbose.Value)
                    {
                        Logger.LogInfo(string.Format(
                            "Translucency: {0} frac={1:F0}% peak={2} soft={3} ({4})",
                            _data.Materials[i].Name, frac * 100f, peak, soft,
                            frac >= 0.30f && peak >= 64 || soft ? "blend" : "cutout"));
                    }
                    // The UV sample can miss narrow alpha bands: the 10th model's
                    // q203/q301 panels landed almost entirely on opaque texels even
                    // though their sheets are soft-alpha. Trust the sheet as well.
                    blend[i] = (frac >= 0.30f && peak >= 64) || soft;
                    if (blend[i])
                    {
                        blended.Add(_data.Materials[i].Name);
                    }
                }
                if (blended.Count > 0)
                {
                    Logger.LogInfo("Translucent materials (alpha blended): " + string.Join(", ", blended.ToArray()));
                }
                else
                {
                    Logger.LogInfo("Translucent materials: none.");
                }
                if (fallback.Count > 0)
                {
                    Logger.LogInfo("Alpha fallback (too few triangles): "
                        + string.Join(", ", fallback.ToArray()));
                }
            }
            catch (Exception e)
            {
                // Worst case every material keeps the old cutout behaviour. The
                // result is not cached, so the next switch to this model retries.
                _blendScanFailed = true;
                Logger.LogWarning("Translucency scan failed: " + e.Message);
            }
            return blend;
        }

        /// <summary>
        /// Texture-wide alpha mix, used when a material has too few triangles to
        /// classify. Textures that are mostly soft alpha (cloth, hair cards, decals)
        /// must be blended, not cut out.
        /// </summary>
        private bool TextureMostlyTranslucent(string file)
        {
            try
            {
                Texture2D tex = LoadTexture(file);
                if (tex == null)
                {
                    // Nothing to sample: the material draws with Unity's default
                    // white texture, so blending and cutout look the same.
                    return false;
                }
                if (!tex.isReadable)
                {
                    // No alpha information at all. Drawing a soft-alpha sheet as a
                    // 0.5 cutout is exactly the failure seen as black-and-white
                    // speckle, so assume the sheet is translucent.
                    Logger.LogWarning("Alpha unreadable for '" + file
                        + "': drawing this material as translucent.");
                    return true;
                }
                int mw;
                int mh;
                Color32[] px = GetAlphaPixels(tex, 128, out mw, out mh);
                if (px == null)
                {
                    return false;
                }
                int opaque = 0;
                int partial = 0;
                int total = 0;
                for (int y = 0; y < mh; y += 2)
                {
                    int row = y * mw;
                    for (int x = 0; x < mw; x += 2)
                    {
                        int a = px[row + x].a;
                        total++;
                        if (a > 240)
                        {
                            opaque++;
                        }
                        else if (a > 15)
                        {
                            partial++;
                        }
                    }
                }
                // The sheet is soft when most of it is not fully opaque and its
                // semi-transparent area is larger than its opaque area. The old
                // "partial > 25% of the sheet" test sat right on the line for q3.png
                // of the 10th model (25-33% depending on how the copy was made), so a
                // harmless change in sampling flipped a translucent sheet to a hard
                // cutout. Comparing the two areas against each other is what the
                // decision actually means, and it does not sit on a boundary.
                return total > 0 && opaque * 4 < total &&
                       partial * 10 >= total && partial > opaque;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void LogShaderTextures(Shader shader)
        {
            try
            {
                int count = shader.GetPropertyCount();
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(shader.GetPropertyName(i));
                    }
                }
                Logger.LogInfo("Shader texture properties: " + sb);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Diag helper: prints every property of the game's suit material and its shader so
        /// the MarmosetUBER setup can be mirrored when UseGameSuitShader is enabled.
        /// </summary>
        private void DumpMaterial(Material mat)
        {
            if (mat == null)
            {
                return;
            }
            try
            {
                Shader shader = mat.shader;
                Logger.LogInfo(string.Format("Suit material '{0}' shader='{1}' keywords='{2}'",
                    mat.name, shader != null ? shader.name : "null",
                    mat.shaderKeywords != null ? string.Join(" ", mat.shaderKeywords) : ""));
                if (shader == null)
                {
                    return;
                }
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    string pn = shader.GetPropertyName(i);
                    ShaderPropertyType pt = shader.GetPropertyType(i);
                    string value;
                    switch (pt)
                    {
                        case ShaderPropertyType.Texture:
                            Texture t = mat.GetTexture(pn);
                            value = t == null ? "null" : t.name + " (" + t.width + "x" + t.height + ")";
                            break;
                        case ShaderPropertyType.Color:
                            value = mat.GetColor(pn).ToString();
                            break;
                        case ShaderPropertyType.Vector:
                            value = mat.GetVector(pn).ToString();
                            break;
                        default:
                            value = mat.GetFloat(pn).ToString("0.###");
                            break;
                    }
                    Logger.LogInfo(string.Format("  prop {0,-28} {1,-8} = {2}", pn, pt, value));
                }
            }
            catch (Exception e)
            {
                Logger.LogInfo("DumpMaterial failed: " + e.Message);
            }
        }

        /// <summary>
        /// Clones the player's suit material and swaps in the Miku texture. The game shader
        /// already handles Subnautica's ambient/IBL lighting, and its glow channel gives a
        /// working emission fill (the stripped built-in Standard shader has no _EMISSION
        /// variant in this build, so a Standard emission fill renders nothing).
        /// </summary>
        /// <summary>
        /// True when the material is listed in GlowMaterials (comma separated, names
        /// compared case-insensitively).
        /// </summary>
        private bool IsGlowMaterial(string name)
        {
            return IsGlowMaterial(name, _materialModelId);
        }

        private bool IsGlowMaterial(string name, string modelId)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            HashSet<string> set = GlowSetFor(modelId);
            return set != null && set.Contains(name.Trim());
        }

        private Material CreateGameMaterial(MaterialData md, bool isDecal, bool blend)
        {
            Material m = new Material(_suitBaseMat);
            m.name = "Miku_" + md.Name;

            Texture2D tex = LoadTexture(md.Texture);
            if (tex != null)
            {
                if (m.HasProperty("_MainTex"))
                {
                    m.SetTexture("_MainTex", tex);
                }
                if (m.HasProperty("_BaseMap"))
                {
                    m.SetTexture("_BaseMap", tex);
                }
            }

            if (m.HasProperty("_Color"))
            {
                m.SetColor("_Color", Color.white);
            }
            // The suit's normal/spec maps are atlas-specific; drop them so they do not
            // draw suit panel lines across the character.
            if (m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", null);
            }
            if (m.HasProperty("_SpecTex"))
            {
                m.SetTexture("_SpecTex", null);
            }
            if (m.HasProperty("_SIGMap"))
            {
                m.SetTexture("_SIGMap", null);
            }
            if (m.HasProperty("_SpecInt"))
            {
                m.SetFloat("_SpecInt", 0f);
            }
            if (m.HasProperty("_Shininess"))
            {
                m.SetFloat("_Shininess", 0f);
            }
            if (m.HasProperty("_Fresnel"))
            {
                m.SetFloat("_Fresnel", 0f);
            }
            if (m.HasProperty("_MyCullVariable"))
            {
                m.SetFloat("_MyCullVariable", md.Cull);
            }
            if (m.HasProperty("_EnableMainMaps"))
            {
                m.SetFloat("_EnableMainMaps", 1f);
            }
            if (m.HasProperty("_EnableLighting"))
            {
                m.SetFloat("_EnableLighting", 1f);
            }
            if (m.HasProperty("_EnableVrFadeOut"))
            {
                m.SetFloat("_EnableVrFadeOut", 0f);
            }
            // Everything below is copied from the suit's own material. Left alone it
            // reads suit atlases with our UVs: the dither noise punches a speckled
            // alpha pattern into the whole character, the detail maps draw panel
            // lines, and the lightmap lookup uses coordinates our meshes do not
            // have. Indoor ambient still works - that arrives through the
            // SkyApplier property block, not through these switches.
            if (m.HasProperty("_EnableDitherAlpha"))
            {
                m.SetFloat("_EnableDitherAlpha", 0f);
            }
            if (m.HasProperty("_EnableDetailMaps"))
            {
                m.SetFloat("_EnableDetailMaps", 0f);
                m.SetTexture("_DetailDiffuseTex", null);
                m.SetTexture("_DetailBumpTex", null);
                m.SetTexture("_DetailSpecTex", null);
            }
            if (m.HasProperty("_NoiseStr"))
            {
                m.SetFloat("_NoiseStr", 0f);
                m.SetTexture("_NoiseTex", null);
            }
            if (m.HasProperty("_EnableLightmap"))
            {
                m.SetFloat("_EnableLightmap", 0f);
            }
            if (m.HasProperty("_Enable3Color"))
            {
                // The three-colour blend is how the dive suit paints its own atlas;
                // our texture is the whole albedo and must not be tinted by it.
                m.SetFloat("_Enable3Color", 0f);
            }
            if (m.HasProperty("_EnableCutOff"))
            {
                m.SetFloat("_EnableCutOff", blend ? 0f : 1f);
                // The float on its own does nothing. The shader's inspector wires the
                // "Alpha Test" toggle to the MARMO_ALPHA_CLIP keyword
                // (MarmoToggle(MARMO_ALPHA_CLIP, null) next to _EnableCutOff in the
                // shader asset), and only that keyword variant clips the fragment.
                // Without it the material samples the texture and writes it whole,
                // alpha and all - so the transparent areas of a sheet like q3.png,
                // whose unused pixels are black-and-white line art, are drawn as
                // solid pixels: the black-and-white speckle. Blended materials take
                // the MARMO_ALPHA variant instead and must not clip.
                if (blend)
                {
                    m.DisableKeyword("MARMO_ALPHA_CLIP");
                }
                else
                {
                    m.EnableKeyword("MARMO_ALPHA_CLIP");
                }
            }
            if (m.HasProperty("_Cutoff"))
            {
                m.SetFloat("_Cutoff", 0.5f);
            }
            if (blend)
            {
                // Translucent cloth: the same blend state MMD draws it with. The
                // MarmosetUBER FORWARDBASE pass takes its blend factors and depth
                // write straight from these properties.
                if (m.HasProperty("_SrcBlend"))
                {
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                }
                if (m.HasProperty("_DstBlend"))
                {
                    m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                }
                if (m.HasProperty("_ZWrite"))
                {
                    m.SetFloat("_ZWrite", 0f);
                }
                m.DisableKeyword("MARMO_ALPHA_CLIP");
                // The blend factors alone are not enough: MarmosetUBER only writes the
                // diffuse alpha to the framebuffer in its MARMO_ALPHA variant. Without
                // the keyword the pass draws with alpha 1 and the cloth stays solid.
                // This is the same keyword the game's own alpha-blended glass uses.
                m.EnableKeyword("MARMO_ALPHA");
                m.SetOverrideTag("RenderType", "Transparent");
            }

            if (isDecal)
            {
                // The "01" sheet stores its glyph only in the alpha channel; its RGB
                // is flat red everywhere. Clip the transparent pixels so only the
                // glyph is drawn. Baking the sheet onto an opaque skin colour (the
                // previous approach) ignored the alpha and left a flat rectangle
                // stuck to the arm, because its hardcoded skin tone did not match
                // the model's shaded skin.
                if (m.HasProperty("_EnableCutOff"))
                {
                    m.SetFloat("_EnableCutOff", 1f);
                }
                if (m.HasProperty("_Cutoff"))
                {
                    m.SetFloat("_Cutoff", 0.35f);
                }
                m.EnableKeyword("MARMO_ALPHA_CLIP");
                m.DisableKeyword("MARMO_ALPHA");
            }

            // Match the body materials exactly so the baked decal background blends in.
            float fill = _emissionFill.Value;
            bool glowMat = IsGlowMaterial(md.Name);
            float highlight = glowMat ? _highlightGlow.Value : 0f;
            if (glowMat)
            {
                Logger.LogInfo("Glow material: " + md.Name + " (texture " + md.Texture + ")");
            }
            if (!_glowTest.Value && tex != null && (fill > 0f || highlight > 0f))
            {
                // Self-illumination: the albedo texture is fed through the glow channel so
                // surfaces facing away from the sun keep their colour instead of going
                // black. Point _Illum straight at the albedo and fold the brightness into
                // _GlowStrength: the shader multiplies the two, so this reproduces the
                // old baked "albedo x (fill + highlight)" map exactly, without allocating,
                // filling and uploading a full-resolution copy of every texture (which
                // cost ~85 MB of VRAM per 4K material and was the main hitch/crash on a
                // model switch).
                // MarmosetUBER only samples _Illum when the MARMO_EMISSION keyword is on,
                // and the suit material usually does not have it. Without this line the
                // whole glow channel (fill and highlights alike) is silently ignored.
                float glowStrength = fill + highlight;
                if (m.HasProperty("_EnableGlow"))
                {
                    m.SetFloat("_EnableGlow", 1f);
                }
                if (m.HasProperty("_Illum"))
                {
                    m.SetTexture("_Illum", tex);
                }
                if (m.HasProperty("_GlowColor"))
                {
                    m.SetColor("_GlowColor", Color.white);
                }
                if (m.HasProperty("_GlowStrength"))
                {
                    m.SetFloat("_GlowStrength", glowStrength);
                }
                if (m.HasProperty("_GlowStrengthNight"))
                {
                    m.SetFloat("_GlowStrengthNight", glowStrength);
                }
                if (m.HasProperty("_GlowUVfromVC"))
                {
                    m.SetFloat("_GlowUVfromVC", 0f);
                }
                if (!_glowLogged)
                {
                    _glowLogged = true;
                    Logger.LogInfo(string.Format(
                        "Glow setup: _Illum {0}, MARMO_EMISSION was {1}, fill={2:F2}, glow={3:F2}, strength={4:F2}",
                        m.HasProperty("_Illum") ? "present" : "missing",
                        m.IsKeywordEnabled("MARMO_EMISSION") ? "on" : "off",
                        fill, highlight, glowStrength));
                }
                m.EnableKeyword("MARMO_EMISSION");
            }
            if (_glowTest.Value)
            {
                if (m.HasProperty("_EnableGlow"))
                {
                    m.SetFloat("_EnableGlow", 1f);
                }
                if (m.HasProperty("_GlowColor"))
                {
                    m.SetColor("_GlowColor", Color.red);
                }
                if (m.HasProperty("_Illum"))
                {
                    m.SetTexture("_Illum", Texture2D.whiteTexture);
                }
                m.EnableKeyword("MARMO_EMISSION");
            }

            if (blend)
            {
                // After every opaque part, and after the suit's own transparent queue,
                // so a translucent sleeve is not sorted against the body behind it.
                m.renderQueue = 3000;
            }
            else
            {
                m.renderQueue = 2450;
            }
            return m;
        }

        /// <summary>
        /// Diag helper: find materials in the loaded scene that use MarmosetUBER's cutout path
        /// so the decal material can copy the correct switch/keyword combination.
        /// </summary>
        private void DumpCutoutSamples()
        {
            try
            {
                Material[] all = Resources.FindObjectsOfTypeAll<Material>();
                Logger.LogInfo("Material scan: " + all.Length + " materials loaded");
                int found = 0;
                for (int i = 0; i < all.Length && found < 12; i++)
                {
                    Material m = all[i];
                    if (m == null || m.shader == null || m.shader.name != "MarmosetUBER")
                    {
                        continue;
                    }
                    if (!m.HasProperty("_Mode"))
                    {
                        continue;
                    }
                    float mode = m.GetFloat("_Mode");
                    float cutoffOn = m.HasProperty("_EnableCutOff") ? m.GetFloat("_EnableCutOff") : 0f;
                    float dither = m.HasProperty("_EnableDitherAlpha") ? m.GetFloat("_EnableDitherAlpha") : 0f;
                    if (mode == 0f && cutoffOn == 0f && dither == 0f)
                    {
                        continue;
                    }
                    found++;
                    Logger.LogInfo(string.Format(
                        "CUTOUT SAMPLE {0}: '{1}' queue={2} mode={3} cutOn={4} cutoff={5} dither={6} keys=[{7}] src={8} dst={9} zw={10}",
                        found, m.name, m.renderQueue, mode, cutoffOn,
                        m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : -1f, dither,
                        string.Join(" ", m.shaderKeywords),
                        m.HasProperty("_SrcBlend") ? m.GetFloat("_SrcBlend") : -1f,
                        m.HasProperty("_DstBlend") ? m.GetFloat("_DstBlend") : -1f,
                        m.HasProperty("_ZWrite") ? m.GetFloat("_ZWrite") : -1f));
                }
                Logger.LogInfo("CUTOUT SAMPLE count=" + found);
            }
            catch (Exception e)
            {
                Logger.LogInfo("DumpCutoutSamples failed: " + e.Message);
            }
        }

        private Material CreateMaterial(MaterialData md, bool blend)
        {
            bool isDecal = md.Name != null && md.Name.IndexOf("No01", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isDecal)
            {
                blend = false;
            }

            if (_useGameShader.Value && _suitBaseMat != null)
            {
                return CreateGameMaterial(md, isDecal, blend);
            }

            // The "01" is a flat sticker that should always read as the texture does; the
            // lit Standard shader turned it into a dark red smear on the arm.
            Shader shader = _shader;
            if (isDecal)
            {
                Shader unlitCutout = Shader.Find("Unlit/Transparent Cutout");
                if (unlitCutout != null)
                {
                    shader = unlitCutout;
                    Logger.LogInfo("Decal material uses shader " + shader.name);
                }
            }

            Material m = new Material(shader);
            m.name = "Miku_" + md.Name;

            try
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                    {
                        string pn = shader.GetPropertyName(i);
                        if (pn != "_MainTex" && pn != "_BaseMap")
                        {
                            m.SetTexture(pn, null);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            Texture2D tex = LoadTexture(md.Texture);
            if (_verbose.Value)
            {
                Logger.LogInfo(string.Format("Material {0}: texture='{1}' loaded={2}{3}",
                    md.Name, md.Texture, tex != null,
                    tex != null ? " " + tex.width + "x" + tex.height : ""));
            }
            if (tex != null)
            {
                if (m.HasProperty("_MainTex"))
                {
                    m.SetTexture("_MainTex", tex);
                }
                if (m.HasProperty("_BaseMap"))
                {
                    m.SetTexture("_BaseMap", tex);
                }
            }

            if (m.HasProperty("_Color"))
            {
                m.SetColor("_Color", Color.white);
            }
            if (m.HasProperty("_BaseColor"))
            {
                m.SetColor("_BaseColor", Color.white);
            }
            if (m.HasProperty("_Glossiness"))
            {
                m.SetFloat("_Glossiness", 0.15f);
            }
            if (m.HasProperty("_Smoothness"))
            {
                m.SetFloat("_Smoothness", 0.15f);
            }
            if (m.HasProperty("_Metallic"))
            {
                m.SetFloat("_Metallic", 0f);
            }
            if (m.HasProperty("_Cull"))
            {
                m.SetFloat("_Cull", md.Cull);
            }
            if (m.HasProperty("_Cutoff"))
            {
                m.SetFloat("_Cutoff", 0.5f);
            }

            if (isDecal)
            {
                // A thin decal hovering millimetres above the arm shadow-acnes badly when
                // the arm is right behind it; let it draw at full albedo instead.
                m.EnableKeyword("_RECEIVE_SHADOWS_OFF");
            }

            // The game's ambient light is nearly black, so a plain Standard surface that is
            // facing away from the sun renders pure black. Fake some ambient by adding a
            // fraction of the albedo back as emission, plus the painted highlights.
            float stdFill = _emissionFill.Value;
            float stdHighlight = IsGlowMaterial(md.Name) ? _highlightGlow.Value : 0f;
            if (!_glowTest.Value && tex != null && (stdFill > 0f || stdHighlight > 0f))
            {
                // Same identity as the game-shader path: emission map x colour, with the
                // albedo as the map and the brightness in the colour, instead of a baked
                // full-resolution copy of the texture.
                if (m.HasProperty("_EmissionMap"))
                {
                    m.SetTexture("_EmissionMap", tex);
                }
                if (m.HasProperty("_EmissionColor"))
                {
                    float stdStrength = stdFill + stdHighlight;
                    m.SetColor("_EmissionColor", new Color(stdStrength, stdStrength, stdStrength));
                }
                m.EnableKeyword("_EMISSION");
            }

            if (_glowTest.Value)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(1f, 0f, 0f) * 2f);
            }

            if (shader.name == "Standard")
            {
                if (blend)
                {
                    m.SetFloat("_Mode", 3f); // Transparent
                    m.SetOverrideTag("RenderType", "Transparent");
                    m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    m.SetInt("_ZWrite", 0);
                    m.DisableKeyword("_ALPHATEST_ON");
                    m.EnableKeyword("_ALPHABLEND_ON");
                    m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    m.renderQueue = 3000;
                }
                else
                {
                    m.SetFloat("_Mode", 1f); // Cutout
                    m.SetOverrideTag("RenderType", "TransparentCutout");
                    m.SetInt("_SrcBlend", (int)BlendMode.One);
                    m.SetInt("_DstBlend", (int)BlendMode.Zero);
                    m.SetInt("_ZWrite", 1);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.DisableKeyword("_ALPHABLEND_ON");
                    m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    m.renderQueue = 2450;
                }
            }
            else if (m.HasProperty("_Cutoff"))
            {
                m.EnableKeyword("_ALPHATEST_ON");
            }

            if (isDecal)
            {
                m.renderQueue = 2500;
            }

            return m;
        }

        /// <summary>
        /// Nudges decal submeshes along their vertex normals. The "01" decal sits only a
        /// couple of millimetres above the upper arm, so once the animated arm deforms the
        /// decal plane pokes through the skin and only jagged fragments are visible.
        /// </summary>
        private Vector3[] OffsetDecals(MeshData md)
        {
            float offset = _decalOffset != null ? _decalOffset.Value : 0f;
            if (offset <= 0f || _materials == null)
            {
                return md.Vertices;
            }

            Vector3[] verts = null;
            bool[] applied = null;
            for (int s = 0; s < md.Indices.Length; s++)
            {
                int mi = md.SubMeshMaterials[s];
                if (mi < 0 || mi >= _materials.Count)
                {
                    continue;
                }
                Material mat = _materials[mi];
                if (mat == null || mat.name.IndexOf("No01", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (verts == null)
                {
                    verts = (Vector3[])md.Vertices.Clone();
                    applied = new bool[verts.Length];
                }
                int[] idx = md.Indices[s];
                for (int i = 0; i < idx.Length; i++)
                {
                    int vi = idx[i];
                    // The same vertex is referenced by every triangle that uses it; applying
                    // the offset per index would push shared vertices several centimetres out
                    // and turn the flat sticker into a bump.
                    if (applied[vi])
                    {
                        continue;
                    }
                    applied[vi] = true;
                    verts[vi] += md.Normals[vi] * offset;
                }
            }
            return verts ?? md.Vertices;
        }

        private string MaterialName(int index)
        {
            if (index < 0 || index >= _data.Materials.Count)
            {
                return string.Empty;
            }
            return _data.Materials[index].Name ?? string.Empty;
        }

        /// <summary>True for materials that look like hair (MMD names them Hair1,
        /// 髪, ツインテール ...). Used to give imported hair a simulated chain.</summary>
        private bool IsHairMaterialName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            // Not just "tail": "Detail" would match it. 髪/髮/发 are the Japanese,
            // traditional and simplified spellings of the same character; the
            // Chinese-authored models name their hair material 头发/发 and were
            // otherwise left with no hair rig at all, which also made the hair
            // invisible in first person.
            return lower.IndexOf("hair", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("髪", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("髮", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("发", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("ヘア", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("ウィッグ", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("ツインテ", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("ponytail", StringComparison.Ordinal) >= 0;
        }

        private static int BoneIndexOr(Dictionary<string, int> lookup, string name)
        {
            int index;
            return lookup.TryGetValue(name, out index) ? index : -1;
        }

        private static int DominantBone(BoneWeight w)
        {
            int bone = w.boneIndex0;
            float best = w.weight0;
            if (w.weight1 > best) { best = w.weight1; bone = w.boneIndex1; }
            if (w.weight2 > best) { best = w.weight2; bone = w.boneIndex2; }
            if (w.weight3 > best) { bone = w.boneIndex3; }
            return bone;
        }

        private static int DominantTriangleBone(MeshData md, int a, int b, int c)
        {
            int ba = DominantBone(md.Weights[a]);
            int bb = DominantBone(md.Weights[b]);
            int bc = DominantBone(md.Weights[c]);
            if (ba == bb || ba == bc)
            {
                return ba;
            }
            if (bb == bc)
            {
                return bb;
            }
            return ba;
        }

        private struct SleeveAxis
        {
            public Vector3 Mean;
            public Vector3 Dir;
            public bool Valid;
        }

        /// <summary>
        /// Drops geometry that should not be seen from the first-person camera. The main case
        /// is the headset microphone: its glowing tip belongs to the body mesh and is weighted
        /// to head_rig, so hiding the head mesh leaves it floating right in front of the eyes.
        /// The optional clothing filters (top flaps, sleeve lining) are off by default. Submesh
        /// count is preserved; only index lists shrink.
        /// </summary>
        private int[][] FilterFirstPersonClothing(MeshData md)
        {
            bool mic = _hideHeadsetMic.Value && _hideHead.Value;
            if (md.Name != "Miku_Body" ||
                (!_hideTopFrontTails.Value && !_hideSleeveLining.Value && !mic))
            {
                return md.Indices;
            }

            Dictionary<string, int> bones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _data.Bones.Count; i++)
            {
                if (!bones.ContainsKey(_data.Bones[i].Name))
                {
                    bones.Add(_data.Bones[i].Name, i);
                }
            }
            int spine = BoneIndexOr(bones, "spine_1");
            int lower = BoneIndexOr(bones, "lower_body");
            int head = BoneIndexOr(bones, "head_rig");
            bool[] armBones = new bool[_data.Bones.Count];
            string[] armNames =
            {
                "arm_twist_R", "elbow_R", "miku_extra_01",
                "arm_twist_L", "elbow_L", "miku_extra_00"
            };
            for (int i = 0; i < armNames.Length; i++)
            {
                int b = BoneIndexOr(bones, armNames[i]);
                if (b >= 0)
                {
                    armBones[b] = true;
                }
            }
            SleeveAxis[] axes = _hideSleeveLining.Value ? ComputeSleeveAxes(md) : null;

            int[][] result = new int[md.Indices.Length][];
            for (int s = 0; s < md.Indices.Length; s++)
            {
                int[] src = md.Indices[s];
                string mat = MaterialName(md.SubMeshMaterials[s]);
                bool top = _hideTopFrontTails.Value &&
                           string.Equals(mat, "Top", StringComparison.OrdinalIgnoreCase);
                bool trim = _hideTopFrontTails.Value &&
                            string.Equals(mat, "Line", StringComparison.OrdinalIgnoreCase);
                bool sleeve = _hideSleeveLining.Value &&
                              string.Equals(mat, "Black", StringComparison.OrdinalIgnoreCase);
                bool micPart = mic && head >= 0 &&
                               string.Equals(mat, "Light1", StringComparison.OrdinalIgnoreCase);
                if (!top && !trim && !sleeve && !micPart)
                {
                    result[s] = src;
                    continue;
                }

                List<int> keep = new List<int>(src.Length);
                for (int k = 0; k + 2 < src.Length; k += 3)
                {
                    int a = src[k];
                    int b = src[k + 1];
                    int c = src[k + 2];
                    bool drop = false;
                    if (top || trim)
                    {
                        int bone = DominantTriangleBone(md, a, b, c);
                        if ((top && (bone == spine || bone == lower)) ||
                            (trim && bone == lower))
                        {
                            drop = true;
                        }
                    }
                    else if (sleeve)
                    {
                        drop = IsSleeveLining(md, a, b, c, armBones, axes);
                    }
                    else if (micPart)
                    {
                        // Light1 also contains the boot lights, which are weighted to the
                        // legs; only the head-weighted part is the microphone tip.
                        drop = DominantTriangleBone(md, a, b, c) == head;
                    }
                    if (!drop)
                    {
                        keep.Add(a);
                        keep.Add(b);
                        keep.Add(c);
                    }
                }

                result[s] = keep.Count == src.Length ? src : keep.ToArray();
                if (keep.Count != src.Length)
                {
                    Logger.LogInfo(string.Format(
                        "First-person filter: {0} dropped {1} of {2} triangles",
                        mat, (src.Length - keep.Count) / 3, src.Length / 3));
                }
            }
            return result;
        }

        /// <summary>
        /// The bell cuff is far wider than the arm and reaches past the knuckles, so in
        /// first person the cuff hides the hand and cuts straight through anything the hand
        /// holds. Pull the last stretch of sleeve in to the wrist and clamp it there: the
        /// hand comes out of a snug cuff and the item is no longer inside the cloth.
        /// Only positions change - no triangles are removed, so the mesh stays closed.
        /// </summary>
        private void TightenSleeveCuffs(MeshData md, Transform[] bones,
                                        Dictionary<string, int> indexByName)
        {
            if (!_tightenCuffs.Value || md.Vertices == null || bones == null || indexByName == null ||
                !string.Equals(md.Name, "Miku_Body", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Everything is measured along the elbow->hand line in bind pose, where 0 is
            // the elbow and 1 is the wrist.
            const float ZoneStart = 0.45f;
            const float ZoneEnd = 1.10f;
            const float EndT = 1.04f;
            const float CapStart = 0.052f;
            const float CapEnd = 0.034f;
            const float MaxRadius = 0.12f;
            const int Bins = 48;

            bool[] cloth = new bool[md.Vertices.Length];
            for (int s = 0; s < md.Indices.Length; s++)
            {
                string mat = MaterialName(md.SubMeshMaterials[s]);
                if (!string.Equals(mat, "Black", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mat, "Light3", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mat, "Light4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int[] ind = md.Indices[s];
                for (int k = 0; k < ind.Length; k++)
                {
                    cloth[ind[k]] = true;
                }
            }

            string[] sides = { "R", "L" };
            for (int side = 0; side < 2; side++)
            {
                int elbowIndex;
                int handIndex;
                if (!indexByName.TryGetValue("elbow_" + sides[side], out elbowIndex) ||
                    !indexByName.TryGetValue("hand_" + sides[side], out handIndex))
                {
                    continue;
                }
                Vector3 a = _armature.InverseTransformPoint(bones[elbowIndex].position);
                Vector3 wrist = _armature.InverseTransformPoint(bones[handIndex].position);
                Vector3 axis = wrist - a;
                float length = axis.magnitude;
                if (length < 1e-4f)
                {
                    continue;
                }
                axis /= length;

                // Widest point of the sleeve in each slice along the arm. A single scale per
                // slice keeps the trim that is laid on top of the cloth sitting exactly on
                // it; capping every vertex to the same radius instead drags the two layers
                // onto one another and tears the trim's edge open.
                float[] maxR = new float[Bins];
                bool any = false;
                for (int v = 0; v < md.Vertices.Length; v++)
                {
                    if (!cloth[v])
                    {
                        continue;
                    }
                    Vector3 rel = md.Vertices[v] - a;
                    float along = Vector3.Dot(rel, axis);
                    float t = along / length;
                    // The bell flares out past the wrist bone, so everything up to the
                    // cuff mouth is processed; the bin index clamps to the last slice.
                    if (t < ZoneStart)
                    {
                        continue;
                    }
                    float r = (rel - axis * along).magnitude;
                    if (r > MaxRadius)
                    {
                        continue;
                    }
                    int bin = Mathf.Clamp(
                        Mathf.FloorToInt((t - ZoneStart) / (ZoneEnd - ZoneStart) * Bins), 0, Bins - 1);
                    if (r > maxR[bin])
                    {
                        maxR[bin] = r;
                    }
                    any = true;
                }
                if (!any)
                {
                    continue;
                }

                // Empty slices (between two rows of vertices) take the value of their
                // neighbours, so the taper stays continuous.
                int prevBin = -1;
                for (int b = 0; b < Bins; b++)
                {
                    if (maxR[b] <= 0f)
                    {
                        continue;
                    }
                    if (prevBin < 0)
                    {
                        for (int k = 0; k < b; k++)
                        {
                            maxR[k] = maxR[b];
                        }
                    }
                    else
                    {
                        for (int k = prevBin + 1; k < b; k++)
                        {
                            maxR[k] = Mathf.Lerp(maxR[prevBin], maxR[b],
                                (float)(k - prevBin) / (b - prevBin));
                        }
                    }
                    prevBin = b;
                }
                for (int k = prevBin + 1; k < Bins; k++)
                {
                    maxR[k] = maxR[prevBin];
                }

                float[] scale = new float[Bins];
                for (int b = 0; b < Bins; b++)
                {
                    float u = (b + 0.5f) / Bins;
                    float cap = Mathf.Lerp(CapStart, CapEnd, u * u * (3f - 2f * u));
                    scale[b] = maxR[b] > 1e-4f ? Mathf.Min(1f, cap / maxR[b]) : 1f;
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    float[] src = (float[])scale.Clone();
                    for (int b = 0; b < Bins; b++)
                    {
                        scale[b] = (src[Mathf.Max(b - 1, 0)] + 2f * src[b] +
                                    src[Mathf.Min(b + 1, Bins - 1)]) * 0.25f;
                    }
                }

                int moved = 0;
                for (int v = 0; v < md.Vertices.Length; v++)
                {
                    if (!cloth[v])
                    {
                        continue;
                    }
                    Vector3 rel = md.Vertices[v] - a;
                    float along = Vector3.Dot(rel, axis);
                    float t = along / length;
                    // The bell flares out past the wrist bone, so everything up to the
                    // cuff mouth is processed; the bin index clamps to the last slice.
                    if (t < ZoneStart)
                    {
                        continue;
                    }
                    Vector3 radial = rel - axis * along;
                    if (radial.magnitude > MaxRadius)
                    {
                        continue;
                    }

                    float pos = (t - ZoneStart) / (ZoneEnd - ZoneStart) * Bins - 0.5f;
                    int b0 = Mathf.Clamp(Mathf.FloorToInt(pos), 0, Bins - 1);
                    int b1 = Mathf.Clamp(b0 + 1, 0, Bins - 1);
                    float s = Mathf.Lerp(scale[b0], scale[b1], Mathf.Clamp01(pos - b0));

                    md.Vertices[v] = a + axis * Mathf.Min(along, EndT * length) + radial * s;
                    moved++;
                }
                if (moved > 0)
                {
                    Logger.LogInfo(string.Format(
                        "Sleeve cuff {0}: pulled {1} vertices in to the wrist", sides[side], moved));
                }
            }
        }

        private SleeveAxis[] ComputeSleeveAxes(MeshData md)
        {
            SleeveAxis[] axes = new SleeveAxis[2];
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? 1f : -1f;
                HashSet<int> seen = new HashSet<int>();
                List<Vector3> pts = new List<Vector3>();
                for (int s = 0; s < md.Indices.Length; s++)
                {
                    if (!string.Equals(MaterialName(md.SubMeshMaterials[s]), "Black",
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    int[] idx = md.Indices[s];
                    for (int k = 0; k < idx.Length; k++)
                    {
                        int vi = idx[k];
                        if (!seen.Add(vi))
                        {
                            continue;
                        }
                        Vector3 p = md.Vertices[vi];
                        if (p.x * sign > 0.05f)
                        {
                            pts.Add(p);
                        }
                    }
                }
                if (pts.Count < 16)
                {
                    continue;
                }

                Vector3 mean = Vector3.zero;
                for (int i = 0; i < pts.Count; i++)
                {
                    mean += pts[i];
                }
                mean /= pts.Count;

                double c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 d = pts[i] - mean;
                    c00 += d.x * d.x; c01 += d.x * d.y; c02 += d.x * d.z;
                    c11 += d.y * d.y; c12 += d.y * d.z; c22 += d.z * d.z;
                }

                Vector3 axis = new Vector3(1f, 0f, 0f);
                for (int it = 0; it < 80; it++)
                {
                    Vector3 next = new Vector3(
                        (float)(c00 * axis.x + c01 * axis.y + c02 * axis.z),
                        (float)(c01 * axis.x + c11 * axis.y + c12 * axis.z),
                        (float)(c02 * axis.x + c12 * axis.y + c22 * axis.z));
                    if (next.sqrMagnitude < 1e-12f)
                    {
                        break;
                    }
                    axis = next.normalized;
                }
                if (axis.y < 0f)
                {
                    axis = -axis;
                }
                axes[side] = new SleeveAxis { Mean = mean, Dir = axis, Valid = true };
                Logger.LogInfo(string.Format(
                    "Sleeve axis side {0}: mean={1} dir={2} ({3} verts)",
                    side, mean, axis, pts.Count));
            }
            return axes;
        }

        private static bool IsSleeveLining(MeshData md, int a, int b, int c,
                                           bool[] armBones, SleeveAxis[] axes)
        {
            if (axes == null)
            {
                return false;
            }
            int bone = DominantTriangleBone(md, a, b, c);
            if (bone < 0 || bone >= armBones.Length || !armBones[bone])
            {
                return false;
            }

            Vector3 centroid = (md.Vertices[a] + md.Vertices[b] + md.Vertices[c]) / 3f;
            SleeveAxis sa = axes[centroid.x > 0f ? 0 : 1];
            if (!sa.Valid)
            {
                return false;
            }

            Vector3 d = centroid - sa.Mean;
            float along = Vector3.Dot(d, sa.Dir);
            Vector3 radial = d - sa.Dir * along;
            if (radial.sqrMagnitude < 1e-10f)
            {
                return false;
            }
            radial.Normalize();

            Vector3 n = md.Normals[a] + md.Normals[b] + md.Normals[c];
            if (n.sqrMagnitude < 1e-10f)
            {
                return false;
            }
            n.Normalize();
            return Vector3.Dot(n, radial) < -0.35f;
        }

        /// <summary>
        /// The "01" decal is rigidly bound to the shoulder in the source model, while the arm
        /// skin around it blends shoulder and upper-arm weights. Re-bind the decal to the
        /// weights of the nearby skin so it deforms with the arm instead of sliding off it.
        /// </summary>
        private void MatchDecalWeights(MeshData md)
        {
            List<int> decal = new List<int>();
            HashSet<int> decalSet = new HashSet<int>();
            HashSet<int> sourceSet = new HashSet<int>();
            for (int s = 0; s < md.Indices.Length; s++)
            {
                bool isDecal = MaterialName(md.SubMeshMaterials[s])
                    .IndexOf("No01", StringComparison.OrdinalIgnoreCase) >= 0;
                int[] idx = md.Indices[s];
                for (int k = 0; k < idx.Length; k++)
                {
                    if (isDecal)
                    {
                        if (decalSet.Add(idx[k]))
                        {
                            decal.Add(idx[k]);
                        }
                    }
                    else
                    {
                        sourceSet.Add(idx[k]);
                    }
                }
            }
            if (decal.Count == 0 || sourceSet.Count == 0)
            {
                return;
            }

            int[] sources = new int[sourceSet.Count];
            sourceSet.CopyTo(sources);
            const float maxDistSq = 0.05f * 0.05f;
            int[] nearIdx = new int[4];
            float[] nearDist = new float[4];

            for (int di = 0; di < decal.Count; di++)
            {
                int vi = decal[di];
                Vector3 p = md.Vertices[vi];
                int count = 0;
                for (int i = 0; i < sources.Length; i++)
                {
                    float d = (md.Vertices[sources[i]] - p).sqrMagnitude;
                    if (d > maxDistSq)
                    {
                        continue;
                    }
                    int at = count;
                    while (at > 0 && nearDist[at - 1] > d)
                    {
                        at--;
                    }
                    if (at >= 4)
                    {
                        continue;
                    }
                    for (int m = Math.Min(count, 3); m > at; m--)
                    {
                        nearIdx[m] = nearIdx[m - 1];
                        nearDist[m] = nearDist[m - 1];
                    }
                    nearIdx[at] = sources[i];
                    nearDist[at] = d;
                    if (count < 4)
                    {
                        count++;
                    }
                }
                if (count == 0)
                {
                    continue;
                }

                Dictionary<int, float> acc = new Dictionary<int, float>();
                for (int i = 0; i < count; i++)
                {
                    float w = 1f / (Mathf.Sqrt(nearDist[i]) + 0.002f);
                    BoneWeight bw = md.Weights[nearIdx[i]];
                    AddBoneWeight(acc, bw.boneIndex0, bw.weight0 * w);
                    AddBoneWeight(acc, bw.boneIndex1, bw.weight1 * w);
                    AddBoneWeight(acc, bw.boneIndex2, bw.weight2 * w);
                    AddBoneWeight(acc, bw.boneIndex3, bw.weight3 * w);
                }

                List<KeyValuePair<int, float>> ranked = new List<KeyValuePair<int, float>>(acc);
                ranked.Sort((x, y) => y.Value.CompareTo(x.Value));
                float total = 0f;
                for (int i = 0; i < ranked.Count && i < 4; i++)
                {
                    total += ranked[i].Value;
                }
                if (total <= 0f)
                {
                    continue;
                }

                BoneWeight outWeight = new BoneWeight();
                for (int i = 0; i < ranked.Count && i < 4; i++)
                {
                    float w = ranked[i].Value / total;
                    switch (i)
                    {
                        case 0: outWeight.boneIndex0 = ranked[i].Key; outWeight.weight0 = w; break;
                        case 1: outWeight.boneIndex1 = ranked[i].Key; outWeight.weight1 = w; break;
                        case 2: outWeight.boneIndex2 = ranked[i].Key; outWeight.weight2 = w; break;
                        default: outWeight.boneIndex3 = ranked[i].Key; outWeight.weight3 = w; break;
                    }
                }
                md.Weights[vi] = outWeight;
            }
            Logger.LogInfo("Matched " + decal.Count + " decal vertices to nearby skin weights.");
        }

        private static void AddBoneWeight(Dictionary<int, float> acc, int bone, float weight)
        {
            if (bone < 0 || weight <= 0f)
            {
                return;
            }
            float cur;
            acc.TryGetValue(bone, out cur);
            acc[bone] = cur + weight;
        }

        /// <summary>
        /// Starts UnityWebRequestTexture jobs for every PNG/JPG the model references.
        /// The request is decoded natively off the main thread; the finished texture
        /// is picked up by <see cref="LoadTexture"/>. BMP/TGA/DDS keep using the
        /// synchronous decoder (they are small and Unity's image loader cannot read
        /// them).
        /// </summary>
        private void PrefetchModelTextures(ModelData data, string modelId)
        {
            if (data == null)
            {
                return;
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < data.Materials.Count; i++)
            {
                string file = data.Materials[i].Texture;
                if (string.IsNullOrEmpty(file) || !seen.Add(file))
                {
                    continue;
                }
                List<string> paths = TextureCandidates(file);
                for (int p = 0; p < paths.Count; p++)
                {
                    string path = paths[p];
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    // TextureCandidates is ordered: the first existing file is the
                    // one the synchronous path would decode, so only prefetch that.
                    if (IsAsyncDecodable(path))
                    {
                        StartTexturePrefetch(path, modelId);
                    }
                    break;
                }
            }
        }

        private static bool IsAsyncDecodable(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static void StartTexturePrefetch(string path, string modelId)
        {
            if (string.IsNullOrEmpty(path) ||
                _prefetchRequested.Contains(path) ||
                _prefetchedTextures.ContainsKey(path))
            {
                return;
            }
            UnityWebRequest request = null;
            try
            {
                // A file URI also handles spaces and non-ASCII folder names.
                Uri uri = new Uri(path);
                request = UnityWebRequestTexture.GetTexture(uri.AbsoluteUri, false);
                request.SendWebRequest();
            }
            catch (Exception)
            {
                if (request != null)
                {
                    request.Dispose();
                }
                return;
            }
            _prefetchRequested.Add(path);
            PrefetchJob job = new PrefetchJob();
            job.Path = path;
            job.ModelId = modelId ?? "";
            job.Request = request;
            _prefetchJobs.Add(job);
        }

        private static void PumpTexturePrefetches()
        {
            if (_prefetchJobs.Count == 0)
            {
                return;
            }
            for (int i = _prefetchJobs.Count - 1; i >= 0; i--)
            {
                PrefetchJob job = _prefetchJobs[i];
                UnityWebRequest request = job.Request;
                if (request == null)
                {
                    _prefetchJobs.RemoveAt(i);
                    _prefetchRequested.Remove(job.Path);
                    continue;
                }
                if (!request.isDone)
                {
                    continue;
                }
                _prefetchJobs.RemoveAt(i);
                _prefetchRequested.Remove(job.Path);
                Texture2D tex = null;
                try
                {
                    if (!request.isNetworkError && !request.isHttpError &&
                        string.IsNullOrEmpty(request.error))
                    {
                        tex = DownloadHandlerTexture.GetContent(request);
                    }
                }
                catch (Exception)
                {
                    tex = null;
                }
                request.Dispose();
                if (tex == null)
                {
                    continue;
                }
                Texture2D old;
                if (_prefetchedTextures.TryGetValue(job.Path, out old) && old != null)
                {
                    Destroy(old);
                }
                _prefetchedTextures[job.Path] = tex;
                _prefetchOrder.Add(job.Path);
                TrimPrefetchCache();
            }
        }

        // The cache only exists to bridge the gap between "requested" and "model
        // built"; a switched-away model can leave a few unused textures behind.
        // Bound the VRAM instead of holding every previewed model forever.
        private static void TrimPrefetchCache()
        {
            while (_prefetchOrder.Count > 48)
            {
                string path = _prefetchOrder[0];
                _prefetchOrder.RemoveAt(0);
                Texture2D tex;
                if (_prefetchedTextures.TryGetValue(path, out tex))
                {
                    _prefetchedTextures.Remove(path);
                    if (tex != null)
                    {
                        Destroy(tex);
                    }
                }
            }
        }

        private static Texture2D TakePrefetchedTexture(string path)
        {
            Texture2D tex;
            if (string.IsNullOrEmpty(path) || !_prefetchedTextures.TryGetValue(path, out tex))
            {
                return null;
            }
            _prefetchedTextures.Remove(path);
            _prefetchOrder.Remove(path);
            if (tex == null)
            {
                return null;
            }
            return tex;
        }

        private static int PendingTextureCount(string modelId)
        {
            int count = 0;
            string id = modelId ?? "";
            for (int i = 0; i < _prefetchJobs.Count; i++)
            {
                if (string.Equals(_prefetchJobs[i].ModelId ?? "", id,
                        StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
        }

        private Texture2D LoadTexture(string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                return null;
            }

            Texture2D cached;
            if (_textures.TryGetValue(file, out cached))
            {
                return cached;
            }

            List<string> paths = TextureCandidates(file);
            Texture2D tex = null;
            string used = null;
            for (int i = 0; i < paths.Count && tex == null; i++)
            {
                // Prefer a texture the background request already decoded.
                tex = TakePrefetchedTexture(paths[i]);
                if (tex == null)
                {
                    tex = DecodeTexture(paths[i]);
                }
                if (tex != null)
                {
                    used = paths[i];
                }
            }
            if (tex == null)
            {
                Logger.LogWarning(paths.Count == 0
                    ? "Texture not found: " + file
                    : "Could not decode texture: " + file);
                _textures[file] = null;
                return null;
            }
            if (!string.Equals(used, paths[0], StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInfo("Texture '" + file + "' resolved to " + used);
            }
            else if (_verbose.Value)
            {
                Logger.LogInfo("Texture " + file + " -> " + used + " ("
                    + tex.width + "x" + tex.height + ")");
            }

            tex.name = file;
            // Imported MMD models tile their textures; the shipped Miku set does
            // not, so keep the old clamp behaviour for it.
            tex.wrapMode = _textureRepeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 4;
            _textures[file] = tex;
            return tex;
        }

        private static Texture2D DecodeTexture(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
                if (tex.LoadImage(bytes))
                {
                    return tex;
                }
                // Unity's decoder only knows PNG/JPG/EXR; MMD textures are often
                // BMP or TGA.
                Texture2D decoded = TextureDecoder.Decode(bytes);
                Destroy(tex);
                return decoded;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // model construction
        // ------------------------------------------------------------------

        private void BuildModel(Transform pv, Transform male, SourceMap sources)
        {
            _playerView = pv;
            _maleGeo = male;
            _clothChains.Clear();
            _hairRenderer = null;
            _clothVertexMask = null;
            _hairVisibleMask = null;
            _meshParts.Clear();
            // The poles are stored in the old container's space; a rebuild makes them
            // meaningless, so let the next solved frame choose fresh ones.
            _hasPoleL = false;
            _hasPoleR = false;
            _ikLeftActive = false;
            _ikRightActive = false;
            _ikWeightL = 0f;
            _ikWeightR = 0f;
            _aimWeightL = 0f;
            _aimWeightR = 0f;
            _gripWeightL = 0f;
            _gripWeightR = 0f;
            _gripActiveL = false;
            _gripActiveR = false;
            _gripLastValidL = false;
            _gripLastValidR = false;
            _gripBonesByHand.Clear();
            _viewIdle = false;
            _viewIdleLogged = false;
            _viewCinematic = false;
            _suppressViewAnim = false;
            _viewSuppressWeight = 0f;
            _headLookFade = 1f;
            _animator = pv.GetComponent<Animator>();
            if (_animator != null)
            {
                // Hiding the suit renderers can make Unity cull the Animator
                // ("Cull Update Transforms"/"Cull Completely"), freezing the
                // skeleton. Force it to keep evaluating every frame.
                Logger.LogInfo("Animator cullingMode before=" + _animator.cullingMode);
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                Logger.LogInfo("Animator cullingMode after=" + _animator.cullingMode);
            }

            GameObject containerGo = new GameObject("Miku_PlayerModel");
            containerGo.layer = pv.gameObject.layer;
            containerGo.AddComponent<LateRetargetHost>();
            _container = containerGo.transform;
            _container.SetParent(pv, false);
            _container.localRotation = Quaternion.Euler(0f, _yaw.Value, 0f);
            _container.localScale = Vector3.one * _scale.Value;
            // A rebuilt model starts aligned with the camera; the shoulder view
            // picks the detached heading up again on the next frame.
            _modelYawValid = false;
            _faceDetach = 0f;

            GameObject armatureGo = new GameObject("Miku_Armature");
            armatureGo.layer = containerGo.layer;
            _armature = armatureGo.transform;
            _armature.SetParent(_container, false);
            _armature.localPosition = Vector3.zero;
            _armature.localRotation = Quaternion.identity;
            _armature.localScale = Vector3.one;

            int boneCount = _data.Bones.Count;
            Transform[] bones = new Transform[boneCount];
            int[] depths = new int[boneCount];

            // First create every bone object, then bind parents (parent indices are
            // topological, but this also tolerates any ordering).
            for (int i = 0; i < boneCount; i++)
            {
                GameObject go = new GameObject(_data.Bones[i].Name);
                go.layer = containerGo.layer;
                bones[i] = go.transform;
            }

            for (int i = 0; i < boneCount; i++)
            {
                BoneData b = _data.Bones[i];
                Transform parent = b.Parent >= 0 ? bones[b.Parent] : _armature;
                bones[i].SetParent(parent, false);
                bones[i].localPosition = b.LocalPos;
                bones[i].localRotation = b.LocalRot;
                bones[i].localScale = Vector3.one;
                depths[i] = b.Parent >= 0 ? depths[b.Parent] + 1 : 0;
            }

            Dictionary<string, int> boneIndexByName = new Dictionary<string, int>(boneCount);
            for (int i = 0; i < boneCount; i++)
            {
                boneIndexByName[_data.Bones[i].Name] = i;
            }

            if (_imported)
            {
                BakeImportedArmPose(bones, boneIndexByName, sources);
            }

            // Size the model to the vanilla body: the head is pinned to the
            // game's head bone, so scaling the container grows the body below
            // it. The fit is measured from rigid bone distances (the live game
            // pose gives the suit's true rest dimensions) and multiplied on top
            // of the Scale config.
            _scaleFit = MeasureScaleFit(sources, bones, boneIndexByName);
            _container.localScale = Vector3.one * (_scale.Value * _scaleFit);
            Logger.LogInfo("Body fit: scale=" + _scale.Value.ToString("F3")
                + " fit=" + _scaleFit.ToString("F3")
                + " effective=" + (_scale.Value * _scaleFit).ToString("F3"));
            // Arm proportion fitting was tried and reverted: stretching the
            // clavicle/upper/forearm changed the hand-to-tool relation and the
            // wrist skin. Model mode uses the model's own proportions.

            int headIndex;
            _headBone = boneIndexByName.TryGetValue("head_rig", out headIndex) ? bones[headIndex] : null;
            int neckIndex;
            _neckBone = boneIndexByName.TryGetValue("neck", out neckIndex) ? bones[neckIndex] : null;

            // Extra bones for the simulated cloth (twintails, skirt). They have to exist
            // before the bind poses are built, because the cloth vertices are re-weighted
            // onto them.
            List<Transform> clothBones = new List<Transform>();
            // Built even when HairPhysics is off, so the switch can be flipped at any
            // time: a disabled chain is simply held at its bind pose each frame. Left
            // out, turning physics on needed a full model reload.
            if (_imported)
            {
                // Imported MMD models usually carry their own hair bone chain;
                // that one is simulated in place.
                BuildImportedHairRig(bones);
            }
            // Hair that is rigidly skinned to the head (no bones of its own, as
            // in the shipped Miku's twintails) still gets a chain built from the
            // mesh geometry.
            BuildHairRig(clothBones, _headBone, headIndex);
            if (_skirtPhysics.Value && !_imported)
            {
                BuildSkirtRig(clothBones, bones, boneIndexByName);
            }

            // Align the model's head bone with the game's bind-pose head bone.
            Vector3 modelHeadLocal = _headBone != null
                ? _armature.InverseTransformPoint(_headBone.position)
                : new Vector3(0f, 1.4157f, 0.0095f);

            Vector3 sourceHeadWorld;
            if (sources.Head != null)
            {
                // Use the live head bone, not the bind pose: the suit's bind pose puts
                // the head ~1.6 m above the player_view origin while the animated
                // skeleton keeps head_rig at the origin (the first-person body root).
                sourceHeadWorld = sources.Head.Bone.position;
            }
            else
            {
                sourceHeadWorld = pv.position;
                Logger.LogWarning("head_rig not found in the suit skeleton; using player_view origin.");
            }

            Vector3 sourceHeadLocal = pv.InverseTransformPoint(sourceHeadWorld);
            Vector3 scaledModelHead = _container.localRotation
                * (modelHeadLocal * (_scale.Value * _scaleFit));
            _baseLocalPos = sourceHeadLocal - scaledModelHead +
                            new Vector3(_offsetX.Value, _offsetY.Value, _offsetZ.Value);
            _container.localPosition = _baseLocalPos;

            if (_verbose.Value)
            {
                Camera cam = Camera.main;
                Logger.LogInfo(string.Format(
                    "Align: pv={0} cam={1} sourceHeadWorld={2} sourceHeadLocal={3} modelHeadLocal={4} baseLocalPos={5} scale={6}",
                    pv.position, cam != null ? cam.transform.position.ToString() : "none",
                    sourceHeadWorld, sourceHeadLocal, modelHeadLocal, _baseLocalPos,
                    _scale.Value * _scaleFit));

                LogBoneWorld(bones, boneIndexByName, "hips");
                LogBoneWorld(bones, boneIndexByName, "head_rig");
                LogBoneWorld(bones, boneIndexByName, "hand_L");
                LogBoneWorld(bones, boneIndexByName, "hand_R");
                LogBoneWorld(bones, boneIndexByName, "toe_L_rig");
                LogBoneWorld(bones, boneIndexByName, "toe_R");
                LogSourceWorld(sources, "hips");
                LogSourceWorld(sources, "head_rig");
                LogSourceWorld(sources, "hand_L");
                LogSourceWorld(sources, "hand_R");
            }

            // Capture the model's rest rotations in container-local space.
            Quaternion containerRestRot = _container.rotation;
            Quaternion containerRestInv = Quaternion.Inverse(containerRestRot);
            Quaternion[] targetRest = new Quaternion[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                targetRest[i] = containerRestInv * bones[i].rotation;
            }

            // Arm bind frames for the IK. Captured here, after the imported arm
            // pose was baked onto the game's shoulder direction and before any
            // animation pass, so they are the model's true rest pose.
            _adaptiveHands[0] = null;
            _adaptiveHands[1] = null;
            _armRest[0] = null;
            _armRest[1] = null;
            _holdPoses.Clear();
            _holdCapturePending = false;
            _holdPoseLast = null;
            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "L" : "R";
                int ui, mi, ei;
                if (!boneIndexByName.TryGetValue("shoulder_" + side, out ui) ||
                    !boneIndexByName.TryGetValue("elbow_" + side, out mi) ||
                    !boneIndexByName.TryGetValue("hand_" + side, out ei))
                {
                    continue;
                }

                ArmRest rest = new ArmRest();
                rest.UpperLocal = targetRest[ui];
                rest.MidLocal = targetRest[mi];
                Vector3 upperDir = containerRestInv * (bones[mi].position - bones[ui].position);
                Vector3 midDir = containerRestInv * (bones[ei].position - bones[mi].position);
                if (upperDir.sqrMagnitude < 1e-8f || midDir.sqrMagnitude < 1e-8f)
                {
                    continue;
                }
                rest.UpperDir = upperDir.normalized;
                rest.MidDir = midDir.normalized;

                // The hand's rotation relative to the forearm in the bind pose.
                // The IK pins the hand to the game hand's world rotation, so
                // the forearm's roll has to be rebuilt from that hand; using
                // the retargeted roll instead left the forearm (and elbow skin)
                // a quarter turn away from the wrist, and the wrist folded to
                // keep the palm on the item.
                rest.WristLocal = Quaternion.Inverse(bones[mi].rotation) * bones[ei].rotation;
                rest.ElbowLocal = Quaternion.Inverse(bones[ui].rotation) * bones[mi].rotation;
                float wristAngle = Quaternion.Angle(Quaternion.identity, rest.WristLocal);
                float elbowAngle = Quaternion.Angle(Quaternion.identity, rest.ElbowLocal);
                // A sane bind pose has the hand bone close to the forearm's
                // frame. A huge angle means this model's hand bone is bound
                // flipped; the old inherited-roll path is safer there.
                rest.HasWrist = wristAngle < 100f;

                SourceBone srcUpper, srcElbow, srcHandBone;
                if (sources.Bones.TryGetValue("shoulder_" + side, out srcUpper) &&
                    sources.Bones.TryGetValue("elbow_" + side, out srcElbow) &&
                    sources.Bones.TryGetValue("hand_" + side, out srcHandBone) &&
                    srcUpper.Bone != null && srcElbow.Bone != null && srcHandBone.Bone != null)
                {
                    rest.GameWristLocal = Quaternion.Inverse(srcElbow.Bone.rotation)
                        * srcHandBone.Bone.rotation;
                    rest.GameElbowLocal = Quaternion.Inverse(srcUpper.Bone.rotation)
                        * srcElbow.Bone.rotation;
                    // RestLocalRot is the bone's bind-pose world rotation, so
                    // their quotient is the game's rest wrist local.
                    rest.GameWristRestLocal =
                        Quaternion.Inverse(srcElbow.RestLocalRot)
                        * srcHandBone.RestLocalRot;
                    rest.HasGameWrist = true;
                    rest.HasGameElbow = true;
                    Logger.LogInfo(string.Format(
                        "Wrist {0}: bind wrist {1:F1} deg, suit wrist now {2:F1} deg",
                        side,
                        Quaternion.Angle(Quaternion.identity, rest.GameWristRestLocal),
                        Quaternion.Angle(rest.GameWristRestLocal, rest.GameWristLocal)));
                }
                Logger.LogInfo(string.Format(
                    "Wrist {0}: rest local {1:F1} deg, elbow rest {2:F1} deg{3}",
                    side, wristAngle, elbowAngle,
                    rest.HasWrist ? "" : " (not used)"));

                // MMD twist group between the upper arm and the elbow. The
                // helpers are siblings of the main 腕捩 bone that inherit a
                // fraction of its rotation; find them through the PMX link so
                // the IK can spread the elbow's roll along the upper arm.
                int twistIdx;
                if (boneIndexByName.TryGetValue("arm_twist_" + side, out twistIdx) &&
                    twistIdx >= 0 && twistIdx < bones.Length && bones[twistIdx] != null &&
                    bones[twistIdx].parent == bones[ui])
                {
                    rest.ArmTwist = bones[twistIdx];
                    List<Transform> helpers = new List<Transform>();
                    List<float> ratios = new List<float>();
                    for (int bi = 0; bi < _data.Bones.Count && bi < bones.Length; bi++)
                    {
                        BoneData bd = _data.Bones[bi];
                        if (bi == twistIdx || !bd.InheritsRotation ||
                            bd.InheritParent != twistIdx || bones[bi] == null ||
                            bones[bi].parent != bones[ui])
                        {
                            continue;
                        }
                        helpers.Add(bones[bi]);
                        ratios.Add(Mathf.Clamp01(bd.InheritRatio));
                    }
                    rest.ArmTwistHelpers = helpers.ToArray();
                    rest.ArmTwistRatios = ratios.ToArray();
                    Logger.LogInfo(string.Format(
                        "Arm twist {0}: {1} helpers at {2}", side, helpers.Count,
                        string.Join(", ", ratios.ConvertAll(r => r.ToString("F2")).ToArray())));
                }

                // Grip calibration. Each rig's palm frame is built from its own
                // wrist, middle-finger base and thumb base at rest (fingers
                // forward, thumb side); the local frame is constant as long as
                // the fingers are not re-parented, so it survives any pose. The
                // IK sets the model hand to the game hand's world rotation times
                // PalmFix, which makes the model palm wrap the held item exactly
                // the way the suit's gloved hand does - no guessing at a roll
                // offset, and it works for both hands and every bind pose.
                int pi, ti;
                SourceBone srcHand, srcMid, srcThumb;
                if (boneIndexByName.TryGetValue("hand_" + side + "_midl_base", out pi) &&
                    boneIndexByName.TryGetValue("hand_" + side + "_thumb_base", out ti) &&
                    sources.Bones.TryGetValue("hand_" + side, out srcHand) &&
                    sources.Bones.TryGetValue("hand_" + side + "_midl_base", out srcMid) &&
                    sources.Bones.TryGetValue("hand_" + side + "_thumb_base", out srcThumb))
                {
                    Quaternion gamePalm, modelPalm;
                    if (PalmFrame(srcHand.Bone, srcMid.Bone, srcThumb.Bone, out gamePalm) &&
                        PalmFrame(bones[ei], bones[pi], bones[ti], out modelPalm))
                    {
                        Quaternion gamePalmLocal =
                            Quaternion.Inverse(srcHand.Bone.rotation) * gamePalm;
                        Quaternion modelPalmLocal =
                            Quaternion.Inverse(bones[ei].rotation) * modelPalm;
                        rest.HasPalm = true;
                        rest.PalmFix = gamePalmLocal * Quaternion.Inverse(modelPalmLocal);
                        Logger.LogInfo("Grip " + side + ": palm calibration "
                            + Quaternion.Angle(Quaternion.identity, rest.PalmFix)
                                .ToString("F1") + " deg");
                    }
                }

                // Grip-anchored IK frame. Same three measurements the game side
                // builds live (palm point, knuckle line, palm normal), but taken
                // once in the model hand's own space so they stay valid in any
                // pose. The normal comes from the palm plane itself (knuckle
                // line x finger direction). The thumb base is deliberately not
                // used: 親指０ sits near the wrist and points across the palm,
                // so a thumb-derived normal diverges sharply between rigs.
                int midlBase, midlMid, pointBase, pinkyBase2;
                if (boneIndexByName.TryGetValue("hand_" + side + "_midl_base", out midlBase) &&
                    boneIndexByName.TryGetValue("hand_" + side + "_midl_mid", out midlMid) &&
                    boneIndexByName.TryGetValue("hand_" + side + "_point_base", out pointBase) &&
                    boneIndexByName.TryGetValue("hand_" + side + "_pinky_base", out pinkyBase2))
                {
                    Transform handT = bones[ei];
                    Quaternion invHand = Quaternion.Inverse(handT.rotation);
                    Vector3 fingerDir = bones[midlBase].position - handT.position;
                    Vector3 knuckle = bones[pinkyBase2].position - bones[pointBase].position;
                    if (fingerDir.sqrMagnitude > 1e-8f
                        && knuckle.sqrMagnitude > 1e-8f)
                    {
                        float palmLen = fingerDir.magnitude;
                        fingerDir.Normalize();
                        Vector3 normal = Vector3.Cross(knuckle.normalized, fingerDir);
                        if (normal.sqrMagnitude > 1e-8f)
                        {
                            normal.Normalize();
                            Vector3 point = (bones[midlBase].position
                                + bones[midlMid].position) * 0.5f;
                            rest.HasGripFrame = true;
                            rest.GripPointLocal = invHand * (point - handT.position);
                            rest.GripAxisLocal = (invHand * knuckle).normalized;
                            rest.GripNormalLocal = (invHand * normal).normalized;
                            rest.ForearmDirLocal = (invHand
                                * (handT.position - bones[mi].position)).normalized;
                            rest.PalmLen = palmLen;
                            rest.L1 = Vector3.Distance(
                                bones[ui].position, bones[mi].position);
                            rest.L2 = Vector3.Distance(
                                bones[mi].position, bones[ei].position);
                            int clavIdx;
                            if (boneIndexByName.TryGetValue("clav_" + side, out clavIdx))
                            {
                                rest.ClavLen = Vector3.Distance(
                                    bones[clavIdx].position, bones[ui].position);
                            }
                            Logger.LogInfo(string.Format(
                                "Grip frame {0}: g=({1:F3},{2:F3},{3:F3}) "
                                + "a=({4:F2},{5:F2},{6:F2}) n=({7:F2},{8:F2},{9:F2}) "
                                + "palm={10:F3} l1={11:F3} l2={12:F3} clav={13:F3}",
                                side,
                                rest.GripPointLocal.x, rest.GripPointLocal.y,
                                rest.GripPointLocal.z,
                                rest.GripAxisLocal.x, rest.GripAxisLocal.y,
                                rest.GripAxisLocal.z,
                                rest.GripNormalLocal.x, rest.GripNormalLocal.y,
                                rest.GripNormalLocal.z,
                                rest.PalmLen, rest.L1, rest.L2, rest.ClavLen));
                        }
                    }
                }

                // The parent the upper arm inherits its swing from: the clavicle
                // when the model has one, otherwise whatever it is attached to
                // (the armature, which only turns with the container).
                int parent = _data.Bones[ui].Parent;
                rest.ParentLocal = parent >= 0 ? targetRest[parent] : Quaternion.identity;

                // Clavicle rest frame, used to strip the retargeted roll from
                // the shoulder while keeping the game's lift direction. MMD
                // models put 肩C between 肩 and 腕; the game's AimIK lift lives
                // on the matching clav_*_aim bone.
                int ci;
                if (boneIndexByName.TryGetValue("clav_" + side, out ci))
                {
                    int cci;
                    bool hasC = boneIndexByName.TryGetValue("clav_C_" + side, out cci);
                    int childIndex = hasC ? cci : ui;
                    Vector3 clavDir =
                        containerRestInv * (bones[childIndex].position - bones[ci].position);
                    if (clavDir.sqrMagnitude > 1e-8f)
                    {
                        rest.HasClav = true;
                        rest.ClavLocal = targetRest[ci];
                        int cp = _data.Bones[ci].Parent;
                        rest.ClavParentLocal = cp >= 0
                            ? targetRest[cp] : Quaternion.identity;
                        rest.ClavDir = clavDir.normalized;
                        // 肩C's swing carries the game's aim lift (the arm
                        // pitching with the tool); keep its rest direction to
                        // the upper arm so the rebuild preserves that lift.
                        if (hasC && _data.Bones[ui].Parent == cci)
                        {
                            Vector3 clavCDir = containerRestInv
                                * (bones[ui].position - bones[cci].position);
                            if (clavCDir.sqrMagnitude > 1e-8f)
                            {
                                rest.HasClavC = true;
                                rest.ClavCDir = clavCDir.normalized;
                            }
                        }
                    }
                }
                _armRest[s] = rest;
                CalibrateAdaptive(s, bones, boneIndexByName, sources);
            }

            // Skinned mesh renderers.
            int rendererLayer = sources.SuitRenderers[0].gameObject.layer;
            int rootBoneIndex = 0;
            for (int i = 0; i < boneCount; i++)
            {
                if (_data.Bones[i].Parent < 0)
                {
                    rootBoneIndex = i;
                    break;
                }
            }
            int namedRoot;
            if (boneIndexByName.TryGetValue("miku_root", out namedRoot))
            {
                rootBoneIndex = namedRoot;
            }

            // The cloth bones are appended to the skeleton; the original bone indices of
            // every existing weight stay valid.
            Transform[] renderBones = new Transform[boneCount + clothBones.Count];
            for (int i = 0; i < boneCount; i++)
            {
                renderBones[i] = bones[i];
            }
            for (int i = 0; i < clothBones.Count; i++)
            {
                renderBones[boneCount + i] = clothBones[i];
            }

            for (int mi = 0; mi < _data.Meshes.Count; mi++)
            {
                MeshData md = _data.Meshes[mi];
                GameObject meshGo = new GameObject(md.Name);
                meshGo.layer = rendererLayer;
                meshGo.transform.SetParent(_armature, false);

                SkinnedMeshRenderer smr = meshGo.AddComponent<SkinnedMeshRenderer>();

                if (md.Name == "Miku_Body")
                {
                    MatchDecalWeights(md);
                }
                int[][] allSubIndices = FilterFirstPersonClothing(md);
                TightenSleeveCuffs(md, bones, boneIndexByName);

                // The twintails get their own renderer so they can stay visible while
                // the head is hidden in first person, and so only they are re-skinned
                // onto the simulated hair bones.
                int[][] subIndices = allSubIndices;
                int[] hairIndices = null;
                int hairMaterial = -1;
                List<int[]> hairGroups = null;
                List<int> hairMaterials = null;
                if (md.Role == MeshData.RoleHead && !_imported)
                {
                    List<int[]> kept = new List<int[]>(allSubIndices.Length);
                    for (int s = 0; s < allSubIndices.Length; s++)
                    {
                        if (string.Equals(MaterialName(md.SubMeshMaterials[s]), "Hair03",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            hairIndices = allSubIndices[s];
                            hairMaterial = md.SubMeshMaterials[s];
                            continue;
                        }
                        kept.Add(allSubIndices[s]);
                    }
                    subIndices = kept.ToArray();
                }
                else if (md.Role == MeshData.RoleHead && SplitMaskFor(md) != null)
                {
                    // Imported model: BuildHairRig marked the long hair - the part it
                    // re-skinned onto the simulated chain, or (when no chain could be
                    // built) everything that hangs below the collarbone. Move exactly
                    // those triangles into their own renderer, so hiding the head in
                    // first person no longer takes the hair with it (and the fringe,
                    // which stays on the head, still hides instead of blocking the
                    // view).
                    bool[] splitMask = SplitMaskFor(md);
                    hairGroups = new List<int[]>();
                    hairMaterials = new List<int>();
                    List<int[]> kept = new List<int[]>(allSubIndices.Length);
                    for (int s = 0; s < allSubIndices.Length; s++)
                    {
                        int[] ind = allSubIndices[s];
                        List<int> headTris = new List<int>(ind.Length);
                        List<int> hairTris = new List<int>(ind.Length / 8);
                        for (int k = 0; k + 2 < ind.Length; k += 3)
                        {
                            // Whole triangle on the mask: the borderline ring stays
                            // with the head, which keeps the seam invisible.
                            bool isHair = splitMask[ind[k]] &&
                                          splitMask[ind[k + 1]] &&
                                          splitMask[ind[k + 2]];
                            List<int> dst = isHair ? hairTris : headTris;
                            dst.Add(ind[k]);
                            dst.Add(ind[k + 1]);
                            dst.Add(ind[k + 2]);
                        }
                        if (hairTris.Count > 0)
                        {
                            hairGroups.Add(hairTris.ToArray());
                            hairMaterials.Add(md.SubMeshMaterials[s]);
                        }
                        kept.Add(headTris.ToArray());
                    }
                    subIndices = kept.ToArray();
                }

                Mesh mesh = new Mesh();
                mesh.name = md.Name;
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.vertices = OffsetDecals(md);
                mesh.normals = md.Normals;
                mesh.uv = md.Uvs;
                mesh.boneWeights = md.Weights;
                mesh.subMeshCount = subIndices.Length;

                for (int s = 0; s < subIndices.Length; s++)
                {
                    mesh.SetTriangles(subIndices[s], s);
                }

                Matrix4x4 smrLocalToWorld = smr.transform.localToWorldMatrix;
                Matrix4x4[] bindposes = new Matrix4x4[renderBones.Length];
                for (int i = 0; i < renderBones.Length; i++)
                {
                    bindposes[i] = renderBones[i].worldToLocalMatrix * smrLocalToWorld;
                }
                mesh.bindposes = bindposes;
                mesh.RecalculateBounds();

                Material[] meshMaterials = new Material[subIndices.Length];
                for (int s = 0; s < subIndices.Length; s++)
                {
                    int matIndex = md.SubMeshMaterials[s];
                    meshMaterials[s] = _materials[matIndex];
                    RecordPart(mesh, s, subIndices[s], matIndex);
                }

                smr.sharedMesh = mesh;
                smr.bones = renderBones;
                smr.rootBone = renderBones[rootBoneIndex];
                smr.sharedMaterials = meshMaterials;
                smr.updateWhenOffscreen = true;
                smr.shadowCastingMode = ShadowCastingMode.On;
                smr.receiveShadows = true;

                if (md.Role == MeshData.RoleHead)
                {
                    _headRenderer = smr;
                    if (hairIndices != null && hairIndices.Length > 0)
                    {
                        _hairRenderer = BuildHairRenderer(md, mesh.vertices, bindposes, renderBones,
                            rootBoneIndex, new int[][] { hairIndices }, new int[] { hairMaterial },
                            rendererLayer);
                    }
                    else if (hairGroups != null && hairGroups.Count > 0)
                    {
                        _hairRenderer = BuildHairRenderer(md, mesh.vertices, bindposes, renderBones,
                            rootBoneIndex, hairGroups.ToArray(), hairMaterials.ToArray(), rendererLayer);
                    }
                }
                else if (_bodyRenderer == null)
                {
                    _bodyRenderer = smr;
                }

                Logger.LogInfo(string.Format(
                    "{0}: layer={1} bounds center={2} size={3} submeshes={4} mat0={5}",
                    md.Name, meshGo.layer, smr.bounds.center, smr.bounds.size, subIndices.Length,
                    meshMaterials.Length > 0 ? meshMaterials[0].name : "none"));
            }

            // The game does not light dynamic objects from a global ambient: a
            // SkyApplier writes the ambient/IBL of the biome, or of the base
            // interior the player is currently in, into a per-renderer material
            // property block. Without one our meshes only get the very dark
            // global ambient, which is why the character went almost black
            // indoors. The Player broadcasts SkyEnvironmentChanged to every
            // SkyApplier below it when entering or leaving a base, so one
            // component on the container covers body, head and the split-off
            // tail renderer.
            try
            {
                Renderer[] lit = _container.GetComponentsInChildren<Renderer>(true);
                if (lit.Length > 0)
                {
                    SkyApplier sky = containerGo.AddComponent<SkyApplier>();
                    sky.renderers = lit;
                    sky.dynamic = true;
                    sky.anchorSky = Skies.Auto;
                    // The model can be attached while the player is already inside
                    // a base, long after the last enter/leave broadcast; ask for
                    // the current environment once so the first frame is lit too.
                    if (Player.main != null)
                    {
                        SkyEnvironmentChanged.Send(containerGo, Player.main.GetSkyEnvironment());
                    }
                    Logger.LogInfo("SkyApplier added for " + lit.Length + " renderer(s).");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not add SkyApplier: " + e.Message);
            }

            // Build the retargeting pairs.
            List<BonePair> pairList = new List<BonePair>(80);
            AddPairs(pairList, sources, bones, depths, boneIndexByName, targetRest);

            pairList.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            _pairs = pairList.ToArray();
            _deltas = new Quaternion[_pairs.Length];
            _prevPoseRot = new Quaternion[_pairs.Length];
            _prevSourceValid = false;
            _poseJumpLock = 0f;
            _poseTransition = 0f;
            _poseTransitionLength = 0f;
            _poseFromRot = null;
            _poseFromValid = false;
            _lastBaseRot = null;
            _lastBaseValid = false;

            _hipsSource = sources.Hips;
            if (_hipsSource != null)
            {
                _hipsRestLocalRot = _hipsSource.RestLocalRot;
                _hipsRefWorld = _hipsSource.Bone.position;
            }

            _headSourceBone = sources.Head != null ? sources.Head.Bone : null;

            _gameBodyRenderers.Clear();
            _gameBodyRenderers.AddRange(sources.GameBodyRenderers);
            _nextBodyCheck = 0f;

            _diagBones = bones;
            _diagIndexByName = boneIndexByName;
            _diagSources = sources;
            _nextPoseCheck = 0f;
            _poseSamples = 0;
            _nextToolLog = 0f;
            _toolLogSamples = 0;

            if (_verbose.Value)
            {
                Logger.LogInfo(string.Format("Retarget pairs: {0}", _pairs.Length));
            }

            // Part hiding and the PDA preview both work off the finished renderers.
            if (_container != null)
            {
                _previewRenderers = _container.GetComponentsInChildren<Renderer>(true);
                _previewLayers = new int[_previewRenderers.Length];
                _previewShadows = new ShadowCastingMode[_previewRenderers.Length];
                ApplyPartVisibility();
                _buildGeneration++;
            }

            // Cloth collision geometry: the body volume the hair and skirt press
            // against, and how thick the cloth shell is around its own bone line.
            // Both are measured now, while the rig is still in its bind pose.
            try
            {
                BuildBodySlices();
                MeasureClothPadding();
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not build cloth collision geometry: " + e.Message);
            }

            _errorLogged = false;
        }

        // ------------------------------------------------------------------
        // twintail physics
        // ------------------------------------------------------------------

        /// <summary>
        /// The vertex set that has to be drawn while the head is hidden. Prefers the
        /// simulated chain, which is the exact set of animated hair strands, and falls
        /// back to the long-hair set for models where no chain could be built.
        /// </summary>
        private bool[] SplitMaskFor(MeshData md)
        {
            if (md == null || md.Vertices == null)
            {
                return null;
            }
            // The long-hair set first: it covers every long hair shell, including the
            // ones bound to the model's own hair bones. The chain mask only knows the
            // vertices this plugin re-skinned, so on a model that has both kinds of
            // tail it would leave the authored ones welded to the head.
            // An all-false mask also means "nothing was marked" - fall through instead
            // of returning it.
            if (_hairVisibleMask != null && _hairVisibleMask.Length == md.Vertices.Length &&
                AnyTrue(_hairVisibleMask))
            {
                return _hairVisibleMask;
            }
            if (_clothVertexMask != null && _clothVertexMask.Length == md.Vertices.Length &&
                AnyTrue(_clothVertexMask))
            {
                return _clothVertexMask;
            }
            return null;
        }

        private static bool AnyTrue(bool[] mask)
        {
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Drops the top band of each twintail from the first-person visible set.
        /// In first person the camera sits inside the skull and the tails are
        /// rooted right behind the ears, so the top of the shell is what swings
        /// into the view when the head turns. The band is short enough that the
        /// rest of the tail still reads as hair; the cut ends up hidden behind
        /// the head. Only affects the first-person split (the head renderer), so
        /// the tails look unchanged in every other view.
        /// </summary>
        private static int HideHairRoots(MeshData md, bool[] visible)
        {
            // Roughly half a head height along the tail. A fixed length is fine:
            // every model is scaled to a human body before this runs.
            const float Band = 0.12f;
            float topLeft = float.MinValue;
            float topRight = float.MinValue;
            for (int v = 0; v < visible.Length; v++)
            {
                if (!visible[v])
                {
                    continue;
                }
                float y = md.Vertices[v].y;
                if (md.Vertices[v].x >= 0f)
                {
                    if (y > topLeft) topLeft = y;
                }
                else if (y > topRight)
                {
                    topRight = y;
                }
            }
            if (topLeft == float.MinValue && topRight == float.MinValue)
            {
                return 0;
            }
            int hidden = 0;
            for (int v = 0; v < visible.Length; v++)
            {
                if (!visible[v])
                {
                    continue;
                }
                float top = md.Vertices[v].x >= 0f ? topLeft : topRight;
                if (top != float.MinValue && md.Vertices[v].y > top - Band)
                {
                    visible[v] = false;
                    hidden++;
                }
            }
            return hidden;
        }

        /// <summary>
        /// Builds two chains of extra bones down the twintails and re-weights the tail
        /// vertices onto them. The chains are laid out from the tail geometry itself:
        /// the vertices are sliced by height and each slice's centre becomes a joint, so
        /// the bones follow the authored curve of the hair instead of a straight line.
        /// </summary>
        private void BuildHairRig(List<Transform> extraBones, Transform headBone, int headIndex)
        {
            if (headBone == null || headIndex < 0)
            {
                return;
            }

            MeshData md = null;
            for (int i = 0; i < _data.Meshes.Count; i++)
            {
                if (string.Equals(_data.Meshes[i].Name, "Miku_Head", StringComparison.OrdinalIgnoreCase) ||
                    _data.Meshes[i].Role == MeshData.RoleHead)
                {
                    md = _data.Meshes[i];
                    break;
                }
            }
            if (md == null || md.Vertices == null || md.Weights == null)
            {
                return;
            }

            bool[] hair = new bool[md.Vertices.Length];
            for (int s = 0; s < md.Indices.Length; s++)
            {
                string matName = MaterialName(md.SubMeshMaterials[s]);
                bool isHairMaterial = _imported
                    ? IsHairMaterialName(matName)
                    : string.Equals(matName, "Hair03", StringComparison.OrdinalIgnoreCase);
                if (!isHairMaterial)
                {
                    continue;
                }
                int[] ind = md.Indices[s];
                for (int k = 0; k < ind.Length; k++)
                {
                    hair[ind[k]] = true;
                }
            }

            // Every vertex that belongs to a hair material, before the vertices bound
            // to the model's own hair bones are taken out of the simulated set. The
            // renderer split below works from this copy.
            bool[] allHair = _imported ? (bool[])hair.Clone() : null;

            if (_imported)
            {
                // Vertices already driven by the model's own hair bones keep that
                // rig; only hair bound rigidly to the head needs one built here.
                bool[] hairBone = new bool[_data.Bones.Count];
                for (int i = 0; i < _data.Bones.Count; i++)
                {
                    hairBone[i] = PmxConverter.IsHairName(_data.Bones[i].Name);
                }
                for (int v = 0; v < hair.Length; v++)
                {
                    if (!hair[v])
                    {
                        continue;
                    }
                    BoneWeight w = md.Weights[v];
                    int best = w.boneIndex0;
                    float bestW = w.weight0;
                    if (w.weight1 > bestW) { bestW = w.weight1; best = w.boneIndex1; }
                    if (w.weight2 > bestW) { bestW = w.weight2; best = w.boneIndex2; }
                    if (w.weight3 > bestW) { bestW = w.weight3; best = w.boneIndex3; }
                    if (best >= 0 && best < hairBone.Length && hairBone[best])
                    {
                        hair[v] = false;
                    }
                }
            }

            // Hair is not one piece: it comes as separate connected shells. The cap
            // and the fringe are short shells wrapped around the head, while the
            // twintails are long shells that hang down the back. Only the long ones
            // may stay visible in first person - the cap is double sided, so if it
            // is drawn while the camera sits inside it, it fills the whole view.
            bool[] longVert = null;
            if (_imported)
            {
                int vcount = md.Vertices.Length;
                int[] parent = new int[vcount];
                for (int i = 0; i < vcount; i++)
                {
                    parent[i] = i;
                }
                for (int s = 0; s < md.Indices.Length; s++)
                {
                    if (!IsHairMaterialName(MaterialName(md.SubMeshMaterials[s])))
                    {
                        continue;
                    }
                    int[] ind = md.Indices[s];
                    for (int k = 0; k + 2 < ind.Length; k += 3)
                    {
                        if (ind[k] < 0 || ind[k] >= vcount || ind[k + 1] < 0 ||
                            ind[k + 1] >= vcount || ind[k + 2] < 0 || ind[k + 2] >= vcount)
                        {
                            continue;
                        }
                        UnionRoot(parent, ind[k], ind[k + 1]);
                        UnionRoot(parent, ind[k], ind[k + 2]);
                    }
                }

                float[] minY = new float[vcount];
                float[] maxY = new float[vcount];
                float[] minZ = new float[vcount];
                float[] maxZ = new float[vcount];
                int[] counts = new int[vcount];
                for (int v = 0; v < vcount; v++)
                {
                    if (!hair[v])
                    {
                        continue;
                    }
                    int r = FindRoot(parent, v);
                    Vector3 p = md.Vertices[v];
                    if (counts[r] == 0)
                    {
                        minY[r] = maxY[r] = p.y;
                        minZ[r] = maxZ[r] = p.z;
                    }
                    else
                    {
                        if (p.y < minY[r]) minY[r] = p.y;
                        if (p.y > maxY[r]) maxY[r] = p.y;
                        if (p.z < minZ[r]) minZ[r] = p.z;
                        if (p.z > maxZ[r]) maxZ[r] = p.z;
                    }
                    counts[r]++;
                }

                float headY = _armature.InverseTransformPoint(headBone.position).y;
                bool[] longShell = new bool[vcount];
                for (int v = 0; v < vcount; v++)
                {
                    if (!hair[v])
                    {
                        continue;
                    }
                    int r = FindRoot(parent, v);
                    // 30 vertices rather than a few hundred: the twintails are
                    // split into several thin ribbon shells, and a small 60-80
                    // vertex piece at the outside edge of each tail used to fall
                    // under the old threshold and stayed welded to the head -
                    // exactly one stiff strand on each side. The span test below
                    // is what keeps the scalp cap out, not the vertex count.
                    if (!longShell[r] && counts[r] >= 30 &&
                        minY[r] < headY - 0.25f &&
                        (maxY[r] - minY[r] > 0.45f || maxZ[r] - minZ[r] > 0.35f))
                    {
                        longShell[r] = true;
                    }
                }

                // longVert drives the simulation and includes the whole tail shell,
                // right up to the crown, and it is exactly the set that stays
                // visible while the head is hidden in first person. The crown slice
                // used to be cut off (y below the head origin only), which took away
                // the part of the tails closest to the camera and left them looking
                // clipped. The cap itself is a separate, short shell, so it stays on
                // the head renderer and is still hidden instead of filling the view.
                longVert = new bool[vcount];
                int tailCount = 0;
                for (int v = 0; v < vcount; v++)
                {
                    if (hair[v] && longShell[FindRoot(parent, v)])
                    {
                        longVert[v] = true;
                        tailCount++;
                    }
                }
                Logger.LogInfo("Imported hair shells: keeping " + tailCount +
                    " tail vertices visible (head y=" + headY.ToString("F2") + ").");

                // The same shell test, run over every hair vertex instead of only the
                // ones that need a simulated chain. Vertices that follow the model's
                // own hair bones never enter the chain, but the renderer split needs
                // them too: without this a model with a built-in hair rig kept its
                // whole hairstyle on the head mesh, and it vanished as soon as the
                // head was hidden in first person.
                float[] aMinY = new float[vcount];
                float[] aMaxY = new float[vcount];
                float[] aMinZ = new float[vcount];
                float[] aMaxZ = new float[vcount];
                int[] aCounts = new int[vcount];
                for (int v = 0; v < vcount; v++)
                {
                    if (!allHair[v])
                    {
                        continue;
                    }
                    int r = FindRoot(parent, v);
                    Vector3 p = md.Vertices[v];
                    if (aCounts[r] == 0)
                    {
                        aMinY[r] = aMaxY[r] = p.y;
                        aMinZ[r] = aMaxZ[r] = p.z;
                    }
                    else
                    {
                        if (p.y < aMinY[r]) aMinY[r] = p.y;
                        if (p.y > aMaxY[r]) aMaxY[r] = p.y;
                        if (p.z < aMinZ[r]) aMinZ[r] = p.z;
                        if (p.z > aMaxZ[r]) aMaxZ[r] = p.z;
                    }
                    aCounts[r]++;
                }
                bool[] longShellAll = new bool[vcount];
                for (int v = 0; v < vcount; v++)
                {
                    if (!allHair[v])
                    {
                        continue;
                    }
                    int r = FindRoot(parent, v);
                    if (!longShellAll[r] && aCounts[r] >= 30 &&
                        aMinY[r] < headY - 0.25f &&
                        (aMaxY[r] - aMinY[r] > 0.45f || aMaxZ[r] - aMinZ[r] > 0.35f))
                    {
                        longShellAll[r] = true;
                    }
                }
                bool[] visible = new bool[vcount];
                int visibleCount = 0;
                for (int v = 0; v < vcount; v++)
                {
                    if (allHair[v] && longShellAll[FindRoot(parent, v)])
                    {
                        visible[v] = true;
                        visibleCount++;
                    }
                }
                if (visibleCount == 0)
                {
                    // Hair that never came as a separate long shell - some models pack
                    // the scalp, the fringe and the tails into one piece. Keep only
                    // what hangs below the collarbone: the part around the head still
                    // hides with the head, so the view stays clear.
                    for (int v = 0; v < vcount; v++)
                    {
                        if (allHair[v] && md.Vertices[v].y < headY - 0.45f)
                        {
                            visible[v] = true;
                            visibleCount++;
                        }
                    }
                    Logger.LogInfo("Hair visibility: no long shell, keeping " +
                        visibleCount + " vertices below the collarbone.");
                }
                else
                {
                    // The tails themselves are fine; only the handful of vertices
                    // right at the root are a problem in first person. Trim those
                    // instead of touching the rest of the shell.
                    int rootHidden = HideHairRoots(md, visible);
                    if (rootHidden > 0)
                    {
                        Logger.LogInfo("Hair visibility: hid " + rootHidden +
                            " root vertices at the head for first person.");
                    }
                }
                _hairVisibleMask = visible;
            }

            // More joints than the old five: the tail needs to bend along its length
            // to look like hair, not swing around its root like a stiff plank.
            const int Segments = 8;
            const int Bins = Segments + 1;
            // One chain per tail. Three independent ribbon chains used to run down
            // each tail, but each simulated on its own: whenever the tail pointed up
            // (diving, or just after the flip) the three chains settled into different
            // branches and fanned apart by half a metre. The vertices are skinned
            // across those chains, so the sheet between them was stretched into long
            // spikes - far worse than moving the tail as one bundle. A static cross
            // section has no such failure mode.
            const int Strands = 1;
            int boneBase = _data.Bones.Count;
            int created = 0;
            List<int>[] binVerts = new List<int>[Bins];
            for (int b = 0; b < Bins; b++)
            {
                binVerts[b] = new List<int>();
            }
            // Only imported models need the mask: their tails sit in the same mesh as
            // the face, so the renderer has to be split afterwards.
            bool[] clothVerts = _imported ? new bool[md.Vertices.Length] : null;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? 1f : -1f;
                List<int> tail = new List<int>();
                float yMin = float.MaxValue;
                float yMax = float.MinValue;
                for (int v = 0; v < md.Vertices.Length; v++)
                {
                    // Imported models: only the long tail shells take part in the
                    // chain. The cap and the fringe lie on the scalp and must keep
                    // following the head exactly; binding them to a swinging chain
                    // is what turned the new models' hair into a mess. The built-in
                    // model has no long-shell mask and uses the whole hair mesh.
                    if (_imported ? !longVert[v] : !hair[v])
                    {
                        continue;
                    }
                    Vector3 p = md.Vertices[v];
                    if (p.x * sign <= 0.02f)
                    {
                        continue;
                    }
                    tail.Add(v);
                    if (p.y < yMin) yMin = p.y;
                    if (p.y > yMax) yMax = p.y;
                }
                if (tail.Count < 100 || yMax - yMin < 0.2f)
                {
                    continue;
                }
                float yRange = yMax - yMin;

                // Height slices. The centre of the whole tail inside a slice is the
                // spine that lateral offsets are measured from.
                Vector3[] spine = new Vector3[Bins];
                int[] spineHits = new int[Bins];
                for (int b = 0; b < Bins; b++)
                {
                    binVerts[b].Clear();
                }
                for (int t = 0; t < tail.Count; t++)
                {
                    int v = tail[t];
                    Vector3 p = md.Vertices[v];
                    int b = Mathf.FloorToInt((yMax - p.y) / yRange * Bins);
                    b = Mathf.Clamp(b, 0, Bins - 1);
                    binVerts[b].Add(v);
                    spine[b] += p;
                    spineHits[b]++;
                }
                for (int b = 0; b < Bins; b++)
                {
                    if (spineHits[b] > 0)
                    {
                        spine[b] /= spineHits[b];
                    }
                }
                FillEmptySlices(spine, spineHits);

                // The chain runs down the centre of the bundle. Splitting each slice
                // laterally into several strands is still supported by the weighting
                // below, but one chain per tail is what keeps the flat sheets from
                // being pulled apart.
                Vector3[,] center = new Vector3[Strands, Bins];
                int[,] centerHits = new int[Strands, Bins];
                for (int b = 0; b < Bins; b++)
                {
                    if (spineHits[b] == 0)
                    {
                        continue;
                    }
                    binVerts[b].Sort(delegate(int a, int c)
                    {
                        float ua = (md.Vertices[a].x - spine[b].x) * sign;
                        float uc = (md.Vertices[c].x - spine[b].x) * sign;
                        return ua.CompareTo(uc);
                    });
                    int n = binVerts[b].Count;
                    for (int s = 0; s < Strands; s++)
                    {
                        int a0 = s * n / Strands;
                        int a1 = (s + 1) * n / Strands;
                        Vector3 sum = Vector3.zero;
                        for (int k = a0; k < a1; k++)
                        {
                            sum += md.Vertices[binVerts[b][k]];
                        }
                        if (a1 > a0)
                        {
                            center[s, b] = sum / (a1 - a0);
                            centerHits[s, b] = a1 - a0;
                        }
                        else
                        {
                            center[s, b] = spine[b];
                        }
                    }
                }
                for (int s = 0; s < Strands; s++)
                {
                    Vector3[] one = new Vector3[Bins];
                    int[] oneHits = new int[Bins];
                    for (int b = 0; b < Bins; b++)
                    {
                        one[b] = centerHits[s, b] > 0 ? center[s, b] : spine[b];
                        oneHits[b] = centerHits[s, b];
                    }
                    FillEmptySlices(one, oneHits);
                    for (int b = 0; b < Bins; b++)
                    {
                        center[s, b] = one[b];
                    }
                }

                int sideBase = boneBase + created * Segments;
                for (int s = 0; s < Strands; s++)
                {
                    // Create the bone chain under the head bone, in the rest pose.
                    Transform parent = headBone;
                    Transform[] chainBones = new Transform[Segments];
                    Vector3[] jointWorld = new Vector3[Bins];
                    for (int k = 0; k < Bins; k++)
                    {
                        jointWorld[k] = _armature.TransformPoint(center[s, k]);
                    }
                    for (int k = 0; k < Segments; k++)
                    {
                        GameObject go = new GameObject("miku_hair_" + (side == 0 ? "L" : "R")
                            + s + "_" + k);
                        go.layer = _container.gameObject.layer;
                        Transform t = go.transform;
                        t.SetParent(parent, false);
                        t.position = jointWorld[k];
                        t.localScale = Vector3.one;
                        parent = t;
                        chainBones[k] = t;
                        extraBones.Add(t);
                    }

                    ClothChain chain = new ClothChain();
                    chain.Name = "Hair " + (side == 0 ? "L" : "R") + s;
                    chain.Root = headBone;
                    chain.RestRootRot = headBone.rotation;
                    // A different phase per side, so the two tails do not sway in
                    // lockstep.
                    chain.FlowPhase = (side == 0 ? 0.4f : 2.7f) + s * 1.9f;
                    chain.FlowScale = 0.92f + 0.06f * s;
                    chain.StiffnessScale = 1f - 0.08f * Mathf.Abs(s - 1);
                    chain.Bones = chainBones;
                    chain.RestLocal = new Vector3[Bins];
                    chain.RestDirLocal = new Vector3[Segments];
                    chain.RestDirWorld = new Vector3[Segments];
                    chain.RestUpWorld = new Vector3[Segments];
                    chain.RestRotWorld = new Quaternion[Segments];
                    chain.RestBend = new Quaternion[Segments - 1];
                    chain.Length = new float[Segments];
                    chain.Points = new Vector3[Bins];
                    chain.Prev = new Vector3[Bins];
                    for (int k = 0; k < Bins; k++)
                    {
                        chain.RestLocal[k] = headBone.InverseTransformPoint(jointWorld[k]);
                        chain.Points[k] = jointWorld[k];
                        chain.Prev[k] = jointWorld[k];
                    }
                    float chainLength = 0f;
                    for (int k = 0; k < Segments; k++)
                    {
                        Vector3 dir = jointWorld[k + 1] - jointWorld[k];
                        chain.Length[k] = dir.magnitude;
                        chainLength += chain.Length[k];
                        chain.RestDirLocal[k] =
                            Quaternion.Inverse(chainBones[k].rotation) * dir.normalized;
                        // The bind pose, frozen once. The simulated points live in
                        // world space, so the bone must be posed relative to this
                        // fixed reference; anything relative to the *current* head
                        // rotation turns the player's accumulated yaw into a roll
                        // around the tail's own length (the spinning tips).
                        chain.RestDirWorld[k] = dir.normalized;
                        chain.RestRotWorld[k] = chainBones[k].rotation;
                    }
                    BuildRestFrames(chain.RestDirWorld, chain.RestUpWorld);
                    // The rest turn between consecutive segments is what the solver
                    // treats as the tail's "shape": keeping it preserves the authored
                    // curl, while the chain is still free to swing as a whole.
                    for (int k = 0; k < Segments - 1; k++)
                    {
                        chain.RestBend[k] = Quaternion.FromToRotation(
                            chain.RestDirLocal[k], chain.RestDirLocal[k + 1]);
                    }
                    if (_verbose.Value)
                    {
                        Logger.LogInfo("Hair chain " + chain.Name + ": joints=" + Bins
                            + " length=" + chainLength.ToString("F3")
                            + " headScale=" + headBone.lossyScale
                            + " containerScale=" + _container.lossyScale);
                    }
                    CaptureRestRot(chain);
                    chain.Hair = true;
                    _clothChains.Add(chain);
                    created++;
                }

                // Re-weight the tail vertices: by height along the chain (blending the
                // top into the head so the tail stays attached) and across the sheet
                // between the two nearest strands, so neighbouring ribbons share
                // deformation instead of tearing apart at a hard seam.
                for (int t = 0; t < tail.Count; t++)
                {
                    int v = tail[t];
                    Vector3 p = md.Vertices[v];
                    float f = Mathf.Clamp01((yMax - p.y) / yRange);
                    float pos = f * Segments;
                    int b0 = Mathf.Clamp(Mathf.FloorToInt(pos), 0, Segments);
                    int b1 = Mathf.Clamp(b0 + 1, 0, Segments);
                    float frac = Mathf.Clamp01(pos - b0);

                    // Strand offsets at this vertex's height, interpolated between
                    // the two nearest slices.
                    float fc = Mathf.Clamp((yMax - p.y) / yRange * Bins - 0.5f, 0f, Bins - 1f);
                    int i0 = Mathf.FloorToInt(fc);
                    int i1 = Mathf.Min(i0 + 1, Bins - 1);
                    float tf = fc - i0;
                    float spx = Mathf.Lerp(spine[i0].x, spine[i1].x, tf);
                    float u = (p.x - spx) * sign;
                    float[] cu = new float[Strands];
                    for (int s = 0; s < Strands; s++)
                    {
                        float cx = Mathf.Lerp(center[s, i0].x, center[s, i1].x, tf);
                        cu[s] = (cx - spx) * sign;
                        if (s > 0 && cu[s] < cu[s - 1] + 1e-4f)
                        {
                            cu[s] = cu[s - 1] + 1e-4f;
                        }
                    }
                    float[] cw = new float[Strands];
                    if (u <= cu[0])
                    {
                        cw[0] = 1f;
                    }
                    else if (u >= cu[Strands - 1])
                    {
                        cw[Strands - 1] = 1f;
                    }
                    else
                    {
                        for (int s = 0; s < Strands - 1; s++)
                        {
                            if (u <= cu[s + 1])
                            {
                                float span = Mathf.Max(1e-4f, cu[s + 1] - cu[s]);
                                float w = Mathf.Clamp01((u - cu[s]) / span);
                                cw[s] = 1f - w;
                                cw[s + 1] = w;
                                break;
                            }
                        }
                    }

                    int[] boneIdx = new int[4];
                    float[] boneW = new float[4];
                    for (int s = 0; s < Strands; s++)
                    {
                        if (cw[s] <= 1e-5f)
                        {
                            continue;
                        }
                        int bA = b0 == 0 ? headIndex : sideBase + s * Segments + (b0 - 1);
                        int bB = b1 == 0 ? headIndex : sideBase + s * Segments + (b1 - 1);
                        AddWeight(boneIdx, boneW, bA, cw[s] * (1f - frac));
                        AddWeight(boneIdx, boneW, bB, cw[s] * frac);
                    }
                    float total = boneW[0] + boneW[1] + boneW[2] + boneW[3];
                    if (total <= 1e-5f)
                    {
                        boneIdx[0] = headIndex;
                        boneW[0] = 1f;
                        total = 1f;
                    }

                    BoneWeight bw = new BoneWeight();
                    bw.boneIndex0 = boneIdx[0];
                    bw.weight0 = boneW[0] / total;
                    bw.boneIndex1 = boneIdx[1];
                    bw.weight1 = boneW[1] / total;
                    bw.boneIndex2 = boneIdx[2];
                    bw.weight2 = boneW[2] / total;
                    bw.boneIndex3 = boneIdx[3];
                    bw.weight3 = boneW[3] / total;
                    md.Weights[v] = bw;
                    if (clothVerts != null)
                    {
                        clothVerts[v] = true;
                    }
                }

                Logger.LogInfo(string.Format(
                    "Hair physics: {0} chain(s) x {1} bones from {2} tail vertices (y {3:F2}..{4:F2})",
                    Strands, Segments, tail.Count, yMin, yMax));
            }

            if (clothVerts != null)
            {
                // Visible everywhere the simulated chain drives a long hair shell:
                // the whole tail stays drawn in first person, right up to the crown.
                _clothVertexMask = clothVerts;
            }
        }

        private static int FindRoot(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void UnionRoot(int[] parent, int a, int b)
        {
            int ra = FindRoot(parent, a);
            int rb = FindRoot(parent, b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        /// <summary>
        /// Adds one bone influence, merging duplicates. Used while skinning the hair
        /// across three strands x two height bones (four influences per vertex).
        /// </summary>
        private static void AddWeight(int[] bone, float[] weight, int index, float w)
        {
            if (w <= 1e-5f)
            {
                return;
            }
            for (int i = 0; i < bone.Length; i++)
            {
                if (weight[i] <= 0f)
                {
                    bone[i] = index;
                    weight[i] = w;
                    return;
                }
                if (bone[i] == index)
                {
                    weight[i] += w;
                    return;
                }
            }
        }

        /// <summary>
        /// Builds a twist-free reference frame for each rest segment: a vector
        /// perpendicular to the segment, carried from joint to joint by the same
        /// parallel transport the runtime uses. At rest the transported frame equals
        /// this one, so the bone pose is exactly the bind pose.
        /// </summary>
        private static void BuildRestFrames(Vector3[] dir, Vector3[] up)
        {
            Vector3 reference = Mathf.Abs(Vector3.Dot(dir[0], Vector3.up)) < 0.9f
                ? Vector3.up
                : Vector3.forward;
            Vector3 cur = reference;
            for (int k = 0; k < dir.Length; k++)
            {
                if (k > 0)
                {
                    cur = Quaternion.FromToRotation(dir[k - 1], dir[k]) * cur;
                }
                Vector3 u = cur - dir[k] * Vector3.Dot(cur, dir[k]);
                if (u.sqrMagnitude < 1e-6f)
                {
                    u = Vector3.Cross(dir[k], Vector3.forward);
                    if (u.sqrMagnitude < 1e-6f)
                    {
                        u = Vector3.Cross(dir[k], Vector3.right);
                    }
                }
                cur = u.normalized;
                up[k] = cur;
            }
        }

        /// <summary>
        /// Gives empty height slices a position by interpolating between the nearest
        /// populated ones, so a chain stays continuous where the mesh has a gap.
        /// </summary>
        private static void FillEmptySlices(Vector3[] joint, int[] hits)
        {
            int first = -1;
            for (int b = 0; b < hits.Length; b++)
            {
                if (hits[b] > 0)
                {
                    first = b;
                    break;
                }
            }
            if (first < 0)
            {
                return;
            }
            for (int b = 0; b < first; b++)
            {
                joint[b] = joint[first];
            }
            int filled = first;
            for (int b = first + 1; b < hits.Length; b++)
            {
                if (hits[b] <= 0)
                {
                    continue;
                }
                for (int k = filled + 1; k < b; k++)
                {
                    joint[k] = Vector3.Lerp(joint[filled], joint[b],
                        (float)(k - filled) / (b - filled));
                }
                filled = b;
            }
            for (int b = filled + 1; b < hits.Length; b++)
            {
                joint[b] = joint[filled];
            }
        }

        /// <summary>
        /// Builds a ring of short chains around the skirt and re-weights the skirt vertices
        /// onto them. The seams between neighbouring chains are blended by angle, and the
        /// waist stays pinned to the hips, so the skirt keeps its shape while the hem can
        /// swing behind the player's motion.
        /// </summary>
        private void BuildSkirtRig(List<Transform> extraBones, Transform[] bones,
            Dictionary<string, int> boneIndexByName)
        {
            int hipsIndex;
            if (!boneIndexByName.TryGetValue("hips", out hipsIndex))
            {
                if (!boneIndexByName.TryGetValue("spine_1", out hipsIndex))
                {
                    return;
                }
            }
            Transform hips = bones[hipsIndex];

            MeshData md = null;
            for (int i = 0; i < _data.Meshes.Count; i++)
            {
                if (string.Equals(_data.Meshes[i].Name, "Miku_Body", StringComparison.OrdinalIgnoreCase))
                {
                    md = _data.Meshes[i];
                    break;
                }
            }
            if (md == null || md.Vertices == null || md.Weights == null)
            {
                return;
            }

            bool[] skirt = new bool[md.Vertices.Length];
            int count = 0;
            for (int s = 0; s < md.Indices.Length; s++)
            {
                string mat = MaterialName(md.SubMeshMaterials[s]);
                if (!(string.Equals(mat, "Skirt", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(mat, "Skirt1", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(mat, "Skirt+", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                int[] ind = md.Indices[s];
                for (int k = 0; k < ind.Length; k++)
                {
                    if (!skirt[ind[k]])
                    {
                        skirt[ind[k]] = true;
                        count++;
                    }
                }
            }
            if (count < 100)
            {
                return;
            }

            float yMin = float.MaxValue;
            float yMax = float.MinValue;
            for (int v = 0; v < md.Vertices.Length; v++)
            {
                if (!skirt[v])
                {
                    continue;
                }
                float y = md.Vertices[v].y;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
            if (yMax - yMin < 0.1f)
            {
                return;
            }

            const int Sectors = 8;
            const int Segments = 3;
            const int Bins = Segments + 1;
            float step = Mathf.PI * 2f / Sectors;

            // Centre of each height slice, per angular sector.
            Vector3[,] joint = new Vector3[Sectors, Bins];
            int[,] hits = new int[Sectors, Bins];
            for (int v = 0; v < md.Vertices.Length; v++)
            {
                if (!skirt[v])
                {
                    continue;
                }
                Vector3 p = md.Vertices[v];
                int sec = Mathf.RoundToInt(Mathf.Atan2(p.x, p.z) / step);
                sec = ((sec % Sectors) + Sectors) % Sectors;
                int b = Mathf.Clamp(
                    Mathf.FloorToInt((yMax - p.y) / (yMax - yMin) * Bins), 0, Bins - 1);
                joint[sec, b] += p;
                hits[sec, b]++;
            }

            // A sector without geometry still needs a chain: the weighting blends every
            // vertex towards its two neighbouring chains, so all of them must exist.
            for (int sec = 0; sec < Sectors; sec++)
            {
                int populated = 0;
                for (int b = 0; b < Bins; b++)
                {
                    if (hits[sec, b] > 0)
                    {
                        joint[sec, b] /= hits[sec, b];
                        populated++;
                    }
                }
                if (populated >= 2)
                {
                    continue;
                }
                for (int d = 1; d <= Sectors / 2; d++)
                {
                    int other = ((sec + d) % Sectors + Sectors) % Sectors;
                    int otherCount = 0;
                    for (int b = 0; b < Bins; b++)
                    {
                        if (hits[other, b] > 0) otherCount++;
                    }
                    if (otherCount >= 2)
                    {
                        for (int b = 0; b < Bins; b++)
                        {
                            joint[sec, b] = joint[other, b];
                        }
                        break;
                    }
                }
            }

            int boneBase = _data.Bones.Count;
            int created = 0;
            for (int sec = 0; sec < Sectors; sec++)
            {
                // Empty slices take an interpolated position between their neighbours.
                int first = -1;
                for (int b = 0; b < Bins; b++)
                {
                    if (hits[sec, b] > 0)
                    {
                        first = b;
                        break;
                    }
                }
                if (first >= 0)
                {
                    for (int b = 0; b < first; b++)
                    {
                        joint[sec, b] = joint[sec, first];
                    }
                    int filled = first;
                    for (int b = first + 1; b < Bins; b++)
                    {
                        if (hits[sec, b] <= 0)
                        {
                            continue;
                        }
                        for (int k = filled + 1; k < b; k++)
                        {
                            joint[sec, k] = Vector3.Lerp(joint[sec, filled], joint[sec, b],
                                (float)(k - filled) / (b - filled));
                        }
                        filled = b;
                    }
                    for (int b = filled + 1; b < Bins; b++)
                    {
                        joint[sec, b] = joint[sec, filled];
                    }
                }

                Transform parent = hips;
                Transform[] chainBones = new Transform[Segments];
                Vector3[] jointWorld = new Vector3[Bins];
                for (int k = 0; k < Bins; k++)
                {
                    jointWorld[k] = _armature.TransformPoint(joint[sec, k]);
                }
                for (int k = 0; k < Segments; k++)
                {
                    GameObject go = new GameObject("miku_skirt_" + sec + "_" + k);
                    go.layer = _container.gameObject.layer;
                    Transform t = go.transform;
                    t.SetParent(parent, false);
                    t.position = jointWorld[k];
                    t.localScale = Vector3.one;
                    parent = t;
                    chainBones[k] = t;
                    extraBones.Add(t);
                }

                ClothChain chain = new ClothChain();
                chain.Name = "Skirt " + sec;
                chain.Root = hips;
                chain.RestRootRot = hips.rotation;
                chain.Bones = chainBones;
                chain.RestLocal = new Vector3[Bins];
                chain.RestDirLocal = new Vector3[Segments];
                chain.RestDirWorld = new Vector3[Segments];
                chain.RestUpWorld = new Vector3[Segments];
                chain.RestRotWorld = new Quaternion[Segments];
                chain.RestBend = new Quaternion[Segments - 1];
                chain.Length = new float[Segments];
                chain.Points = new Vector3[Bins];
                chain.Prev = new Vector3[Bins];
                chain.Skirt = true;
                chain.Cloth = true;
                chain.StiffnessScale = 2.4f;
                chain.FlowScale = 0.35f;
                chain.GravityScale = 1.3f;
                chain.FlowPhase = sec * 0.9f;
                for (int k = 0; k < Bins; k++)
                {
                    chain.RestLocal[k] = hips.InverseTransformPoint(jointWorld[k]);
                    chain.Points[k] = jointWorld[k];
                    chain.Prev[k] = jointWorld[k];
                }
                float totalLen = 0f;
                for (int k = 0; k < Segments; k++)
                {
                    Vector3 dir = jointWorld[k + 1] - jointWorld[k];
                    chain.Length[k] = dir.magnitude;
                    totalLen += chain.Length[k];
                    chain.RestDirLocal[k] = Quaternion.Inverse(chainBones[k].rotation) * dir.normalized;
                    chain.RestDirWorld[k] = dir.normalized;
                    chain.RestRotWorld[k] = chainBones[k].rotation;
                }
                // Same leash as the imported skirts: sway freely, but never fold
                // the panel up over the hip on a sudden turn.
                float lagMax = Mathf.Max(0.5f, _container.lossyScale.x);
                chain.MaxLag = Mathf.Clamp(totalLen * 0.32f, 0.03f * lagMax,
                    0.12f * lagMax);
                BuildRestFrames(chain.RestDirWorld, chain.RestUpWorld);
                for (int k = 0; k < Segments - 1; k++)
                {
                    chain.RestBend[k] = Quaternion.FromToRotation(
                        chain.RestDirLocal[k], chain.RestDirLocal[k + 1]);
                }
                _clothChains.Add(chain);
                created++;
            }
            if (created == 0)
            {
                return;
            }

            // Re-weight the skirt: vertical blend along each chain, angular blend between
            // the two nearest chains.
            for (int v = 0; v < md.Vertices.Length; v++)
            {
                if (!skirt[v])
                {
                    continue;
                }
                Vector3 p = md.Vertices[v];
                float u = Mathf.Atan2(p.x, p.z) / step + 0.5f;
                int i0 = Mathf.FloorToInt(u);
                float af = u - i0;
                int i1 = i0 + 1;
                i0 = ((i0 % Sectors) + Sectors) % Sectors;
                i1 = ((i1 % Sectors) + Sectors) % Sectors;
                if (af < 0f) af = 0f;
                if (af > 1f) af = 1f;

                float f = Mathf.Clamp01((yMax - p.y) / (yMax - yMin));
                float pos = f * Segments;
                int b0 = Mathf.Clamp(Mathf.FloorToInt(pos), 0, Segments);
                int b1 = Mathf.Clamp(b0 + 1, 0, Segments);
                float vf = Mathf.Clamp01(pos - b0);

                int a0 = b0 == 0 ? hipsIndex : boneBase + i0 * Segments + (b0 - 1);
                int a1 = b1 == 0 ? hipsIndex : boneBase + i0 * Segments + (b1 - 1);
                int c0 = b0 == 0 ? hipsIndex : boneBase + i1 * Segments + (b0 - 1);
                int c1 = b1 == 0 ? hipsIndex : boneBase + i1 * Segments + (b1 - 1);

                BoneWeight bw = new BoneWeight();
                bw.boneIndex0 = a0;
                bw.weight0 = (1f - af) * (1f - vf);
                bw.boneIndex1 = a1;
                bw.weight1 = (1f - af) * vf;
                bw.boneIndex2 = c0;
                bw.weight2 = af * (1f - vf);
                bw.boneIndex3 = c1;
                bw.weight3 = af * vf;
                md.Weights[v] = bw;
            }

            Logger.LogInfo(string.Format(
                "Skirt physics: {0} chains of {1} bones from {2} skirt vertices (y {3:F2}..{4:F2})",
                created, Segments, count, yMin, yMax));
        }

        /// <summary>
        /// Splits the twintails into their own skinned renderer. They share the vertex data
        /// (and therefore the re-weighted hair bones) with the head mesh, but live separately
        /// so they can keep rendering while the head itself is hidden in first person.
        /// </summary>
        private SkinnedMeshRenderer BuildHairRenderer(MeshData md, Vector3[] vertices,
            Matrix4x4[] bindposes, Transform[] renderBones, int rootBoneIndex,
            int[][] triangleGroups, int[] materialIndices, int layer)
        {
            GameObject go = new GameObject("Miku_Hair");
            go.layer = layer;
            go.transform.SetParent(_armature, false);

            SkinnedMeshRenderer smr = go.AddComponent<SkinnedMeshRenderer>();

            Mesh mesh = new Mesh();
            mesh.name = "Miku_Hair";
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.normals = md.Normals;
            mesh.uv = md.Uvs;
            mesh.boneWeights = md.Weights;
            mesh.subMeshCount = triangleGroups.Length;
            for (int s = 0; s < triangleGroups.Length; s++)
            {
                mesh.SetTriangles(triangleGroups[s], s);
            }
            mesh.bindposes = bindposes;
            mesh.RecalculateBounds();

            Material[] materials = new Material[triangleGroups.Length];
            for (int s = 0; s < triangleGroups.Length; s++)
            {
                materials[s] = _materials[materialIndices[s]];
                RecordPart(mesh, s, triangleGroups[s], materialIndices[s]);
            }

            smr.sharedMesh = mesh;
            smr.bones = renderBones;
            smr.rootBone = renderBones[rootBoneIndex];
            smr.sharedMaterials = materials;
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.receiveShadows = true;
            return smr;
        }

        /// <summary>
        /// One step of the cloth simulation (twintails and skirt panels). Each joint is a
        /// Verlet particle with anisotropic drag, so the cloth trails when the player
        /// turns or swims and barely sinks underwater. Per-joint bending stiffness keeps
        /// the authored curve while letting drag accumulate along the strand, which is
        /// what makes long hair trail and float instead of swinging as one stiff piece.
        /// </summary>
        /// <summary>
        /// Generic hair physics for imported models. MMD models usually ship a hair
        /// bone chain (twintail, ponytail, sidelocks) under the head, and the mesh is
        /// already weighted to those bones, so the chain is simulated in place instead
        /// of building a new rig from the mesh geometry.
        /// </summary>
        /// <summary>
        /// MMD models usually stand in an A-pose, while the game's suit skeleton rests
        /// with the upper arms much closer to the body. The retarget only carries over
        /// rotation deltas, so without this the imported arms would hang 25-30 degrees
        /// too wide. Rotate each upper arm onto the game bind pose and bake the same
        /// rotation into the mesh (the bind poses are rebuilt afterwards, which would
        /// otherwise leave the mesh exactly where it was).
        /// </summary>
        private void BakeImportedArmPose(Transform[] bones, Dictionary<string, int> index,
                                         SourceMap sources)
        {
            if (sources == null || _armature == null)
            {
                return;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                string side = pass == 0 ? "L" : "R";
                int shoulder;
                int elbow;
                if (!index.TryGetValue("shoulder_" + side, out shoulder) ||
                    !index.TryGetValue("elbow_" + side, out elbow))
                {
                    continue;
                }

                SourceBone srcShoulder;
                SourceBone srcElbow;
                if (!sources.Bones.TryGetValue("shoulder_" + side, out srcShoulder) ||
                    !sources.Bones.TryGetValue("elbow_" + side, out srcElbow))
                {
                    continue;
                }

                // Take the game's rest pose from the bind matrices, not from the live
                // bones: the player may already be swimming when the model is built.
                Vector3 gameDir =
                    srcElbow.SmrTransform.TransformPoint(srcElbow.RestLocalPos) -
                    srcShoulder.SmrTransform.TransformPoint(srcShoulder.RestLocalPos);
                Vector3 pivot = bones[shoulder].position;
                Vector3 modelDir = bones[elbow].position - pivot;
                if (gameDir.sqrMagnitude < 1e-8f || modelDir.sqrMagnitude < 1e-8f)
                {
                    continue;
                }

                Quaternion qWorld = Quaternion.FromToRotation(modelDir, gameDir);
                float angle = Quaternion.Angle(Quaternion.identity, qWorld);
                if (angle < 0.5f)
                {
                    continue;
                }

                bones[shoulder].rotation = qWorld * bones[shoulder].rotation;

                if (_data.ArmPoseBaked)
                {
                    // Vertices were already rotated by an earlier build; only the fresh
                    // bone hierarchy needs the rotation.
                    continue;
                }

                Quaternion armRot = _armature.rotation;
                Quaternion qLocal = Quaternion.Inverse(armRot) * qWorld * armRot;
                Vector3 pivotLocal = _armature.InverseTransformPoint(pivot);

                bool[] inArm = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    int p = i;
                    int guard = 0;
                    while (p >= 0 && p < bones.Length && guard++ <= bones.Length)
                    {
                        if (p == shoulder)
                        {
                            inArm[i] = true;
                            break;
                        }
                        p = _data.Bones[p].Parent;
                    }
                }

                for (int mi = 0; mi < _data.Meshes.Count; mi++)
                {
                    MeshData md = _data.Meshes[mi];
                    if (md.Vertices == null || md.Weights == null || md.Normals == null)
                    {
                        continue;
                    }
                    for (int v = 0; v < md.Vertices.Length; v++)
                    {
                        BoneWeight w = md.Weights[v];
                        float amount = 0f;
                        if (w.weight0 > 0f && inArm[w.boneIndex0]) amount += w.weight0;
                        if (w.weight1 > 0f && inArm[w.boneIndex1]) amount += w.weight1;
                        if (w.weight2 > 0f && inArm[w.boneIndex2]) amount += w.weight2;
                        if (w.weight3 > 0f && inArm[w.boneIndex3]) amount += w.weight3;
                        if (amount <= 1e-4f)
                        {
                            continue;
                        }
                        Vector3 offset = md.Vertices[v] - pivotLocal;
                        md.Vertices[v] = md.Vertices[v] + amount * (qLocal * offset - offset);
                        Vector3 n = md.Normals[v];
                        md.Normals[v] = Vector3.Normalize(n + amount * (qLocal * n - n));
                    }
                }

                Logger.LogInfo(string.Format(
                    "Imported arm {0}: rotated {1:F1} deg onto the game bind pose", side, angle));
            }

            _data.ArmPoseBaked = true;
        }

        /// <summary>
        /// Builds secondary-motion strands for imported models from the PMX's own
        /// bone chains. Hair and hanging accessories (neckties, scarves, capes) are
        /// both handled: any maximal run of chain-named bones becomes one strand.
        /// A branch point starts a new strand, so a fringe fan simulates every
        /// strand instead of only the longest one, and no bone is driven twice.
        /// </summary>
        private void BuildImportedHairRig(Transform[] bones)
        {
            if (bones == null || _data == null || _data.Bones.Count != bones.Length)
            {
                return;
            }

            int n = bones.Length;
            bool[] chain = new bool[n];
            int chainBones = 0;
            int hairBones = 0;
            int skirtBones = 0;
            int clothesBones = 0;
            for (int i = 0; i < n; i++)
            {
                string name = _data.Bones[i].Name;
                if (!PmxConverter.IsPhysicsChainName(name))
                {
                    continue;
                }
                chain[i] = true;
                chainBones++;
                if (PmxConverter.IsHairName(name))
                {
                    hairBones++;
                }
                else if (PmxConverter.IsSkirtName(name))
                {
                    skirtBones++;
                }
                else if (PmxConverter.IsClothesName(name))
                {
                    clothesBones++;
                }
            }
            if (chainBones == 0)
            {
                return;
            }

            List<int>[] children = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                if (!chain[i])
                {
                    continue;
                }
                int p = _data.Bones[i].Parent;
                if (p >= 0 && p < n && chain[p])
                {
                    if (children[p] == null)
                    {
                        children[p] = new List<int>();
                    }
                    children[p].Add(i);
                }
            }

            int made = 0;
            int lightMade = 0;
            int skirtMade = 0;
            List<int> strand = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!chain[i])
                {
                    continue;
                }
                // A strand starts at a chain bone that either hangs off the body
                // (its parent is not simulated) or is one of several children of a
                // branch. Anything else is the middle of an existing strand.
                int parent = _data.Bones[i].Parent;
                if (parent >= 0 && parent < n && chain[parent] &&
                    ChainChildren(children[parent], chain) < 2)
                {
                    continue;
                }

                strand.Clear();
                int cur = i;
                while (cur >= 0)
                {
                    strand.Add(cur);
                    List<int> kids = children[cur];
                    if (ChainChildren(kids, chain) != 1)
                    {
                        break;
                    }
                    for (int k = 0; k < kids.Count; k++)
                    {
                        if (chain[kids[k]])
                        {
                            cur = kids[k];
                            break;
                        }
                    }
                }

                // One bone has no direction to aim at; two is the shortest chain
                // that does. MMD fringes are often just 髪 / 髪先 pairs.
                bool lightStrand;
                if (strand.Count >= 2 &&
                    AddImportedHairChain(strand, bones, made, out lightStrand))
                {
                    made++;
                    if (lightStrand)
                    {
                        lightMade++;
                    }
                    else if (PmxConverter.IsSkirtName(_data.Bones[strand[0]].Name))
                    {
                        skirtMade++;
                    }
                }
            }

            Logger.LogInfo("Imported secondary motion: " + made + " strands over "
                + chainBones + " chain bones (" + hairBones + " hair, "
                + lightMade + " light, " + skirtBones + " skirt bones in "
                + skirtMade + " strands, " + clothesBones + " clothes bones, "
                + (chainBones - hairBones - skirtBones - clothesBones)
                + " accessory bones).");
        }

        private static int ChainChildren(List<int> kids, bool[] chain)
        {
            if (kids == null)
            {
                return 0;
            }
            int count = 0;
            for (int k = 0; k < kids.Count; k++)
            {
                if (chain[kids[k]])
                {
                    count++;
                }
            }
            return count;
        }

        private bool AddImportedHairChain(List<int> chain, Transform[] bones, int index,
            out bool light)
        {
            light = false;
            int segments = chain.Count;
            Transform[] chainBones = new Transform[segments];
            for (int k = 0; k < segments; k++)
            {
                chainBones[k] = bones[chain[k]];
                if (chainBones[k] == null)
                {
                    return false;
                }
            }

            Transform root = chainBones[0].parent;
            if (root == null)
            {
                return false;
            }

            int bins = segments + 1;
            Vector3[] joint = new Vector3[bins];
            for (int k = 0; k < segments; k++)
            {
                joint[k] = chainBones[k].position;
            }
            Vector3 tail = chainBones[segments - 1].position -
                           chainBones[segments - 2].position;
            float tailLen = tail.magnitude;
            if (tailLen < 1e-4f)
            {
                return false;
            }
            joint[segments] = chainBones[segments - 1].position + tail;

            float total = 0f;
            for (int k = 0; k < segments; k++)
            {
                float len = Vector3.Distance(joint[k], joint[k + 1]);
                if (len < 0.002f)
                {
                    return false;
                }
                total += len;
            }

            // Three kinds of chain, decided by name and length:
            //  * accessories (necktie, strap, scarf) swing like a tail;
            //  * fringe and sideburns (前髪, もみあげ, アホ毛) drift slightly;
            //  * everything else that is hair is a scalp layer or a short bob
            //    unless it is long enough to be a real tail. Scalp hair is left
            //    rigid - if it is bound to the solver it crawls over the skull.
            string rootName = _data.Bones[chain[0]].Name;
            bool hair = PmxConverter.IsHairName(rootName);
            bool skirt = PmxConverter.IsSkirtName(rootName);
            // Checked after the skirt: "petticoat" contains "coat".
            bool clothes = !hair && !skirt && PmxConverter.IsClothesName(rootName);
            light = hair && PmxConverter.IsLightHairName(rootName);
            float minTotal = hair ? (light ? 0.03f : 0.12f) : 0.02f;
            if (total < minTotal)
            {
                return false;
            }
            if (hair && !light && total < 0.30f)
            {
                return false;
            }
            if (hair && !light && _headBone != null)
            {
                // Even a long bone chain is scalp hair if it never leaves the
                // skull: a bob or a hair cap that follows the head round. Only a
                // strand that reaches away from the head may swing.
                float reach = 0.24f * Mathf.Max(0.5f, _container.lossyScale.x);
                bool hugs = true;
                for (int k = 0; k <= segments; k++)
                {
                    if ((joint[k] - _headBone.position).sqrMagnitude > reach * reach)
                    {
                        hugs = false;
                        break;
                    }
                }
                if (hugs)
                {
                    return false;
                }
            }

            ClothChain ch = new ClothChain();
            ch.Name = "Strand " + index + " (" + rootName + ")"
                + (skirt ? " [skirt]" : light ? " [light]" : hair ? "" : " [accessory]");
            ch.Light = light;
            ch.Skirt = skirt;
            ch.Root = root;
            ch.RestRootRot = root.rotation;
            ch.FlowPhase = 0.9f + index * 1.37f;
            if (skirt)
            {
                // Cloth, not hair: heavier and wider than a necktie, and the whole
                // ring moves together. A firm spring back to the authored flare,
                // and only a third of the ambient current so 18 panels do not
                // ripple in lockstep.
                ch.FlowScale = 0.35f;
                ch.StiffnessScale = 2.4f;
                ch.DampingScale = 1.1f;
                // The hem is fabric with weight: without a little more gravity
                // than hair it floats up with every stroke.
                ch.GravityScale = 1.3f;
                // Hard ceiling on how far any point may leave the pose the hips
                // alone produce. A sudden turn throws the hem upwards (the chain
                // pivots around the waist, so a straight panel flipping up has no
                // curvature for the bend spring to fight); the leash makes that
                // impossible while still allowing normal sway, since it is far
                // larger than the few centimetres the cloth travels when swimming.
                float lagMax = Mathf.Max(0.5f, _container.lossyScale.x);
                ch.MaxLag = Mathf.Clamp(total * 0.32f, 0.03f * lagMax,
                    0.12f * lagMax);
                // The panel vertices hang inside the bone line, so the capsules
                // have to start pushing before the skin actually reaches the bone:
                // measured on the installed models the cloth sits 0.5-3 cm inside.
                ch.Margin = _skirtClearance.Value * Mathf.Max(0.5f, _container.lossyScale.x);
            }
            else if (clothes)
            {
                // A cape or coat hem is cloth too, but it hangs from the shoulders
                // and is much larger than a skirt panel: firm enough to keep the
                // authored drape, no leash (it is meant to stream), and the same
                // clearance off the legs a long coat needs.
                ch.FlowScale = 0.45f;
                ch.StiffnessScale = 2.0f;
                ch.DampingScale = 1.1f;
                ch.GravityScale = 1.2f;
                ch.Margin = _skirtClearance.Value * Mathf.Max(0.5f, _container.lossyScale.x);
            }
            else if (light)
            {
                // Clearly softer than a tail but with only a fraction of its
                // travel: a stiffer spring back to the modelled shape, less
                // gravity so it cannot sag, and a lag clamp as the hard ceiling.
                // A too-tight clamp made the fringe look welded to the head.
                ch.FlowScale = 0.85f;
                ch.StiffnessScale = 1.8f;
                ch.GravityScale = 0.55f;
                ch.DampingScale = 1.2f;
                ch.MaxLag = 0.055f * Mathf.Max(0.5f, _container.lossyScale.x);
            }
            else
            {
                ch.FlowScale = 0.95f;
                ch.StiffnessScale = 0.9f;
                if (hair)
                {
                    // Dry hair has real weight: nearly full gravity instead of the
                    // near-neutral buoyancy it has in water, and enough air drag to
                    // settle in about one pass. At the wet-side damping (10/s) the
                    // hair stops almost at once, but in air the old 3/s left the
                    // tail swinging top-to-bottom several times before it hung
                    // straight, which read as weightless. These factors only touch
                    // the dry portion, so swimming is unchanged.
                    ch.AirGravityScale = 1.8f;
                    ch.AirDampScale = 2.6f;
                }
            }
            ch.Bones = chainBones;
            ch.RestLocal = new Vector3[bins];
            ch.RestDirLocal = new Vector3[segments];
            ch.RestDirWorld = new Vector3[segments];
            ch.RestUpWorld = new Vector3[segments];
            ch.RestRotWorld = new Quaternion[segments];
            ch.RestBend = new Quaternion[segments - 1];
            ch.Length = new float[segments];
            ch.Points = new Vector3[bins];
            ch.Prev = new Vector3[bins];

            for (int k = 0; k < bins; k++)
            {
                ch.RestLocal[k] = root.InverseTransformPoint(joint[k]);
                ch.Points[k] = joint[k];
                ch.Prev[k] = joint[k];
            }
            for (int k = 0; k < segments; k++)
            {
                Vector3 dir = joint[k + 1] - joint[k];
                ch.Length[k] = dir.magnitude;
                dir /= ch.Length[k];
                ch.RestDirLocal[k] = Quaternion.Inverse(chainBones[k].rotation) * dir;
                ch.RestDirWorld[k] = dir;
                ch.RestRotWorld[k] = chainBones[k].rotation;
            }
            BuildRestFrames(ch.RestDirWorld, ch.RestUpWorld);
            for (int k = 0; k < segments - 1; k++)
            {
                ch.RestBend[k] = Quaternion.FromToRotation(
                    ch.RestDirLocal[k], ch.RestDirLocal[k + 1]);
            }

            if (_verbose.Value)
            {
                Logger.LogInfo("Imported strand " + ch.Name + ": joints=" + bins
                    + " length=" + total.ToString("F3"));
            }
            CaptureRestRot(ch);
            // Every chain kind has its own switch, so the four can be toggled
            // independently: hair, skirt, other garments, hanging accessories.
            ch.Hair = hair;
            ch.Clothes = clothes;
            ch.Accessory = !hair && !skirt && !clothes;
            ch.Cloth = skirt || clothes;
            _clothChains.Add(ch);
            return true;
        }

        // A capsule fitted to the body. A == B is a plain sphere.
        private struct ClothCollider
        {
            public Vector3 A;
            public Vector3 B;
            public float Radius;
            // Leg capsules take the skirt's extra clearance; the torso capsules do
            // not, or the waistband would be puffed off the hips.
            public bool Leg;
        }

        private readonly List<ClothCollider> _colliders = new List<ClothCollider>();
        private bool _collidersLogged;
        // Read once per simulated frame so the per-sample collision loop does not
        // touch the config entry (and so a switch change applies immediately).
        private bool _limbCollisionActive = true;

        // The body volume the cloth is blocked by, one ellipsoid per fitted bone.
        //
        // The handful of hand-tuned capsules these replace were guesses at the skin
        // surface, and on the imported models they were several centimetres inside
        // it (the chest capsule was 8 cm where the bust is 11.6, the hip sphere
        // 10.4 where the pelvis is 13.6). Cloth that reached the capsule was still
        // buried in the mesh, which is why the backside, the twintails and the
        // necktie all went straight through. Each ellipsoid is instead fitted to
        // the vertices the bone actually deforms, in that bone's space, so it
        // covers the real silhouette and follows the animated skeleton.
        // A stack of elliptical cross-sections along the bone's up axis. A single
        // ellipsoid was still too small: fitted to the box of one bone's vertices,
        // the surface of a pelvis sits at 1.1-1.5 of its radius (a box corner is
        // 1.73), i.e. up to four centimetres outside the collider, so the skirt
        // could sink a couple of centimetres into the backside before anything
        // pushed it. Every slice only sees the vertices at its own height, so the
        // measured surface is the skin itself.
        private sealed class BodySlices
        {
            public Transform Bone;
            public Vector3[] Frames; // per slice centre (x, y, z), bone space
            public Vector2[] Radii;  // per slice (rx, rz), bone space
            public float Y0;         // bone space, bottom edge of the span
            public float Step;       // slice spacing
            public float Cap;        // vertical falloff length beyond the ends
            public bool Leg;         // thighs/calves/ankles take the skirt clearance
            public bool Arm;         // clavicle/arm/hand; ignored by skirts and clothes
            public bool Limb;        // arm or leg; obeys the LimbCollision switch
            // Extra clearance while the leg is moving fast. A running thigh covers
            // several centimetres between two frames and the cloth lags behind it,
            // so the collider has to lead the leg a little or the panel is already
            // inside the thigh by the time the push is computed.
            public Vector3 LastPos;
            public bool HasLastPos;
            public float MotionPad;
            public float Bound;      // radius around the bone origin that covers the stack
        }

        private readonly List<BodySlices> _bodySlices = new List<BodySlices>();

        // Vertical samples per body bone. Six rings every few centimetres follow a
        // pelvis, a bust and a thigh closely; more would not change the silhouette.
        private const int BodySliceCount = 6;

        // Bind local rotation of every joint, taken while the rig is still in its
        // authored pose. Restoring these is what "hair physics off" means: the
        // strand keeps following the head exactly like the rigidly skinned hair it
        // replaced, without the simulation running.
        private static void CaptureRestRot(ClothChain ch)
        {
            if (ch.Bones == null || ch.Bones.Length == 0)
            {
                ch.RestLocalRot = null;
                return;
            }
            ch.RestLocalRot = new Quaternion[ch.Bones.Length];
            for (int i = 0; i < ch.Bones.Length; i++)
            {
                ch.RestLocalRot[i] = ch.Bones[i] != null
                    ? ch.Bones[i].localRotation
                    : Quaternion.identity;
            }
        }

        private static void RestPoseChain(ClothChain ch)
        {
            if (ch.Bones == null || ch.RestLocalRot == null)
            {
                return;
            }
            int n = Mathf.Min(ch.Bones.Length, ch.RestLocalRot.Length);
            for (int i = 0; i < n; i++)
            {
                Transform b = ch.Bones[i];
                if (b != null)
                {
                    b.localRotation = ch.RestLocalRot[i];
                }
            }
            // Re-anchor on the next enabled frame instead of easing from wherever
            // the last simulated pose left the strand.
            ch.HasLastRoot = false;
            ch.HasSheetFrame = false;
            ch.HasRoll = false;
        }

        // Bones that get a fitted body ellipsoid, in the standard names the
        // importer renames every rig to.
        private static readonly string[] FittedBodyBones =
        {
            "head_rig", "neck", "chest", "spine_1", "hips", "lower_body",
            // Arms: clavicle and shoulder blend into the upper arm, and the twist
            // bone deforms half of it, so all of them are measured. Hands only
            // cover the palm; the finger bones are far too thin to collide with.
            "clav_L", "shoulder_L", "arm_twist_L", "elbow_L", "hand_L",
            "clav_R", "shoulder_R", "arm_twist_R", "elbow_R", "hand_R",
            "thigh_L", "calf_L", "ankle_L", "toe_L_rig",
            "thigh_R", "calf_R", "ankle_R", "toe_R"
        };

        private static bool IsLegBone(string name)
        {
            return name.StartsWith("thigh", StringComparison.Ordinal)
                || name.StartsWith("calf", StringComparison.Ordinal)
                || name.StartsWith("ankle", StringComparison.Ordinal)
                || name.StartsWith("toe", StringComparison.Ordinal);
        }

        private static bool IsArmBone(string name)
        {
            return name.StartsWith("clav", StringComparison.Ordinal)
                || name.StartsWith("shoulder", StringComparison.Ordinal)
                || name.StartsWith("arm_twist", StringComparison.Ordinal)
                || name.StartsWith("elbow", StringComparison.Ordinal)
                || name.StartsWith("hand", StringComparison.Ordinal);
        }

        private static float AxisValue(Vector3 v, int axis)
        {
            return axis == 0 ? v.x : (axis == 1 ? v.y : v.z);
        }

        private static float AxisPercentile(List<Vector3> pts, int axis, float p)
        {
            float[] a = new float[pts.Count];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = AxisValue(pts[i], axis);
            }
            Array.Sort(a);
            int idx = Mathf.Clamp(Mathf.RoundToInt(p * (a.Length - 1)),
                0, a.Length - 1);
            return a[idx];
        }

        // One ring stack per bone, fitted to the vertices that bone dominates.
        // Runs once per model build, while the armature is still in its bind
        // pose. Falls back to the hand-tuned capsules when a model has no usable
        // skin weights.
        private void BuildBodySlices()
        {
            _bodySlices.Clear();
            if (_container == null)
            {
                return;
            }

            List<Vector3>[] points = new List<Vector3>[FittedBodyBones.Length];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new List<Vector3>(4096);
            }

            SkinnedMeshRenderer[] sources = { _bodyRenderer, _headRenderer };
            for (int r = 0; r < sources.Length; r++)
            {
                SkinnedMeshRenderer smr = sources[r];
                if (smr == null || smr.sharedMesh == null || smr.bones == null)
                {
                    continue;
                }
                Mesh mesh = smr.sharedMesh;
                Vector3[] verts = mesh.vertices;
                BoneWeight[] weights = mesh.boneWeights;
                if (verts.Length == 0 || weights == null || weights.Length != verts.Length)
                {
                    continue;
                }
                // Bone instance -> index into FittedBodyBones.
                Dictionary<Transform, int> fitOf =
                    new Dictionary<Transform, int>(smr.bones.Length);
                for (int b = 0; b < smr.bones.Length; b++)
                {
                    Transform bone = smr.bones[b];
                    if (bone == null || fitOf.ContainsKey(bone))
                    {
                        continue;
                    }
                    for (int k = 0; k < FittedBodyBones.Length; k++)
                    {
                        if (string.Equals(bone.name, FittedBodyBones[k],
                                StringComparison.OrdinalIgnoreCase))
                        {
                            fitOf[bone] = k;
                            break;
                        }
                    }
                }
                if (fitOf.Count == 0)
                {
                    continue;
                }
                Matrix4x4 meshToWorld = smr.transform.localToWorldMatrix;
                for (int v = 0; v < verts.Length; v++)
                {
                    BoneWeight bw = weights[v];
                    int bi = bw.boneIndex0;
                    float best = bw.weight0;
                    if (bw.weight1 > best) { best = bw.weight1; bi = bw.boneIndex1; }
                    if (bw.weight2 > best) { best = bw.weight2; bi = bw.boneIndex2; }
                    if (bw.weight3 > best) { best = bw.weight3; bi = bw.boneIndex3; }
                    if (best < 0.5f || bi < 0 || bi >= smr.bones.Length)
                    {
                        continue;
                    }
                    Transform bone = smr.bones[bi];
                    int fit;
                    if (bone == null || !fitOf.TryGetValue(bone, out fit))
                    {
                        continue;
                    }
                    Vector3 world = meshToWorld.MultiplyPoint3x4(verts[v]);
                    points[fit].Add(bone.InverseTransformPoint(world));
                }
            }

            List<Vector3>[] slabs = new List<Vector3>[BodySliceCount];
            for (int k = 0; k < BodySliceCount; k++)
            {
                slabs[k] = new List<Vector3>(1024);
            }
            int rings = 0;
            for (int i = 0; i < FittedBodyBones.Length; i++)
            {
                List<Vector3> list = points[i];
                if (list.Count < 48)
                {
                    continue;
                }
                Transform bone = ModelTransform(FittedBodyBones[i]);
                if (bone == null)
                {
                    continue;
                }
                // The 2nd and 98th percentile of the height trim stray vertices
                // without clipping the rump or the top of the head.
                float lo = AxisPercentile(list, 1, 0.02f);
                float hi = AxisPercentile(list, 1, 0.98f);
                float span = hi - lo;
                if (span < 0.02f)
                {
                    continue;
                }
                float step = span / BodySliceCount;
                for (int k = 0; k < BodySliceCount; k++)
                {
                    slabs[k].Clear();
                }
                for (int v = 0; v < list.Count; v++)
                {
                    Vector3 p = list[v];
                    int k = Mathf.FloorToInt((p.y - lo) / step);
                    if (k < 0 || k >= BodySliceCount)
                    {
                        continue;
                    }
                    slabs[k].Add(p);
                }
                Vector3[] frames = new Vector3[BodySliceCount];
                Vector2[] radii = new Vector2[BodySliceCount];
                bool[] filled = new bool[BodySliceCount];
                bool sane = true;
                for (int k = 0; k < BodySliceCount; k++)
                {
                    List<Vector3> slab = slabs[k];
                    if (slab.Count < 6)
                    {
                        continue;
                    }
                    float lx = float.MaxValue, hx = float.MinValue;
                    float lz = float.MaxValue, hz = float.MinValue;
                    for (int v = 0; v < slab.Count; v++)
                    {
                        Vector3 p = slab[v];
                        if (p.x < lx) lx = p.x;
                        if (p.x > hx) hx = p.x;
                        if (p.z < lz) lz = p.z;
                        if (p.z > hz) hz = p.z;
                    }
                    float cx = (lx + hx) * 0.5f;
                    float cz = (lz + hz) * 0.5f;
                    float rx = (hx - lx) * 0.5f;
                    float rz = (hz - lz) * 0.5f;
                    if (rx < 0.004f || rz < 0.004f)
                    {
                        continue;
                    }
                    // A ring fitted to its own height still under-covers that
                    // height's box corners (x and z extremes at once). Grow it
                    // until 95% of the slab's vertices are inside the ring.
                    float[] us = new float[slab.Count];
                    for (int v = 0; v < slab.Count; v++)
                    {
                        float dx = (slab[v].x - cx) / rx;
                        float dz = (slab[v].z - cz) / rz;
                        us[v] = Mathf.Sqrt(dx * dx + dz * dz);
                    }
                    Array.Sort(us);
                    int idx95 = Mathf.Clamp(Mathf.RoundToInt(0.95f * (us.Length - 1)),
                        0, us.Length - 1);
                    float reach = Mathf.Clamp(us[idx95] * 1.02f, 1f, 1.35f);
                    rx *= reach;
                    rz *= reach;
                    if (rx > 0.35f || rz > 0.35f)
                    {
                        sane = false;
                        break;
                    }
                    frames[k] = new Vector3(cx, lo + (k + 0.5f) * step, cz);
                    radii[k] = new Vector2(rx, rz);
                    filled[k] = true;
                }
                if (!sane)
                {
                    // A bone that deforms a huge or wild region would become one
                    // giant blob; skip it and let the neighbours cover that part.
                    continue;
                }
                int first = -1;
                for (int k = 0; k < BodySliceCount; k++)
                {
                    if (filled[k])
                    {
                        first = k;
                        break;
                    }
                }
                if (first < 0)
                {
                    continue;
                }
                // A thin bone (the neck ring) can leave a slice empty; copy the
                // nearest filled one so the stack has no hole to fall through.
                for (int k = 0; k < BodySliceCount; k++)
                {
                    if (filled[k])
                    {
                        continue;
                    }
                    int j = k - 1;
                    while (j >= 0 && !filled[j]) j--;
                    int m = k + 1;
                    while (m < BodySliceCount && !filled[m]) m++;
                    int src = j >= 0
                        ? (m < BodySliceCount && (m - k) < (k - j) ? m : j)
                        : m;
                    frames[k] = new Vector3(frames[src].x, lo + (k + 0.5f) * step,
                        frames[src].z);
                    radii[k] = radii[src];
                }
                BodySlices slices = new BodySlices();
                slices.Bone = bone;
                slices.Frames = frames;
                slices.Radii = radii;
                slices.Y0 = lo;
                slices.Step = step;
                slices.Cap = step;
                // World-space bounding radius around the bone origin, used to
                // reject far-away stacks without a transform per sample.
                float bound = 0f;
                for (int k = 0; k < BodySliceCount; k++)
                {
                    Vector3 f = frames[k];
                    float r = Mathf.Max(radii[k].x, radii[k].y);
                    bound = Mathf.Max(bound, new Vector3(f.x, f.y, f.z).magnitude + r);
                }
                // The stack is expressed in bone space and the rejection test runs
                // in world space, so the bound has to carry the bone's world scale
                // (the container scale) or the far end of an arm is skipped.
                float worldScale = Mathf.Abs(bone.lossyScale.x);
                if (worldScale <= 1e-4f)
                {
                    worldScale = 1f;
                }
                slices.Bound = (bound + 0.15f) * worldScale;
                string name = FittedBodyBones[i];
                slices.Leg = IsLegBone(name);
                slices.Arm = IsArmBone(name);
                slices.Limb = slices.Leg || slices.Arm;
                _bodySlices.Add(slices);
                rings += BodySliceCount;
            }

            if (_bodySlices.Count > 0)
            {
                int limbStacks = 0;
                int legStacks = 0;
                int armStacks = 0;
                for (int i = 0; i < _bodySlices.Count; i++)
                {
                    if (_bodySlices[i].Limb)
                    {
                        limbStacks++;
                    }
                    if (_bodySlices[i].Leg)
                    {
                        legStacks++;
                    }
                    if (_bodySlices[i].Arm)
                    {
                        armStacks++;
                    }
                }
                Logger.LogInfo("Body collision: " + _bodySlices.Count
                    + " fitted body collider(s), " + rings + " ring(s), "
                    + limbStacks + " limb stack(s) (" + legStacks + " leg, "
                    + armStacks + " arm).");
            }
        }

        // The cloth meshes are shells around the simulated bone line, but the
        // solver only moves the line. For every joint, measure where the shell
        // actually sits relative to the joint (its mean offset - the tie ribbon
        // hangs in front of its bones, a tail is centred on them) and how thick
        // it is around that point. Collision then tests the shell's centre, not
        // the bare joint, so a tie is not shoved off the chest and a twintail is
        // not half buried while its bone is exactly on the skin.
        private void MeasureClothPadding()
        {
            for (int c = 0; c < _clothChains.Count; c++)
            {
                _clothChains[c].Padding = null;
                _clothChains[c].Shell = null;
            }
            if (_clothChains.Count == 0)
            {
                return;
            }

            int chains = _clothChains.Count;
            Dictionary<Transform, List<KeyValuePair<int, int>>> owner =
                new Dictionary<Transform, List<KeyValuePair<int, int>>>();
            Vector3[][] rest = new Vector3[chains][];
            List<Vector3>[][] joint = new List<Vector3>[chains][];
            for (int c = 0; c < chains; c++)
            {
                ClothChain ch = _clothChains[c];
                if (ch.Bones == null || ch.Bones.Length == 0)
                {
                    rest[c] = new Vector3[0];
                    joint[c] = new List<Vector3>[0];
                    continue;
                }
                rest[c] = new Vector3[ch.Bones.Length];
                joint[c] = new List<Vector3>[ch.Bones.Length];
                for (int j = 0; j < ch.Bones.Length; j++)
                {
                    Transform b = ch.Bones[j];
                    rest[c][j] = b != null ? b.position : Vector3.zero;
                    joint[c][j] = new List<Vector3>(16);
                    // The first joint is usually a shared body bone (the hips for
                    // the skirt, the head for the tails), so it dominates a huge
                    // part of the mesh; its "thickness" would be the whole pelvis
                    // radius. It is pinned anyway and never resolved here.
                    if (b == null || j == 0)
                    {
                        continue;
                    }
                    List<KeyValuePair<int, int>> list;
                    if (!owner.TryGetValue(b, out list))
                    {
                        list = new List<KeyValuePair<int, int>>(1);
                        owner[b] = list;
                    }
                    list.Add(new KeyValuePair<int, int>(c, j));
                }
            }
            if (owner.Count == 0)
            {
                return;
            }

            SkinnedMeshRenderer[] sources = { _bodyRenderer, _headRenderer, _hairRenderer };
            for (int r = 0; r < sources.Length; r++)
            {
                SkinnedMeshRenderer smr = sources[r];
                if (smr == null || smr.sharedMesh == null || smr.bones == null)
                {
                    continue;
                }
                Mesh mesh = smr.sharedMesh;
                Vector3[] verts = mesh.vertices;
                BoneWeight[] weights = mesh.boneWeights;
                if (verts.Length == 0 || weights == null || weights.Length != verts.Length)
                {
                    continue;
                }
                Matrix4x4 meshToWorld = smr.transform.localToWorldMatrix;
                for (int v = 0; v < verts.Length; v++)
                {
                    BoneWeight bw = weights[v];
                    int bi = bw.boneIndex0;
                    float best = bw.weight0;
                    if (bw.weight1 > best) { best = bw.weight1; bi = bw.boneIndex1; }
                    if (bw.weight2 > best) { best = bw.weight2; bi = bw.boneIndex2; }
                    if (bw.weight3 > best) { best = bw.weight3; bi = bw.boneIndex3; }
                    if (best < 0.5f || bi < 0 || bi >= smr.bones.Length)
                    {
                        continue;
                    }
                    Transform bone = smr.bones[bi];
                    List<KeyValuePair<int, int>> owned;
                    if (bone == null || !owner.TryGetValue(bone, out owned))
                    {
                        continue;
                    }
                    Vector3 world = meshToWorld.MultiplyPoint3x4(verts[v]);
                    for (int o = 0; o < owned.Count; o++)
                    {
                        int c = owned[o].Key;
                        int nearest;
                        Vector3 closest;
                        PolylineDistance(rest[c], world, out nearest, out closest);
                        List<Vector3> s = joint[c][nearest];
                        if (s.Count < 4096)
                        {
                            // Bone space, so the offset turns with the simulated
                            // chain instead of following the frozen bind pose.
                            Transform jb = _clothChains[c].Bones[nearest];
                            s.Add(jb != null
                                ? Quaternion.Inverse(jb.rotation) * (world - closest)
                                : world - closest);
                        }
                    }
                }
            }

            float worldScale = _container != null ? Mathf.Abs(_container.lossyScale.x) : 1f;
            float floorPad = _skirtClearance != null
                ? Mathf.Max(0f, _skirtClearance.Value) * Mathf.Max(0.5f, worldScale)
                : 0f;
            for (int c = 0; c < chains; c++)
            {
                ClothChain ch = _clothChains[c];
                int n = joint[c].Length;
                if (n < 2)
                {
                    continue;
                }
                float[] pad = new float[n];
                Vector3[] shell = new Vector3[n];
                bool any = false;
                for (int j = 0; j < n; j++)
                {
                    List<Vector3> samples = joint[c][j];
                    if (samples.Count < 6)
                    {
                        continue;
                    }
                    Vector3 mean = Vector3.zero;
                    for (int k = 0; k < samples.Count; k++)
                    {
                        mean += samples[k];
                    }
                    mean /= samples.Count;
                    float spread = 0f;
                    for (int k = 0; k < samples.Count; k++)
                    {
                        spread += (samples[k] - mean).magnitude;
                    }
                    spread /= samples.Count;
                    shell[j] = mean;
                    // Skirts and long clothes keep their panel offset around the
                    // legs. Hair and ties are thin shells, capped so a ribbon does
                    // not hover a hand's width off the chest.
                    pad[j] = Mathf.Clamp(spread, 0f, ch.Cloth ? 0.06f : 0.03f);
                    any = true;
                }
                if (!any)
                {
                    continue;
                }
                if (ch.Cloth && floorPad > 0f)
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (pad[j] < floorPad)
                        {
                            pad[j] = floorPad;
                        }
                    }
                }
                ch.Padding = pad;
                ch.Shell = shell;
            }
        }

        private static float PolylineDistance(Vector3[] pts, Vector3 p, out int joint,
            out Vector3 closest)
        {
            joint = 0;
            closest = p;
            if (pts == null || pts.Length == 0)
            {
                return 0f;
            }
            if (pts.Length == 1)
            {
                closest = pts[0];
                return Vector3.Distance(pts[0], p);
            }
            float best = float.MaxValue;
            for (int k = 0; k + 1 < pts.Length; k++)
            {
                Vector3 a = pts[k];
                Vector3 ab = pts[k + 1] - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-10f
                    ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2)
                    : 0f;
                Vector3 q = a + ab * t;
                float d = (p - q).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    closest = q;
                    joint = t < 0.5f ? k : k + 1;
                }
            }
            return best == float.MaxValue ? 0f : Mathf.Sqrt(best);
        }

        // Let the leg colliders lead a fast-moving leg. See BodySlices.MotionPad.
        private void UpdateLegMotion()
        {
            for (int i = 0; i < _bodySlices.Count; i++)
            {
                BodySlices s = _bodySlices[i];
                if (!s.Leg || s.Bone == null)
                {
                    s.MotionPad = 0f;
                    continue;
                }
                Vector3 p = s.Bone.position;
                if (!s.HasLastPos)
                {
                    s.LastPos = p;
                    s.HasLastPos = true;
                    s.MotionPad = 0f;
                    continue;
                }
                float moved = (p - s.LastPos).magnitude;
                s.LastPos = p;
                float target = Mathf.Clamp(moved * 0.6f, 0f, 0.04f);
                // Fast attack, slower release: a leg that stops should not pop the
                // skirt back to the leg in one frame.
                s.MotionPad = Mathf.Max(target, s.MotionPad * 0.8f);
            }
        }

        private void GatherClothColliders()
        {
            _colliders.Clear();
            _limbCollisionActive = _limbCollision == null || _limbCollision.Value;
            UpdateLegMotion();
            // Hair and the skirt have separate switches but share the body capsules:
            // gather them when either kind of simulation is on, and let each chain
            // decide below whether it uses them.
            bool hairOn = _hairCollision == null || _hairCollision.Value;
            bool skirtOn = _skirtPhysics == null || _skirtPhysics.Value;
            if ((!hairOn && !skirtOn) || _container == null)
            {
                return;
            }
            // A fitted body volume beats anything we can guess at. When the model
            // has one it is the only body collider, so the cloth is blocked by the
            // real silhouette instead of a capsule buried a few centimetres inside
            // the skin. The one thing it cannot cover is a part the fit failed on
            // (a mesh with no dominant thigh weights): then the hand-tuned capsules
            // are still built below as a safety net for the legs.
            if (_bodySlices.Count > 0)
            {
                bool anyLeg = false;
                for (int i = 0; i < _bodySlices.Count; i++)
                {
                    if (_bodySlices[i].Leg)
                    {
                        anyLeg = true;
                        break;
                    }
                }
                if (anyLeg)
                {
                    if (!_collidersLogged)
                    {
                        _collidersLogged = true;
                        Logger.LogInfo("Cloth collision: " + _bodySlices.Count
                            + " fitted body collider(s), skirt clearance="
                            + _skirtClearance.Value.ToString("F3") + " m.");
                    }
                    return;
                }
                if (!_collidersLogged)
                {
                    _collidersLogged = true;
                    Logger.LogInfo("Cloth collision: " + _bodySlices.Count
                        + " fitted body collider(s) without a leg fit, plus"
                        + " fallback capsules.");
                }
            }
            float s = _container.lossyScale.x;
            if (s <= 0.001f)
            {
                s = 1f;
            }
            Transform head = ModelTransform("head_rig");
            Transform chest = ModelTransform("chest");
            if (chest == null)
            {
                chest = ModelTransform("spine_1");
            }
            Transform hips = ModelTransform("hips");
            if (head != null)
            {
                // The head bone sits at the neck joint; the skull's centre is a
                // little above it. The radius is deliberately inside the face, so
                // the fringe rests on the forehead instead of being ejected.
                Vector3 skull = head.position + head.up * (0.085f * s);
                _colliders.Add(Collider(skull, skull, 0.09f * s));
                if (chest != null)
                {
                    _colliders.Add(Collider(chest.position, head.position, 0.06f * s));
                }
            }
            if (chest != null && hips != null)
            {
                // Kept a little inside the visible torso: clothes (the necktie)
                // are modelled against the skin and must not be pushed off it.
                _colliders.Add(Collider(chest.position, hips.position, 0.075f * s));
            }
            if (hips != null)
            {
                _colliders.Add(Collider(hips.position, hips.position, 0.095f * s));
            }
            // Legs. Hair only met the torso before, and the skirt panels had no
            // body to push against at all: they cut straight through the thighs as
            // soon as the swim animation lifted the knees. The capsules follow the
            // animated skeleton, so the hem rides over the leg instead.
            for (int side = 0; _limbCollisionActive && side < 2; side++)
            {
                string suffix = side == 0 ? "_L" : "_R";
                Transform thigh = ModelTransform("thigh" + suffix);
                if (thigh == null)
                {
                    continue;
                }
                Transform calf = ModelTransform("calf" + suffix);
                Transform ankle = ModelTransform("ankle" + suffix);
                if (calf != null)
                {
                    // Radii measured off the installed PMX models: the widest
                    // thigh vertex is 8.4 cm from the bone axis and the widest
                    // calf 7.1 cm once the model scale is applied. The old values
                    // were a few millimetres inside the skin, so a skirt resting
                    // on the capsule still showed the leg.
                    _colliders.Add(Collider(thigh.position, calf.position,
                        0.078f * s, true));
                    if (ankle != null)
                    {
                        _colliders.Add(Collider(calf.position, ankle.position,
                            0.066f * s, true));
                    }
                }
            }
            if (!_collidersLogged)
            {
                _collidersLogged = true;
                int legs = 0;
                for (int i = 0; i < _colliders.Count; i++)
                {
                    if (_colliders[i].Leg)
                    {
                        legs++;
                    }
                }
                Logger.LogInfo("Cloth colliders: " + _colliders.Count
                    + " capsule(s), " + legs + " leg capsule(s), skirt clearance="
                    + _skirtClearance.Value.ToString("F3") + " m.");
            }
        }

        private static ClothCollider Collider(Vector3 a, Vector3 b, float radius)
        {
            return Collider(a, b, radius, false);
        }

        private static ClothCollider Collider(Vector3 a, Vector3 b, float radius, bool leg)
        {
            ClothCollider c;
            c.A = a;
            c.B = b;
            c.Radius = radius;
            c.Leg = leg;
            return c;
        }

        private void ResolveClothCollision(ClothChain ch)
        {
            int joints = ch.Points.Length;
            for (int i = 1; i < joints; i++)
            {
                // The last simulated point is the strand tip and has no bone of
                // its own; it borrows the last joint's shell so the hem and the
                // hair tips are guarded too.
                int boneIndex = ch.Bones != null && ch.Bones.Length > 0
                    ? Mathf.Min(i, ch.Bones.Length - 1) : 0;
                // Collide the measured shell centre, not the bare bone: a tie's
                // bones hang inside the chest while the ribbon itself lies on it.
                Vector3 shell = ShellOffset(ch, boneIndex);
                float pad = JointPad(ch, i, boneIndex);
                Vector3 sample = PushSampleOutOfBody(ch, i, boneIndex, pad,
                    ch.Points[i] + shell);
                ch.Points[i] = sample - shell;
            }

            // The points above are the simulated joints, but the visible cloth is
            // ruled between them. A running knee swings through the middle of a
            // 5-10 cm skirt segment while both ends stay outside the leg, which is
            // why the thighs still showed through the hem when the player ran.
            // Sample each segment at two intermediate positions and push those
            // samples too, spreading the correction over the two end joints.
            if (!ch.Cloth)
            {
                return;
            }
            int bones = ch.Bones != null ? ch.Bones.Length : 0;
            for (int i = 1; i + 1 < joints; i++)
            {
                int b0 = bones > 0 ? Mathf.Min(i, bones - 1) : 0;
                int b1 = bones > 0 ? Mathf.Min(i + 1, bones - 1) : 0;
                Vector3 shell0 = ShellOffset(ch, b0);
                Vector3 shell1 = ShellOffset(ch, b1);
                float pad0 = JointPad(ch, i, b0);
                float pad1 = JointPad(ch, i + 1, b1);
                for (int k = 1; k <= 2; k++)
                {
                    float f = k / 3f;
                    Vector3 line = Vector3.Lerp(ch.Points[i], ch.Points[i + 1], f);
                    Vector3 shell = Vector3.Lerp(shell0, shell1, f);
                    float pad = Mathf.Lerp(pad0, pad1, f);
                    int near = f < 0.5f ? i : i + 1;
                    int nearBone = f < 0.5f ? b0 : b1;
                    Vector3 pushed = PushSampleOutOfBody(ch, near, nearBone, pad,
                        line + shell);
                    Vector3 delta = pushed - (line + shell);
                    if (delta.sqrMagnitude <= 1e-12f)
                    {
                        continue;
                    }
                    // Least-squares split: move each end along the segment so the
                    // sampled point lands exactly on the pushed position, and the
                    // two ends move as little as possible.
                    float denom = (1f - f) * (1f - f) + f * f;
                    ch.Points[i] += delta * ((1f - f) / denom);
                    ch.Points[i + 1] += delta * (f / denom);
                }
            }
        }

        // Offset of the visible shell centre from a joint, in world space.
        private static Vector3 ShellOffset(ClothChain ch, int boneIndex)
        {
            if (ch.Shell == null || ch.Shell.Length <= boneIndex)
            {
                return Vector3.zero;
            }
            Transform jb = ch.Bones != null && boneIndex < ch.Bones.Length
                ? ch.Bones[boneIndex] : null;
            return jb != null ? jb.rotation * ch.Shell[boneIndex] : ch.Shell[boneIndex];
        }

        // How thick the cloth shell is around a joint. The joints are pushed, the
        // mesh is drawn around them, so the solver has to keep the joint that much
        // further out than the skin. The first joints sit on the surface the cloth
        // is attached to (the tail base on the skull, the waistband on the hips)
        // and are meant to touch it; holding their full thickness away would tear
        // the authored shape open at the seam. pointIndex may be fractional for a
        // sample between two joints.
        private static float JointPad(ClothChain ch, float pointIndex, int boneIndex)
        {
            float pad = ch.Padding != null && ch.Padding.Length > boneIndex
                ? ch.Padding[boneIndex] : 0f;
            int joints = ch.Points.Length;
            if (joints > 2)
            {
                pad *= Mathf.Clamp01(pointIndex / Mathf.Max(1f, (joints - 1) * 0.35f));
            }
            return pad;
        }

        // Push one cloth shell sample out of the body volume. pointIndex is only
        // used to pick a sane direction when the sample lands dead centre.
        private Vector3 PushSampleOutOfBody(ClothChain ch, int pointIndex, int boneIndex,
            float pad, Vector3 sample)
        {
            for (int c = 0; c < _colliders.Count; c++)
            {
                ClothCollider col = _colliders[c];
                Vector3 axis = col.B - col.A;
                float t = axis.sqrMagnitude > 1e-8f
                    ? Mathf.Clamp01(Vector3.Dot(sample - col.A, axis) / axis.sqrMagnitude)
                    : 0f;
                Vector3 nearest = col.A + axis * t;
                Vector3 d = sample - nearest;
                // Skirt and coat chains get a wider shell on the legs only: the
                // panel vertices are offset inside the bone line, so the capsule
                // has to start pushing before the skin reaches the simulated joint.
                float clearance = pad;
                if (col.Leg && ch.Cloth && ch.Margin > clearance)
                {
                    clearance = ch.Margin;
                }
                float radius = col.Radius + clearance;
                float m2 = d.sqrMagnitude;
                if (m2 >= radius * radius)
                {
                    continue;
                }
                float m = Mathf.Sqrt(m2);
                Vector3 push;
                if (m > 1e-5f)
                {
                    push = d / m;
                }
                else
                {
                    // Dead centre: push towards the modelled pose, which is
                    // always on the outside of the body.
                    push = ch.Root.TransformPoint(ch.RestLocal[pointIndex]) - nearest;
                    push = push.sqrMagnitude > 1e-8f
                        ? push.normalized
                        : ch.Root.up;
                }
                sample = nearest + push * radius;
            }
            for (int e = 0; e < _bodySlices.Count; e++)
            {
                BodySlices el = _bodySlices[e];
                if (el.Bone == null || el.Bound <= 0f)
                {
                    continue;
                }
                // Arms and legs are optional: the switch leaves the head/torso
                // colliders alone so hair can still be blocked by the body.
                if (el.Limb && !_limbCollisionActive)
                {
                    continue;
                }
                // Skirts and coat panels do not use the arm stacks. The hands
                // rest inside the skirt at the sides of the thighs, so every
                // swim stroke drove the hem out with the hand and straight back
                // into the thigh, and the two colliders fought frame after
                // frame. Hair and accessories still collide with the arms.
                if (el.Arm && ch.Cloth)
                {
                    continue;
                }
                // Cheap rejection: the stack cannot reach this sample.
                if ((sample - el.Bone.position).sqrMagnitude > el.Bound * el.Bound)
                {
                    continue;
                }
                PushOutOfSlices(ch, pointIndex, el, pad, ref sample);
            }
            return sample;
        }

        // Push one cloth shell sample out of a body ring stack. Everything is in
        // the bone's space, so the volume follows the animated skeleton (the hips
        // rolling in a side stroke, the chest turning with the head). The push is
        // horizontal: the stacks overlap along the spine, so a point between two
        // rings is kept outside both.
        private void PushOutOfSlices(ClothChain ch, int i, BodySlices el,
            float pad, ref Vector3 sample)
        {
            if (el.Bone == null || el.Frames == null || el.Frames.Length == 0)
            {
                return;
            }
            Vector3 local = el.Bone.InverseTransformPoint(sample);
            float margin = pad;
            if (el.Leg && ch.Cloth)
            {
                if (ch.Margin > margin)
                {
                    margin = ch.Margin;
                }
                margin += el.MotionPad;
            }
            // The radii are in bone space; the clearance is in world metres.
            float scale = el.Bone.lossyScale.x;
            if (scale > 1e-4f)
            {
                margin /= scale;
            }

            int count = el.Frames.Length;
            // Slice centres sit half a step inside the measured span, so the
            // first and last half slices are full radius and only the space
            // beyond the span (the caps) fades out.
            float s = (local.y - el.Y0) / el.Step - 0.5f;
            float fade = 1f;
            if (s < -0.5f)
            {
                float d = -(s + 0.5f) * el.Step;
                if (el.Cap <= 1e-5f || d >= el.Cap)
                {
                    return;
                }
                fade = Mathf.Sqrt(1f - (d / el.Cap) * (d / el.Cap));
                s = 0f;
            }
            else if (s > count - 0.5f)
            {
                float d = (s - (count - 0.5f)) * el.Step;
                if (el.Cap <= 1e-5f || d >= el.Cap)
                {
                    return;
                }
                fade = Mathf.Sqrt(1f - (d / el.Cap) * (d / el.Cap));
                s = count - 1;
            }
            else
            {
                s = Mathf.Clamp(s, 0f, count - 1f);
            }

            int k0 = Mathf.Clamp(Mathf.FloorToInt(s), 0, count - 1);
            int k1 = Mathf.Min(k0 + 1, count - 1);
            float frac = Mathf.Clamp01(s - k0);
            Vector3 f0 = el.Frames[k0];
            Vector3 f1 = el.Frames[k1];
            float cx = Mathf.Lerp(f0.x, f1.x, frac);
            float cz = Mathf.Lerp(f0.z, f1.z, frac);
            float rx = Mathf.Lerp(el.Radii[k0].x, el.Radii[k1].x, frac) * fade;
            float rz = Mathf.Lerp(el.Radii[k0].y, el.Radii[k1].y, frac) * fade;
            if (rx <= 1e-4f || rz <= 1e-4f)
            {
                return;
            }
            rx += margin;
            rz += margin;

            float dx = local.x - cx;
            float dz = local.z - cz;
            float ux = dx / rx;
            float uz = dz / rz;
            float u2 = ux * ux + uz * uz;
            if (u2 >= 1f)
            {
                return;
            }
            float u = Mathf.Sqrt(u2);
            float nx;
            float nz;
            if (u > 1e-4f)
            {
                nx = ux / u;
                nz = uz / u;
            }
            else
            {
                // Dead centre: push towards the authored pose, which is always on
                // the outside of the body.
                Vector3 rest = ch.RestLocal != null && i < ch.RestLocal.Length
                    && ch.Root != null
                    ? ch.Root.TransformPoint(ch.RestLocal[i]) - sample
                    : Vector3.zero;
                Vector3 ld = el.Bone.InverseTransformDirection(rest);
                float lu = Mathf.Sqrt((ld.x / rx) * (ld.x / rx)
                    + (ld.z / rz) * (ld.z / rz));
                if (lu < 1e-4f)
                {
                    nx = 1f;
                    nz = 0f;
                }
                else
                {
                    nx = (ld.x / rx) / lu;
                    nz = (ld.z / rz) / lu;
                }
            }
            local = new Vector3(cx + nx * rx, local.y, cz + nz * rz);
            sample = el.Bone.TransformPoint(local);
        }

        // Ocean surface height in world space, cached for the frame. NaN when no
        // ocean or player is loaded (main menu), which turns the dry/wet split off.
        private static float _waterSurfaceY = float.NaN;
        private static int _waterSurfaceFrame = -1;

        private static float WaterSurfaceY()
        {
            int frame = Time.frameCount;
            if (frame == _waterSurfaceFrame)
            {
                return _waterSurfaceY;
            }
            _waterSurfaceFrame = frame;
            float level = float.NaN;
            try
            {
                Player player = Player.main;
                level = player != null ? player.GetWaterLevel() : Ocean.GetOceanLevel();
            }
            catch (Exception)
            {
                level = float.NaN;
            }
            _waterSurfaceY = level;
            return level;
        }

        // A base, lifepod or vehicle interior is filled with air even though it
        // sits under the sea, so the whole model is treated as dry there. The
        // alien containment is inside a base but flooded, hence the underwater
        // check: there the model must keep the wet motion.
        private static bool PlayerInDryAir()
        {
            try
            {
                Player player = Player.main;
                return player != null && player.IsInside() && !player.IsUnderwater();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void SimulateCloth(float dt)
        {
            if (_clothChains.Count == 0 || dt <= 0f)
            {
                return;
            }

            dt = Mathf.Clamp(dt, 0.001f, 0.05f);
            float t = Time.time;
            GatherClothColliders();
            bool hairOn = _hairPhysics == null || _hairPhysics.Value;
            bool skirtOn = _skirtPhysics == null || _skirtPhysics.Value;
            bool clothesOn = _clothesPhysics == null || _clothesPhysics.Value;
            bool accessoryOn = _accessoryPhysics == null || _accessoryPhysics.Value;
            bool hairCollide = _hairCollision == null || _hairCollision.Value;
            // Waterline for the surface/dry split. NaN means no ocean is loaded
            // (main menu), in which case the old all-water behaviour applies.
            float waterLevel = WaterSurfaceY();
            bool surfaceOn = (_surfacePhysics == null || _surfacePhysics.Value)
                && !float.IsNaN(waterLevel);
            float waterY = surfaceOn ? waterLevel : 0f;
            bool dryAll = surfaceOn && PlayerInDryAir();
            float waterDamp = Mathf.Max(0f, _hairDamping.Value);
            float airDamp = _airDamping != null ? Mathf.Max(0f, _airDamping.Value) : waterDamp;
            float waterGravity = Mathf.Max(0f, _hairGravity.Value);
            float airGravity = _airGravity != null ? Mathf.Max(0f, _airGravity.Value) : waterGravity;

            for (int c = 0; c < _clothChains.Count; c++)
            {
                ClothChain ch = _clothChains[c];
                if (ch.Root == null)
                {
                    continue;
                }
                if ((ch.Hair && !hairOn) || (ch.Skirt && !skirtOn)
                    || (ch.Clothes && !clothesOn) || (ch.Accessory && !accessoryOn))
                {
                    // The switches are live: park the strand at its authored pose and
                    // skip the solver instead of waiting for a model reload.
                    RestPoseChain(ch);
                    continue;
                }
                // Fringe and sideburns are on a leash, and the in-game slider scales
                // the whole tier: leash, idle current and gravity together. Zero
                // welds them to the head, 1 is the authored drift.
                float sway = ch.Light ? Mathf.Clamp(_fringeSway.Value, 0f, 2f) : 1f;
                int joints = ch.Points.Length;
                if (ch.Vel == null || ch.Vel.Length != joints)
                {
                    ch.Vel = new Vector3[joints];
                }
                // Teleports and scene changes would otherwise fling the cloth. Only a
                // jump of a whole metre in a single frame counts: swimming covers a few
                // centimetres per frame, but has to keep simulating, not reset.
                Vector3 rootNow = ch.Root.position;
                bool snapped = !ch.HasLastRoot || (rootNow - ch.LastRoot).sqrMagnitude > 1f;
                ch.LastRoot = rootNow;
                ch.HasLastRoot = true;
                if (snapped)
                {
                    for (int i = 0; i < joints; i++)
                    {
                        Vector3 rigid = ch.Root.TransformPoint(ch.RestLocal[i]);
                        ch.Points[i] = rigid;
                        ch.Prev[i] = rigid;
                        ch.Vel[i] = Vector3.zero;
                    }
                    _clothResets++;
                    // The roll reference is meaningless after a teleport: re-seed
                    // it from the body frame instead of easing from the old world.
                    ch.HasRoll = false;
                }

                // Roll reference: the head rotation, but lagging. The simulated points
                // keep their world orientation when the player turns, so copying the
                // head's rotation straight onto the bones rolled every flat ribbon
                // around its own length and sent the tips circling. A first-order lag
                // keeps the sheet's face steady through small head motions (the swim
                // animation sways the head every frame) and still follows slow turns.
                Quaternion headRot = ch.Root.rotation;
                if (snapped || !ch.HasSheetFrame)
                {
                    ch.SheetFrame = headRot;
                    ch.HasSheetFrame = true;
                }
                else
                {
                    float follow = 1f - Mathf.Exp(-Mathf.Max(0.05f, _hairTwistFollow.Value) * dt);
                    ch.SheetFrame = Quaternion.Slerp(ch.SheetFrame, headRot, follow);
                }
                Quaternion sheetFrame = ch.SheetFrame;

                float bendK = Mathf.Max(0f, _hairStiffness.Value) * ch.StiffnessScale;
                // Skirts and coats have their own stiffness slider. 1.0 is the
                // authored cloth behaviour (HairStiffness scaled up for fabric);
                // lower values let the hem flow, higher values hold the shape.
                if (ch.Cloth && _skirtStiffness != null)
                {
                    bendK *= Mathf.Max(0.01f, _skirtStiffness.Value);
                }
                // Dry-only gravity/drag multipliers. Hair raises both so it drops
                // with real weight and stops after one swing; everything else
                // keeps the shared air settings.
                float chainAirGravity = airGravity * Mathf.Max(0f, ch.AirGravityScale);
                float chainAirDamp = airDamp * Mathf.Max(0f, ch.AirDampScale);

                // Anisotropic drag. Hair is a bundle of thin fibres: water resists
                // sideways motion far more than motion along the strand. Splitting the
                // velocity is what lets the tail slip through the water and float
                // instead of swinging about like a rubber band. The split, the damping
                // and gravity are all per joint, because a point that has risen above
                // the water line is dry cloth: it falls with real gravity and the air
                // hardly slows it, while the submerged part below still floats.
                for (int i = 1; i < joints; i++)
                {
                    float w = (float)i / (joints - 1);
                    float air = 0f;
                    if (surfaceOn)
                    {
                        if (dryAll)
                        {
                            air = 1f;
                        }
                        else
                        {
                            // A 5 cm band around the waterline so a hem sweeping
                            // through the surface crossfades instead of snapping.
                            const float band = 0.05f;
                            air = Mathf.Clamp01(
                                (ch.Points[i].y - (waterY - band)) / (2f * band));
                        }
                    }
                    float dampValue = Mathf.Lerp(waterDamp, chainAirDamp, air);
                    float damp = Mathf.Exp(-dampValue * ch.DampingScale * dt);
                    float dampAlong = Mathf.Exp(-dampValue * ch.DampingScale
                        * Mathf.Lerp(0.3f, 0.85f, air) * dt);
                    Vector3 gravity = Vector3.down
                        * (Mathf.Lerp(waterGravity, chainAirGravity, air)
                            * ch.GravityScale * sway * dt * dt);
                    float flowShare = 1f - air;
                    Vector3 dir = ch.Points[i] - ch.Points[i - 1];
                    if (dir.sqrMagnitude > 1e-10f)
                    {
                        dir.Normalize();
                    }
                    else
                    {
                        dir = Vector3.down;
                    }

                    Vector3 vel = ch.Points[i] - ch.Prev[i];
                    float along = Vector3.Dot(vel, dir);
                    Vector3 velAcross = vel - dir * along;
                    vel = dir * (along * dampAlong) + velAcross * damp;
                    ch.Prev[i] = ch.Points[i];

                    // A slow travelling wave down the strand. Each joint is pushed a
                    // little later than the one above it, so the tail ripples instead
                    // of swinging as a single piece; the w^1.5 ramp keeps the scalp end
                    // still. Still water is never quite still, which is why the hair
                    // keeps drifting while the player floats.
                    float ripple = Mathf.Sqrt(w) * w;
                    Vector3 flow = new Vector3(
                        Mathf.Sin(t * 1.1f + ch.FlowPhase - i * 0.55f),
                        0.5f * Mathf.Sin(t * 0.83f + ch.FlowPhase * 1.7f - i * 0.41f),
                        0.7f * Mathf.Cos(t * 0.95f + ch.FlowPhase * 1.3f - i * 0.63f))
                        * (_hairFlow.Value * ch.FlowScale * sway * flowShare * ripple * dt);

                    Vector3 step = vel + gravity + flow;
                    // Safety net for hard turns and the first frames after a scene load:
                    // the constraint passes feed corrections back as velocity, and an
                    // uncapped whip can fling a joint across the screen. 12 cm per frame
                    // is over 7 m/s at 60 fps, far beyond any swimming motion.
                    if (step.sqrMagnitude > 0.0144f)
                    {
                        step = step.normalized * 0.12f;
                    }
                    ch.Vel[i] = step;
                    ch.Points[i] += step;
                }
                ch.Points[0] = ch.Root.TransformPoint(ch.RestLocal[0]);

                // Per-joint bending stiffness. Each segment is turned back towards the
                // rest turn relative to the segment above it, measured in the head's
                // frame. This is a different thing from pulling joints towards their
                // authored positions: the chain can still swing and stream as a whole,
                // but the curvature is spread along its length, so drag accumulates
                // towards the tip instead of rotating one stiff silhouette. The roots
                // keep the model's curve, the ends straighten out in the current.
                if (bendK > 0f && ch.RestBend != null)
                {
                    Quaternion rootRot = sheetFrame;
                    Quaternion invRootRot = Quaternion.Inverse(rootRot);
                    for (int i = 1; i < joints - 1; i++)
                    {
                        Vector3 a = ch.Points[i] - ch.Points[i - 1];
                        Vector3 b = ch.Points[i + 1] - ch.Points[i];
                        if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f)
                        {
                            continue;
                        }
                        Vector3 along = a.normalized;
                        // Hair loosens towards the tip: the model's tails flare
                        // outwards and holding that flare rigidly while swimming
                        // turns the ribbon into a stiff fan. A skirt panel is the
                        // opposite: it is a flat piece of fabric that must keep its
                        // authored course, so it retains the curve all the way down
                        // and the correction gets stronger at the hem rather than
                        // softer.
                        float w = (float)i / (joints - 1);
                        float retain = ch.Cloth ? 1f - 0.10f * w : 1f - 0.45f * w;
                        Vector3 want = (rootRot * ch.RestBend[i - 1] * invRootRot) * along;
                        want = Vector3.Slerp(along, want, retain);
                        // The tip also responds more softly than the root: a stiff
                        // correction on a light end makes it ring at the frame rate.
                        float strength = ch.Cloth ? 1f + 0.35f * w : 1f - 0.4f * w;
                        float bend = 1f - Mathf.Exp(-bendK * strength * dt);
                        Quaternion turn = Quaternion.FromToRotation(b.normalized, want);
                        turn = Quaternion.Slerp(Quaternion.identity, turn, bend);
                        // Rotating about the joint above keeps the segment length.
                        ch.Points[i + 1] = ch.Points[i] + turn * b;
                    }
                }

                int tip = joints - 1;
                float swing = Vector3.Distance(ch.Points[tip],
                    ch.Root.TransformPoint(ch.RestLocal[tip]));
                if (swing > ch.MaxSwing)
                {
                    ch.MaxSwing = swing;
                }

                // Keep the segments rigid. Three passes: with eight joints per tail,
                // two leave visible kinks when the soft tip is pushed around.
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int i = 1; i < joints; i++)
                    {
                        Vector3 d = ch.Points[i] - ch.Points[i - 1];
                        float len = d.magnitude;
                        if (len > 1e-5f)
                        {
                            ch.Points[i] = ch.Points[i - 1] + d * (ch.Length[i - 1] / len);
                        }
                    }
                }

                // Fringe and sideburns only get a short leash: anything that has
                // drifted further than MaxLag from the pose the head alone
                // produces is pulled straight back. They can still lag and settle,
                // but a swim stroke can no longer fling them over the face.
                float maxLag = ch.MaxLag * sway;
                // The leash is what stops a skirt folding up over the waist on a
                // sudden turn, but it also used to make the hardness slider feel
                // dead: a soft panel was pulled back to exactly the same shape as
                // a stiff one. Soft settings now also get a longer leash, so the
                // hem can lift and fold like light fabric; stiff settings hold it
                // closer to the authored pose. Square root keeps the mid range
                // gentle and only opens up near the soft end.
                if (ch.Cloth && maxLag > 0f && _skirtStiffness != null)
                {
                    float stiff = Mathf.Max(0.1f, _skirtStiffness.Value);
                    maxLag *= Mathf.Clamp(1f / Mathf.Sqrt(stiff), 0.5f, 3f);
                }
                // User-facing leash: a straight multiplier on the same clamp, so
                // the hem's travel can be tuned without changing how springy the
                // fabric is. 0 pins the panel to the authored pose; it still
                // turns rigidly with the hips because only the lag is clamped.
                if (ch.Cloth && maxLag > 0f && _skirtLeash != null)
                    maxLag *= Mathf.Max(0f, _skirtLeash.Value);
                if (ch.Light)
                {
                    // A light strand is not bent at all: the clamped offset of its
                    // tip is turned into one small rotation of the whole strand
                    // around its root. Leashing every joint on its own let the nine
                    // fringe strands curl into each other until they read as one
                    // clump; a rigid lean keeps the authored shape, the gaps
                    // between the strands and each flat card's roll untouched. At
                    // sway 0 the offset is zero and the points land exactly on the
                    // model's own pose.
                    Vector3 rootPos = ch.Points[0];
                    Vector3 rigidTip = ch.Root.TransformPoint(ch.RestLocal[tip]);
                    Vector3 delta = ch.Points[tip] - rigidTip;
                    float mag = delta.magnitude;
                    if (mag > maxLag)
                    {
                        delta *= maxLag / mag;
                    }
                    Vector3 from = rigidTip - rootPos;
                    Vector3 to = from + delta;
                    ch.Swing = from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f
                        ? Quaternion.FromToRotation(from.normalized, to.normalized)
                        : Quaternion.identity;
                    for (int i = 1; i < joints; i++)
                    {
                        Vector3 rigid = ch.Root.TransformPoint(ch.RestLocal[i]);
                        ch.Points[i] = rootPos + ch.Swing * (rigid - rootPos);
                    }
                }
                else if (ch.MaxLag > 0f)
                {
                    for (int i = 1; i < joints; i++)
                    {
                        Vector3 rigid = ch.Root.TransformPoint(ch.RestLocal[i]);
                        Vector3 delta = ch.Points[i] - rigid;
                        float mag = delta.magnitude;
                        if (mag > maxLag)
                        {
                            ch.Points[i] = maxLag > 0f
                                ? rigid + delta * (maxLag / mag)
                                : rigid;
                        }
                    }
                }

                // Collision with the body. The solver only knows the root, so
                // without this the tails cut through the back and the necktie
                // through the chest whenever the player turns quickly.
                // Fringe chains are exempt: their bones sit inside the skull by
                // design, so the head capsule would shove every bang outward and
                // leave the fringe permanently splayed away from the authored pose.
                // Skirts obey the SkirtPhysics switch here, hair and accessories the
                // HairCollision one, so switching one off does not silently disable
                // collision for the other.
                bool collide = !ch.Light && (ch.Skirt ? skirtOn : hairCollide);
                if (collide && (_colliders.Count > 0 || _bodySlices.Count > 0))
                {
                    // Two rounds: pushing one joint out of a leg pulls its neighbour
                    // back in through the length constraint, and one pass left the
                    // next panel just inside the thigh. Skirt and coat panels get a
                    // third: their segment midpoints also push both ends, so the
                    // per-round correction is larger and needs a little more time to
                    // settle when a running knee sweeps through the hem.
                    int rounds = ch.Cloth ? 3 : 2;
                    for (int round = 0; round < rounds; round++)
                    {
                        ResolveClothCollision(ch);
                        for (int i = 1; i < joints; i++)
                        {
                            Vector3 d = ch.Points[i] - ch.Points[i - 1];
                            float len = d.magnitude;
                            if (len > 1e-5f)
                            {
                                ch.Points[i] = ch.Points[i - 1] +
                                    d * (ch.Length[i - 1] / len);
                            }
                        }
                    }
                    if (ch.Cloth)
                    {
                        // The last length pass is free to pull a panel back into the
                        // thigh - the running knee is the worst case, because the
                        // collider has just been expanded for its motion. One final
                        // push is collision-only, so nothing can undo it until the
                        // next frame's simulation. Hair does not need this: a few
                        // millimetres deeper into the skull is invisible, whereas
                        // skirt panels clipping through a leg are not.
                        ResolveClothCollision(ch);
                    }
                }

                // The bending and length passes move the joints directly. Feeding
                // that correction back as velocity re-excites the light tip, which
                // then rings at close to the frame rate instead of drifting like hair.
                // Keep the integrated (drag/gravity/flow) motion as the velocity, and
                // only a small share of the constraint correction - least towards the
                // tip, where the ringing is visible.
                for (int i = 1; i < joints; i++)
                {
                    float w = (float)i / (joints - 1);
                    Vector3 corrected = ch.Points[i] - (ch.Prev[i] + ch.Vel[i]);
                    float keep = Mathf.Lerp(0.25f, 0.04f, w);
                    ch.Prev[i] = ch.Points[i] - (ch.Vel[i] + corrected * keep);
                }

                // Point the bones along the simulated segments, relative to the pose the
                // mesh was bound in. The particle chain lives in world space: it does
                // not rotate just because the player turned. Posing the bone as
                // FromToRotation(current frame, aim) * current frame therefore folded
                // the player's accumulated yaw into a roll around the tail's own
                // length - a hanging tail is nearly vertical, and yaw about the vertical
                // IS roll about its axis - so the flat ribbons spun while steering.
                //
                // The roll is carried along the chain instead: start from the bind
                // reference at the root and parallel-transport it from segment to
                // segment. That is the twist-free choice (no spin around the tail's
                // axis), it is continuous even when the tail points straight up - the
                // minimal-arc rotation is singular there and made the tips flip - and
                // a static chain still yields the identity rotation.
                Vector3 prevAim = Vector3.zero;
                Vector3 up = Vector3.zero;
                // Light strands move as one rigid piece: the head's rotation since
                // the bind pose plus the small lean the solver allowed. Applying
                // that delta to the authored bone rotations keeps every flat card
                // facing the way the model author built it - posing each bone from
                // a transported up-vector rolled the fringe cards into each other.
                // The root's rotation since the bind pose. Both the bind aim and
                // the bind up-vector are rest references in world space, so both
                // must be carried by this delta. Leaving the up-vector in world
                // space froze the strand's twist: a hanging tie barely changes its
                // aim when the body yaws, so its whole rotation comes from the up
                // reference. The accessories below use this delta as the target
                // frame they lag behind, not as an immediate replacement.
                Quaternion rootDelta = ch.Root.rotation * Quaternion.Inverse(ch.RestRootRot);
                Quaternion lightDelta = ch.Light
                    ? ch.Swing * rootDelta
                    : Quaternion.identity;
                if (ch.Accessory)
                {
                    if (ch.RollUp == null || ch.RollUp.Length != ch.Bones.Length)
                    {
                        ch.RollUp = new Vector3[ch.Bones.Length];
                        ch.RollAim = new Vector3[ch.Bones.Length];
                        ch.HasRoll = false;
                    }
                    if (!ch.HasRoll)
                    {
                        // Seed from the body frame so the first simulated frame
                        // does not read as a full turn of the tie.
                        for (int k = 0; k < ch.Bones.Length; k++)
                        {
                            ch.RollUp[k] = rootDelta * ch.RestUpWorld[k];
                            ch.RollAim[k] = rootDelta * ch.RestDirWorld[k];
                        }
                        ch.HasRoll = true;
                    }
                }
                for (int i = 0; i < ch.Bones.Length; i++)
                {
                    Transform b = ch.Bones[i];
                    if (ch.Light)
                    {
                        b.rotation = lightDelta * ch.RestRotWorld[i];
                        continue;
                    }
                    Vector3 aim = ch.Points[i + 1] - ch.Points[i];
                    float mag = aim.magnitude;
                    if (mag < 1e-4f)
                    {
                        continue;
                    }
                    aim /= mag;
                    if (i == 0)
                    {
                        // Anchor the first segment to the body. Posing it fully from
                        // the simulated points peels the strip out of the scalp (or
                        // the skirt out of its waistband) and shows the modelling
                        // seam at the root - the root edge of the hair cards on some
                        // models. Keeping half of the bind direction there while the
                        // rest of the strand still swings hides it.
                        Vector3 restAim = rootDelta * ch.RestDirWorld[0];
                        if (restAim.sqrMagnitude > 1e-8f)
                        {
                            aim = Vector3.Slerp(restAim.normalized, aim, 0.5f);
                            aim.Normalize();
                        }
                    }
                    if (ch.Accessory)
                    {
                        // Top-led twist. A necktie hangs almost straight down, so
                        // a body yaw barely changes its aim: what turns is the roll
                        // of the blade. Carry last frame's roll along the segment
                        // and ease it towards the body frame, fastest at the knot
                        // and slower towards the tip. The top therefore starts the
                        // turn and each segment below is dragged after it, instead
                        // of the whole flat blade snapping round with the body like
                        // a hair card (which read as the tie leading the turn).
                        Vector3 bodyUp = rootDelta * ch.RestUpWorld[i];
                        Vector3 carried = ch.RollAim[i].sqrMagnitude > 1e-8f
                            ? Quaternion.FromToRotation(ch.RollAim[i], aim) * ch.RollUp[i]
                            : bodyUp;
                        float w = ch.Bones.Length > 1
                            ? (float)i / (ch.Bones.Length - 1) : 0f;
                        float rate = Mathf.Max(0.05f, _hairTwistFollow.Value)
                            * Mathf.Lerp(3.2f, 1.0f, w);
                        up = Vector3.Slerp(carried.normalized, bodyUp.normalized,
                            1f - Mathf.Exp(-rate * dt));
                    }
                    else if (i == 0)
                    {
                        // Carried by the root, not left in world space: this is
                        // what makes the strand's twist follow the body.
                        up = rootDelta * ch.RestUpWorld[0];
                    }
                    else if (Vector3.Dot(prevAim, aim) < -0.9999f)
                    {
                        // A hairpin between two segments: transporting through it is
                        // undefined. Keep the previous reference; the next step
                        // re-orthogonalises it against the new aim.
                    }
                    else
                    {
                        up = Quaternion.FromToRotation(prevAim, aim) * up;
                    }
                    Vector3 u = up - aim * Vector3.Dot(up, aim);
                    if (u.sqrMagnitude < 1e-6f)
                    {
                        u = Vector3.Cross(aim, Vector3.forward);
                        if (u.sqrMagnitude < 1e-6f)
                        {
                            u = Vector3.Cross(aim, Vector3.right);
                        }
                    }
                    u.Normalize();
                    up = u;
                    if (ch.Accessory)
                    {
                        ch.RollUp[i] = u;
                        ch.RollAim[i] = aim;
                    }

                    Quaternion target = Quaternion.LookRotation(aim, u);
                    Quaternion bind = Quaternion.LookRotation(
                        ch.RestDirWorld[i], ch.RestUpWorld[i]);
                    b.rotation = (target * Quaternion.Inverse(bind)) * ch.RestRotWorld[i];
                    prevAim = aim;
                }
            }

            if (_verbose.Value && Time.unscaledTime >= _nextHairLog)
            {
                _nextHairLog = Time.unscaledTime + 2f;
                for (int c = 0; c < _clothChains.Count; c++)
                {
                    ClothChain ch = _clothChains[c];
                    int last = ch.Points.Length - 1;
                    Vector3 rigid = ch.Root.TransformPoint(ch.RestLocal[last]);
                    Logger.LogInfo("Cloth " + ch.Name
                        + ": tipLocal=" + ch.Root.InverseTransformPoint(ch.Points[last])
                        + " swing=" + Vector3.Distance(ch.Points[last], rigid).ToString("F3")
                        + " peak=" + ch.MaxSwing.ToString("F3"));
                    ch.MaxSwing = 0f;
                }
                Logger.LogInfo("Cloth resets in last interval: " + _clothResets);
                _clothResets = 0;
            }
        }

        private void LogBoneWorld(Transform[] bones, Dictionary<string, int> indexByName, string name)
        {
            int index;
            if (indexByName.TryGetValue(name, out index))
            {
                Logger.LogInfo("Model bone " + name + " world=" + bones[index].position);
            }
        }

        private Vector3 SourcePos(string name)
        {
            SourceBone sb;
            if (_diagSources != null && _diagSources.Bones.TryGetValue(name, out sb))
            {
                return sb.Bone.position;
            }
            return Vector3.zero;
        }

        private Vector3 ModelPos(string name)
        {
            int index;
            if (_diagIndexByName != null && _diagIndexByName.TryGetValue(name, out index))
            {
                return _diagBones[index].position;
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Moves one model hand bone onto the matching game hand bone. Only used when the
        /// IK cannot run at all; it stretches the forearm, so it is a last resort.
        /// </summary>
        private void AlignHand(string name)
        {
            SourceBone sb;
            int index;
            if (_diagSources == null || _diagIndexByName == null ||
                !_diagSources.Bones.TryGetValue(name, out sb) ||
                !_diagIndexByName.TryGetValue(name, out index) ||
                sb.Bone == null || _diagBones[index] == null)
            {
                return;
            }
            _diagBones[index].position = sb.Bone.position;
        }

        /// <summary>
        /// Two-bone IK for one arm, built on the YYB model's own proportions.
        /// Every dimension the solver uses - the upper arm and forearm lengths,
        /// the palm length that turns the game's wrist target into a knuckle
        /// target, the reach limit, the maximum shoulder compensation and the
        /// tolerances - is measured from the model's skeleton (or derived as a
        /// fraction of those measurements) at build time, never taken from the
        /// suit's rig. The game's hand bone supplies the target pose only.
        ///
        /// The pipeline, in order: clear last frame's shoulder slide and twist,
        /// rebuild the clavicle roll from the model's rest frame, convert the
        /// game wrist target to a model knuckle target, solve the two-bone chain
        /// (with a proportional shoulder slide when the suit out-reaches the
        /// model), pin the hand to the game palm frame, rebuild the forearm from
        /// the hand while keeping the retargeted upper arm roll, hand the
        /// leftover roll to the MMD twist group, and finally blend the whole
        /// solve towards the retargeted pose while the IK fades.
        /// </summary>
        private void RebuildArmRootRoll(string side)
        {
            // Rebuild the clavicle (and 肩C, the game's aim bone) from their rest
            // frames, keeping only the direction the game's shoulder lift gave
            // them. Their retargeted roll is expressed in the suit's bind frame
            // and shows up as the skin at the arm root twisting (most obvious
            // with the seaglide, where the aim bone rotates up to ~80 degrees).
            // Called by the IK solve and by the direct-vanilla hold path, so the
            // gripping arm keeps the untwisted root in both modes.
            ArmRest rest = _armRest[side == "L" ? 0 : 1];
            Transform root = ModelTransform("clav_" + side);
            Transform upper = ModelTransform("shoulder_" + side);
            if (rest == null || !rest.HasClav || root == null || root.parent == null
                || _container == null)
            {
                return;
            }
            Quaternion containerRot = _container.rotation;
            Transform clavC = ModelTransform("clav_C_" + side);
            Transform child = clavC != null ? clavC : upper;
            if (child == null)
            {
                return;
            }
            Vector3 wantDir = child.position - root.position;
            if (wantDir.sqrMagnitude > 1e-8f)
            {
                Quaternion clavParentSwing = root.parent.rotation
                    * Quaternion.Inverse(containerRot * rest.ClavParentLocal);
                Quaternion refClav = clavParentSwing * (containerRot * rest.ClavLocal);
                Vector3 refClavDir = clavParentSwing * (containerRot * rest.ClavDir);
                root.rotation = Quaternion.FromToRotation(refClavDir, wantDir) * refClav;

                // 肩C sits at the shoulder joint; its rotation carries the
                // shoulder skin, the upper arm's inherited swing and the game's
                // aim lift (the arm pitching with the tool). Keep the rest roll
                // but map its rest direction onto the live upper-arm direction:
                // that preserves the aim lift, which a roll-only rebuild folds
                // away (the grip then loses its pitch). The live direction was
                // produced by the retargeted 肩C, so mapping onto it is a fixed
                // point - the lift is kept, the bind-frame roll is dropped.
                if (clavC != null && upper != null && upper.parent == clavC)
                {
                    Quaternion clavSwing = root.rotation
                        * Quaternion.Inverse(containerRot * rest.ClavLocal);
                    Quaternion refClavC = clavSwing * (containerRot * rest.ParentLocal);
                    clavC.rotation = refClavC;
                    if (rest.HasClavC)
                    {
                        Vector3 refCDir = clavSwing * (containerRot * rest.ClavCDir);
                        Vector3 wantCDir = upper.position - clavC.position;
                        if (refCDir.sqrMagnitude > 1e-8f
                            && wantCDir.sqrMagnitude > 1e-8f)
                        {
                            clavC.rotation = Quaternion.FromToRotation(refCDir, wantCDir)
                                * refClavC;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The hand bone of the game's first-person arms rig (the one the glove
        /// mesh and the tool socket hang from). This is the visible vanilla arm:
        /// it is what pitches up and down with the camera, while the retargeted
        /// body skeleton (which the model copies) does not. Returns null in a
        /// build where the rig is not available.
        /// </summary>
        private Transform VanillaHand(string side)
        {
            Player player = Player.main;
            ArmsController arms = player != null ? player.armsController : null;
            if (arms == null)
            {
                return null;
            }
            Transform hand = side == "L" ? arms.leftHand : arms.rightHand;
            if (hand != null && hand.gameObject.activeInHierarchy)
            {
                return hand;
            }
            // A tool plugged into the socket still marks where the vanilla hand
            // is, so fall back to it when the rig itself is hidden.
            Inventory inv = Inventory.main;
            return inv != null ? inv.toolSocket : hand;
        }

        /// <summary>
        /// Aims the model's gripping arm at the vanilla first-person hand with a
        /// two-bone solve - upper arm onto the solved elbow, forearm onto the
        /// hand - instead of swinging the whole chain rigidly. A rigid swing
        /// keeps the elbow on the shoulder-to-hand chord, but the vanilla arm is
        /// bent at the elbow, so a straight model arm pushed both elbows out to
        /// the side. Bending at the elbow puts them back where the body
        /// animation has them. Bone lengths never change (only rotations and
        /// one root slide), so nothing is stretched; the elbow skin bends,
        /// which is what the rig's elbow helpers exist for.
        ///
        /// The first-person arms rig has no clavicle, so aiming at its hand
        /// would put the whole swing on the model's shoulder bone and shear the
        /// sleeve at the arm root. A share of the swing is given to the
        /// clavicle first (the way a real shoulder girdle rotates with the
        /// arm), capped to a plausible clavicle angle; the shoulder only has to
        /// take the rest, which halves the crease at the arm root.
        ///
        /// The remaining distance is closed by sliding the arm root (the
        /// clavicle) along the aim line, capped to the same fraction of the
        /// arm's own length that the IK uses.
        /// </summary>
        /// <summary>
        /// Elbow direction for a held item, on the plane perpendicular to the
        /// aim line.
        ///
        /// The first choice is the elbow position that leaves the model's own
        /// wrist untwisted: the model's rest forearm direction, turned by the
        /// hand's current world rotation, is the direction the forearm would
        /// have if the wrist were at rest, so the elbow belongs l2 back along
        /// it from the wrist. Taking that point's offset from the aim axis as
        /// the pole minimizes the wrist bend the solve has to add.
        ///
        /// Falling back to the retargeted elbow is the second choice: it is
        /// right while the body animation really bends the arm, but when the
        /// retargeted arm is nearly straight its perpendicular offset is only a
        /// few millimetres and its direction is noise - that is what folded an
        /// elbow backwards. Below a real bend the pole becomes "down, slightly
        /// outwards", where a held item's elbow belongs.
        /// </summary>
        private Vector3 HoldPole(string side, ArmRest rest, Vector3 a, Vector3 elbow,
            Vector3 tDir, Quaternion handWorld, Vector3 target, float l2)
        {
            if (rest != null)
            {
                Vector3 naturalFore = handWorld * rest.MidDir;
                Vector3 ideal = target - naturalFore * l2 - a;
                ideal -= Vector3.Dot(ideal, tDir) * tDir;
                if (ideal.sqrMagnitude > 1e-4f)
                {
                    return ideal.normalized;
                }
            }
            Vector3 raw = elbow - a;
            raw -= Vector3.Dot(raw, tDir) * tDir;
            if (raw.sqrMagnitude > 0.05f * 0.05f)
            {
                return raw.normalized;
            }
            Vector3 down = _container != null ? -_container.up : Vector3.down;
            Vector3 outDir = _container != null ? _container.right : Vector3.right;
            Vector3 desired = down + outDir * (side == "L" ? -0.3f : 0.3f);
            desired -= Vector3.Dot(desired, tDir) * tDir;
            if (desired.sqrMagnitude < 1e-6f)
            {
                Vector3 fwd = _container != null ? _container.forward : Vector3.forward;
                desired = Vector3.Cross(tDir, fwd);
                desired -= Vector3.Dot(desired, tDir) * tDir;
            }
            return desired.sqrMagnitude > 1e-8f ? desired.normalized : Vector3.up;
        }

        private void AimArmAt(string side, Vector3 targetPos, float weight,
            bool supportPole)
        {
            Transform root = ModelTransform("clav_" + side);
            Transform upper = ModelTransform("shoulder_" + side);
            Transform mid = ModelTransform("elbow_" + side);
            Transform end = ModelTransform("hand_" + side);
            if (upper == null || mid == null || end == null)
            {
                return;
            }

            float w = Mathf.Clamp01(weight);
            if (w <= 0.0001f)
            {
                return;
            }

            // The hand's world rotation is already right: the retarget copies the
            // body suit hand, and the vanilla first-person hand has exactly that
            // rotation (logged as handDiff=0 every frame). Only its *position*
            // differs, so the solve moves the wrist and this rotation is put back
            // afterwards. Any correction that writes a different hand rotation
            // instead is what turned the palm face-down: the suit's bind wrist is
            // ~180 deg from its elbow, so a "wrist local delta" drags that bind
            // frame (the T-pose palm-down hand) onto the model.
            Quaternion handWorld = end.rotation;
            // The wrist angle the retarget already had. Whatever the solve adds
            // on top of this is the part the elbow choice is responsible for.
            float wrist0 = Quaternion.Angle(mid.rotation, end.rotation);
            ArmRest rest = _armRest[side == "L" ? 0 : 1];

            // Bone lengths are invariant; the solve only rotates and slides.
            float l1 = Vector3.Distance(upper.position, mid.position);
            float l2 = Vector3.Distance(mid.position, end.position);
            float armLen = l1 + l2;
            if (l1 < 1e-4f || l2 < 1e-4f)
            {
                return;
            }

            Vector3 t = targetPos;
            Vector3 a = upper.position;
            Vector3 b = mid.position;
            Vector3 want = t - a;
            if (want.sqrMagnitude < 1e-8f)
            {
                return;
            }

            Vector3 tDir = want.normalized;
            // First approximation of the bend plane, for the clavicle share
            // below; it is recomputed after the root moves. The support solve
            // uses the retargeted elbow (StablePole) instead of the held-item
            // pole: the support hand must follow the item, not guess a grip.
            Vector3 pole = supportPole
                ? StablePole(side, a, b, tDir)
                : HoldPole(side, rest, a, b, tDir, handWorld, t, l2);

            // How much the upper arm would have to swing with the clavicle left
            // alone. Only its angle is used, for the shoulder share below.
            float reach = armLen - 0.002f;
            float dd0 = Mathf.Clamp(want.magnitude,
                Mathf.Abs(l1 - l2) + 0.002f, reach);
            float ang0 = Mathf.Acos(Mathf.Clamp(
                (l1 * l1 + dd0 * dd0 - l2 * l2) / (2f * l1 * dd0), -1f, 1f));
            Vector3 elbowNeed = a + (tDir * Mathf.Cos(ang0) + pole * Mathf.Sin(ang0)) * l1;
            Quaternion swingNeed = Quaternion.FromToRotation(b - a, elbowNeed - a);
            float needAngle = Quaternion.Angle(Quaternion.identity, swingNeed);

            // Give the clavicle a capped share of that swing before the shoulder
            // has to take any of it. Rotating the clavicle moves the shoulder
            // joint (that is the point), so the solve below runs from the new
            // shoulder position.
            const float ClavAimShare = 0.35f;
            float clavAngle = 0f;
            if (root != null && needAngle > 0.01f)
            {
                clavAngle = Mathf.Min(needAngle * ClavAimShare, 25f) * w;
                root.rotation = Quaternion.Slerp(Quaternion.identity, swingNeed,
                    clavAngle / needAngle) * root.rotation;
            }

            a = upper.position;
            b = mid.position;
            want = t - a;
            float dist = want.magnitude;
            if (dist < 1e-5f)
            {
                return;
            }
            tDir = want / dist;
            // The clavicle swing moved the shoulder and tilted the aim plane;
            // recompute the bend plane from the new geometry.
            pole = supportPole
                ? StablePole(side, a, b, tDir)
                : HoldPole(side, rest, a, b, tDir, handWorld, t, l2);

            // The suit's arm out-reaches the model's; slide the arm root along
            // the aim line to close the shortfall (capped like the IK).
            float gap = dist - reach;
            float slide = 0f;
            if (dist > reach)
            {
                slide = Mathf.Min(dist - reach, 0.2f * armLen) * w;
                if (slide > 0.0005f)
                {
                    (root != null ? root : upper).position += tDir * slide;
                    a = upper.position;
                    want = t - a;
                    dist = want.magnitude;
                    if (dist < 1e-5f)
                    {
                        return;
                    }
                    tDir = want / dist;
                    pole = supportPole
                        ? StablePole(side, a, b, tDir)
                        : HoldPole(side, rest, a, b, tDir, handWorld, t, l2);
                }
            }

            // Two-bone solve: the elbow goes on the circle where both bones can
            // still reach the target, on the chosen side of the aim line.
            float dd = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 0.002f, reach);
            float ang = Mathf.Acos(Mathf.Clamp(
                (l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd), -1f, 1f));
            Vector3 newB = a + (tDir * Mathf.Cos(ang) + pole * Mathf.Sin(ang)) * l1;

            // Upper arm: minimal arc onto the solved elbow. A minimal arc adds
            // no roll, so the sleeve twist stays exactly as retargeted.
            Vector3 upperNow = mid.position - a;
            Quaternion upperRot = Quaternion.identity;
            if (upperNow.sqrMagnitude > 1e-8f && (newB - a).sqrMagnitude > 1e-8f)
            {
                upperRot = Quaternion.Slerp(Quaternion.identity,
                    Quaternion.FromToRotation(upperNow, newB - a), w);
                upper.rotation = upperRot * upper.rotation;
            }

            // Forearm: minimal arc onto the hand target. With the elbow on the
            // solve circle this puts the hand exactly on the vanilla hand.
            Vector3 b2 = mid.position;
            Vector3 aim = t - b2;
            Vector3 foreNow = end.position - b2;
            if (aim.sqrMagnitude > 1e-8f && foreNow.sqrMagnitude > 1e-8f)
            {
                mid.rotation = Quaternion.Slerp(Quaternion.identity,
                    Quaternion.FromToRotation(foreNow, aim), w) * mid.rotation;
            }

            // Put the hand back to the orientation the retarget gave it (see
            // handWorld above); the wrist joint absorbs the forearm's new
            // direction, which is the only place that angle belongs.
            end.rotation = Quaternion.Slerp(end.rotation, handWorld, w);
            float wristAngle = Quaternion.Angle(mid.rotation, end.rotation);

            if (w > 0.5f && Time.unscaledTime >=
                (side == "L" ? _nextAimLogL : _nextAimLogR))
            {
                if (side == "L")
                {
                    _nextAimLogL = Time.unscaledTime + 2f;
                }
                else
                {
                    _nextAimLogR = Time.unscaledTime + 2f;
                }
                Vector3 down = _container != null ? -_container.up : Vector3.down;
                Vector3 outDir = _container != null ? _container.right : Vector3.right;
                if (side == "L")
                {
                    outDir = -outDir;
                }
                Logger.LogInfo("Aim " + side + ": swing="
                    + Quaternion.Angle(Quaternion.identity, upperRot).ToString("F0")
                    + " clav=" + clavAngle.ToString("F0")
                    + " bend=" + Vector3.Angle(a - mid.position,
                        end.position - mid.position).ToString("F0")
                    + " gap=" + gap.ToString("F3")
                    + " slide=" + slide.ToString("F3")
                    + " handErr=" + Vector3.Distance(end.position, t).ToString("F3")
                    + " armLen=" + armLen.ToString("F3")
                    + " wrist0=" + wrist0.ToString("F0")
                    + " wrist=" + wristAngle.ToString("F0")
                    + " poleDown=" + Vector3.Dot(pole, down).ToString("F2")
                    + " poleOut=" + Vector3.Dot(pole, outDir).ToString("F2")
                    // How far the vanilla arms-rig hand's frame is from the body
                    // suit hand's: zero means only the hand's position needs the
                    // solve, and its rotation must be left alone.
                    + " handDiff=" + (VanillaHand(side) != null
                        && SourceTransform("hand_" + side) != null
                        ? Quaternion.Angle(VanillaHand(side).rotation,
                            SourceTransform("hand_" + side).rotation).ToString("F0")
                        : "n/a"));
            }
        }

        private void SolveArm(string side, float weight)
        {
            Transform root = ModelTransform("clav_" + side);
            Transform upper = ModelTransform("shoulder_" + side);
            Transform mid = ModelTransform("elbow_" + side);
            Transform end = ModelTransform("hand_" + side);
            Transform target = SourceTransform("hand_" + side);
            if (upper == null || mid == null || end == null || target == null)
            {
                AlignHand("hand_" + side);
                return;
            }

            // The pose the plain retarget produced this pass. The solved pose is
            // blended towards it when the IK is fading in or out, so picking an
            // item up or holstering it does not snap the arm in a single frame.
            // Captured before ResetArmRoot: clearing last frame's twist group
            // rotates the elbow and the hand in world space (their locals were
            // written by the retarget against that twist), and the fade target
            // has to be the un-twisted retargeted pose.
            Quaternion retargetUpper = upper.rotation;
            Quaternion retargetMid = mid.rotation;
            Quaternion retargetEnd = end.rotation;

            // The retarget pass only writes rotations, so any slide applied to the arm
            // root last frame is still there. Put it back before solving anew.
            ResetArmRoot(side);

            Vector3 retargetUpperPos = upper.position;
            Vector3 retargetRootPos = root != null ? root.position : Vector3.zero;

            ArmRest rest = _armRest[side == "L" ? 0 : 1];
            Quaternion containerRot = _container != null
                ? _container.rotation : Quaternion.identity;

            // Proportions. Bone offsets are fixed in the model's rest skeleton,
            // so these are the YYB arm dimensions regardless of the current pose.
            float l1 = Vector3.Distance(upper.position, mid.position);
            float l2 = Vector3.Distance(mid.position, end.position);
            float reach = l1 + l2;
            if (l1 < 1e-4f || l2 < 1e-4f)
            {
                AlignHand("hand_" + side);
                return;
            }
            // The suit's arms are longer than YYB's, so the game sometimes asks
            // for a reach the model does not have. The arm root may slide after
            // the target, but only within a fixed fraction of the model's own
            // arm length: a hard-coded distance would mean different things on
            // models of different sizes.
            float maxSlide = 0.2f * reach;

            RebuildArmRootRoll(side);

            Vector3 a = upper.position;
            Vector3 b = mid.position;
            Vector3 c = end.position;
            Vector3 t = target.position;

            // The model's hand bone is the wrist, while a held item is gripped in the
            // middle of the palm. The MMD palm is shorter than the suit glove's, so
            // matching wrists leaves the item sitting on the fingertips. Match the base
            // of the middle finger instead (it does not move when the fingers curl):
            // that is the point the grip is built around.
            Transform modelKnuckle = ModelTransform("hand_" + side + "_midl_base");
            Transform gameKnuckle = SourceTransform("hand_" + side + "_midl_base");
            float palmLength = 0f;
            if (modelKnuckle != null && gameKnuckle != null)
            {
                Vector3 palm = modelKnuckle.position - c;
                Vector3 gameHandDir = gameKnuckle.position - target.position;
                palmLength = palm.magnitude;
                if (palmLength > 0.02f * reach && palmLength < 0.5f * l1
                    && gameHandDir.sqrMagnitude > 1e-8f)
                {
                    // The rebuilt wrist points the model's knuckle along the
                    // suit's wrist-to-knuckle direction, so the knuckle ends up
                    // one model palm length from the wrist along that direction.
                    // Put the wrist where that lands on the game's knuckle. If
                    // that lies more than a fifth of a forearm away from the
                    // game wrist, the palm calibration is not trustworthy.
                    Vector3 grip = gameKnuckle.position
                        - gameHandDir.normalized * palmLength;
                    if (Vector3.Distance(grip, t) < 0.35f * l2)
                    {
                        t = grip;
                    }
                }
            }

            Vector3 at = t - a;
            float dist = at.magnitude;
            if (dist < 1e-4f)
            {
                AlignHand("hand_" + side);
                return;
            }

            // The model's arms keep their YYB length; the shoulder may move, but
            // only by a fraction of that length (maxSlide, from the measurements
            // at the top). When the game stretches its arm (swimming strokes,
            // items held at arm's length) the hand cannot reach the item, so the
            // whole arm slides toward it instead of the mesh being stretched. The
            // slide has to happen at the clavicle (肩), not at the upper arm:
            // moving shoulder_* away from its parent left a gap at the shoulder
            // joint, and the skin between the two bones folded over itself while
            // holding the seaglide. Sliding the clavicle keeps the whole arm chain
            // connected; its root sits inside the shoulder pad and the torso.
            float slide = 0f;
            if (dist > reach)
            {
                slide = Mathf.Min(dist - reach + 0.001f, maxSlide);
                Vector3 slideDir = at / dist;
                if (root != null)
                {
                    root.position = retargetRootPos + slideDir * slide;
                    a = upper.position;
                }
                else
                {
                    upper.position = a + slideDir * slide;
                }
                // Re-read both ends of the upper arm: the slide moved the whole
                // chain, and the re-aim below must start from the arm's actual
                // current direction. Using the pre-slide elbow here rotated the
                // upper arm by the slide error, which showed up as the folded
                // shoulder.
                a = upper.position;
                b = mid.position;
                c = end.position;
                at = t - a;
                dist = at.magnitude;
            }

            // Clamp into the reachable annulus: an arm that cannot reach just points
            // straight at the target instead of stretching.
            float dd = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 0.002f, l1 + l2 - 0.002f);
            Vector3 tDir = at / dist;

            Vector3 perp = StablePole(side, a, b, tDir);

            float cosA = (l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd);
            float angA = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            Vector3 newB = a + (tDir * Mathf.Cos(angA) + perp * Mathf.Sin(angA)) * l1;

            // The orientation the retarget gave the hand, kept for the fallback
            // write below (when there are no thumb bones to calibrate the palm
            // against).
            Quaternion handRot = end.rotation;

            bool hasGrip = rest != null && rest.HasPalm;

            // The hand's final world rotation. The grip calibration carries the
            // game hand's own rotation onto the model hand's palm frame, so the
            // palm wraps the held item exactly as the suit's gloved hand does.
            // This is a palm-to-palm alignment, not a wrist-direction guess: the
            // retargeted roll lives in the suit's bind frame and left the palm
            // a quarter turn off the handle, while rebuilding the hand from the
            // model's rest frame flipped it the other way.
            Quaternion handWorld = hasGrip
                ? target.rotation * rest.PalmFix
                : Quaternion.identity;

            // The hand is pinned to the game hand, so the forearm's roll has to
            // be rebuilt from it. The upper arm's roll must not be: the
            // clavicle, the shoulder pad and the sleeve at the arm root all
            // inherit it, and rebuilding it from the hand moved the entire
            // bind-frame roll difference up to the root, which is what made the
            // arm root twist (worst with the seaglide and the propulsion
            // cannon). Keep the retargeted roll here and replace only the
            // direction; the roll the pinned wrist leaves over is handed to the
            // MMD twist group at the end of the solve, which spreads it along
            // the upper arm instead of folding it into the elbow.
            bool chain = hasGrip && rest.HasWrist;
            Quaternion desiredMid = chain
                ? handWorld * Quaternion.Inverse(rest.WristLocal)
                : Quaternion.identity;

            {
                Vector3 upperNow = mid.position - a;
                if (upperNow.sqrMagnitude > 1e-8f && (newB - a).sqrMagnitude > 1e-8f)
                {
                    // Rolling a bone about the shoulder-elbow axis moves
                    // nothing, so the elbow position is unchanged by the roll.
                    upper.rotation = Quaternion.FromToRotation(upperNow, newB - a)
                        * upper.rotation;
                }
            }

            Vector3 b2 = mid.position;
            Vector3 aim = t - b2;

            if (chain)
            {
                // Rebuild the forearm's roll from the pinned hand (through the
                // bind-pose wrist angle), then the minimal arc puts the hand on
                // the target. Only the wrist roll is taken from the hand; the
                // upper arm keeps the retargeted roll above.
                mid.rotation = desiredMid;
                // Read the hand's actual offset under that rotation (there may be
                // twist bones in between) and swing the forearm onto the target.
                Vector3 dirNow = end.position - b2;
                if (aim.sqrMagnitude > 1e-8f && dirNow.sqrMagnitude > 1e-8f)
                {
                    mid.rotation = Quaternion.FromToRotation(dirNow, aim) * desiredMid;
                }
            }
            else if (aim.sqrMagnitude > 1e-8f)
            {
                // The forearm has already inherited the upper arm's swing (it is
                // a child), so a minimal arc onto the hand target keeps the
                // pronation this pose retargeted onto it.
                Vector3 c2 = end.position;
                mid.rotation = Quaternion.FromToRotation(c2 - b2, aim) * mid.rotation;
            }

            if (hasGrip)
            {
                end.rotation = handWorld;
            }
            else
            {
                // No thumb bones to calibrate from. Carry the forearm's IK
                // swing onto the retargeted hand: the wrist inherits the elbow
                // bend the IK added instead of keeping its pre-IK orientation
                // (which read as a 90 degree wrist), and the palm keeps the
                // retargeted roll.
                Quaternion handSwing = mid.rotation * Quaternion.Inverse(retargetMid);
                end.rotation = handSwing * handRot;
            }

            if (weight < 0.9999f)
            {
                // Blend the slide at the clavicle, the bone that carries the arm:
                // moving the upper arm alone here would tear it off its parent
                // again while the weight is still fading in or out.
                if (root != null)
                {
                    root.position = Vector3.Lerp(retargetRootPos, root.position, weight);
                }
                else
                {
                    upper.position = Vector3.Lerp(retargetUpperPos, upper.position, weight);
                }
                upper.rotation = Quaternion.Slerp(retargetUpper, upper.rotation, weight);
                mid.rotation = Quaternion.Slerp(retargetMid, mid.rotation, weight);
                end.rotation = Quaternion.Slerp(retargetEnd, end.rotation, weight);
            }

            // Hand the roll difference the pinned hand leaves over to the
            // model's MMD twist group. 腕捩 (arm_twist_*) sits between the upper
            // arm and the elbow and 腕捩1/2/3 are siblings that copy 25/50/75%
            // of its rotation, so the sleeve twists gradually from the shoulder
            // to the elbow instead of folding all at the elbow joint. The group
            // is not part of the retarget, so its local rotations are rebuilt
            // from the rest pose here every frame (nothing accumulates).
            string twistDiag = "";
            if (chain && rest.ArmTwist != null)
            {
                Quaternion elbowWorld = mid.rotation;
                Vector3 twistAxis = mid.position - rest.ArmTwist.position;
                if (twistAxis.sqrMagnitude > 1e-8f)
                {
                    twistAxis.Normalize();
                    Quaternion invUpper = Quaternion.Inverse(upper.rotation);
                    // Measured against the upper arm's retargeted frame, so
                    // this is exactly the roll the elbow would otherwise carry.
                    // Scaling by the IK weight keeps it fading in and out with
                    // the rest of the solved pose.
                    float twist = weight * SignedTwist(invUpper * elbowWorld, twistAxis);
                    rest.ArmTwist.localRotation =
                        Quaternion.AngleAxis(twist, invUpper * twistAxis);
                    if (rest.ArmTwistHelpers != null && rest.ArmTwistRatios != null)
                    {
                        int n = Mathf.Min(rest.ArmTwistHelpers.Length,
                            rest.ArmTwistRatios.Length);
                        for (int i = 0; i < n; i++)
                        {
                            Transform helper = rest.ArmTwistHelpers[i];
                            if (helper != null)
                            {
                                helper.localRotation = Quaternion.AngleAxis(
                                    twist * rest.ArmTwistRatios[i],
                                    invUpper * twistAxis);
                            }
                        }
                    }
                    // 腕捩 is the elbow's parent, so its new rotation disturbed
                    // the elbow's world frame. The elbow sits on the rotation
                    // axis, so only the rotation moved: write the solved frame
                    // back so the pinned hand stays exactly where it was.
                    mid.rotation = elbowWorld;
                    float residual = SignedTwist(
                        Quaternion.Inverse(rest.ArmTwist.rotation) * elbowWorld,
                        twistAxis);
                    twistDiag = string.Format(" twist={0:F0}/{1:F0}", twist, residual);
                }
            }

            float nextLog = side == "L" ? _nextIkLogL : _nextIkLogR;
            if ((_verbose.Value || weight > 0.5f) && Time.unscaledTime >= nextLog)
            {
                if (side == "L")
                {
                    _nextIkLogL = Time.unscaledTime + 0.5f;
                }
                else
                {
                    _nextIkLogR = Time.unscaledTime + 0.5f;
                }

                // Interior angle at the elbow (180 = straight arm) and how far the
                // model's shoulder joint sits from the game's. Together with the
                // target distance ratio this says whether the forearm fold comes
                // from the limb proportions or from a misplaced shoulder.
                float elbowDeg = Vector3.Angle(a - b2, t - b2);
                Transform srcSh = SourceTransform("shoulder_" + side);
                float shErr = srcSh != null ? Vector3.Distance(srcSh.position, a) : -1f;
                string aimDelta = "";
                SourceBone aimSb;
                if (_diagSources != null &&
                    _diagSources.Bones.TryGetValue("clav_" + side + "_aim", out aimSb) &&
                    aimSb.Bone != null && aimSb.SmrTransform != null)
                {
                    Quaternion restWorld = aimSb.SmrTransform.rotation * aimSb.RestLocalRot;
                    aimDelta = " aim=" + Quaternion.Angle(restWorld, aimSb.Bone.rotation).ToString("F0");
                }

                // How far the wrist deviates from its bind-pose angle, against
                // the same quantity on the suit. A large model value with a
                // small game value means the forearm roll is disagreeing with
                // the pinned hand again.
                string wristDiag = "";
                if (rest != null && rest.HasWrist)
                {
                    float modelWrist = Quaternion.Angle(rest.WristLocal,
                        Quaternion.Inverse(mid.rotation) * end.rotation);
                    float gameWrist = -1f;
                    Transform srcElbow = SourceTransform("elbow_" + side);
                    Transform srcHand = SourceTransform("hand_" + side);
                    if (rest.HasGameWrist && srcElbow != null && srcHand != null)
                    {
                        gameWrist = Quaternion.Angle(rest.GameWristLocal,
                            Quaternion.Inverse(srcElbow.rotation) * srcHand.rotation);
                    }
                    wristDiag = string.Format(" wrist={0:F0}/{1:F0}", modelWrist, gameWrist);
                }

                // The same check at the elbow joint. Keeping both joints at
                // their bind-pose angle is what stops the roll error from
                // bunching up in the elbow skin.
                string elbowDiag = "";
                if (rest != null && rest.HasWrist)
                {
                    float modelElbow = Quaternion.Angle(rest.ElbowLocal,
                        Quaternion.Inverse(upper.rotation) * mid.rotation);
                    float gameElbow = -1f;
                    Transform srcUpper = SourceTransform("shoulder_" + side);
                    Transform srcElbow = SourceTransform("elbow_" + side);
                    if (rest.HasGameElbow && srcUpper != null && srcElbow != null)
                    {
                        gameElbow = Quaternion.Angle(rest.GameElbowLocal,
                            Quaternion.Inverse(srcUpper.rotation) * srcElbow.rotation);
                    }
                    elbowDiag = string.Format(" elbowJ={0:F0}/{1:F0}", modelElbow, gameElbow);
                }

                Logger.LogInfo(string.Format(
                    "IK {0}: l1={1:F3} l2={2:F3} dist={3:F3} reach={4:F3} ratio={5:F2} "
                    + "elbow={6:F0} slide={7:F3} err={8:F3} shErr={9:F3}{10}{13}{14}{15} "
                    + "shMdl={11} tgt={12}",
                    side, l1, l2, dist, reach, dist / reach, elbowDeg, slide,
                    Vector3.Distance(end.position, t), shErr, aimDelta,
                    a.ToString("F3"), t.ToString("F3"), wristDiag, elbowDiag,
                    twistDiag));
            }
        }

        // ------------------------------------------------------------------
        // Grip-anchored hold IK (HoldIkMode = Grip)
        //
        // A held tool is constrained where the hand meets the handle, not at
        // the wrist. The model side measures three things once, in the hand
        // bone's own space: the palm point (between the middle finger's base
        // and its first joint), the knuckle line (index to pinky) and the palm
        // normal. The game side measures the same three live from its own
        // finger bones. Rotating the model hand so its frame lands on the
        // game's frame puts the palm on the handle; the wrist position is
        // then simply wherever that hand rotation requires (point - R*point),
        // and the two-bone solve puts the arm there. This is the inverse of
        // the old wrist-to-wrist target: the palm leads and the wrist
        // follows, so the model's shorter palm no longer has to be absorbed
        // by an over-bent, over-rolled wrist.
        // ------------------------------------------------------------------

        private Transform HeldItemTransform()
        {
            Inventory inv = Inventory.main;
            InventoryItem held = inv != null && inv.quickSlots != null
                ? inv.quickSlots.heldItem : null;
            return held != null && held.item != null ? held.item.transform : null;
        }

        /// <summary>
        /// The five finger-base bones the grip frame is built from, found under
        /// a hand transform and cached. The view-model hand may have no finger
        /// bones of its own; returning null lets the caller fall back to the
        /// body hand's frame carried across by the hand-to-hand delta.
        /// </summary>
        private Transform[] GripBonesOf(Transform hand, string side)
        {
            int id = hand.GetInstanceID();
            GripBoneSet set;
            if (_gripBonesByHand.TryGetValue(id, out set) && set.Searched)
            {
                if (!set.Valid)
                {
                    return null;
                }
                for (int i = 0; i < set.Bones.Length; i++)
                {
                    if (set.Bones[i] == null)
                    {
                        // The rig was rebuilt under the same hand transform;
                        // search again rather than touch a destroyed bone.
                        set.Searched = false;
                        break;
                    }
                }
                if (set.Searched)
                {
                    return set.Bones;
                }
            }

            string[] suffixes = { "midl_base", "midl_mid", "point_base",
                "pinky_base", "thumb_base" };
            Transform[] bones = new Transform[suffixes.Length];
            Transform[] all = hand.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < suffixes.Length; i++)
            {
                string exact = "hand_" + side + "_" + suffixes[i];
                for (int j = 0; j < all.Length; j++)
                {
                    if (all[j] != hand && all[j].name == exact)
                    {
                        bones[i] = all[j];
                        break;
                    }
                }
                if (bones[i] == null)
                {
                    for (int j = 0; j < all.Length; j++)
                    {
                        if (all[j] == hand)
                        {
                            continue;
                        }
                        string n = all[j].name;
                        if (n == suffixes[i] || n.EndsWith("_" + suffixes[i]))
                        {
                            bones[i] = all[j];
                            break;
                        }
                    }
                }
            }
            bool valid = bones[0] != null && bones[1] != null
                && bones[2] != null && bones[3] != null;
            set = new GripBoneSet();
            set.Bones = bones;
            set.Valid = valid;
            set.Searched = true;
            _gripBonesByHand[id] = set;
            return valid ? bones : null;
        }

        /// <summary>
        /// Builds the grip frame from a hand and its finger bases:
        /// point = middle of the palm point, axis = index knuckle to pinky
        /// knuckle, normal = knuckle line x finger direction. Both fields the
        /// normal is built from lie in the palm plane, so the construction is
        /// stable across rigs; the thumb is not used (see the build block).
        /// </summary>
        private static bool GripFrameFromBones(Transform hand, Transform[] fingers,
            out Vector3 point, out Vector3 axis, out Vector3 normal)
        {
            point = Vector3.zero;
            axis = Vector3.right;
            normal = Vector3.up;
            if (hand == null || fingers == null || fingers.Length < 4
                || fingers[0] == null || fingers[1] == null
                || fingers[2] == null || fingers[3] == null)
            {
                return false;
            }
            Vector3 fwd = fingers[0].position - hand.position;
            Vector3 knuckle = fingers[3].position - fingers[2].position;
            if (fwd.sqrMagnitude < 1e-8f || knuckle.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            fwd.Normalize();
            axis = knuckle.normalized;
            normal = Vector3.Cross(axis, fwd);
            if (normal.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            normal.Normalize();
            point = (fingers[0].position + fingers[1].position) * 0.5f;
            return true;
        }

        /// <summary>
        /// Orthonormal hand axes from the raw measured pair. The knuckle line
        /// is not exactly perpendicular to the palm normal (the little finger
        /// sits lower), so its normal component is removed before building the
        /// basis. The same operation is applied to the model and the game side,
        /// which is what makes the two bases differ by a pure rotation.
        /// </summary>
        private static bool GripAxes(Vector3 axis, Vector3 normal,
            out Vector3 x, out Vector3 y)
        {
            x = Vector3.right;
            y = Vector3.up;
            if (axis.sqrMagnitude < 1e-10f || normal.sqrMagnitude < 1e-10f)
            {
                return false;
            }
            y = normal.normalized;
            x = axis - y * Vector3.Dot(axis, y);
            if (x.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            x.Normalize();
            return true;
        }

        /// <summary>
        /// Picks which hand transform carries the tool's live grip frame. The
        /// view-model hand is preferred because the tool hangs from it and it
        /// pitches with the view; the body hand (the retarget source) is the
        /// fallback. If the item is parented under one of them, that one wins
        /// outright - that is the authoritative answer to "which hand is the
        /// tool in". A view-model hand that has no finger bones of its own
        /// borrows the body frame carried across by the hand-to-hand delta.
        /// </summary>
        private bool TryGameGripFrame(string side, out Vector3 point,
            out Vector3 axis, out Vector3 normal, out string src, out string info)
        {
            point = Vector3.zero;
            axis = Vector3.right;
            normal = Vector3.up;
            src = "none";
            info = "";

            Player player = Player.main;
            ArmsController arms = player != null ? player.armsController : null;
            Transform fp = arms != null
                ? (side == "L" ? arms.leftHand : arms.rightHand) : null;
            Transform body = SourceTransform("hand_" + side);
            Transform item = HeldItemTransform();
            bool itemUnderFp = item != null && fp != null
                && (item == fp || item.IsChildOf(fp));
            bool itemUnderBody = item != null && body != null
                && (item == body || item.IsChildOf(body));
            string itemParent = "-";
            if (item != null)
            {
                Transform p = item.parent;
                itemParent = p != null ? p.name : "-";
                if (p != null && p.parent != null)
                {
                    itemParent += "<" + p.parent.name;
                }
            }

            Vector3 bp = Vector3.zero, ba = Vector3.right, bn = Vector3.up;
            bool bodyOk = body != null
                && GripFrameFromBones(body, GripBonesOf(body, side),
                    out bp, out ba, out bn);

            Vector3 pp = Vector3.zero, pa = Vector3.right, pn = Vector3.up;
            bool fpOk = false;
            string fpSrc = null;
            if (fp != null && fp != body)
            {
                if (GripFrameFromBones(fp, GripBonesOf(fp, side),
                    out pp, out pa, out pn))
                {
                    fpOk = true;
                    fpSrc = "fp";
                }
                else if (bodyOk)
                {
                    // Same animation drives both hands; carry the body's
                    // measured frame across the hand-to-hand rotation and
                    // offset instead of guessing axes from the hand bone.
                    Quaternion delta = fp.rotation * Quaternion.Inverse(body.rotation);
                    pp = fp.position + delta * (bp - body.position);
                    pa = delta * ba;
                    pn = delta * bn;
                    fpOk = true;
                    fpSrc = "fp-delta";
                }
            }

            float fpItem = fpOk && item != null
                ? Vector3.Distance(pp, item.position) : -1f;
            float bodyItem = bodyOk && item != null
                ? Vector3.Distance(bp, item.position) : -1f;
            float fpBody = fpOk && bodyOk ? Vector3.Distance(pp, bp) : -1f;

            bool useBody = !fpOk;
            if (fpOk)
            {
                // Parentage is authoritative; the distance check only catches
                // a stale view-model hand (hidden rig, wrong avatar) that sits
                // far from the item while the body hand is close.
                bool staleFp = !itemUnderFp && item != null
                    && fpItem > 0.35f && bodyItem >= 0f
                    && bodyItem < fpItem - 0.1f;
                useBody = bodyOk && (itemUnderBody || staleFp);
            }

            if (!useBody)
            {
                point = pp;
                axis = pa;
                normal = pn;
                src = fpSrc;
            }
            else if (bodyOk)
            {
                point = bp;
                axis = ba;
                normal = bn;
                src = "body";
            }
            else
            {
                return false;
            }

            info = string.Format(
                "src={0} item={1} under={2} fpItem={3} bodyItem={4} fpBody={5} parent={6}",
                src,
                item != null ? item.position.ToString("F3") : "-",
                itemUnderFp ? "fp" : (itemUnderBody ? "body" : "-"),
                fpItem >= 0f ? fpItem.ToString("F3") : "-",
                bodyItem >= 0f ? bodyItem.ToString("F3") : "-",
                fpBody >= 0f ? fpBody.ToString("F3") : "-",
                itemParent);
            return true;
        }

        /// <summary>
        /// Elbow direction for the grip solve: the model's own forearm
        /// direction (in hand-local space) turned by the solved hand gives the
        /// elbow that leaves the wrist at its bind angle; the retargeted elbow
        /// keeps the result close to the animation. The old HoldPole used
        /// rest.MidDir (container space) here, which was the wrong frame for a
        /// world hand rotation.
        /// </summary>
        private Vector3 GripPole(string side, ArmRest rest, Vector3 shoulder,
            Vector3 elbow, Vector3 tDir, Quaternion handWorld, Vector3 target,
            float l2)
        {
            Vector3 raw = elbow - shoulder;
            raw -= Vector3.Dot(raw, tDir) * tDir;
            bool hasRaw = raw.sqrMagnitude > 2.5e-4f;

            if (rest != null && rest.HasGripFrame)
            {
                Vector3 fore = handWorld * rest.ForearmDirLocal;
                Vector3 ideal = target - fore * l2 - shoulder;
                ideal -= Vector3.Dot(ideal, tDir) * tDir;
                if (ideal.sqrMagnitude > 1e-6f)
                {
                    Vector3 pole = ideal.normalized;
                    if (hasRaw)
                    {
                        // Prefer the animated elbow plane: the forearm keeps
                        // its authored roll now, so the hand-derived pole is
                        // only a correction, not the primary direction. A
                        // hand-heavy mix put the elbow in odd places on the
                        // heavier tools.
                        pole = (pole * 0.3f + raw.normalized * 0.7f).normalized;
                    }
                    return pole;
                }
            }
            if (hasRaw)
            {
                return raw.normalized;
            }
            Vector3 down = _container != null ? -_container.up : Vector3.down;
            Vector3 outDir = _container != null ? _container.right : Vector3.right;
            Vector3 desired = down + outDir * (side == "L" ? -0.3f : 0.3f);
            desired -= Vector3.Dot(desired, tDir) * tDir;
            if (desired.sqrMagnitude < 1e-6f)
            {
                Vector3 fwd = _container != null ? _container.forward : Vector3.forward;
                desired = Vector3.Cross(tDir, fwd);
                desired -= Vector3.Dot(desired, tDir) * tDir;
            }
            return desired.sqrMagnitude > 1e-8f ? desired.normalized : Vector3.up;
        }

        private void SolveGripIk(string side, float weight, bool liveTarget)
        {
            float w = Mathf.Clamp01(weight);
            if (w <= 0.0001f)
            {
                return;
            }

            int si = side == "L" ? 0 : 1;
            ArmRest rest = _armRest[si];
            Transform root = ModelTransform("clav_" + side);
            Transform upper = ModelTransform("shoulder_" + side);
            Transform mid = ModelTransform("elbow_" + side);
            Transform end = ModelTransform("hand_" + side);
            if (rest == null || !rest.HasGripFrame || upper == null
                || mid == null || end == null)
            {
                return;
            }

            // Clear last frame's slide/twist and rebuild the vanilla arm-root
            // roll first, so an early return (no target yet) still leaves the
            // arm in the exact direct-vanilla pose.
            ResetArmRoot(side);
            RebuildArmRootRoll(side);

            // Target: live while the item is held; the stored frame while the
            // weight fades out, so holstering does not snap to the swim pose.
            Vector3 gamePoint, gameAxis, gameNormal;
            string src;
            string info = "";
            if (!liveTarget)
            {
                if (!(si == 0 ? _gripLastValidL : _gripLastValidR)
                    || Time.unscaledTime - _gripLastTime[si] > 0.75f)
                {
                    return;
                }
                gamePoint = _gripLastPoint[si];
                gameAxis = _gripLastAxis[si];
                gameNormal = _gripLastNormal[si];
                src = "last";
            }
            else if (!TryGameGripFrame(side, out gamePoint, out gameAxis,
                out gameNormal, out src, out info))
            {
                if (!(si == 0 ? _gripLastValidL : _gripLastValidR)
                    || Time.unscaledTime - _gripLastTime[si] > 0.35f)
                {
                    return;
                }
                gamePoint = _gripLastPoint[si];
                gameAxis = _gripLastAxis[si];
                gameNormal = _gripLastNormal[si];
                src = "last";
            }
            else
            {
                _gripLastPoint[si] = gamePoint;
                _gripLastAxis[si] = gameAxis;
                _gripLastNormal[si] = gameNormal;
                _gripLastTime[si] = Time.unscaledTime;
                if (si == 0)
                {
                    _gripLastValidL = true;
                }
                else
                {
                    _gripLastValidR = true;
                }
            }

            Vector3 gx, gy;
            if (!GripAxes(gameAxis, gameNormal, out gx, out gy))
            {
                return;
            }

            // The model's own live grip frame, not the build-time bind frame.
            // The game's fingers are animated (grip states change with the
            // tool and while moving) and the model's fingers follow them every
            // frame. Measuring the model side at bind made the hand absorb the
            // whole finger motion, which is what tilted it off the item.
            Vector3 lx = Vector3.right, ly = Vector3.up;
            Vector3 modelPointWorld;
            Vector3 modelAxisWorld, modelNormalWorld;
            bool liveFrame = false;
            if (GripFrameFromBones(end, GripBonesOf(end, side),
                out modelPointWorld, out modelAxisWorld, out modelNormalWorld))
            {
                liveFrame = GripAxes(modelAxisWorld, modelNormalWorld,
                    out lx, out ly);
            }
            if (!liveFrame)
            {
                if (!GripAxes(rest.GripAxisLocal, rest.GripNormalLocal,
                    out lx, out ly))
                {
                    return;
                }
                modelPointWorld = end.position
                    + end.rotation * rest.GripPointLocal;
            }
            Vector3 gripLocal = Quaternion.Inverse(end.rotation)
                * (modelPointWorld - end.position);
            string frameSrc = liveFrame ? "live" : "bind";
            // basisModel must be a world-space basis in both paths. The bind
            // fallback used to feed the hand-local axes straight in, which is
            // only right when the retargeted hand happens to be at identity.
            Quaternion modelBasis = liveFrame
                ? Quaternion.LookRotation(Vector3.Cross(lx, ly), ly)
                : end.rotation * Quaternion.LookRotation(Vector3.Cross(lx, ly), ly);

            // The rebuilt direct-vanilla pose is what the solve fades towards,
            // so the grip never snaps the sleeve root when it engages.
            Quaternion retUpper = upper.rotation;
            Quaternion retMid = mid.rotation;
            Quaternion retEnd = end.rotation;
            Quaternion retClav = root != null ? root.rotation : Quaternion.identity;
            Vector3 retRootPos = root != null ? root.position : Vector3.zero;
            Vector3 retUpperPos = upper.position;

            float l1 = Vector3.Distance(upper.position, mid.position);
            float l2 = Vector3.Distance(mid.position, end.position);
            float reach = l1 + l2;
            if (l1 < 1e-4f || l2 < 1e-4f)
            {
                return;
            }

            // Position mode keeps the retargeted hand rotation and only moves
            // the wrist so the palm point meets the game's. This is the mode
            // that cannot twist the sleeve: the hand never rotates relative to
            // the animation, and the arm swing is a minimal arc. Grip mode
            // additionally forces the palm frame onto the game's, which costs
            // a large wrist roll on rigs whose bind palms differ.
            bool orient = _holdIkMode != null
                && string.Equals(_holdIkMode.Value, "Grip",
                    StringComparison.OrdinalIgnoreCase);
            Quaternion handWorld;
            if (orient)
            {
                Quaternion basisA = Quaternion.LookRotation(
                    Vector3.Cross(gx, gy), gy);
                // The other orientation that satisfies the same handle is a
                // half turn about the palm-forward axis: axis and normal both
                // flipped. Pick whichever is closer to the retargeted hand so
                // a sign ambiguity can never spin the wrist half a turn.
                Quaternion basisB = Quaternion.LookRotation(
                    Vector3.Cross(gx, gy), -gy);
                Quaternion invModel = Quaternion.Inverse(modelBasis);
                Quaternion handA = basisA * invModel * retEnd;
                Quaternion handB = basisB * invModel * retEnd;
                handWorld = Quaternion.Angle(retEnd, handA)
                    <= Quaternion.Angle(retEnd, handB) ? handA : handB;

                float poseCap = _holdIkMaxPose != null ? _holdIkMaxPose.Value : 75f;
                float poseDev = Quaternion.Angle(retEnd, handWorld);
                if (poseCap > 0f && poseDev > poseCap && poseDev > 0.001f)
                {
                    handWorld = Quaternion.Slerp(handWorld, retEnd,
                        (poseDev - poseCap) / poseDev);
                }
            }
            else
            {
                handWorld = retEnd;
            }
            float handDev = Quaternion.Angle(retEnd, handWorld);
            float wristAnim = Quaternion.Angle(retMid, handWorld);

            // Wrist clamp before the target is built, against the retargeted
            // forearm. Clamping after the arm solve left the palm point on the
            // unclamped orientation, so a clamped hand pulled off the item.
            Quaternion wristRest = rest.HasWrist
                ? rest.WristLocal : Quaternion.identity;
            float wristMax = _holdIkMaxWrist != null ? _holdIkMaxWrist.Value : 45f;
            float wristDev = Quaternion.Angle(wristRest,
                Quaternion.Inverse(retMid) * handWorld);
            float wristClamp = 0f;
            if (orient && wristMax > 0f && wristDev > wristMax
                && wristDev > 0.001f)
            {
                wristClamp = wristDev - wristMax;
                Quaternion natural = retMid * wristRest;
                handWorld = Quaternion.Slerp(handWorld, natural,
                    wristClamp / wristDev);
            }

            // Palm point in the world for the final hand rotation, then the
            // wrist that puts it there.
            Vector3 t = gamePoint - handWorld * gripLocal;

            Vector3 a = upper.position;
            Vector3 b = mid.position;
            Vector3 want = t - a;
            if (want.sqrMagnitude < 1e-8f)
            {
                return;
            }
            float dist = want.magnitude;
            Vector3 tDir = want / dist;
            float reach2 = reach - 0.002f;

            Vector3 pole = GripPole(side, rest, a, b, tDir, handWorld, t, l2);

            // Clavicle share of the swing the upper arm would need.
            float dd0 = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 0.002f, reach2);
            float ang0 = Mathf.Acos(Mathf.Clamp(
                (l1 * l1 + dd0 * dd0 - l2 * l2) / (2f * l1 * dd0), -1f, 1f));
            Vector3 elbowNeed = a + (tDir * Mathf.Cos(ang0) + pole * Mathf.Sin(ang0)) * l1;
            Quaternion swingNeed = Quaternion.FromToRotation(b - a, elbowNeed - a);
            float needAngle = Quaternion.Angle(Quaternion.identity, swingNeed);
            float clavAngle = 0f;
            if (root != null && needAngle > 0.01f)
            {
                clavAngle = Mathf.Min(needAngle * 0.35f, 22f);
                root.rotation = Quaternion.Slerp(Quaternion.identity, swingNeed,
                    clavAngle / needAngle) * root.rotation;
                a = upper.position;
                b = mid.position;
                want = t - a;
                dist = want.magnitude;
                if (dist < 1e-5f)
                {
                    return;
                }
                tDir = want / dist;
                pole = GripPole(side, rest, a, b, tDir, handWorld, t, l2);
                pole = StablePole(side, a, a + pole * 0.1f, tDir);
            }

            // Reach: slide the arm root along the target line, capped to a
            // fraction of the model's own arm. Never stretch a bone.
            float slide = 0f;
            if (dist > reach2)
            {
                slide = Mathf.Min(dist - reach2, 0.2f * reach);
                if (slide > 0.0005f)
                {
                    (root != null ? root : upper).position += tDir * slide;
                    a = upper.position;
                    b = mid.position;
                    want = t - a;
                    dist = want.magnitude;
                    if (dist < 1e-5f)
                    {
                        return;
                    }
                    tDir = want / dist;
                    pole = GripPole(side, rest, a, b, tDir, handWorld, t, l2);
                    pole = StablePole(side, a, a + pole * 0.1f, tDir);
                }
            }

            float dd = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 0.002f, reach2);
            float ang = Mathf.Acos(Mathf.Clamp(
                (l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd), -1f, 1f));
            Vector3 newB = a + (tDir * Mathf.Cos(ang) + pole * Mathf.Sin(ang)) * l1;

            Vector3 upperNow = mid.position - a;
            if (upperNow.sqrMagnitude > 1e-8f && (newB - a).sqrMagnitude > 1e-8f)
            {
                upper.rotation = Quaternion.FromToRotation(upperNow, newB - a)
                    * upper.rotation;
            }

            // Forearm: swing the retargeted bone just enough to point at the
            // wrist target, keeping its authored roll. Rebuilding the roll
            // from the hand instead (the old chain) is what wound the sleeve
            // up when the bind palms differed.
            Vector3 b2 = mid.position;
            Vector3 aim = t - b2;
            Vector3 c2 = end.position;
            if (aim.sqrMagnitude > 1e-8f && (c2 - b2).sqrMagnitude > 1e-8f)
            {
                // c2-b2 already includes the upper-arm swing, so the delta must
                // be applied to the current forearm rotation, not retMid: with
                // retMid the delta was computed from a different starting
                // direction and the hand stopped short of the target.
                mid.rotation = Quaternion.FromToRotation(c2 - b2, aim) * mid.rotation;
            }

            end.rotation = handWorld;

            if (w < 0.9999f)
            {
                if (root != null)
                {
                    root.position = Vector3.Lerp(retRootPos, root.position, w);
                    root.rotation = Quaternion.Slerp(retClav, root.rotation, w);
                }
                else
                {
                    // No clavicle: the reach slide went onto the upper arm.
                    upper.position = Vector3.Lerp(retUpperPos, upper.position, w);
                }
                upper.rotation = Quaternion.Slerp(retUpper, upper.rotation, w);
                mid.rotation = Quaternion.Slerp(retMid, mid.rotation, w);
                end.rotation = Quaternion.Slerp(retEnd, end.rotation, w);
            }

            // No twist redistribution: both the upper arm and the forearm
            // keep their authored rolls, and the arm solve only adds minimal
            // arcs, so the twist chain stays at its rest (reset) value. That
            // is what keeps the sleeve from winding up as the aim changes.

            if (_holdIkLog == null || !_holdIkLog.Value || w < 0.15f)
            {
                return;
            }
            float nextLog = si == 0 ? _nextGripLogL : _nextGripLogR;
            if (Time.unscaledTime < nextLog)
            {
                return;
            }
            if (si == 0)
            {
                _nextGripLogL = Time.unscaledTime + 1f;
            }
            else
            {
                _nextGripLogR = Time.unscaledTime + 1f;
            }

            Vector3 modelPoint = end.position + end.rotation * gripLocal;
            Quaternion frameRot = end.rotation * Quaternion.Inverse(retEnd);
            Vector3 modelAxis = frameRot * lx;
            Vector3 modelNormal = frameRot * ly;
            float gripErr = Vector3.Distance(modelPoint, gamePoint);
            float posErr = Vector3.Distance(end.position, t);
            float axisErr = Vector3.Angle(modelAxis, gx);
            float palmErr = Vector3.Angle(modelNormal, gy);
            float elbowDeg = Vector3.Angle(a - mid.position, t - mid.position);
            float finalWrist = Quaternion.Angle(wristRest,
                Quaternion.Inverse(mid.rotation) * end.rotation);
            float finalPose = Mathf.Max(Quaternion.Angle(retUpper, upper.rotation),
                Mathf.Max(Quaternion.Angle(retMid, mid.rotation),
                    Quaternion.Angle(retEnd, end.rotation)));
            float gameWrist = -1f;
            Transform srcElbow = SourceTransform("elbow_" + side);
            Transform srcHand = SourceTransform("hand_" + side);
            if (rest.HasGameWrist && srcElbow != null && srcHand != null)
            {
                gameWrist = Quaternion.Angle(rest.GameWristRestLocal,
                    Quaternion.Inverse(srcElbow.rotation) * srcHand.rotation);
            }
            Logger.LogInfo(string.Format(
                "GripIK {0}: {1} mode={2} frame={3} w={4:F2} "
                + "dist={5:F3}/{6:F3} slide={7:F3} clav={8:F0} "
                + "elbow={9:F0} wrist={10:F0} pose={11:F0} "
                + "handDev={12:F0} wristWant={13:F0} gameWrist={14:F0} "
                + "gripErr={15:F3} wristErr={16:F3} axisErr={17:F0} "
                + "palmErr={18:F0} wristClamp={19:F0}",
                side, src, orient ? "grip" : "pos", frameSrc, w, dist,
                reach, slide, clavAngle, elbowDeg, finalWrist, finalPose,
                handDev, wristAnim, gameWrist, gripErr, posErr, axisErr,
                palmErr, wristClamp));
            if (info.Length > 0)
            {
                Logger.LogInfo("GripIK target " + side + ": " + info);
            }
        }

        /// <summary>
        /// Signed angle, in degrees, of the twist component of
        /// <paramref name="q"/> about <paramref name="axis"/> (swing-twist
        /// decomposition). Both are expressed in the same frame. The quaternion
        /// is read as the shortest arc, so a negative w is flipped first.
        /// </summary>
        private static float SignedTwist(Quaternion q, Vector3 axis)
        {
            if (axis.sqrMagnitude < 1e-10f)
            {
                return 0f;
            }
            axis.Normalize();
            if (q.w < 0f)
            {
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            }
            float along = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            float angle = 2f * Mathf.Atan2(Mathf.Abs(along), q.w) * Mathf.Rad2Deg;
            return along < 0f ? -angle : angle;
        }

        /// <summary>
        /// The palm frame of a hand, built from its own bones: z along the
        /// wrist-to-middle-finger direction, x towards the thumb, y the palm
        /// normal. Both the suit and the model are built the same way, so the
        /// rotation that carries one frame onto the other is a genuine palm-to-
        /// palm calibration, independent of how each rig's hand bone is bound.
        /// </summary>
        private static bool PalmFrame(Transform hand, Transform knuckle, Transform thumb,
            out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (hand == null || knuckle == null || thumb == null)
            {
                return false;
            }

            Vector3 forward = knuckle.position - hand.position;
            Vector3 side = thumb.position - hand.position;
            if (forward.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            forward.Normalize();
            side -= forward * Vector3.Dot(side, forward);
            if (side.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            side.Normalize();
            frame = Quaternion.LookRotation(forward, Vector3.Cross(forward, side));
            return true;
        }

        /// <summary>
        /// Picks the elbow bend direction, perpendicular to the target direction.
        /// The raw choice is the retargeted elbow, but that vector passes through zero
        /// (and its direction flips) whenever the arm straightens towards the target,
        /// which made the elbow - and with it the forearm and hand - snap to the other
        /// side mid-stroke. Keep the previous side through that dead zone and ease
        /// towards the new one, so the bend direction always changes continuously.
        /// </summary>
        private Vector3 StablePole(string side, Vector3 a, Vector3 elbow, Vector3 tDir)
        {
            bool left = side == "L";
            // Stored in the model container's space so it turns with the player.
            Vector3 prev = left ? _poleL : _poleR;
            if (_container != null)
            {
                prev = _container.TransformDirection(prev);
            }
            bool hasPrev = left ? _hasPoleL : _hasPoleR;

            Vector3 raw = elbow - a;
            raw -= Vector3.Dot(raw, tDir) * tDir;
            bool valid = raw.sqrMagnitude > 2.5e-4f; // > ~0.016 m off the arm line
            Vector3 desired = valid ? raw.normalized : Vector3.zero;

            if (hasPrev)
            {
                // Project last frame's pole on the current plane and never let it flip
                // in a single frame.
                Vector3 old = prev - Vector3.Dot(prev, tDir) * tDir;
                if (old.sqrMagnitude > 1e-8f)
                {
                    old.Normalize();
                    if (!valid)
                    {
                        // The arm is nearly straight, so the perpendicular offset has no
                        // usable direction: hold the previous side through the dead zone.
                        desired = old;
                    }
                    else
                    {
                        float dot = Vector3.Dot(old, desired);
                        if (dot >= 0f)
                        {
                            desired = Vector3.Slerp(old, desired, 0.35f);
                            desired.Normalize();
                        }
                        else
                        {
                            // The animation has swung the elbow to the other side of the
                            // arm line. A full swim stroke does this while the free hand
                            // paddles. Turn towards it at a limited rate instead of
                            // locking to the stale side, which used to leave the free
                            // arm bent backwards for the rest of the stroke.
                            float angle = Vector3.Angle(old, desired);
                            float step = Mathf.Min(angle, 20f);
                            Vector3 axis = Vector3.Cross(old, desired);
                            if (axis.sqrMagnitude < 1e-8f)
                            {
                                axis = Vector3.Cross(old, tDir);
                            }
                            if (axis.sqrMagnitude < 1e-8f)
                            {
                                axis = Vector3.Cross(old, Vector3.up);
                            }
                            if (axis.sqrMagnitude < 1e-8f)
                            {
                                desired = old;
                            }
                            else
                            {
                                desired = Quaternion.AngleAxis(step, axis.normalized) * old;
                            }
                            if (_verbose.Value && Time.unscaledTime >= _nextPoleLog)
                            {
                                _nextPoleLog = Time.unscaledTime + 0.5f;
                                Logger.LogInfo(string.Format(
                                    "Pole {0}: crossing dot={1:F2} angle={2:F0} step={3:F1}",
                                    side, dot, angle, step));
                            }
                        }
                    }
                }
            }

            if (desired.sqrMagnitude < 1e-8f)
            {
                // No history and the elbow sits on the arm line: bend down, only
                // slightly outwards. A stronger outward bias is what made both
                // elbows splay when the aim had to fall back to this pole.
                Vector3 down = _container != null ? -_container.up : Vector3.down;
                Vector3 outDir = _container != null ? _container.right : Vector3.right;
                desired = down + outDir * (left ? -0.3f : 0.3f);
                desired -= Vector3.Dot(desired, tDir) * tDir;
                if (desired.sqrMagnitude < 1e-6f)
                {
                    desired = Vector3.Cross(tDir, Vector3.up);
                    desired -= Vector3.Dot(desired, tDir) * tDir;
                }
                if (desired.sqrMagnitude < 1e-6f)
                {
                    desired = Vector3.Cross(tDir, Vector3.forward);
                }
                desired.Normalize();
            }

            Vector3 worldPole = desired;
            if (_container != null)
            {
                desired = _container.InverseTransformDirection(desired);
            }
            if (left)
            {
                _poleL = desired;
                _hasPoleL = true;
            }
            else
            {
                _poleR = desired;
                _hasPoleR = true;
            }
            return worldPole;
        }

        private int ModelIndex(string name)
        {
            int index;
            if (_diagIndexByName != null && _diagIndexByName.TryGetValue(name, out index))
            {
                return index;
            }
            return -1;
        }

        /// <summary>
        /// Restores the arm root to its bind offset so a slide applied on an earlier
        /// frame does not accumulate (the retarget pass never writes local positions).
        /// The hand's bind offset is restored as well, which undoes the stretched
        /// position the old AlignHand fallback could leave behind.
        /// </summary>
        private void ResetArmRoot(string side)
        {
            // The reach slide lives on the clavicle (肩) so the shoulder joint
            // stays connected; put it back before every solve and whenever the
            // IK is off, or it would accumulate frame after frame.
            Transform clav = ModelTransform("clav_" + side);
            int clavIndex = ModelIndex("clav_" + side);
            if (clav != null && clavIndex >= 0 && clavIndex < _data.Bones.Count)
            {
                clav.localPosition = _data.Bones[clavIndex].LocalPos;
            }

            Transform upper = ModelTransform("shoulder_" + side);
            int index = ModelIndex("shoulder_" + side);
            if (upper != null && index >= 0 && index < _data.Bones.Count)
            {
                upper.localPosition = _data.Bones[index].LocalPos;
            }

            Transform hand = ModelTransform("hand_" + side);
            int handIndex = ModelIndex("hand_" + side);
            if (hand != null && handIndex >= 0 && handIndex < _data.Bones.Count)
            {
                hand.localPosition = _data.Bones[handIndex].LocalPos;
            }

            // The twist group is not written by the retarget (the suit has no
            // arm twist bone), so the extra rotation SolveArm put on it would
            // stick after the IK lets go. Put the group back to rest whenever
            // the arm is not being solved.
            ArmRest rest = _armRest[side == "L" ? 0 : 1];
            if (rest != null && rest.ArmTwist != null)
            {
                rest.ArmTwist.localRotation = Quaternion.identity;
                if (rest.ArmTwistHelpers != null)
                {
                    for (int i = 0; i < rest.ArmTwistHelpers.Length; i++)
                    {
                        if (rest.ArmTwistHelpers[i] != null)
                        {
                            rest.ArmTwistHelpers[i].localRotation = Quaternion.identity;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// True while a quick-slot tool is in the hand. The item is attached to the right
        /// hand, so only that arm should be pulled onto the game's hand; the other arm is
        /// left to the plain retarget so it keeps the vanilla animation exactly.
        /// </summary>
        private bool HoldingItem()
        {
            Inventory inv = Inventory.main;
            return inv != null && inv.quickSlots != null && inv.quickSlots.heldItem != null;
        }

        /// <summary>
        /// True when the held item is gripped with the left hand too, so the left arm has
        /// to be pulled onto the game's left hand as well. That covers the seaglide (both
        /// hands are on it even though its PlayerTool does not aim the left arm) and any
        /// tool that drives the game's own left-arm IK, i.e. the two-handed grips.
        /// </summary>
        private bool HeldItemUsesLeftHand()
        {
            Inventory inv = Inventory.main;
            if (inv == null || inv.quickSlots == null)
            {
                return false;
            }
            InventoryItem held = inv.quickSlots.heldItem;
            if (held == null || held.item == null)
            {
                return false;
            }
            // GetComponent lookups every frame showed up in the profiler while a
            // tool was held; the answer only changes when the held item does.
            if (!ReferenceEquals(held, _heldDecisionItem))
            {
                _heldDecisionItem = held;
                _heldDecisionLeftHand = ComputeHeldUsesLeftHand(held);
                _heldDecisionSeaglide = IsSeaglide(held);
            }
            return _heldDecisionLeftHand;
        }

        /// <summary>
        /// True only for the seaglide. Its aim pose already points the head at
        /// the camera, so the added head pitch has to stay off for it; every
        /// other held item follows the camera pitch like the empty hand does.
        /// </summary>
        private bool HoldingSeaglide()
        {
            Inventory inv = Inventory.main;
            if (inv == null || inv.quickSlots == null)
            {
                return false;
            }
            InventoryItem held = inv.quickSlots.heldItem;
            if (held == null || held.item == null)
            {
                return false;
            }
            if (!ReferenceEquals(held, _heldDecisionItem))
            {
                _heldDecisionItem = held;
                _heldDecisionLeftHand = ComputeHeldUsesLeftHand(held);
                _heldDecisionSeaglide = IsSeaglide(held);
            }
            return _heldDecisionSeaglide;
        }

        private static bool IsSeaglide(InventoryItem held)
        {
            return held != null && held.item != null
                && (held.item.GetComponent<Seaglide>() != null
                    || held.item.GetTechType() == TechType.Seaglide);
        }

        private static bool ComputeHeldUsesLeftHand(InventoryItem held)
        {
            if (held == null || held.item == null)
            {
                return false;
            }
            if (IsSeaglide(held))
            {
                return true;
            }
            PlayerTool tool = held.item.GetComponent<PlayerTool>();
            if (tool == null)
            {
                return false;
            }
            if (tool.ikAimLeftArm)
            {
                return true;
            }
            return tool.leftHandIKTarget != null && tool.leftHandIKTarget.gameObject.activeInHierarchy;
        }

        /// <summary>
        /// True while the PDA is open. Both hands work on it, so both arms are solved.
        /// </summary>
        private bool PdaInUse()
        {
            Player player = Player.main;
            if (player == null)
            {
                return false;
            }
            PDA pda = player.GetPDA();
            return pda != null && pda.isInUse;
        }

        private Transform ModelTransform(string name)
        {
            int index;
            if (_diagIndexByName != null && _diagIndexByName.TryGetValue(name, out index) &&
                _diagBones != null && _diagBones[index] != null)
            {
                return _diagBones[index];
            }
            return null;
        }

        /// <summary>
        /// How far the retargeted arm sits from the game's arm before the hand snap.
        /// Large values mean the snap has to stretch the forearm, which shows up as the
        /// sleeve and hand being deformed.
        /// </summary>
        private void LogArm(string side)
        {
            Transform srcHand = SourceTransform("hand_" + side);
            Transform mdlHand = ModelTransform("hand_" + side);
            if (srcHand == null || mdlHand == null)
            {
                return;
            }
            Transform srcElbow = SourceTransform("elbow_" + side);
            Transform mdlElbow = ModelTransform("elbow_" + side);
            float elbow = srcElbow != null && mdlElbow != null
                ? Vector3.Distance(srcElbow.position, mdlElbow.position)
                : -1f;
            string palm = "";
            Transform srcKnuckle = SourceTransform("hand_" + side + "_midl_base");
            Transform mdlKnuckle = ModelTransform("hand_" + side + "_midl_base");
            if (srcKnuckle != null && mdlKnuckle != null)
            {
                palm = " palmGame=" + Vector3.Distance(srcKnuckle.position, srcHand.position).ToString("F3")
                    + " palmModel=" + Vector3.Distance(mdlKnuckle.position, mdlHand.position).ToString("F3");
            }
            Logger.LogInfo("Arm" + side + ": handDelta="
                + Vector3.Distance(srcHand.position, mdlHand.position).ToString("F3")
                + " elbowDelta=" + elbow.ToString("F3")
                + " reach=" + Vector3.Distance(mdlElbow != null ? mdlElbow.position : mdlHand.position,
                    mdlHand.position).ToString("F3")
                + palm);
        }

        private void LogSourceWorld(SourceMap sources, string name)
        {
            SourceBone sb;
            if (sources != null && sources.Bones.TryGetValue(name, out sb))
            {
                Logger.LogInfo("Source bone " + name + " world=" + sb.Bone.position);
            }
        }

        /// <summary>
        /// Ratio that makes the model's head-to-toe bone chain as long as the
        /// vanilla suit's. The chain is the spine (head/neck/chest/spine/hips)
        /// plus one leg (thigh/calf/ankle/toe); every segment is a rigid bone
        /// distance, so the live game pose measures the suit's true rest
        /// dimensions. Returns 1 (no change) when any segment is missing on
        /// either side. Clamped so a malformed skeleton cannot blow the model
        /// up; the measured value is logged at build.
        /// </summary>
        private static float MeasureScaleFit(SourceMap sources, Transform[] bones,
            Dictionary<string, int> boneIndexByName)
        {
            string[,] chain =
            {
                { "head_rig", "neck" }, { "neck", "chest" },
                { "chest", "spine_1" }, { "spine_1", "hips" },
                { "hips", "thigh_L" }, { "thigh_L", "calf_L" },
                { "calf_L", "ankle_L" }, { "ankle_L", "toe_L_rig" },
            };
            float vanillaH = 0f;
            float modelH = 0f;
            for (int i = 0; i < chain.GetLength(0); i++)
            {
                SourceBone sp, sc;
                if (sources == null
                    || !sources.Bones.TryGetValue(chain[i, 0], out sp)
                    || !sources.Bones.TryGetValue(chain[i, 1], out sc)
                    || sp == null || sc == null
                    || sp.Bone == null || sc.Bone == null)
                {
                    return 1f;
                }
                vanillaH += Vector3.Distance(sp.Bone.position, sc.Bone.position);
                int mp, mc;
                if (!boneIndexByName.TryGetValue(chain[i, 0], out mp)
                    || !boneIndexByName.TryGetValue(chain[i, 1], out mc))
                {
                    return 1f;
                }
                modelH += Vector3.Distance(bones[mp].position, bones[mc].position);
            }
            if (modelH < 1e-4f)
            {
                return 1f;
            }
            return Mathf.Clamp(vanillaH / modelH, 0.85f, 1.5f);
        }

        /// <summary>
        /// Stretch the model's clavicle/upper-arm/forearm segments to the game
        /// lengths, at build time, without changing their bind directions. The
        /// body fit only matches the torso/legs; the arms of most MMD models
        /// are still several centimetres short, which is what left the held
        /// hand behind the tool (knife/builder clipping into the palm, the
        /// seaglide hands inside the grips, wrong stun/repulsion gun hands).
        /// Pure retarget with matched bone lengths puts the hand on the game
        /// hand with no per-frame IK, so it cannot lag on movement/pitch and
        /// cannot buzz.
        /// </summary>
        private void FitArmProportions(Transform[] bones,
            Dictionary<string, int> boneIndexByName, SourceMap sources)
        {
            if (sources == null)
            {
                return;
            }
            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "L" : "R";
                int ci, si, ei, hi;
                if (!boneIndexByName.TryGetValue("clav_" + side, out ci)
                    || !boneIndexByName.TryGetValue("shoulder_" + side, out si)
                    || !boneIndexByName.TryGetValue("elbow_" + side, out ei)
                    || !boneIndexByName.TryGetValue("hand_" + side, out hi))
                {
                    continue;
                }
                SourceBone sc, ss, se, sh;
                if (!sources.Bones.TryGetValue("clav_" + side, out sc)
                    || !sources.Bones.TryGetValue("shoulder_" + side, out ss)
                    || !sources.Bones.TryGetValue("elbow_" + side, out se)
                    || !sources.Bones.TryGetValue("hand_" + side, out sh)
                    || sc.Bone == null || ss.Bone == null
                    || se.Bone == null || sh.Bone == null)
                {
                    continue;
                }
                Transform mClav = bones[ci];
                Transform mShoulder = bones[si];
                Transform mElbow = bones[ei];
                Transform mHand = bones[hi];
                if (mClav == null || mShoulder == null
                    || mElbow == null || mHand == null)
                {
                    continue;
                }
                float gClav = Vector3.Distance(sc.Bone.position, ss.Bone.position);
                float gUpper = Vector3.Distance(ss.Bone.position, se.Bone.position);
                float gFore = Vector3.Distance(se.Bone.position, sh.Bone.position);
                float mClavLen = Vector3.Distance(mClav.position, mShoulder.position);
                float mUpperLen = Vector3.Distance(mShoulder.position, mElbow.position);
                float mForeLen = Vector3.Distance(mElbow.position, mHand.position);
                if (mClavLen < 1e-4f || mUpperLen < 1e-4f || mForeLen < 1e-4f
                    || gClav < 1e-4f || gUpper < 1e-4f || gFore < 1e-4f)
                {
                    continue;
                }
                float kc = Mathf.Clamp(gClav / mClavLen, 0.85f, 1.35f);
                float ku = Mathf.Clamp(gUpper / mUpperLen, 0.85f, 1.35f);
                float kf = Mathf.Clamp(gFore / mForeLen, 0.85f, 1.35f);
                Vector3 dirClav = (mShoulder.position - mClav.position).normalized;
                Vector3 dirUpper = (mElbow.position - mShoulder.position).normalized;
                Vector3 dirFore = (mHand.position - mElbow.position).normalized;
                // Move the joints to the wanted distances along their current
                // directions. Children follow their parent, so do this root to
                // tip; the hand rotation itself is untouched.
                mShoulder.position = mClav.position + dirClav * (mClavLen * kc);
                mElbow.position = mShoulder.position + dirUpper * (mUpperLen * ku);
                mHand.position = mElbow.position + dirFore * (mForeLen * kf);
                Logger.LogInfo(string.Format(
                    "Arm fit {0}: clav {1:F3}->{2:F3} (x{3:F2}), "
                    + "upper {4:F3}->{5:F3} (x{6:F2}), fore {7:F3}->{8:F3} (x{9:F2})",
                    side, mClavLen, mClavLen * kc, kc,
                    mUpperLen, mUpperLen * ku, ku,
                    mForeLen, mForeLen * kf, kf));
            }
        }

        /// <summary>
        /// Rate-limited trace of the arm pitch chain while a tool is held:
        /// the player_view and container world pitch, the game and model arm
        /// chain pitch (shoulder to hand), and the aim bone's lift (the angle
        /// between the clavicle and the upper arm on each side). Shows where
        /// the camera pitch stops on its way from the game's arm to the grip.
        /// </summary>
        private void LogPitchDiag(string side)
        {
            Transform srcShoulder = SourceTransform("shoulder_" + side);
            Transform srcHand = SourceTransform("hand_" + side);
            Transform srcClav = SourceTransform("clav_" + side);
            Transform srcAim = SourceTransform("clav_" + side + "_aim");
            Transform mdlShoulder = ModelTransform("shoulder_" + side);
            Transform mdlHand = ModelTransform("hand_" + side);
            Transform mdlClav = ModelTransform("clav_" + side);
            Transform mdlClavC = ModelTransform("clav_C_" + side);
            float srcArmPitch = 0f;
            if (srcShoulder != null && srcHand != null)
            {
                Vector3 d = srcHand.position - srcShoulder.position;
                if (d.sqrMagnitude > 1e-8f)
                {
                    d.Normalize();
                    srcArmPitch = Mathf.Asin(d.y) * Mathf.Rad2Deg;
                }
            }
            float mdlArmPitch = 0f;
            if (mdlShoulder != null && mdlHand != null)
            {
                Vector3 d = mdlHand.position - mdlShoulder.position;
                if (d.sqrMagnitude > 1e-8f)
                {
                    d.Normalize();
                    mdlArmPitch = Mathf.Asin(d.y) * Mathf.Rad2Deg;
                }
            }
            float srcAimLift = 0f;
            if (srcClav != null && srcAim != null && srcShoulder != null)
            {
                Vector3 c = srcAim.position - srcClav.position;
                Vector3 u = srcShoulder.position - srcAim.position;
                if (c.sqrMagnitude > 1e-8f && u.sqrMagnitude > 1e-8f)
                {
                    srcAimLift = Vector3.Angle(c, u);
                }
            }
            float mdlAimLift = 0f;
            if (mdlClav != null && mdlClavC != null && mdlShoulder != null)
            {
                Vector3 c = mdlClavC.position - mdlClav.position;
                Vector3 u = mdlShoulder.position - mdlClavC.position;
                if (c.sqrMagnitude > 1e-8f && u.sqrMagnitude > 1e-8f)
                {
                    mdlAimLift = Vector3.Angle(c, u);
                }
            }
            float pvPitch = 0f;
            float containerPitch = 0f;
            if (_container != null)
            {
                containerPitch = NormEuler(_container.rotation.eulerAngles.x);
                if (_container.parent != null)
                {
                    pvPitch = NormEuler(_container.parent.rotation.eulerAngles.x);
                }
            }
            // The vanilla first-person arm (ArmsController) is the thing that
            // actually pitches with the camera and carries the tool socket. Its
            // shoulder is not exposed, so report its hand height relative to the
            // camera and how far the model hand is from it: the aim should hold
            // that distance small and near-constant while the view pitches.
            Transform vanilla = VanillaHand(side);
            float vPitch = 0f;
            float mGap = -1f;
            if (vanilla != null)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 d = vanilla.position - cam.transform.position;
                    if (d.sqrMagnitude > 1e-8f)
                    {
                        vPitch = Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f))
                            * Mathf.Rad2Deg;
                    }
                }
                if (mdlHand != null)
                {
                    mGap = Vector3.Distance(mdlHand.position, vanilla.position);
                }
            }
            ArmRest rest = _armRest[side == "L" ? 0 : 1];
            Logger.LogInfo("Pitch " + side + ": pv=" + pvPitch.ToString("F1")
                + " cont=" + containerPitch.ToString("F1")
                + " srcArm=" + srcArmPitch.ToString("F1")
                + " mdlArm=" + mdlArmPitch.ToString("F1")
                + " srcAim=" + srcAimLift.ToString("F1")
                + " mdlAim=" + mdlAimLift.ToString("F1")
                + " vanillaHand=" + (vanilla != null ? "yes" : "no")
                + " vPitch=" + vPitch.ToString("F1")
                + " handGap=" + mGap.ToString("F3")
                + " aimW=" + (side == "L" ? _aimWeightL : _aimWeightR).ToString("F2")
                + " hasClav=" + (rest != null && rest.HasClav)
                + " hasClavC=" + (rest != null && rest.HasClavC));
        }

        private static float NormEuler(float angle)
        {
            return angle > 180f ? angle - 360f : angle;
        }

        private static readonly string[,] BaseMap =
        {
            { "hips", "hips" },
            { "spine_1", "spine_1" },
            { "chest", "chest" },
            { "neck", "neck" },
            { "head_rig", "head_rig" },

            { "clav_L", "clav_L" },
            { "clav_C_L", "clav_L_aim" },
            { "shoulder_L", "shoulder_L" },
            { "arm_twist_L", "shoulder_L_twist1_rig" },
            { "elbow_L", "elbow_L" },
            { "hand_L", "hand_L" },

            { "clav_R", "clav_R" },
            { "clav_C_R", "clav_R_aim" },
            { "shoulder_R", "shoulder_R" },
            { "arm_twist_R", "shoulder_R_twist1_rig" },
            { "elbow_R", "elbow_R" },
            { "hand_R", "hand_R" },

            { "thigh_L", "thigh_L" },
            { "calf_L", "calf_L" },
            { "ankle_L", "ankle_L" },
            { "toe_L_rig", "toe_L_rig" },

            { "thigh_R", "thigh_R" },
            { "calf_R", "calf_R" },
            { "ankle_R", "ankle_R" },
            { "toe_R", "toe_R" },
        };

        private static readonly string[] FingerNames =
        {
            "thumb", "point", "midl", "ring", "pinky"
        };

        private void AddPairs(List<BonePair> list, SourceMap sources, Transform[] bones,
            int[] depths, Dictionary<string, int> boneIndexByName, Quaternion[] targetRest)
        {
            for (int i = 0; i < BaseMap.GetLength(0); i++)
            {
                string target = BaseMap[i, 0];
                string source = BaseMap[i, 1];
                // MMD twist bones (腕捩) are deformation helpers: MMD motions rotate
                // the arm and the twist bone follows it, they do not animate it on its
                // own. The game's twist1_rig only carries part of the shoulder rotation,
                // so driving the imported twist bone from it shears the upper arm
                // between the vertices weighted to the twist bone and those weighted to
                // the upper arm. Let the imported twist bone follow the upper arm.
                if (_imported && target.StartsWith("arm_twist_", StringComparison.Ordinal))
                {
                    source = "shoulder_" + target.Substring("arm_twist_".Length);
                }
                AddPair(list, sources, bones, depths, boneIndexByName, targetRest,
                    target, source);
            }

            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "L" : "R";
                string tipSuffix = s == 0 ? "" : "_rig";
                for (int f = 0; f < FingerNames.Length; f++)
                {
                    string finger = FingerNames[f];
                    // Both the model and the game suit skeleton use the same naming
                    // scheme, e.g. "hand_L_thumb_base" / "hand_R_thumb_tip_rig".
                    string prefix = "hand_" + side + "_" + finger + "_";
                    AddPair(list, sources, bones, depths, boneIndexByName, targetRest,
                        prefix + "base", prefix + "base");
                    AddPair(list, sources, bones, depths, boneIndexByName, targetRest,
                        prefix + "mid", prefix + "mid");
                    AddPair(list, sources, bones, depths, boneIndexByName, targetRest,
                        prefix + "tip" + tipSuffix, prefix + "tip" + tipSuffix);
                }
            }
        }

        private void AddPair(List<BonePair> list, SourceMap sources, Transform[] bones,
            int[] depths, Dictionary<string, int> boneIndexByName, Quaternion[] targetRest,
            string targetName, string sourceName)
        {
            int targetIndex;
            if (!boneIndexByName.TryGetValue(targetName, out targetIndex))
            {
                if (_verbose.Value)
                {
                    Logger.LogWarning("Target bone missing: " + targetName);
                }
                return;
            }

            SourceBone sb;
            if (!sources.Bones.TryGetValue(sourceName, out sb))
            {
                if (_verbose.Value)
                {
                    Logger.LogWarning("Source bone missing: " + sourceName + " (for " + targetName + ")");
                }
                return;
            }

            BonePair p = new BonePair();
            p.Source = sb.Bone;
            p.SmrTransform = sb.SmrTransform;
            p.SourceRestLocalInverse = Quaternion.Inverse(sb.RestLocalRot);
            p.Target = bones[targetIndex];
            p.TargetRestRot = targetRest[targetIndex];
            p.Depth = depths[targetIndex];
            list.Add(p);
        }

        // ------------------------------------------------------------------
        // per-frame retarget
        // ------------------------------------------------------------------

        public void FrameLate()
        {
            if (_data == null || _pairs == null || _container == null)
            {
                return;
            }

            try
            {
                // How far the game's hand bone moved after Player.LateUpdate? If FinalIK
                // really solves the arms later in the frame, this is the error that made
                // held items drift away from the model's hand while pitching.
                if (_verbose.Value)
                {
                    Transform probeHand = SourceTransform("hand_R");
                    if (probeHand != null)
                    {
                        if (!_latePass)
                        {
                            _handAtPlayerLate = probeHand.position;
                            _handAtPlayerLateFrame = Time.frameCount;
                        }
                        else if (_handAtPlayerLateFrame == Time.frameCount &&
                                 Time.unscaledTime >= _nextLagLog)
                        {
                            float lag = Vector3.Distance(_handAtPlayerLate, probeHand.position);
                            if (lag > 0.001f)
                            {
                                _nextLagLog = Time.unscaledTime + 1f;
                                Logger.LogInfo("Late: game hand moved " + lag.ToString("F4")
                                    + " m after Player.LateUpdate");
                            }
                        }
                    }
                }

                if (!_loggedView)
                {
                    _loggedView = true;
                    Camera c = Camera.main;
                    Logger.LogInfo("View: camera=" + (c != null ? c.transform.position.ToString() : "none")
                        + " head=" + (_headBone != null ? _headBone.position.ToString() : "none")
                        + " container=" + _container.position);
                }

                if (_verbose.Value && _poseSamples < 6 && Time.unscaledTime >= _nextPoseCheck)
                {
                    _nextPoseCheck = Time.unscaledTime + 1f;
                    _poseSamples++;
                    Logger.LogInfo("Pose: srcHead=" + SourcePos("head_rig") + " mdlHead=" + ModelPos("head_rig")
                        + " srcHandL=" + SourcePos("hand_L") + " mdlHandL=" + ModelPos("hand_L")
                        + " srcHandR=" + SourcePos("hand_R") + " mdlHandR=" + ModelPos("hand_R"));
                }

                if (_verbose.Value && _rigSamples < 40 && Time.unscaledTime >= _nextRigLog)
                {
                    _nextRigLog = Time.unscaledTime + 0.5f;
                    _rigSamples++;
                    Transform srcCam = null;
                    Transform srcChest = null;
                    Transform srcHandL = null;
                    Transform srcHips = null;
                    if (_diagSources != null)
                    {
                        SourceBone sb;
                        if (_diagSources.Bones.TryGetValue("Cam", out sb)) srcCam = sb.Bone;
                        if (_diagSources.Bones.TryGetValue("chest", out sb)) srcChest = sb.Bone;
                        if (_diagSources.Bones.TryGetValue("hand_L", out sb)) srcHandL = sb.Bone;
                        if (_diagSources.Bones.TryGetValue("hips", out sb)) srcHips = sb.Bone;
                    }
                    Transform mdlHandL = null;
                    int mi;
                    if (_diagIndexByName != null && _diagIndexByName.TryGetValue("hand_L", out mi))
                    {
                        mdlHandL = _diagBones[mi];
                    }
                    Camera cam = Camera.main;
                    string animState = "n/a";
                    if (_animator != null && _animator.isActiveAndEnabled)
                    {
                        AnimatorClipInfo[] clips = _animator.GetCurrentAnimatorClipInfo(0);
                        AnimatorStateInfo si = _animator.GetCurrentAnimatorStateInfo(0);
                        animState = (clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "none")
                                    + "@" + si.normalizedTime.ToString("0.00");
                    }
                    Logger.LogInfo("Rig: pvRot=" + Rot(_playerView)
                        + " pvPos=" + (_playerView != null ? _playerView.position.ToString() : "none")
                        + " camRot=" + Rot(cam != null ? cam.transform : null)
                        + " anim=" + animState
                        + " lHead=" + LocalRot(_playerView, _headSourceBone)
                        + " lChest=" + LocalRot(_playerView, srcChest)
                        + " lHandL=" + LocalRot(_playerView, srcHandL)
                        + " lHandR=" + LocalRot(_playerView, _diagSources != null && _diagSources.Bones.ContainsKey("hand_R") ? _diagSources.Bones["hand_R"].Bone : null)
                        + " mdlHandL=" + Rot(mdlHandL) + " mdlHandR=" + ModelRot("hand_R")
                        + " bodyB=" + BoundsText(_bodyRenderer) + " headB=" + BoundsText(_headRenderer)
                        + " headShadow=" + (_headRenderer != null
                            ? _headRenderer.shadowCastingMode.ToString() : "none"));
                }

                if (_hideGameBody.Value)
                {
                    if (_maleGeo != null && Time.unscaledTime >= _nextBodyCheck)
                    {
                        _nextBodyCheck = Time.unscaledTime + 0.25f;
                        _maleGeo.GetComponentsInChildren(true, _gameBodyRenderers);
                    }

                    for (int i = 0; i < _gameBodyRenderers.Count; i++)
                    {
                        Renderer r = _gameBodyRenderers[i];
                        if (r != null && r.enabled)
                        {
                            r.enabled = false;
                        }
                    }
                }

                if (_verbose.Value && !_renderersLogged)
                {
                    _renderersLogged = true;
                    Renderer[] all = _playerView.GetComponentsInChildren<Renderer>(true);
                    Logger.LogInfo("Renderers under player_view: " + all.Length);
                    for (int i = 0; i < all.Length; i++)
                    {
                        Renderer r = all[i];
                        Logger.LogInfo("Rend: " + GetPath(r.transform, _playerView)
                            + " type=" + r.GetType().Name
                            + " enabled=" + r.enabled
                            + " active=" + r.gameObject.activeInHierarchy
                            + " layer=" + r.gameObject.layer);
                    }
                }

                // Held-tool diagnostics: where the game attaches the tool and what the
                // first-person arms rig looks like. The tool follows the arms rig (which
                // reacts to view_pitch), while the body skeleton we retarget from does not.
                if (_verbose.Value && _toolLogSamples < 40 && Time.unscaledTime >= _nextToolLog)
                {
                    _nextToolLog = Time.unscaledTime + 0.5f;
                    try
                    {
                        Player player = Player.main;
                        Inventory inv = Inventory.main;
                        QuickSlots slots = inv != null ? inv.quickSlots : null;
                        InventoryItem held = slots != null ? slots.heldItem : null;
                        if (held != null && held.item != null)
                        {
                            _toolLogSamples++;
                            Transform ht = held.item.transform;
                            PlayerTool pt = held.item.GetComponent<PlayerTool>();
                            ArmsController arms = player != null ? player.armsController : null;
                            Logger.LogInfo("Tool: item=" + GetPath(ht, null)
                                + " parent=" + (ht.parent != null ? ht.parent.name : "none")
                                + " pos=" + ht.position
                                + " socket=" + (pt != null ? pt.socket.ToString() : "n/a")
                                + " ikR=" + (pt != null && pt.ikAimRightArm)
                                + " ikL=" + (pt != null && pt.ikAimLeftArm)
                                + " toolSocket=" + (inv.toolSocket != null ? GetPath(inv.toolSocket, null) : "none")
                                + " camSocket=" + (inv.cameraSocket != null ? GetPath(inv.cameraSocket, null) : "none")
                                + " arms=" + (arms != null ? GetPath(arms.transform, null) : "none")
                                + " rHand=" + (arms != null && arms.rightHand != null ? arms.rightHand.position.ToString() : "none")
                                + " rAttach=" + (arms != null && arms.rightHandAttach != null ? arms.rightHandAttach.position.ToString() : "none")
                                + " lHand=" + (arms != null && arms.leftHand != null ? arms.leftHand.position.ToString() : "none")
                                + " srcHandR=" + SourcePos("hand_R") + " mdlHandR=" + ModelPos("hand_R"));
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning("Tool diag failed: " + e.Message);
                        _toolLogSamples = 40;
                    }
                }

                Transform pv = _container.parent;
                if (pv == null)
                {
                    return;
                }

                // 1. rotation deltas of the source bones, expressed in their own
                // skinned-mesh space instead of world space. A world-space delta is
                // invariant to the body's global orientation, which made the model
                // ignore the view; mesh-space deltas keep the yaw, and the container
                // (parented to player_view) adds it back below.
                for (int i = 0; i < _pairs.Length; i++)
                {
                    BonePair p = _pairs[i];
                    Quaternion smrInv = Quaternion.Inverse(p.SmrTransform.rotation);
                    _deltas[i] = smrInv * p.Source.rotation * p.SourceRestLocalInverse;
                }

                // Abrupt-switch detector. The held-item and cinematic triggers in
                // UpdatePoseState cover the known cases; this catches everything
                // else by watching the game's own pose. It compares the mesh-space
                // deltas, not the raw world rotations: a quick mouse flick turns
                // the whole rig and would trip a world-space test even though the
                // pose relative to the body did not change.
                //
                // It votes instead of firing on the first bone. One quick bone is
                // an animation frame (toes, fingers and shoulders swing far in a
                // stroke); a switch moves several proximal bones at once. Distal
                // bones never vote, and an already running crossfade or its
                // one-second lockout is never restarted: both were what kept the
                // model re-blending and made some actions play in slow motion.
                // Sample once per frame, in the first pass only.
                if (!_latePass)
                {
                    if (_prevPoseRot != null && _prevPoseRot.Length == _pairs.Length)
                    {
                        if (_prevSourceValid && _poseTransition <= 0f && _poseJumpLock <= 0f)
                        {
                            int votes = 0;
                            float worst = 0f;
                            string worstName = null;
                            for (int i = 0; i < _pairs.Length; i++)
                            {
                                float jumped = Quaternion.Angle(_prevPoseRot[i], _deltas[i]);
                                if (jumped <= PoseJumpAngle)
                                {
                                    continue;
                                }
                                string boneName = _pairs[i].Source.name;
                                if (IsDistalSourceBone(boneName))
                                {
                                    continue;
                                }
                                votes++;
                                if (jumped > worst)
                                {
                                    worst = jumped;
                                    worstName = boneName;
                                }
                            }
                            if (votes >= PoseJumpVotes)
                            {
                                // A jump noticed while a tool is gripped must
                                // keep the grip hand on the live vanilla
                                // animation, like the equip/holster triggers.
                                StartPoseTransition(0, GripSideNow());
                                _poseJumpLock = 1f;
                                Logger.LogInfo("Pose jump: " + worstName + " "
                                    + worst.ToString("F0") + " deg, " + votes
                                    + " bones over " + PoseJumpAngle.ToString("F0"));
                            }
                        }
                        for (int i = 0; i < _pairs.Length; i++)
                        {
                            _prevPoseRot[i] = _deltas[i];
                        }
                        _prevSourceValid = true;
                    }
                }

                // Shoulder view: the model keeps its own heading while the camera
                // orbits, so the container's world rotation is written here instead
                // of simply following player_view. Outside that view this restores
                // the authored local yaw, which is what the model used before.
                ApplyModelFacing(pv);

                // 2. anchor the whole model to the game's head bone. The game's
                // first-person body root is the head, so this keeps the eye exactly
                // inside the model's head regardless of pose/teleports; the body
                // hangs below through the retargeted spine.
                _container.localPosition = _baseLocalPos;

                // 3. apply parent-first, absolute world rotations. While a pose
                // crossfade is running, blend from the pose captured when the
                // switch happened to the live retargeted pose, with an ease-in-out
                // curve. Blending from a fixed captured pose (instead of chasing
                // the target from the previous frame) keeps the motion symmetric:
                // it starts gently, peaks in the middle and settles at the end,
                // instead of covering most of the distance in the last few frames
                // and reading as a whip. Both late passes use the same captured
                // pose, so the double update per frame cannot accelerate it.
                float poseK = 1f;
                bool poseBlend = _poseTransition > 0f;
                if (poseBlend)
                {
                    float t = Mathf.Clamp01(1f - _poseTransition
                        / Mathf.Max(0.01f, _poseTransitionLength));
                    poseK = t * t * (3f - 2f * t);
                    if (!_poseFromValid)
                    {
                        // First frame of the crossfade: remember what the model was
                        // showing last frame. Take the base pose, i.e. what the
                        // retarget wrote before ApplyHeadLook premultiplied the
                        // camera look onto the neck and head: that look is added
                        // again on top of the blended pose every frame, so
                        // capturing the post-look rotations would apply it twice
                        // during the crossfade and made the head tilt extra (a
                        // visible nod/lift every time an item was taken out or
                        // holstered). The IK is absolute and re-solved after the
                        // blend, so the pre-IK base is the right starting point
                        // for the arms as well.
                        if (_poseFromRot == null || _poseFromRot.Length != _pairs.Length)
                        {
                            _poseFromRot = new Quaternion[_pairs.Length];
                        }
                        for (int i = 0; i < _pairs.Length; i++)
                        {
                            _poseFromRot[i] = _lastBaseValid && _lastBaseRot != null
                                && _lastBaseRot.Length == _pairs.Length
                                ? _lastBaseRot[i]
                                : _pairs[i].Target.rotation;
                        }
                        _poseFromValid = true;
                    }
                }
                else
                {
                    _poseFromValid = false;
                }
                Quaternion containerRot = _container.rotation;

                for (int i = 0; i < _pairs.Length; i++)
                {
                    BonePair p = _pairs[i];
                    Quaternion targetRot = containerRot * _deltas[i] * p.TargetRestRot;
                    // The free arm is never crossfaded: while a one-handed item
                    // is taken out or holstered the game's animation for it is
                    // already the right one, and blending it from the captured
                    // pose froze it for the whole fade (the "other hand stutters"
                    // report). The gripping arm(s) are excluded for the same
                    // reason: they grip the vanilla tool, and crossfading them
                    // from the captured pose pulls the hands off the tool for
                    // the whole fade. The body still gets the eased transition.
                    int gripSkip = _poseGripSide;
                    bool blendThis = poseBlend && (_poseFreeSide == 0
                        || SourceArmSide(p.Source.name) != _poseFreeSide)
                        && (gripSkip == 0
                        || (gripSkip == 3
                            ? SourceArmSide(p.Source.name) == 0
                            : SourceArmSide(p.Source.name) != gripSkip));
                    if (blendThis)
                    {
                        targetRot = Quaternion.Slerp(_poseFromRot[i], targetRot, poseK);
                    }
                    p.Target.rotation = targetRot;
                }

                // Remember the base pose (what the retarget wrote, before the head
                // look and the arm IK are applied) as the starting point for the
                // next crossfade. The late pass runs last, so its snapshot is the
                // one that survives the frame.
                if (_lastBaseRot == null || _lastBaseRot.Length != _pairs.Length)
                {
                    _lastBaseRot = new Quaternion[_pairs.Length];
                }
                for (int i = 0; i < _pairs.Length; i++)
                {
                    _lastBaseRot[i] = _pairs[i].Target.rotation;
                }
                _lastBaseValid = true;

                if (_headSourceBone != null && _headBone != null)
                {
                    Vector3 headSrc = _headSourceBone.position;
                    if (_faceDetach > 0.0001f && pv != null && _modelYawValid)
                    {
                        // The shoulder view decouples the model's heading from the
                        // aim, but the game rig itself still turns with the camera,
                        // so the source head bone -- a child of the rig origin --
                        // swings around that origin on a small circle (its own
                        // horizontal offset, ~0.1-0.3 m) whenever the mouse moves.
                        // Anchoring the model to that world point dragged the whole
                        // body around with the camera while the player stood still,
                        // and the world-space cloth pinned to its bones lagged
                        // behind: the hair, tie and skirt swung every time the view
                        // was orbited. Rebuild the anchor in the model's own visual
                        // frame instead of the camera's, so only real movement and
                        // pose changes move it. (_modelYaw is the visual heading;
                        // the container carries the authored mesh Yaw on top.)
                        Vector3 localHead = pv.InverseTransformPoint(headSrc);
                        Vector3 anchor = pv.position + Quaternion.Euler(0f, _modelYaw, 0f) * localHead;
                        headSrc = Vector3.Lerp(headSrc, anchor, _faceDetach);
                    }
                    _container.position += headSrc - _headBone.position;
                }

                // Turn skid: while the heading is detached and changing, the body
                // slides a little to the outside of the arc (the direction it was
                // travelling before the turn) instead of spinning around the head.
                // Applied after the head anchor so it reads as a body-led turn; the
                // game's own body and collision stay exactly where they were.
                if (_faceDetach > 0.0001f && Mathf.Abs(_lean) > 0.0001f)
                {
                    _container.position -= _container.right * (_lean * 0.12f * _faceDetach);
                }

                // The vanilla body skeleton never pitches (the view direction lives
                // on the camera rig only). Add the glance on top of the retargeted
                // pose, after the head alignment so the body is not shifted.
                ApplyHeadLook();

                // Where the retargeted hand sits relative to the game hand; only a
                // diagnostic now that Model mode does not bend the arm at all.
                if (_verbose.Value && Time.unscaledTime >= _nextArmLog)
                {
                    _nextArmLog = Time.unscaledTime + 1f;
                    LogArm("L");
                    LogArm("R");
                }

                // A held quick-slot item is handled by one of two pipelines:
                //
                //   Model (default) - model-led. The arm keeps the retargeted
                //     pose untouched: its own upper-arm/forearm lengths, its own
                //     elbow and its natural wrist. The item is carried into the
                //     model's palm instead (both palms for a two-handed tool),
                //     measured from the live palm frames, so no joint has to
                //     absorb a proportion mismatch. This is the path that does
                //     not trade a natural wrist for a perfectly socketed tool.
                //   Game - legacy. The arm is solved onto the game hand/grip
                //     (two-bone IK, clavicle share, wrist clamp). Kept as an
                //     escape hatch and for comparison; it can twist the wrist
                //     on models whose arms are shorter than the suit's.
                //   Off - pure retarget, neither the arm nor the item is
                //     touched.
                //
                // The PDA is not a quick-slot item and has no palms to carry
                // it, so it always uses the legacy hand IK (AlignHandsToGameHands).
                bool holding = HoldingItem();
                bool pda = PdaInUse();
                string holdMode = _holdMode != null ? _holdMode.Value : "Model";
                bool modeOff = string.Equals(holdMode, "Off",
                    StringComparison.OrdinalIgnoreCase);
                bool modeGame = string.Equals(holdMode, "Game",
                    StringComparison.OrdinalIgnoreCase);
                // Model is also the fallback for an empty or mistyped value: the
                // model-led path must not depend on the user getting the string
                // exactly right.
                bool modeAdaptive = AdaptiveMode();
                bool modeModel = !modeOff && !modeGame && !modeAdaptive;
                bool modelGrip = modeModel && !pda && holding;
                // Only the explicit Game mode may still solve a quick-slot
                // item's arm. Model and Off never do: in Off the arm and the
                // item are both left exactly as the retarget authored them.
                bool legacyTool = modeGame && !pda && holding;
                if (modeAdaptive)
                {
                    _gripWeightL = _gripWeightR = _aimWeightL = _aimWeightR = 0f;
                    _modelArmWeightL = _modelArmWeightR = 0f;
                    if (!pda) _ikWeightL = _ikWeightR = 0f;
                }
                bool gripIk = legacyTool && _holdPoseMode.Value
                    && _holdIkMode != null
                    && !string.Equals(_holdIkMode.Value, "Off",
                        StringComparison.OrdinalIgnoreCase);
                bool vanillaHold = legacyTool && _holdPoseMode.Value && !gripIk;
                bool adaptItem = modelGrip
                    || (legacyTool && (_itemToModelHand.Value || vanillaHold || gripIk));
                // Model mode pulls the item towards the character after the
                // draw settles (see UpdateModelItemPull); the legacy
                // MoveHeldItemToModelHand path still does its full carry.
                _itemGlueOn = modelGrip || (legacyTool && _itemToModelHand.Value);
                bool solveLeft = _alignHands.Value
                    && (pda || (legacyTool && !adaptItem && HeldItemUsesLeftHand()));
                bool solveRight = _alignHands.Value
                    && (pda || (legacyTool && !adaptItem));
                bool gripLeft = gripIk && HeldItemUsesLeftHand();
                bool gripRight = gripIk;
                // While a grip weight is fading, the grip solve owns the arm;
                // running the old wrist IK on the same arm would fight it (the
                // PDA can open while a tool is still held). The old IK starts
                // once the grip has fully faded.
                bool gripOwnsL = gripLeft || _gripWeightL > 0.0001f;
                bool gripOwnsR = gripRight || _gripWeightR > 0.0001f;
                // No capture ever runs, so _holdPoseLast stays null and
                // holdPoseApply below never engages: the arm keeps the plain
                // retargeted vanilla pose.
                bool holdPoseActive = false;
                // Applies while the weight is fading too (swim start, holster):
                // blending the pose towards the retargeted stroke is a smooth
                // release, whereas SolveArm would pull the arm onto the game's
                // hand mid-stroke and fold the swim motion backwards.
                bool holdPoseApply = !solveRight && _holdPoseLast != null
                    && _holdPoseLast.Valid
                    && (holdPoseActive || _ikWeightR > 0.0001f);
                bool wasSolveLeft = _ikLeftActive;
                bool wasSolveRight = _ikRightActive;
                if (solveLeft != wasSolveLeft || solveRight != wasSolveRight)
                {
                    // One line per grip/release tells whether the IK actually
                    // engaged, and whether the weight can step: if the only
                    // pass running is the late pass (late=True), or deltaTime
                    // is 0 (paused/PDA), the weights stay at 0 and the arm
                    // keeps its plain retargeted pose.
                    Logger.LogInfo(string.Format(
                        "IK gate: holding={0} pda={1} solveL={2} solveR={3} "
                        + "wL={4:F2} wR={5:F2} dt={6:F3} late={7}",
                        holding, pda, solveLeft, solveRight, _ikWeightL,
                        _ikWeightR, Time.deltaTime, _latePass));
                }
                if ((solveLeft && !_ikLeftActive) || (solveRight && !_ikRightActive))
                {
                    // A new held-item pose just started: pick the elbow side from the
                    // game's arm instead of carrying over the previous pose's direction.
                    _hasPoleL = false;
                    _hasPoleR = false;
                }
                _ikLeftActive = solveLeft;
                _ikRightActive = solveRight || holdPoseActive;
                if (gripLeft != _gripActiveL || gripRight != _gripActiveR)
                {
                    // New grip: pick the elbow side from this pose instead of
                    // carrying the swim stroke's pole into it.
                    _hasPoleL = false;
                    _hasPoleR = false;
                    Logger.LogInfo(string.Format(
                        "GripIK gate: holding={0} pda={1} gripL={2} gripR={3} "
                        + "vanilla={4} late={5}",
                        holding, pda, gripLeft, gripRight, vanillaHold, _latePass));
                }
                _gripActiveL = gripLeft;
                _gripActiveR = gripRight;
                // Holstering an item used to drop the correction in one frame, which
                // made the arm snap out of the held pose mid-stroke. Step the weight
                // once per frame (this pass runs twice) and blend the solved pose in.
                if (!_latePass)
                {
                    float step = Time.deltaTime > 0f ? Time.deltaTime / 0.22f : 0f;
                    // While the grip solve owns an arm, the old wrist IK waits
                    // at zero: otherwise both would write the same bones during
                    // the grip's fade (PDA opened with a tool still held) and
                    // the stronger one would snap the arm.
                    _ikWeightL = Mathf.MoveTowards(_ikWeightL,
                        solveLeft && !gripOwnsL ? 1f : 0f, step);
                    _ikWeightR = Mathf.MoveTowards(_ikWeightR,
                        (solveRight || holdPoseActive) && !gripOwnsR ? 1f : 0f,
                        step);
                    // The vanilla-hand aim is opt-in (HoldAim) and off by
                    // default: with it off the gripping arm keeps the plain
                    // retargeted pose, exactly as the game's arm animation
                    // has it, and the hand stays a few centimetres from the
                    // tool instead of twisting the wrist onto it.
                    bool holdAim = vanillaHold && _holdAim.Value;
                    _aimWeightL = Mathf.MoveTowards(_aimWeightL,
                        holdAim && HeldItemUsesLeftHand() ? 1f : 0f, step);
                    _aimWeightR = Mathf.MoveTowards(_aimWeightR,
                        holdAim ? 1f : 0f, step);
                    _gripWeightL = Mathf.MoveTowards(_gripWeightL,
                        gripLeft ? 1f : 0f, step);
                    _gripWeightR = Mathf.MoveTowards(_gripWeightR,
                        gripRight ? 1f : 0f, step);
                }
                if (_ikWeightL > 0.0001f && !gripOwnsL)
                {
                    SolveArm("L", _ikWeightL);
                }
                else
                {
                    ResetArmRoot("L");
                }
                if (_ikWeightR > 0.0001f && !gripOwnsR)
                {
                    if (holdPoseApply)
                    {
                        ApplyHoldPose("R", _ikWeightR, _holdPoseLast);
                    }
                    else if (solveRight)
                    {
                        SolveArm("R", _ikWeightR);
                    }
                    else
                    {
                        ResetArmRoot("R");
                    }
                }
                else
                {
                    _holdPoseLast = null;
                    ResetArmRoot("R");
                }

                // Grip-anchored hold solve. Runs after the plain IK block so
                // it starts from the fresh retargeted pose; during its fade it
                // blends towards the rebuilt vanilla arm pose, never towards
                // the old wrist-to-wrist solve.
                if (_gripWeightL > 0.0001f)
                {
                    SolveGripIk("L", _gripWeightL, gripLeft);
                }
                if (_gripWeightR > 0.0001f)
                {
                    SolveGripIk("R", _gripWeightR, gripRight);
                }

                // Direct vanilla hold: the gripping arm plays the game's own
                // animation, but its retargeted clavicle/aim roll lives in the
                // suit's bind frame and twists the sleeve at the arm root
                // (worst with the seaglide). Strip the roll exactly as the IK
                // solve used to. Only the gripping arm(s): the free arm and
                // the swim stroke keep the pure retarget.
                if (vanillaHold)
                {
                    // Legacy direct-vanilla hold only. Model mode deliberately
                    // leaves the retargeted clavicle alone: RebuildArmRootRoll
                    // rotates the clavicle from the upper arm's current
                    // direction, and with the per-frame arm solve on top that
                    // feedback made the knife/builder hand buzz and pulled the
                    // upper arm into the chest.
                    RebuildArmRootRoll("R");
                    if (HeldItemUsesLeftHand())
                    {
                        RebuildArmRootRoll("L");
                    }
                }
                // Optional (HoldAim, off by default): the body skeleton the model
                // copies never pitches with the view, the vanilla first-person
                // arms rig does, and the held tool hangs from it. Aiming the
                // gripping arm at that hand closes the gap but over-constrains
                // the arm - hand position, hand rotation, the model's own bone
                // lengths and a natural wrist cannot all hold at once - and the
                // wrist absorbs the difference. Kept for the fade-out path so a
                // stale weight never leaves the arm mid-aim.
                if (_aimWeightR > 0.0001f && !gripRight)
                {
                    Transform aimR = VanillaHand("R");
                    if (aimR != null)
                    {
                        AimArmAt("R", aimR.position, _aimWeightR, false);
                    }
                }
                if (_aimWeightL > 0.0001f && !gripLeft)
                {
                    Transform aimL = VanillaHand("L");
                    if (aimL != null)
                    {
                        AimArmAt("L", aimL.position, _aimWeightL, false);
                    }
                }
                // Model mode: pull the item towards the character after the
                // draw settles, then solve the arm POSITION onto the pulled
                // grips (both hands). The hand rotation, fingers and clavicle
                // are untouched, so this cannot stretch fingers or fold the
                // upper arm. Active only after the pull has faded in, which is
                // after the equip animation has finished.
                if (_latePass)
                {
                    UpdateModelItemPull(modelGrip);
                    if (modelGrip && _holdArmIk != null && _holdArmIk.Value
                        && _itemPullWeight > 0.0001f)
                    {
                        Vector3 targetR = PalmPoint("R", false) + _itemPullVec;
                        Vector3 targetL = PalmPoint("L", false) + _itemPullVec;
                        UpdateModelArms(true, HeldItemUsesLeftHand(),
                            targetR, targetL);
                    }
                    else
                    {
                        UpdateModelArms(false, false, Vector3.zero, Vector3.zero);
                    }
                }
                // Hand palm alignment stays off: rotating the hand stretched
                // the finger skin.
                _handAlignWeightR = 0f;
                _handAlignWeightL = 0f;
                if (vanillaHold)
                {
                    // Pitch chain trace while a tool is held (every 2 s): where
                    // the camera pitch stops between the game's arm and the grip.
                    if (Time.unscaledTime >= _nextPitchDiag)
                    {
                        _nextPitchDiag = Time.unscaledTime + 2f;
                        LogPitchDiag("R");
                        if (HeldItemUsesLeftHand())
                        {
                            LogPitchDiag("L");
                        }
                    }
                }

                // Compact trace of how the right hand relates to the game's hand and to the
                // held item. Constant values while the view pitches mean the model hand is
                // riding with the item; drifting values mean it is not.
                if (_verbose.Value && Time.unscaledTime >= _nextToolTrace)
                {
                    _nextToolTrace = Time.unscaledTime + 0.5f;
                    Transform srcHand = null;
                    if (_diagSources != null)
                    {
                        SourceBone sb;
                        if (_diagSources.Bones.TryGetValue("hand_R", out sb))
                        {
                            srcHand = sb.Bone;
                        }
                    }
                    Transform mdlHand = null;
                    int mi;
                    if (_diagIndexByName != null && _diagIndexByName.TryGetValue("hand_R", out mi))
                    {
                        mdlHand = _diagBones[mi];
                    }
                    if (srcHand != null && mdlHand != null)
                    {
                        string text = "Trace: handPosErr="
                            + Vector3.Distance(srcHand.position, mdlHand.position).ToString("F3")
                            + " handRotErr=" + Quaternion.Angle(srcHand.rotation, mdlHand.rotation).ToString("F1");
                        Inventory inv = Inventory.main;
                        InventoryItem held = inv != null && inv.quickSlots != null
                            ? inv.quickSlots.heldItem : null;
                        if (held != null && held.item != null)
                        {
                            Transform ht = held.item.transform;
                            Vector3 local = mdlHand.InverseTransformPoint(ht.position);
                            Quaternion localRot = Quaternion.Inverse(mdlHand.rotation) * ht.rotation;
                            Vector3 e = localRot.eulerAngles;
                            text += " itemLocal=(" + local.x.ToString("F3") + ","
                                + local.y.ToString("F3") + "," + local.z.ToString("F3") + ")"
                                + " itemRotLocal=(" + e.x.ToString("F0") + ","
                                + e.y.ToString("F0") + "," + e.z.ToString("F0") + ")";
                            Camera cam = Camera.main;
                            if (cam != null)
                            {
                                text += " itemCam=" + Vector3.Distance(cam.transform.position, ht.position).ToString("F3")
                                    + " handCam=" + Vector3.Distance(cam.transform.position, mdlHand.position).ToString("F3");
                            }
                        }
                        Logger.LogInfo(text);
                    }
                }

                // Debug: park the model a few metres ahead so it can be inspected.
                // Both passes re-anchor the container to the head first, so the offset
                // has to be applied in both of them; it does not accumulate.
                if (_showcase.Value > 0f)
                {
                    Camera cam = Camera.main;
                    if (cam != null)
                    {
                        _container.position += cam.transform.forward * _showcase.Value;
                    }
                }

                // Twintail physics. Only from the last pass of the frame: FrameLate runs
                // twice, once right after Player.LateUpdate and once at the very end after
                // FinalIK; stepping the simulation twice would double the speed.
                if (_latePass)
                {
                    SimulateCloth(Time.deltaTime);
                }

                // Third/second-person camera. Runs after everything else that could touch
                // the camera rig, and before the head-hide test below, which reads the
                // camera position: a few metres out the head must stay visible.
                if (_latePass)
                {
                    UpdateViewCamera();
                    // After the view blend is up to date: the mask belongs to the
                    // first-person look and is hidden in both other views.
                    UpdatePlayerMask();
                }

                // 4. hide the head when the camera is inside it. Only in first
                // person: in the third-person views the head is on screen and has to
                // stay there even when the camera is pulled close (numpad distance,
                // terrain collision), so the distance test is skipped as soon as the
                // view offset is in effect. During the blend into or out of first
                // person the camera still passes through the head, so the test keeps
                // running while the offset is small.
                if (_hideHead.Value && _headRenderer != null && _headBone != null)
                {
                    Camera cam = Camera.main;
                    bool thirdPersonView = _viewApplied != 0 && _viewBlend > 0.1f;
                    if (cam == null || thirdPersonView)
                    {
                        SetHeadHidden(false);
                    }
                    else
                    {
                        Vector3 camPos = cam.transform.position;
                        bool hide = Vector3.Distance(camPos, _headBone.position) < _headHideDistance.Value
                                    || _headRenderer.bounds.Contains(camPos);
                        SetHeadHidden(hide);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    Logger.LogError("Retarget error: " + e);
                }
            }
        }

        /// <summary>
        /// Hides the head from the camera without removing it from the scene, so it keeps
        /// casting a shadow. Plain enabled=false also killed the head's shadow.
        /// </summary>
        private void SetHeadHidden(bool hidden)
        {
            if (_headRenderer == null)
            {
                return;
            }
            if (!_headRenderer.enabled)
            {
                _headRenderer.enabled = true;
            }
            ShadowCastingMode want = hidden ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            if (_headRenderer.shadowCastingMode != want)
            {
                _headRenderer.shadowCastingMode = want;
            }

            // The twintails live in their own renderer: they can stay visible (and
            // simulated) while the head is hidden, otherwise they only cast a shadow.
            if (_hairRenderer != null)
            {
                if (!_hairRenderer.enabled)
                {
                    _hairRenderer.enabled = true;
                }
                ShadowCastingMode hairWant = !hidden || _showHair.Value
                    ? ShadowCastingMode.On
                    : ShadowCastingMode.ShadowsOnly;
                if (_hairRenderer.shadowCastingMode != hairWant)
                {
                    _hairRenderer.shadowCastingMode = hairWant;
                }
            }
        }

        /// <summary>
        /// F5 cycles the camera view: first person -> third person (behind the player)
        /// -> second person (in front of the player, looking back at the character).
        /// The move happens at the very end of the frame. The camera rig itself is left
        /// alone, so the first-person arm pose, held items and the model's anchoring keep
        /// working - only the Camera transform ends up elsewhere.
        /// </summary>
        /// <summary>
        /// Stand-in for <c>PlayerController.forwardReference</c> while second person
        /// has the camera turned around. The game turns movement input into world
        /// directions with that transform's rotation, and the camera transform is the
        /// one it returns - so with the view flipped, W pushed the character the
        /// opposite way and the swim animation looked played backwards. This returns
        /// the orientation the camera would have had without the flip, at the flipped
        /// camera's position. Null in every other view.
        /// </summary>
        internal Transform SecondPersonForward()
        {
            if (!_camMoved || _viewApplied != 2 || _viewBlend <= 0.0001f)
            {
                return null;
            }
            Camera cam = Camera.main;
            if (cam == null)
            {
                return null;
            }
            Transform camT = cam.transform;
            if (_viewProbe == null)
            {
                GameObject go = new GameObject("Miku_ViewProbe");
                go.hideFlags = HideFlags.HideAndDontSave;
                _viewProbe = go.transform;
            }
            Transform parent = camT.parent;
            Quaternion rot = parent != null ? parent.rotation * _camBaseLocalRot : _camBaseLocalRot;
            _viewProbe.SetPositionAndRotation(camT.position, rot);
            return _viewProbe;
        }

        /// <summary>
        /// Second person also has to reach the places that read the camera
        /// transform directly instead of going through
        /// <c>PlayerController.forwardReference</c>: the ground and underwater
        /// motors use that property, but Mono inlines its one-line getter into
        /// them, so a Harmony patch on the getter never runs. The arms controller
        /// reads <c>camRoot.GetAimingTransform()</c> outright to turn the player's
        /// velocity into the swim animation parameters. Borrowing the camera's
        /// unflipped rotation for the duration of those calls fixes both: W moves
        /// the character the way it faces again and the swim cycle plays forwards.
        /// </summary>
        internal static void BeginSecondPersonSwap()
        {
            object box = _instanceBox;
            if (box == null)
            {
                return;
            }
            ((MikuPlayerReplacerPlugin)box).BeginSecondPersonCameraSwap();
        }

        internal static void EndSecondPersonSwap()
        {
            object box = _instanceBox;
            if (box == null)
            {
                return;
            }
            ((MikuPlayerReplacerPlugin)box).EndSecondPersonCameraSwap();
        }

        internal void BeginSecondPersonCameraSwap()
        {
            if (_swapCamT != null)
            {
                // Nested call (or a leftover restore): leave the other one alone.
                return;
            }
            if (!_camMoved || _viewApplied != 2 || _viewBlend <= 0.0001f)
            {
                return;
            }
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            Transform t = cam.transform;
            if (_camMovedT != t)
            {
                return;
            }
            _swapCamT = t;
            _swapSavedRot = t.localRotation;
            t.localRotation = _camBaseLocalRot;
        }

        internal void EndSecondPersonCameraSwap()
        {
            Transform t = _swapCamT;
            _swapCamT = null;
            if (t == null)
            {
                return;
            }
            // Undo only our own write. If the game rotated the camera during the
            // call (it does not in these two paths, but be safe), keep that value.
            if (Quaternion.Angle(t.localRotation, _camBaseLocalRot) < 0.5f)
            {
                t.localRotation = _swapSavedRot;
            }
        }

        /// <summary>
        /// Shoulder view: turn the camera the swim animation reads for the duration
        /// of ArmsController.Update. Vanilla projects the velocity onto the camera
        /// axes to pick the stroke, so with the model facing its movement direction
        /// pressing S played the backward stroke while the character was visibly
        /// swimming forwards and away from the camera. Feeding the blend of camera
        /// and model heading makes the stroke follow the direction the character
        /// actually travels, whichever key produced it. Only the rotation is
        /// borrowed - position and the movement motors are untouched, so the keys
        /// still steer relative to the camera.
        /// </summary>
        internal static void BeginSwimFacingSwap()
        {
            object box = _instanceBox;
            if (box == null)
            {
                return;
            }
            ((MikuPlayerReplacerPlugin)box).BeginSwimFacingCameraSwap();
        }

        internal static void EndSwimFacingSwap()
        {
            object box = _instanceBox;
            if (box == null)
            {
                return;
            }
            ((MikuPlayerReplacerPlugin)box).EndSwimFacingCameraSwap();
        }

        internal void BeginSwimFacingCameraSwap()
        {
            if (_swimCamT != null)
            {
                return;
            }
            if (_faceDetach <= 0.05f || _viewApplied != 1)
            {
                return;
            }
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            Transform t = cam.transform;
            if (!_camMoved || _camMovedT != t)
            {
                return;
            }
            _swimCamT = t;
            _swimSavedRot = t.localRotation;
            // Keep the pitch the game is looking at (up/down strokes stay honest),
            // replace only the heading, easing from the camera to the model.
            Vector3 euler = t.eulerAngles;
            float refYaw = Mathf.LerpAngle(euler.y, _modelYaw, Mathf.Clamp01(_faceDetach));
            t.rotation = Quaternion.Euler(euler.x, refYaw, 0f);
        }

        internal void EndSwimFacingCameraSwap()
        {
            Transform t = _swimCamT;
            _swimCamT = null;
            if (t == null)
            {
                return;
            }
            t.localRotation = _swimSavedRot;
        }

        // ---------------------------------------------------------------------
        // Shoulder view: the model keeps its own heading.
        // ---------------------------------------------------------------------

        /// <summary>
        /// True while a cinematic (bed, cure, grab, vehicle entry, ...) owns the
        /// player and the camera. The model has to follow the game's first-person
        /// body root there instead of keeping its own heading.
        /// </summary>
        private static bool CinematicActive()
        {
            Player player = Player.main;
            if (player != null && player.cinematicModeActive)
            {
                return true;
            }
            // Some interactions (sitting on a bench/chair) only set the camera's
            // flag, not the player's.
            MainCameraControl cam = MainCameraControl.main;
            return cam != null && cam.cinematicMode;
        }

        /// <summary>
        /// Once per frame: the state shared by the idle head look and the
        /// ArmsController animator patch. Keeping it in one place means the model
        /// and the game animator cannot disagree about whether the player is idle
        /// or in a cinematic.
        /// </summary>
        private void UpdatePoseState(float dt)
        {
            _viewCinematic = CinematicActive();
            bool third = _viewApplied != 0 && _viewBlend > 0.1f;

            // Movement input, not the rigidbody velocity. Underwater the player
            // never quite stops: buoyancy, the idle swim bob and currents keep
            // PlayerController.velocity above the old 0.25 m/s threshold even when
            // the player is not touching a key. The idle branch therefore never
            // engaged, view_turn/view_pitch kept feeding the camera swing into the
            // spine, and orbiting the camera dragged the hair and skirt along.
            // GetMoveDirection() is the raw stick/WASD state (zero when input is
            // cleared) and includes the up/down swim axis.
            Vector3 moveInput = GameInput.GetMoveDirection();
            // Input alone is not enough: with an item in hand (the seaglide
            // especially) the player keeps gliding for a second or two after the
            // stick is released, and treating that as idle froze the head follow
            // and the swim pose mid-stroke. Actual travel counts as moving too.
            // The travel is measured over a short window instead of from
            // PlayerController.velocity: buoyancy and the idle swim bob keep that
            // velocity above a walking pace even when the player just floats, but
            // a bob does not accumulate displacement.
            Player idlePlayer = Player.main;
            bool travelled = false;
            float speed = 0f;
            if (idlePlayer != null)
            {
                Vector3 pos = idlePlayer.transform.position;
                PlayerController controller = idlePlayer.GetComponent<PlayerController>();
                if (controller != null)
                {
                    speed = controller.velocity.magnitude;
                }
                if ((pos - _idleRefPos).sqrMagnitude > 0.09f)
                {
                    travelled = true;
                    _idleRefPos = pos;
                    _idleRefTime = Time.time;
                }
                else if (Time.time - _idleRefTime > 0.5f)
                {
                    // Re-home the reference on a timer, so a slow drift around a
                    // fixed point never adds up to a false "moving".
                    _idleRefPos = pos;
                    _idleRefTime = Time.time;
                }
            }
            bool movingNow = moveInput.sqrMagnitude >= 0.01f || travelled;
            if (movingNow)
            {
                // IdleDelay is the grace period after the last stroke: for that
                // long the head still follows the camera and the swim pose stays
                // live, so a short pause does not freeze the character mid-motion.
                // Only a real standstill (3 s by default) switches the idle freeze
                // on; adjustable live in the PDA model page.
                float idleDelay = _idleDelay != null
                    ? Mathf.Max(0f, _idleDelay.Value) : 3f;
                _idleHoldUntil = Time.time + idleDelay;
            }
            // Empty-handed only. With a quick-slot item in hand the game keeps the
            // body facing the aim (that is what view_turn is for), and the body
            // must keep doing so while standing still; freezing it there left the
            // hands glued to the game's aim while the torso stayed behind, so only
            // the arms moved with the camera and the gripping forearm twisted
            // across the chest. The idle freeze exists for the empty-handed
            // standstill, where orbiting the view must not drag the body.
            _viewIdle = third && !_viewCinematic && !movingNow && !HoldingItem()
                && Time.time >= _idleHoldUntil;

            // One line per idle/move transition: the camera-only hair test is
            // indistinguishable from a real move in the log otherwise.
            if (third && _viewIdle != _viewIdleLogged)
            {
                _viewIdleLogged = _viewIdle;
                Logger.LogInfo("View idle " + (_viewIdle ? "on" : "off")
                    + " input=" + moveInput.magnitude.ToString("F2")
                    + " speed=" + speed.ToString("F2")
                    + " travelled=" + travelled
                    + " holding=" + HoldingItem());
            }

            // view_turn/view_pitch are the camera-driven inputs of the source
            // skeleton. In the third-person views they swing the spine, arms,
            // hair and skirt when the player only orbits the camera (most
            // obvious when standing still), and they fight a fixed cinematic
            // pose. Scale them down there; the game re-damps its own values when
            // it resumes, so moving again blends back in.
            //
            // Only the empty-handed idle suppresses these. While an item is held
            // the body has to keep following the aim: the game turns it with
            // view_turn/view_pitch (the container only inherits player_view, it
            // does not add the camera turn itself), so zeroing them left the
            // torso fixed while the aim IK kept the hands on the camera, and the
            // gripping arm ended up wrenched across the body
            // (_viewIdle already excludes the held-item case above).
            _suppressViewAnim = third && (_viewCinematic || _viewIdle);
            // Fade the suppression itself: stopping dead would snap the arms and
            // head from the camera-pitched pose straight to the neutral one.
            // Asymmetric: settling into the idle pose is slow so the body visibly
            // relaxes instead of dropping into place, while coming back out is
            // quick so the head does not lag behind a fresh movement input.
            float suppressRate = _suppressViewAnim ? dt / 0.55f : dt / 0.25f;
            _viewSuppressWeight = Mathf.MoveTowards(_viewSuppressWeight,
                _suppressViewAnim ? 1f : 0f, suppressRate);

            // The added head yaw/pitch follows the view only while the player is
            // actually doing something. Idle: body and head keep their heading,
            // so turning the camera moves nothing (the idle freeze the user
            // asked for). Cinematics: the animation's own head pose plays.
            // Settling back into idle is deliberately slow (0.8 s, eased in
            // ApplyHeadLook) so the head glides back instead of snapping;
            // resuming the follow is quicker (0.3 s).
            float headTarget = third && !_viewCinematic && !_viewIdle ? 1f : 0f;
            float headRate = headTarget > _headLookFade ? dt / 0.3f : dt / 0.8f;
            _headLookFade = Mathf.MoveTowards(_headLookFade, headTarget, headRate);

            // Pose crossfade trigger. Taking out or holstering a tool switches the
            // game's animation state in a single frame (the seaglide changes the
            // whole upper body), and a cinematic does the same. Start the eased
            // crossfade in the retarget; the source-pose jump detector in
            // FrameLate catches any other abrupt switch. Decrement once per frame
            // so the second retarget pass in the same frame uses the same value.
            bool poseChanged = false;
            // The arm that is not gripping the item must not be crossfaded: its
            // vanilla animation is already live, and blending it from the pose
            // captured when the item changed made the free hand stall until the
            // fade ended. A one-handed item is gripped with the right hand, so
            // the left arm is the free one (both are excluded when the left hand
            // grips too; a cinematic that starts mid-grip also keeps the grip
            // hand live).
            int freeArm = 0;
            // The gripping hand is never crossfaded either: it holds the vanilla
            // tool, so it always plays the game's live animation (equip, holster,
            // use) and stays on the tool. Kept excluded while holstering the
            // previous item and during cinematics that start mid-grip.
            int gripArm = 0;
            Inventory quickSlots = Inventory.main;
            InventoryItem heldNow = quickSlots != null && quickSlots.quickSlots != null
                ? quickSlots.quickSlots.heldItem : null;
            if (!ReferenceEquals(heldNow, _poseHeldItem))
            {
                int wasGripSide = _poseGripSide;
                _poseHeldItem = heldNow;
                poseChanged = true;
                if (heldNow != null)
                {
                    if (!HeldItemUsesLeftHand())
                    {
                        // One-handed tool: right hand grips the vanilla tool.
                        freeArm = 1;
                        gripArm = 2;
                    }
                    else
                    {
                        // Two-handed grip (seaglide and the like): both hands
                        // are on the vanilla tool, so both arms stay on the
                        // game's live animation.
                        gripArm = 3;
                    }
                }
                else if (wasGripSide != 0)
                {
                    // Holstering: the hand(s) that gripped the item play the
                    // vanilla holster animation live as well, and the other
                    // arm never stalls through the fade either.
                    freeArm = 1;
                    gripArm = wasGripSide;
                }
                else
                {
                    freeArm = 1;
                }
                // Direct vanilla mode: no hold-pose capture.
                _holdCapturePending = false;
            }
            if (_viewCinematic != _poseCinematicPrev)
            {
                _poseCinematicPrev = _viewCinematic;
                poseChanged = true;
                freeArm = 0;
                // A cinematic that starts while a tool is gripped keeps the grip
                // hand live so it does not slide off the tool during the fade.
                gripArm = GripSideNow();
            }
            if (poseChanged)
            {
                StartPoseTransition(freeArm, gripArm);
                Logger.LogInfo("Pose transition: held="
                    + (heldNow != null && heldNow.item != null ? heldNow.item.name : "none")
                    + " cinematic=" + _viewCinematic
                    + " freeArm=" + (freeArm == 0 ? "none" : freeArm == 1 ? "L" : "R")
                    + " gripArm=" + (gripArm == 0 ? "none" : gripArm == 1 ? "L"
                        : gripArm == 2 ? "R" : "both"));
            }
            else if (_poseTransition > 0f)
            {
                _poseTransition = Mathf.Max(0f, _poseTransition - dt);
            }
            if (_poseJumpLock > 0f)
            {
                _poseJumpLock = Mathf.Max(0f, _poseJumpLock - dt);
            }

            // No model: leave nothing set that could alter the game's own arms.
            if (_container == null || _data == null)
            {
                _viewIdle = false;
                _viewCinematic = false;
                _suppressViewAnim = false;
                _viewSuppressWeight = 0f;
                _headLookFade = 1f;
                _idleHoldUntil = 0f;
                _headYawShown = 0f;
                _headPitchShown = 0f;
            }
        }

        /// <summary>
        /// How much of the camera-driven animator inputs to remove this frame,
        /// 0..1. Read by the ArmsController postfix; 0 whenever there is no model
        /// to protect, so the game's own first-person arms are never touched.
        /// </summary>
        internal static float ViewAnimSuppression()
        {
            object box = _instanceBox;
            if (box == null)
            {
                return 0f;
            }
            MikuPlayerReplacerPlugin p = (MikuPlayerReplacerPlugin)box;
            if (!p.MasterEnabled || p._data == null || p._container == null)
            {
                return 0f;
            }
            return p._viewSuppressWeight;
        }

        /// <summary>BepInEx log line from the static ArmsController patch.</summary>
        internal static void LogFromPatch(string message)
        {
            object box = _instanceBox;
            if (box != null)
            {
                ((MikuPlayerReplacerPlugin)box).Logger.LogInfo(message);
            }
        }

        internal static bool VerboseLoggingEnabled()
        {
            MikuPlayerReplacerPlugin plugin = _instanceBox as MikuPlayerReplacerPlugin;
            return !ReferenceEquals(plugin, null) && plugin._verbose != null && plugin._verbose.Value;
        }

        /// <summary>
        /// Once per frame: in the shoulder view the model turns towards the
        /// direction it is travelling, so the camera can be orbited with the mouse
        /// without dragging the body around. The game's own facing keeps driving
        /// movement and aiming; only the visible model is turned. Holding a
        /// quick-slot item fades the detached heading out again: the arms are
        /// pulled onto the game's hands by IK, and those hands belong to the body
        /// that still points where the camera looks.
        /// </summary>
        private void UpdateModelFacing(float dt)
        {
            if (_container == null)
            {
                _faceDetach = 0f;
                _modelYawValid = false;
                _moveDirValid = false;
                _lean = 0f;
                // No model, so no third-person view either: never leave the mask
                // overlay hidden if the model was torn down mid-view.
                RestoreMask();
                return;
            }
            Player player = Player.main;
            // With an item in hand the body must face the camera even while
            // standing still: the seaglide points where the camera looks, and
            // leaving the body behind left the arm twisted across the chest to
            // keep the hand on it. Empty-handed the heading is detached, so
            // orbiting the camera does not spin the model and fling the hair
            // and skirt around.
            bool want = _viewMode == 1 && _viewApplied == 1 && _viewBlend > 0.001f
                && (_faceMovement == null || _faceMovement.Value)
                && player != null
                && player.motorMode != Player.MotorMode.Vehicle
                && !_viewCinematic
                && !HoldingItem();
            if (want)
            {
                PDA pda = player.GetPDA();
                if (pda != null && pda.isInUse)
                {
                    want = false;
                }
            }
            _faceDetach = Mathf.MoveTowards(_faceDetach, want ? 1f : 0f, dt / 0.2f);

            if (_faceDetach <= 0.0001f)
            {
                _modelYawValid = false;
                _moveDirValid = false;
                _lean = 0f;
                _modelTurnRate = 0f;
                return;
            }
            if (!_modelYawValid)
            {
                // Start from the heading the model already shows, so entering the
                // view never spins it. The body forward is where the model faces
                // when it follows the camera (Yaw corrects the mesh's own axis).
                Transform pv = _container.parent;
                Vector3 f = pv != null ? pv.forward : _container.forward;
                f.y = 0f;
                if (f.sqrMagnitude > 1e-6f)
                {
                    _modelYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                }
                _moveDirValid = false;
                _modelYawValid = true;
            }
            PlayerController pc = player != null ? player.playerController : null;
            Vector3 v = pc != null ? pc.velocity : Vector3.zero;
            v.y = 0f;
            float speed = v.magnitude;
            // In water PlayerController.velocity comes from the rigidbody, so the
            // direction only changes on the physics tick (50 Hz by default). Step
            // the yaw straight towards it and the model turns in visible chunks at
            // any higher frame rate. Ease the direction and the yaw instead, with
            // hysteresis so a slowly drifting body does not turn on the spot.
            if (speed > (_moveDirValid ? 0.12f : 0.3f))
            {
                Vector3 dir = v / speed;
                if (!_moveDirValid)
                {
                    _moveDir = dir;
                    _moveDirValid = true;
                }
                else
                {
                    _moveDir = Vector3.Lerp(_moveDir, dir, 1f - Mathf.Exp(-dt / 0.07f));
                    if (_moveDir.sqrMagnitude > 1e-6f)
                    {
                        _moveDir.Normalize();
                    }
                }
                float target = Mathf.Atan2(_moveDir.x, _moveDir.z) * Mathf.Rad2Deg;
                float err = Mathf.Abs(Mathf.DeltaAngle(_modelYaw, target));
                // Ease-out follow, capped so a 180 degree turn still takes a beat.
                float step = Mathf.Min(err * (1f - Mathf.Exp(-dt / 0.08f)), 900f * dt);
                _modelYaw = Mathf.MoveTowardsAngle(_modelYaw, target, step);
            }

            // Bank and skid. A swimmer who changes heading does not pivot on the
            // spot: the body leans into the turn and the hips swing out over the
            // old heading before the feet catch up. Derive that from how fast the
            // visible heading is actually changing, not from the keys, so it
            // follows the eased turn above and dies out with it.
            float yawDelta = _modelYawPrevValid ? Mathf.DeltaAngle(_modelYawPrev, _modelYaw) : 0f;
            _modelYawPrev = _modelYaw;
            _modelYawPrevValid = true;
            float turnRate = dt > 0.0001f ? yawDelta / dt : 0f;
            _modelTurnRate = Mathf.Lerp(_modelTurnRate, Mathf.Clamp(turnRate, -540f, 540f),
                1f - Mathf.Exp(-dt / 0.1f));
            float targetLean = Mathf.Clamp(_modelTurnRate / 240f, -1f, 1f) * _faceDetach;
            _lean = Mathf.MoveTowards(_lean, targetLean, dt / 0.18f);
        }

        /// <summary>
        /// Writes the container's world rotation for the retarget pass. With the
        /// heading detached the model faces <see cref="_modelYaw"/>; otherwise it
        /// goes back to simply following player_view with the authored model yaw.
        /// </summary>
        private void ApplyModelFacing(Transform pv)
        {
            float yaw = _yaw != null ? _yaw.Value : 0f;
            Quaternion local = Quaternion.Euler(0f, yaw, 0f);
            if (_faceDetach <= 0.0001f)
            {
                if (Quaternion.Angle(_container.localRotation, local) > 0.01f)
                {
                    _container.localRotation = local;
                }
                return;
            }
            Quaternion parentRot = pv != null ? pv.rotation
                : _container.parent != null ? _container.parent.rotation
                : _container.rotation;
            Quaternion follow = parentRot * local;
            // A positive roll about the model's forward axis drops the left side,
            // so lean the other way: a right turn (positive yaw rate) banks right.
            Quaternion free = Quaternion.Euler(0f, _modelYaw + yaw, -_lean * 14f);
            _container.rotation = Quaternion.Slerp(follow, free, _faceDetach);
        }

        /// <summary>
        /// Numpad nudge for the shoulder camera: 4 / 6 sideways, 8 / 2 up / down,
        /// + / - distance, 5 resets. Values are written back to the config when
        /// the keys are released.
        /// </summary>
        private void AdjustShoulderCamera()
        {
            if (_thirdPersonSide == null || _thirdPersonHeight == null
                || _thirdPersonDistance == null)
            {
                return;
            }
            float step = 1.2f * Time.unscaledDeltaTime;
            float side = _thirdPersonSide.Value;
            float height = _thirdPersonHeight.Value;
            float distance = _thirdPersonDistance.Value;
            bool any = false;
            if (Input.GetKey(KeyCode.Keypad4)) { side -= step; any = true; }
            if (Input.GetKey(KeyCode.Keypad6)) { side += step; any = true; }
            if (Input.GetKey(KeyCode.Keypad2)) { height -= step; any = true; }
            if (Input.GetKey(KeyCode.Keypad8)) { height += step; any = true; }
            if (Input.GetKey(KeyCode.KeypadMinus)) { distance -= step; any = true; }
            if (Input.GetKey(KeyCode.KeypadPlus)) { distance += step; any = true; }
            if (Input.GetKeyDown(KeyCode.Keypad5))
            {
                side = 0.45f;
                height = 0.45f;
                distance = 3.0f;
                any = true;
            }
            if (any)
            {
                _thirdPersonSide.Value = Mathf.Clamp(side, -2f, 2f);
                _thirdPersonHeight.Value = Mathf.Clamp(height, -1f, 2f);
                _thirdPersonDistance.Value = Mathf.Clamp(distance, 1f, 8f);
                _configDirty = true;
                _camAdjusted = true;
                if (Time.unscaledTime >= _camAdjustLogAt)
                {
                    _camAdjustLogAt = Time.unscaledTime + 0.2f;
                    Logger.LogInfo("Camera: side=" + _thirdPersonSide.Value.ToString("F2")
                        + " height=" + _thirdPersonHeight.Value.ToString("F2")
                        + " distance=" + _thirdPersonDistance.Value.ToString("F2"));
                }
            }
            else if (_camAdjusted)
            {
                _camAdjusted = false;
                SaveConfigIfDirty();
            }
        }

        private void UpdateViewCamera()
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                _viewMode = (_viewMode + 1) % 3;
                Logger.LogInfo("Camera view: " + ViewName(_viewMode));
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            Transform camT = cam.transform;

            // While the camera is untouched, keep the game's own local transform. Every
            // offset is measured from it, and it must be recent: the game animates the
            // camera position and look direction every frame.
            if (!_camMoved)
            {
                _camBaseLocalPos = camT.localPosition;
                _camBaseLocalRot = camT.localRotation;
            }

            bool allowed = _viewMode != 0 && Player.main != null
                && Player.main.motorMode != Player.MotorMode.Vehicle;
            PDA pda = Player.main != null ? Player.main.GetPDA() : null;
            if (pda != null && pda.isInUse)
            {
                allowed = false;
            }
            int want = allowed ? _viewMode : 0;

            // Numpad adjusts the shoulder camera while it is actually on screen.
            if (allowed && _viewApplied == 1 && _viewBlend > 0.5f
                && !MmsConsole.IsOpen)
            {
                AdjustShoulderCamera();
            }

            // Switch modes through first person: fade the current offset out, then fade
            // the new one in. Without the detour a third <-> second person switch would
            // snap the camera from behind the player straight to the front.
            if (_viewBlend <= 0f)
            {
                _viewApplied = want;
            }
            if (_viewApplied != want)
            {
                _viewBlend = Mathf.MoveTowards(_viewBlend, 0f, Time.deltaTime / 0.25f);
            }
            else
            {
                _viewBlend = Mathf.MoveTowards(_viewBlend,
                    want != 0 ? 1f : 0f, Time.deltaTime / 0.25f);
            }

            if (_viewBlend <= 0.0001f)
            {
                // Whatever we wrote to the camera has to be undone by hand once the view
                // is back in the head; the next frame picks the restored transform up as
                // the new anchor.
                if (_camMoved)
                {
                    if (_camMovedT == camT)
                    {
                        camT.localPosition = _camBaseLocalPos;
                        camT.localRotation = _camBaseLocalRot;
                    }
                    _camMoved = false;
                }
                return;
            }

            // Second person overwrites the camera rotation. If the game replaced our value
            // with a fresh look direction since the last frame, remember it: that is what
            // has to be restored when the view returns to first person. Compare LOCAL
            // rotations: the parent (the player's body) rotates all the time, and a world
            // comparison would mistake that for the game writing the camera.
            if (_viewApplied == 2 && _camMoved && _camMovedT == camT
                && Quaternion.Angle(camT.localRotation, _camAppliedLocalRot) > 0.5f)
            {
                _camBaseLocalRot = camT.localRotation;
            }

            // The camera's own orientation as the game set it, in world space. In second
            // person the camera is placed along this direction and turned around to face
            // the character, so the player keeps looking where they were looking.
            Transform camParent = camT.parent;
            Quaternion baseRot = camParent != null
                ? camParent.rotation * _camBaseLocalRot
                : _camBaseLocalRot;

            Transform pivotT = _headSourceBone != null ? _headSourceBone : _headBone;
            if (pivotT == null)
            {
                return;
            }

            float blend = _viewBlend * _viewBlend * (3f - 2f * _viewBlend);
            Vector3 pivot = pivotT.position;
            bool behind = _viewApplied == 1;

            // Behind: keep walking backwards along the view direction; the camera keeps
            // its own rotation, so the game's look control stays untouched.
            // In front: step forward along the same view direction, then look back. The
            // direction has to come from the camera's own orientation, not from the
            // player root: in a first-person game the root's forward is the body's
            // facing, which is not where the player is looking.
            Vector3 dir = behind ? -camT.forward : baseRot * Vector3.forward;

            // The shoulder offset moves the camera sideways in the view's own
            // plane, so the character sits off-centre and the crosshair still
            // points where the game is aiming.
            Vector3 sideDir = baseRot * Vector3.right;
            Vector3 offset = dir * (_thirdPersonDistance.Value * blend)
                             + Vector3.up * (_thirdPersonHeight.Value * blend)
                             + sideDir * (behind && _thirdPersonSide != null
                                 ? _thirdPersonSide.Value * blend : 0f);

            // Pull the camera in when there is terrain or a base behind/in front.
            float len = offset.magnitude;
            if (len > 0.01f)
            {
                int n = Physics.SphereCastNonAlloc(pivot, 0.2f, offset / len, _camHits, len,
                    ~0, QueryTriggerInteraction.Ignore);
                float nearest = len;
                for (int i = 0; i < n; i++)
                {
                    Collider col = _camHits[i].collider;
                    if (col == null || IsPlayerCollider(col))
                    {
                        continue;
                    }
                    if (_camHits[i].distance < nearest)
                    {
                        nearest = _camHits[i].distance;
                    }
                }
                if (nearest < len)
                {
                    offset = offset / len * Mathf.Max(0.35f, nearest - 0.05f);
                }
            }

            camT.position = pivot + offset;
            if (!behind)
            {
                // Look back at the character, easing out of the game's own orientation.
                Vector3 lookDir = pivot - camT.position;
                if (lookDir.sqrMagnitude > 1e-6f)
                {
                    Quaternion look = Quaternion.LookRotation(lookDir, baseRot * Vector3.up);
                    camT.rotation = Quaternion.Slerp(baseRot, look, blend);
                }
                _camAppliedLocalRot = camT.localRotation;
            }
            _camMoved = true;
            _camMovedT = camT;
        }

        /// <summary>
        /// The diving-mask overlay is drawn a few centimetres in front of the
        /// first-person camera. It only makes sense from behind the eyes, so it is
        /// hidden in the shoulder and second-person views and back on the moment
        /// the view returns. The renderers are toggled rather than the GameObject
        /// so the game keeps animating the mask into place while it is not seen.
        /// </summary>
        private void UpdatePlayerMask()
        {
            if (_playerMask == null && Time.unscaledTime >= _maskScanAt)
            {
                _maskScanAt = Time.unscaledTime + 1f;
                PlayerMask found = null;
                if (Player.main != null)
                {
                    found = Player.main.GetComponentInChildren<PlayerMask>(true);
                }
                if (found == null && SNCameraRoot.main != null)
                {
                    found = SNCameraRoot.main.GetComponentInChildren<PlayerMask>(true);
                }
                if (found != null)
                {
                    _playerMask = found;
                    _maskRenderers = found.GetComponentsInChildren<Renderer>(true);
                    _maskHidden = false;
                    Logger.LogInfo("Diving mask overlay: " + _maskRenderers.Length + " renderers");
                }
            }
            if (_playerMask == null)
            {
                return;
            }
            bool hide = _viewApplied != 0 && _viewBlend > 0.25f;
            if (hide == _maskHidden)
            {
                return;
            }
            _maskHidden = hide;
            Renderer[] renders = _maskRenderers;
            if (renders == null || renders.Length == 0)
            {
                _maskRenderers = renders = _playerMask.GetComponentsInChildren<Renderer>(true);
            }
            for (int i = 0; i < renders.Length; i++)
            {
                Renderer r = renders[i];
                if (r != null)
                {
                    r.enabled = !hide;
                }
            }
        }

        /// <summary>
        /// Puts the mask overlay back on screen. Used when the model is torn down
        /// (master switch, host destroyed) while one of the third-person views was
        /// hiding it, so the game keeps its first-person mask afterwards.
        /// </summary>
        private void RestoreMask()
        {
            if (!_maskHidden)
            {
                return;
            }
            _maskHidden = false;
            if (_maskRenderers == null)
            {
                return;
            }
            for (int i = 0; i < _maskRenderers.Length; i++)
            {
                Renderer r = _maskRenderers[i];
                if (r != null)
                {
                    r.enabled = true;
                }
            }
        }

        private static string ViewName(int mode)
        {
            if (mode == 1)
            {
                return "shoulder (third person)";
            }
            if (mode == 2)
            {
                return "second person (front)";
            }
            return "first person";
        }

        private bool IsPlayerCollider(Collider col)
        {
            Transform t = col.transform;
            if (Player.main != null && t.IsChildOf(Player.main.transform))
            {
                return true;
            }
            return _container != null && t.IsChildOf(_container);
        }

        private void DumpComponents(Transform t, int depth, int maxDepth, string path)
        {
            if (t == null || depth > maxDepth)
            {
                return;
            }

            string full = path + "/" + t.name;
            StringBuilder sb = new StringBuilder();
            sb.Append(full);

            Animator anim = t.GetComponent<Animator>();
            if (anim != null)
            {
                sb.Append(" [Animator enabled=").Append(anim.enabled)
                  .Append(" ctrl=").Append(anim.runtimeAnimatorController != null
                      ? anim.runtimeAnimatorController.name : "none")
                  .Append(" avatar=").Append(anim.avatar != null ? anim.avatar.name : "none")
                  .Append("]");
            }

            SkinnedMeshRenderer smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null)
            {
                sb.Append(" [SMR enabled=").Append(smr.enabled)
                  .Append(" bones=").Append(smr.bones != null ? smr.bones.Length : 0)
                  .Append(" root=").Append(smr.rootBone != null ? smr.rootBone.name : "none")
                  .Append("]");
            }

            Logger.LogInfo("Comp: " + sb);

            for (int i = 0; i < t.childCount; i++)
            {
                DumpComponents(t.GetChild(i), depth + 1, maxDepth, full);
            }
        }

        private static string GetPath(Transform t, Transform root)
        {
            string path = t.name;
            Transform p = t.parent;
            while (p != null && p != root && p != p.parent)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        private static string BoundsText(Renderer r)
        {
            if (r == null)
            {
                return "none";
            }
            Bounds b = r.bounds;
            return string.Format("c({0:F1},{1:F1},{2:F1}) s({3:F1},{4:F1},{5:F1})",
                b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z);
        }

        private static string Rot(Transform t)
        {
            if (t == null)
            {
                return "none";
            }
            Vector3 e = t.rotation.eulerAngles;
            return string.Format("({0:F0},{1:F0},{2:F0})", e.x, e.y, e.z);
        }

        private static string LocalRot(Transform root, Transform t)
        {
            if (root == null || t == null)
            {
                return "none";
            }
            Vector3 e = (Quaternion.Inverse(root.rotation) * t.rotation).eulerAngles;
            return string.Format("({0:F0},{1:F0},{2:F0})", e.x, e.y, e.z);
        }

        private string ModelRot(string name)
        {
            int index;
            if (_diagIndexByName == null || !_diagIndexByName.TryGetValue(name, out index))
            {
                return "none";
            }
            return Rot(_diagBones[index]);
        }

        private void DumpHierarchy(Transform t, int depth, int maxDepth)
        {
            if (t == null || depth > maxDepth)
            {
                return;
            }
            Logger.LogInfo(new string(' ', depth * 2) + t.name);
            for (int i = 0; i < t.childCount; i++)
            {
                DumpHierarchy(t.GetChild(i), depth + 1, maxDepth);
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }
            if (root.name == name)
            {
                return root;
            }
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private void Start()
        {
            Logger.LogInfo("Start called. activeInHierarchy=" + gameObject.activeInHierarchy);
        }

        private void OnEnable()
        {
            Logger.LogInfo("OnEnable called.");
        }

        private void OnDisable()
        {
            Logger.LogInfo("OnDisable called.");
        }

        public void HandleHostDestroyed()
        {
            RestoreMask();
            // Also destroys the materials, decoded textures and generated meshes; on
            // teardown this keeps a scene reload from stranding a model's worth of VRAM.
            ReleaseModelAssets();
            if (_viewProbe != null)
            {
                Destroy(_viewProbe.gameObject);
                _viewProbe = null;
            }
        }

        private void OnDestroy()
        {
            Logger.LogInfo("OnDestroy called (BepInEx manager).");
            HandleHostDestroyed();
        }
    }

    /// <summary>
    /// Re-runs the retarget at the very end of LateUpdate, after every script that
    /// poses the game skeleton. RootMotion FinalIK aims the first-person arms in its
    /// own LateUpdate, which runs after Player.LateUpdate; without this pass the
    /// model follows last frame's arm pose while the held tool is drawn at this
    /// frame's, so pitching the view slides the item out of the hand.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class LateRetargetHost : MonoBehaviour
    {
        private void LateUpdate()
        {
            MikuPlayerReplacerPlugin.PumpLate();
        }
    }

    [HarmonyPatch(typeof(Player), "LateUpdate")]
    internal static class PlayerLateUpdatePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            MikuPlayerReplacerPlugin.Pump();
        }
    }

    /// <summary>
    /// One place handles every player pain sound: the with-tank, without-tank,
    /// light, maskless and smoke emitters are all started here. Skipping the
    /// method leaves the damage itself (health, red flash, PDA warnings) alone.
    /// </summary>
    [HarmonyPatch(typeof(PlayerDamageSounds), "OnTakeDamage")]
    internal static class PlayerDamageSoundsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !MikuPlayerReplacerPlugin.MutePlayerSoundsActive;
        }
    }

    /// <summary>
    /// Jumping on land plays the character's exertion grunt through
    /// Utils.PlayFMODAsset. That method also emits the landing footstep from
    /// Player.OnJump, so skip only the jump asset itself and leave the rest of
    /// the jump (and every other one-shot in the game) untouched.
    /// </summary>
    [HarmonyPatch(typeof(Utils), "PlayFMODAsset",
        new Type[] { typeof(FMODAsset), typeof(GameObject), typeof(Vector3), typeof(float) })]
    internal static class PlayerJumpSoundPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(FMODAsset asset)
        {
            if (!MikuPlayerReplacerPlugin.MutePlayerSoundsActive || asset == null)
            {
                return true;
            }
            Player player = Player.main;
            return player == null || player.jumpSound == null || asset != player.jumpSound;
        }
    }

    /// <summary>
    /// The underwater mask breathing loop (including the faster low-oxygen
    /// variant, which is the same FMOD event with a parameter change) is driven
    /// entirely from BreathingSound.UpdateSound. When muted, stop the loop and
    /// skip the update; unmuting restarts it on the next quarter-second tick.
    /// </summary>
    [HarmonyPatch(typeof(BreathingSound), "UpdateSound")]
    internal static class BreathingSoundPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(BreathingSound __instance)
        {
            if (!MikuPlayerReplacerPlugin.MutePlayerSoundsActive || __instance == null)
            {
                return true;
            }
            FMOD_StudioEventEmitter loop = __instance.loopingBreathingSound;
            if (loop != null && loop.GetIsPlaying())
            {
                loop.Stop();
            }
            return false;
        }
    }

    /// <summary>
    /// Second person turns the camera around, and the player motors derive their
    /// walking and swimming directions from that same camera transform. Without this
    /// the character moved opposite to the way they face, which also played the swim
    /// animation backwards. The plugin only substitutes a transform while the second
    /// person view is active; every other view uses the game value untouched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "get_forwardReference")]
    internal static class PlayerForwardReferencePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref Transform __result)
        {
            // Reference comparison on purpose: Subnautica destroys the BepInEx manager
            // object after the preloader scene, so the plugin component is Unity-"null"
            // even though it keeps running through this hook. A plain == null here would
            // quietly turn the fix off for the whole session.
            object box = MikuPlayerReplacerPlugin.InstanceBox;
            if (box == null)
            {
                return true;
            }
            Transform probe = ((MikuPlayerReplacerPlugin)box).SecondPersonForward();
            if (probe == null)
            {
                return true;
            }
            __result = probe;
            return false;
        }
    }

    /// <summary>
    /// Movement direction in second person. Patching the property getter above is
    /// not enough: Mono inlines that one-line getter into the motors, so the patch
    /// is installed but never called from there. Both motors are entered through
    /// the virtual <c>PlayerMotor.UpdateMove</c>, which cannot be inlined, so lend
    /// them the unflipped camera rotation for the duration of the call instead.
    /// </summary>
    [HarmonyPatch(typeof(GroundMotor), "UpdateMove")]
    internal static class GroundMotorSecondPersonPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            MikuPlayerReplacerPlugin.BeginSecondPersonSwap();
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            MikuPlayerReplacerPlugin.EndSecondPersonSwap();
        }
    }

    [HarmonyPatch(typeof(UnderwaterMotor), "UpdateMove")]
    internal static class UnderwaterMotorSecondPersonPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            MikuPlayerReplacerPlugin.BeginSecondPersonSwap();
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            MikuPlayerReplacerPlugin.EndSecondPersonSwap();
        }
    }

    /// <summary>
    /// The swim animation reads the camera transform directly (through
    /// <c>camRoot.GetAimingTransform()</c>) to turn the player's velocity into
    /// move_speed_z, so with the camera turned around it played the stroke forward
    /// while the body moved backwards. Same borrow as the motors; Unity calls
    /// Update itself, so the hook cannot be inlined away. The shoulder view borrows
    /// the same transform for a different reason: the stroke has to follow the
    /// detached heading rather than the camera, or every backward key stroke plays
    /// reversed. Both borrows restore the rotation before Update returns.
    /// </summary>
    [HarmonyPatch(typeof(ArmsController), "Update")]
    internal static class ArmsControllerSecondPersonPatch
    {
        private static FieldInfo _animatorField;
        private static bool _animatorFieldLooked;
        private static float _suppressLogAt;
        private static ArmsController _cachedArms;
        private static Animator _cachedAnimator;
        private static readonly int ViewTurnId = Animator.StringToHash("view_turn");
        private static readonly int ViewPitchId = Animator.StringToHash("view_pitch");
        private static readonly int MoveSpeedId = Animator.StringToHash("move_speed");
        private static readonly int MoveXId = Animator.StringToHash("move_speed_x");
        private static readonly int MoveYId = Animator.StringToHash("move_speed_y");
        private static readonly int MoveZId = Animator.StringToHash("move_speed_z");

        [HarmonyPrefix]
        private static void Prefix()
        {
            MikuPlayerReplacerPlugin.BeginSecondPersonSwap();
            MikuPlayerReplacerPlugin.BeginSwimFacingSwap();
        }

        [HarmonyPostfix]
        private static void Postfix(ArmsController __instance)
        {
            MikuPlayerReplacerPlugin.EndSwimFacingSwap();
            MikuPlayerReplacerPlugin.EndSecondPersonSwap();
            SuppressViewAnimation(__instance);
        }

        /// <summary>
        /// The arms controller feeds the camera's yaw rate (view_turn) and pitch
        /// (view_pitch) to the player animator. The retarget copies that skeleton,
        /// so orbiting the camera while standing still used to swing the model's
        /// spine and, through it, the simulated hair, skirt and accessories. While
        /// the player is idle, empty-handed, in the third-person views (or a
        /// cinematic is playing), scale both parameters down after the game has
        /// written them. The game's own damped writes resume as soon as the pose
        /// is live again, so moving blends back in.
        /// </summary>
        private static void SuppressViewAnimation(ArmsController arms)
        {
            try
            {
                float weight = MikuPlayerReplacerPlugin.ViewAnimSuppression();
                if (weight <= 0.001f || arms == null)
                {
                    return;
                }
                if (!_animatorFieldLooked)
                {
                    _animatorFieldLooked = true;
                    _animatorField = typeof(ArmsController).GetField("animator",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (_cachedArms != arms || _cachedAnimator == null)
                {
                    _cachedArms = arms;
                    _cachedAnimator = _animatorField != null
                        ? _animatorField.GetValue(arms) as Animator : null;
                }
                Animator animator = _cachedAnimator;
                if (animator == null || !animator.isActiveAndEnabled)
                {
                    return;
                }
                // Raw camera/velocity inputs the game wrote this frame, before
                // scaling. Kept for the throttle log and reused for the writes.
                float rawTurn = animator.GetFloat(ViewTurnId);
                float rawPitch = animator.GetFloat(ViewPitchId);
                float rawSpeed = animator.GetFloat(MoveSpeedId);
                float rawX = animator.GetFloat(MoveXId);
                float rawY = animator.GetFloat(MoveYId);
                float rawZ = animator.GetFloat(MoveZId);
                // What the camera was doing to the pose while the player stands
                // still. If "view_turn" and "move_speed_x/z" both move with the
                // mouse here, the suppression is what keeps the model still.
                if (MikuPlayerReplacerPlugin.VerboseLoggingEnabled()
                    && weight > 0.99f && Time.unscaledTime >= _suppressLogAt)
                {
                    _suppressLogAt = Time.unscaledTime + 2f;
                    MikuPlayerReplacerPlugin.LogFromPatch("Idle anim raw turn="
                        + rawTurn.ToString("F2") + " pitch=" + rawPitch.ToString("F2")
                        + " speed=" + rawSpeed.ToString("F2")
                        + " x=" + rawX.ToString("F2") + " y=" + rawY.ToString("F2")
                        + " z=" + rawZ.ToString("F2"));
                }
                // Scale the game's own values instead of hard-zeroing them, so
                // the fade in and out blends the arms and head back and forth.
                float keep = 1f - Mathf.Clamp01(weight);
                animator.SetFloat(ViewTurnId, rawTurn * keep);
                animator.SetFloat(ViewPitchId, rawPitch * keep);
                // The float idle still has a little velocity (buoyancy, the swim
                // bob, currents), and SetPlayerSpeedParameters feeds it to the
                // swim blend tree in *camera* space (GetRelativeVelocity uses
                // camRoot). Orbiting the camera therefore rotates that vector:
                // move_speed_x/z swing with the mouse and the animator leans the
                // spine, which is what kept the hair, skirt and tie swaying while
                // the head was already frozen. With no input and no travel the
                // pose should not react to the camera at all, so these go too.
                animator.SetFloat(MoveSpeedId, rawSpeed * keep);
                animator.SetFloat(MoveXId, rawX * keep);
                animator.SetFloat(MoveYId, rawY * keep);
                animator.SetFloat(MoveZId, rawZ * keep);
            }
            catch (Exception)
            {
                // Never break the game's arms update over a cosmetic correction.
            }
        }
    }
}
