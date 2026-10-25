using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MikuPlayerReplacer
{
    // ----------------------------------------------------------------------
    // PMX 2.0 / 2.1 reader
    //
    // Only what the runtime model needs is parsed: header, vertices, faces,
    // textures, materials and bones. Morphs, display frames, rigid bodies and
    // joints live after the bone block, so parsing stops there.
    // ----------------------------------------------------------------------

    internal sealed class PmxBone
    {
        public string Name;
        public string EnglishName;
        public int Parent = -1;
        public Vector3 Position;
        public bool Ik;
        // PMX "inherit rotation" (付与親). Twist helpers (腕捩1/2/3,
        // 手首捩1/2/3) are separate children of the segment bone, not
        // children of the main twist bone, and copy a fraction of its
        // rotation. Without this the twist gradient the model was
        // skinned for is lost and the whole roll bunches at the joint.
        public bool InheritsRotation;
        public int InheritParent = -1;
        public float InheritRatio;
    }

    internal sealed class PmxMaterial
    {
        public string Name;
        public string Texture;
        public bool NoCull;
        public float Alpha = 1f;
        public int SurfaceCount;
    }

    internal sealed class PmxData
    {
        public string Name;
        public string Comment;
        public readonly List<PmxBone> Bones = new List<PmxBone>();
        public readonly List<PmxMaterial> Materials = new List<PmxMaterial>();
        public readonly List<string> Textures = new List<string>();
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] Uvs;
        public int[][] VertexBones;
        public float[][] VertexWeights;
        public int[] Indices;
    }

    // PMX texture references are relative to the model file, but exporters
    // disagree on separators, case, leading "./" and whether they write an
    // absolute path. Normalise once so lookups are predictable.
    internal static class TexturePaths
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }
            string s = path.Trim().Trim('"').Replace('\\', '/');
            if (s.Length > 1 && s[1] == ':')
            {
                s = s.Substring(2);
            }
            s = s.TrimStart('/');
            string[] parts = s.Split('/');
            List<string> stack = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length == 0 || p == ".")
                {
                    continue;
                }
                if (p == "..")
                {
                    if (stack.Count == 0)
                    {
                        // Escapes the model folder; only the name is usable.
                        return BaseName(s);
                    }
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(p);
            }
            return stack.Count == 0 ? "" : string.Join("/", stack.ToArray());
        }

        public static string BaseName(string normalized)
        {
            int slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }
    }

    internal static class PmxReader
    {
        public static PmxData Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream ms = new MemoryStream(bytes, false))
            using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
            {
                string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (magic != "PMX ")
                {
                    throw new InvalidDataException("Not a PMX file: " + magic);
                }

                float version = br.ReadSingle();
                if (version < 2.0f || version > 2.2f)
                {
                    throw new InvalidDataException("Unsupported PMX version " + version);
                }

                int globals = br.ReadByte();
                if (globals < 8)
                {
                    throw new InvalidDataException("Truncated PMX header");
                }

                byte encoding = br.ReadByte();
                bool utf8 = encoding == 1;
                int addUv = br.ReadByte();
                int vertexIndexSize = br.ReadByte();
                int textureIndexSize = br.ReadByte();
                int materialIndexSize = br.ReadByte();
                int boneIndexSize = br.ReadByte();
                br.ReadByte(); // morph index size
                br.ReadByte(); // rigid body index size
                // PMX 2.1 adds a few more global bytes; they are unused here.

                PmxData data = new PmxData();
                data.Name = ReadText(br, utf8);
                data.Comment = ReadText(br, utf8);
                ReadText(br, utf8); // english name
                ReadText(br, utf8); // english comment

                // ---- vertices
                int vertexCount = br.ReadInt32();
                if (vertexCount < 0 || vertexCount > 4000000)
                {
                    throw new InvalidDataException("Bad PMX vertex count " + vertexCount);
                }

                data.Positions = new Vector3[vertexCount];
                data.Normals = new Vector3[vertexCount];
                data.Uvs = new Vector2[vertexCount];
                data.VertexBones = new int[vertexCount][];
                data.VertexWeights = new float[vertexCount][];

                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    Vector3 nrm = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    Vector2 uv = new Vector2(br.ReadSingle(), br.ReadSingle());
                    data.Positions[i] = pos;
                    data.Normals[i] = nrm;
                    data.Uvs[i] = uv;

                    if (addUv > 0)
                    {
                        br.ReadBytes(addUv * 16);
                    }

                    int[] bi = new int[4];
                    float[] bw = new float[4];
                    byte weightType = br.ReadByte();
                    switch (weightType)
                    {
                        case 0: // BDEF1
                            bi[0] = ReadIndex(br, boneIndexSize);
                            bw[0] = 1f;
                            break;
                        case 1: // BDEF2
                            bi[0] = ReadIndex(br, boneIndexSize);
                            bi[1] = ReadIndex(br, boneIndexSize);
                            bw[0] = br.ReadSingle();
                            bw[1] = 1f - bw[0];
                            break;
                        case 2: // BDEF4
                            bi[0] = ReadIndex(br, boneIndexSize);
                            bi[1] = ReadIndex(br, boneIndexSize);
                            bi[2] = ReadIndex(br, boneIndexSize);
                            bi[3] = ReadIndex(br, boneIndexSize);
                            bw[0] = br.ReadSingle();
                            bw[1] = br.ReadSingle();
                            bw[2] = br.ReadSingle();
                            bw[3] = br.ReadSingle();
                            break;
                        case 3: // SDEF: treat as BDEF2, the two correction spheres are unused
                            bi[0] = ReadIndex(br, boneIndexSize);
                            bi[1] = ReadIndex(br, boneIndexSize);
                            bw[0] = br.ReadSingle();
                            bw[1] = 1f - bw[0];
                            br.ReadBytes(36); // C, R0, R1
                            break;
                        case 4: // QDEF
                            bi[0] = ReadIndex(br, boneIndexSize);
                            bi[1] = ReadIndex(br, boneIndexSize);
                            bi[2] = ReadIndex(br, boneIndexSize);
                            bi[3] = ReadIndex(br, boneIndexSize);
                            bw[0] = br.ReadSingle();
                            bw[1] = br.ReadSingle();
                            bw[2] = br.ReadSingle();
                            bw[3] = br.ReadSingle();
                            break;
                        default:
                            throw new InvalidDataException("Unknown PMX weight type " + weightType);
                    }
                    br.ReadSingle(); // edge scale

                    float sum = bw[0] + bw[1] + bw[2] + bw[3];
                    if (sum > 1e-6f && Math.Abs(sum - 1f) > 1e-4f)
                    {
                        bw[0] /= sum;
                        bw[1] /= sum;
                        bw[2] /= sum;
                        bw[3] /= sum;
                    }
                    data.VertexBones[i] = bi;
                    data.VertexWeights[i] = bw;
                }

                // ---- faces
                int indexCount = br.ReadInt32();
                if (indexCount < 0 || indexCount > 12000000)
                {
                    throw new InvalidDataException("Bad PMX face count " + indexCount);
                }
                data.Indices = new int[indexCount];
                for (int i = 0; i < indexCount; i++)
                {
                    // Vertex indices are unsigned in PMX (a 2-byte index can address
                    // up to 65535 vertices); the other index kinds are signed.
                    data.Indices[i] = ReadVertexIndex(br, vertexIndexSize);
                }

                // ---- textures
                int textureCount = br.ReadInt32();
                for (int i = 0; i < textureCount; i++)
                {
                    data.Textures.Add(ReadText(br, utf8));
                }

                // ---- materials
                int materialCount = br.ReadInt32();
                for (int i = 0; i < materialCount; i++)
                {
                    PmxMaterial m = new PmxMaterial();
                    m.Name = ReadText(br, utf8);
                    ReadText(br, utf8); // english name
                    br.ReadBytes(16 + 12 + 4 + 12); // diffuse, specular, strength, ambient
                    byte flags = br.ReadByte();
                    m.NoCull = (flags & 1) != 0;
                    br.ReadBytes(16 + 4); // edge colour, edge size
                    int textureIndex = ReadIndex(br, textureIndexSize);
                    ReadIndex(br, textureIndexSize); // sphere map
                    br.ReadByte(); // sphere mode
                    byte toonFlag = br.ReadByte();
                    if (toonFlag == 0)
                    {
                        ReadIndex(br, textureIndexSize);
                    }
                    else
                    {
                        br.ReadByte();
                    }
                    ReadText(br, utf8); // memo
                    m.SurfaceCount = br.ReadInt32();
                    if (textureIndex >= 0 && textureIndex < data.Textures.Count)
                    {
                        m.Texture = data.Textures[textureIndex];
                    }
                    data.Materials.Add(m);
                }

                // ---- bones (stop here)
                int boneCount = br.ReadInt32();
                for (int i = 0; i < boneCount; i++)
                {
                    PmxBone b = new PmxBone();
                    b.Name = ReadText(br, utf8);
                    b.EnglishName = ReadText(br, utf8);
                    b.Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    b.Parent = ReadIndex(br, boneIndexSize);
                    br.ReadInt32(); // deform layer
                    ushort flags = br.ReadUInt16();
                    b.Ik = (flags & 0x0020) != 0;

                    if ((flags & 0x0001) != 0)
                    {
                        ReadIndex(br, boneIndexSize); // tail bone
                    }
                    else
                    {
                        br.ReadBytes(12); // tail offset
                    }
                    b.InheritsRotation = (flags & 0x0100) != 0;
                    if ((flags & (0x0100 | 0x0200)) != 0)
                    {
                        b.InheritParent = ReadIndex(br, boneIndexSize);
                        b.InheritRatio = br.ReadSingle();
                    }
                    if ((flags & 0x0400) != 0)
                    {
                        br.ReadBytes(12); // fixed axis
                    }
                    if ((flags & 0x0800) != 0)
                    {
                        br.ReadBytes(24); // local axis X/Z
                    }
                    if ((flags & 0x2000) != 0)
                    {
                        br.ReadBytes(4); // external parent key
                    }
                    if (b.Ik)
                    {
                        ReadIndex(br, boneIndexSize); // target
                        br.ReadInt32();               // loop count
                        br.ReadSingle();              // limit angle
                        int links = br.ReadInt32();
                        for (int k = 0; k < links; k++)
                        {
                            ReadIndex(br, boneIndexSize);
                            if (br.ReadByte() != 0)
                            {
                                br.ReadBytes(24); // limit min/max
                            }
                        }
                    }
                    data.Bones.Add(b);
                }

                return data;
            }
        }

        private static string ReadText(BinaryReader br, bool utf8)
        {
            int length = br.ReadInt32();
            if (length < 0 || length > 16 * 1024 * 1024)
            {
                throw new InvalidDataException("Bad PMX string length " + length);
            }
            byte[] bytes = br.ReadBytes(length);
            return utf8 ? Encoding.UTF8.GetString(bytes) : Encoding.Unicode.GetString(bytes);
        }

        private static int ReadVertexIndex(BinaryReader br, int size)
        {
            switch (size)
            {
                case 1:
                    return br.ReadByte();
                case 2:
                    return br.ReadUInt16();
                case 4:
                    long v = br.ReadUInt32();
                    return v > int.MaxValue ? int.MaxValue : (int)v;
                default:
                    throw new InvalidDataException("Bad PMX index size " + size);
            }
        }

        private static int ReadIndex(BinaryReader br, int size)
        {
            switch (size)
            {
                case 1:
                    return br.ReadSByte();
                case 2:
                    return br.ReadInt16();
                case 4:
                    return br.ReadInt32();
                default:
                    throw new InvalidDataException("Bad PMX index size " + size);
            }
        }
    }

    // ----------------------------------------------------------------------
    // PMX -> ModelData
    //
    // MMD model space is mapped to the game's player_view space with a uniform
    // 0.08 scale and a 180 degree turn about Y:
    //
    //     game = (-0.08 x, 0.08 y, -0.08 z)
    //
    // This is the same mapping the offline Blender conversion uses (verified
    // bone for bone against the shipped miku_model.bin: 18 core bones, 0.0000 m
    // residual). It is a pure rotation, so triangle winding is unchanged.
    // ----------------------------------------------------------------------

    internal static class PmxConverter
    {
        public const float UnitScale = 0.08f;

        private static Dictionary<string, string> _nameMap;

        private static readonly string[,] FingerMap =
        {
            { "親指０", "thumb_base" },
            { "親指１", "thumb_mid" },
            { "親指２", "thumb_tip" },
            { "人指１", "point_base" },
            { "人指２", "point_mid" },
            { "人指３", "point_tip" },
            { "中指１", "midl_base" },
            { "中指２", "midl_mid" },
            { "中指３", "midl_tip" },
            { "薬指１", "ring_base" },
            { "薬指２", "ring_mid" },
            { "薬指３", "ring_tip" },
            { "小指１", "pinky_base" },
            { "小指２", "pinky_mid" },
            { "小指３", "pinky_tip" },
        };

        private static Dictionary<string, string> NameMap
        {
            get
            {
                if (_nameMap != null)
                {
                    return _nameMap;
                }
                Dictionary<string, string> map =
                    new Dictionary<string, string>(StringComparer.Ordinal);
                map["全ての親"] = "miku_root";
                map["操作中心"] = "miku_center";
                map["センター"] = "miku_center_ctrl";
                map["グルーブ"] = "miku_groove";
                map["腰"] = "hips";
                map["下半身"] = "lower_body";
                map["上半身"] = "spine_1";
                map["上半身2"] = "chest";
                map["首"] = "neck";
                map["頭"] = "head_rig";

                string[] core = { "肩P.", "肩.", "肩C.", "腕.", "腕捩.", "ひじ.", "手首.",
                                  "足.", "ひざ.", "足首.", "つま先." };
                for (int s = 0; s < 2; s++)
                {
                    string side = s == 0 ? "L" : "R";
                    map["肩P." + side] = "clav_P_" + side;
                    map["肩." + side] = "clav_" + side;
                    map["肩C." + side] = "clav_C_" + side;
                    map["腕." + side] = "shoulder_" + side;
                    map["腕捩." + side] = "arm_twist_" + side;
                    map["ひじ." + side] = "elbow_" + side;
                    map["手首." + side] = "hand_" + side;
                    map["足." + side] = "thigh_" + side;
                    map["ひざ." + side] = "calf_" + side;
                    map["足首." + side] = "ankle_" + side;
                    map["つま先." + side] = side == "L" ? "toe_L_rig" : "toe_R";
                    map["足先." + side] = side == "L" ? "toe_L_rig" : "toe_R";

                    for (int f = 0; f < FingerMap.GetLength(0); f++)
                    {
                        string en = FingerMap[f, 1];
                        // The right-hand tip bones carry a _rig suffix in the game rig.
                        string suffix = side == "R" && en.EndsWith("_tip") ? "_rig" : "";
                        map[FingerMap[f, 0] + "." + side] =
                            "hand_" + side + "_" + en + suffix;
                    }
                }
                _nameMap = map;
                return map;
            }
        }

        /// <summary>Normalise both MMD naming styles to "bone.L" / "bone.R".</summary>
        public static string Canon(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }
            name = name.Replace("．", ".").Trim();
            if (name.EndsWith(".L") || name.EndsWith(".R"))
            {
                return name;
            }
            if (name.Length > 1 && name[0] == '左')
            {
                return name.Substring(1) + ".L";
            }
            if (name.Length > 1 && name[0] == '右')
            {
                return name.Substring(1) + ".R";
            }
            return name;
        }

        private static string MapBone(string name)
        {
            bool viaD;
            return MapBone(name, out viaD);
        }

        /// <summary>
        /// Maps a PMX bone name onto the game's bone name. <paramref name="viaD"/> is
        /// set when the match only worked after dropping the "D" of a MMD deform copy
        /// (左足D, ひざD.L ...). Those copies stand at the same spot as the real bone,
        /// so they must be folded into it instead of becoming a second target bone.
        /// </summary>
        private static string MapBone(string name, out bool viaD)
        {
            viaD = false;
            string canon = Canon(name);
            string mapped;
            if (NameMap.TryGetValue(canon, out mapped))
            {
                return mapped;
            }
            string stripped = StripDeformSuffix(canon);
            if (stripped != null && NameMap.TryGetValue(stripped, out mapped))
            {
                viaD = true;
                return mapped;
            }
            return null;
        }

        /// <summary>
        /// Standard MMD rigs hide helper bones that must follow a regular limb bone:
        /// the "W" copies (腕W / ひじW) are IK-driven duplicates of the upper arm and
        /// forearm, and ひじ補助(+)/ひじ補助1 is the elbow crease helper. They are not
        /// in NameMap (they must not claim the game bone name), but their weights have
        /// to land on the limb bone — folding them into the nearest ancestor instead
        /// leaves the upper arm on the clavicle and the elbow crease on the upper arm,
        /// which is what makes imported arms collapse when they move.
        /// </summary>
        private static string FoldTarget(string name)
        {
            string canon = Canon(name);
            string side = "";
            string stem = canon;
            if (canon.EndsWith(".L") || canon.EndsWith(".R"))
            {
                side = canon.Substring(canon.Length - 2);
                stem = canon.Substring(0, canon.Length - 2);
            }
            string mapped;
            if (stem.StartsWith("ひじ補助") &&
                NameMap.TryGetValue("ひじ" + side, out mapped))
            {
                return mapped;
            }
            // Only the arm/neck copies seen in standard rigs; a bone that merely
            // happens to end in W must not be pulled onto an unrelated limb.
            if (stem.Length > 1 && stem.EndsWith("W"))
            {
                string baseName = stem.Substring(0, stem.Length - 1);
                // Sour's cuff helper 手W is rooted along the forearm. It is
                // not the wrist joint: folding to the shoulder separates the
                // sleeve, and pinning to the hand adds the full wrist bend.
                if (baseName == "手" && NameMap.TryGetValue("ひじ" + side, out mapped))
                    return mapped;
                if ((baseName == "腕" || baseName == "ひじ" || baseName == "手首" ||
                     baseName == "首") &&
                    NameMap.TryGetValue(baseName + side, out mapped))
                {
                    return mapped;
                }
            }
            return null;
        }

        private static string StripDeformSuffix(string canon)
        {
            if (canon.Length >= 3 &&
                (canon.EndsWith(".L") || canon.EndsWith(".R")) &&
                canon[canon.Length - 3] == 'D')
            {
                return canon.Substring(0, canon.Length - 3) + canon.Substring(canon.Length - 2);
            }
            if (canon.Length > 1 && canon.EndsWith("D"))
            {
                return canon.Substring(0, canon.Length - 1);
            }
            // The YYB "NT" rig names its deform copies with a trailing M instead
            // of D: the visible arm skin is weighted to 腕M / ひじM, sitting at
            // the exact position of the animated 腕 / ひじ. Without this they fold
            // into the nearest ancestor (the clavicle), so half of the upper arm
            // follows the shoulder and half follows the chest and the arm tears
            // apart as soon as it moves. Only strip the M when the remainder is a
            // bone this converter knows, so ordinary names ending in M are safe.
            if (canon.Length >= 3 &&
                (canon.EndsWith(".L") || canon.EndsWith(".R")) &&
                canon[canon.Length - 3] == 'M')
            {
                string strippedSide = canon.Substring(0, canon.Length - 3) + canon.Substring(canon.Length - 2);
                if (NameMap.ContainsKey(strippedSide))
                {
                    return strippedSide;
                }
            }
            if (canon.Length > 1 && canon.EndsWith("M"))
            {
                string strippedCenter = canon.Substring(0, canon.Length - 1);
                if (NameMap.ContainsKey(strippedCenter))
                {
                    return strippedCenter;
                }
            }
            return null;
        }

        // Hair is what hangs off the head. 髪 and 髮 are the same kanji in the two
        // Japanese encodings MMD authors use, and models mix them freely (sour暄's
        // rig is 髮親/左髮1..20 while the same file's ornaments say 髪飾り), so both
        // have to be recognised.
        private static readonly string[] HairKeywords =
        {
            "髪", "髮", "ヘア", "hair", "ツインテ", "twin", "テール", "tail", "三つ編",
            "braid", "リボン", "ribbon", "アホ毛", "もみあげ", "ポニー", "ponytail",
            "ウィッグ", "wig"
        };

        // Hair that may drift a little but must never flap: the fringe in front of
        // the face and the sideburn strands beside it. The scalp shell and a short
        // bob have no keyword here on purpose - they stay welded to the head.
        private static readonly string[] LightHairKeywords =
        {
            "前髪", "前髮", "前发", "刘海", "もみあげ", "横髪", "横髮", "側髪", "側髮",
            "サイド", "鬓角", "鬢角", "アホ毛", "ahoge"
        };

        // Long, thin accessories that hang from the body. They are simulated with
        // the same strand solver as the hair, but unlike hair they are not part of
        // the head and must not be hidden with it in first person. Names here are
        // matched as substrings, so "ネクタイ親" and "Tie_0_1" both hit.
        private static readonly string[] ChainKeywords =
        {
            "ネクタイ", "necktie", "tie", "リボン", "ribbon", "マフラー", "muffler",
            "スカーフ", "scarf", "ストール", "stole", "ケープ", "cape", "マント",
            "mantle", "コート", "coat", "ローブ", "robe", "ジャケット", "jacket",
            "上着", "外套", "披风", "斗篷",
            "ネックレス", "necklace", "ペンダント", "pendant", "垂れ", "紐", "ひも",
            "ストラップ", "strap", "吊り", "吊",
            // Skirts and the lower half of long clothes. These bones ship with
            // their own rig in most MMD models (the 10th anniversary Miku has an
            // 18 x 8 skirt lattice), so they are simulated rather than folded
            // rigidly into the hips. "裾" and sleeve hems are deliberately absent:
            // the cuffs must keep the model author's shape.
            "スカート", "skirt", "裙", "ペチコート", "petticoat", "フリル", "frill"
        };

        private static bool ContainsKeyword(string lower, string[] keywords)
        {
            for (int i = 0; i < keywords.Length; i++)
            {
                string key = keywords[i]; // Keywords are normalized constants.
                if (lower.IndexOf(key, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsHairName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return ContainsKeyword(name.ToLowerInvariant(), HairKeywords);
        }

        /// <summary>Fringe or sideburn hair: it is simulated with a much stiffer,
        /// shorter leash than a tail, so it drifts instead of flipping around.</summary>
        public static bool IsLightHairName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return ContainsKeyword(name.ToLowerInvariant(), LightHairKeywords);
        }

        private static readonly string[] SkirtKeywords =
        {
            "スカート", "skirt", "裙", "ペチコート", "petticoat", "フリル", "frill"
        };

        private static readonly string[] ClothesKeywords =
        {
            "ケープ", "cape", "マント", "mantle", "コート", "coat", "ローブ", "robe",
            "ジャケット", "jacket", "上着", "外套", "披风", "斗篷"
        };

        /// <summary>A skirt or petticoat panel chain. These swing like the hair, but
        /// they are driven by the SkirtPhysics switch and collide with the legs.</summary>
        public static bool IsSkirtName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return ContainsKeyword(name.ToLowerInvariant(), SkirtKeywords);
        }

        /// <summary>A cape, coat, mantle or robe chain: cloth like the skirt, but it
        /// hangs from the shoulders and is driven by the ClothesPhysics switch. Call
        /// this only after IsSkirtName, because "petticoat" also contains "coat".</summary>
        public static bool IsClothesName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return ContainsKeyword(name.ToLowerInvariant(), ClothesKeywords);
        }

        /// <summary>Hair or a hanging accessory such as a necktie. These bones are
        /// kept for the strand simulation; hair alone is also head geometry.</summary>
        public static bool IsPhysicsChainName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            if (IsHairName(lower) || ContainsKeyword(lower, ChainKeywords))
            {
                return true;
            }
            // YYB's 10th anniversary Miku names its necktie chain D1..D9. A bare
            // D plus digits is never a body bone, so it is safe to treat as cloth.
            if (lower.Length > 1 && lower[0] == 'd')
            {
                for (int i = 1; i < lower.Length; i++)
                {
                    if (lower[i] < '0' || lower[i] > '9')
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>MMD twist helpers: 腕捩1/2/3 and 手捩1/2/3 (optionally
        /// prefixed with 左/右). They are siblings of the main twist bone and
        /// carry its rotation at 25/50/75%, which is how the limb skin twists in
        /// a gradient. They are unmapped, so without this they would be folded
        /// away and the plug-in could only rotate one joint - the whole forearm
        /// or upper arm would turn at once.</summary>
        public static bool IsTwistHelperName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            int at = name.IndexOf("捩", StringComparison.Ordinal);
            if (at <= 0 || at + 1 >= name.Length)
            {
                return false;
            }
            char limb = name[at - 1];
            if (limb != '腕' && limb != '手' && limb != '首')
            {
                return false;
            }
            for (int i = at + 1; i < name.Length; i++)
            {
                if (name[i] < '0' || name[i] > '9')
                {
                    return false;
                }
            }
            return true;
        }

        private static void MarkAncestors(PmxData src, bool[] keep)
        {
            int n = keep.Length;
            for (int i = 0; i < n; i++)
            {
                if (!keep[i])
                {
                    continue;
                }
                int p = src.Bones[i].Parent;
                int guard = 0;
                while (p >= 0 && p < n && !keep[p] && guard++ < n)
                {
                    keep[p] = true;
                    p = src.Bones[p].Parent;
                }
            }
        }

        private static Vector3 ToGame(Vector3 p)
        {
            return new Vector3(-p.x * UnitScale, p.y * UnitScale, -p.z * UnitScale);
        }

        /// <summary>Rotate a direction into game space (scale-free).</summary>
        public static Vector3 DirToGame(Vector3 p)
        {
            return new Vector3(-p.x, p.y, -p.z);
        }

        public static ModelData Convert(PmxData src, string modelName)
        {
            if (src == null || src.Bones.Count == 0)
            {
                throw new InvalidDataException("PMX has no bones");
            }

            int boneCount = src.Bones.Count;
            string[] mapped = new string[boneCount];
            bool[] deformCopy = new bool[boneCount];
            Dictionary<string, List<int>> claims = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < boneCount; i++)
            {
                bool viaD;
                string game = MapBone(src.Bones[i].Name, out viaD);
                deformCopy[i] = viaD;
                mapped[i] = game;
                if (game != null)
                {
                    List<int> list;
                    if (!claims.TryGetValue(game, out list))
                    {
                        list = new List<int>();
                        claims[game] = list;
                    }
                    list.Add(i);
                }
            }

            // MMD rigs often skin the legs to "D" deform copies (左足D, 左ひざD ...)
            // that stand at exactly the same position as the standard bone and copy its
            // motion. Only the standard bone is driven by the game, so fold the copy
            // into it; otherwise the mesh would stay put while 左足 rotates.
            int[] keeper = new int[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                keeper[i] = i;
            }
            foreach (KeyValuePair<string, List<int>> claim in claims)
            {
                if (claim.Value.Count < 2)
                {
                    continue;
                }
                int best = claim.Value[0];
                for (int k = 0; k < claim.Value.Count; k++)
                {
                    if (!deformCopy[claim.Value[k]])
                    {
                        best = claim.Value[k];
                        break;
                    }
                }
                for (int k = 0; k < claim.Value.Count; k++)
                {
                    int index = claim.Value[k];
                    keeper[index] = best;
                    if (index != best)
                    {
                        mapped[index] = null;
                    }
                }
            }

            // Only the bones the game rig drives are kept, plus the hair and
            // hanging-accessory chains that get simulated. MMD rigs carry hundreds
            // of helper, physics and morph bones; a SkinnedMeshRenderer cannot skin
            // that many. Every other bone is folded into its nearest kept ancestor:
            // at rest the two are identical, so the mesh is unchanged, and none of
            // them are animated anyway.
            bool[] keep = new bool[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                if (mapped[i] != null || (!src.Bones[i].Ik && IsPhysicsChainName(src.Bones[i].Name))
                    || IsTwistHelperName(src.Bones[i].Name))
                {
                    keep[i] = true;
                }
            }
            MarkAncestors(src, keep);

            // A weighted bone whose whole chain is unmapped (no standard names at
            // all) still has to have somewhere to hang: keep the top of that chain.
            bool[] weighted = new bool[boneCount];
            bool anyWeighted = false;
            for (int i = 0; i < src.VertexBones.Length && !anyWeighted; i++)
            {
                int[] bi = src.VertexBones[i];
                float[] bw = src.VertexWeights[i];
                for (int k = 0; k < 4; k++)
                {
                    if (bw[k] > 1e-4f && bi[k] >= 0 && bi[k] < boneCount)
                    {
                        weighted[bi[k]] = true;
                        anyWeighted = true;
                    }
                }
            }
            if (anyWeighted)
            {
                bool changed = false;
                for (int i = 0; i < boneCount; i++)
                {
                    if (!weighted[i] || keep[i])
                    {
                        continue;
                    }
                    int top = i;
                    int p = src.Bones[i].Parent;
                    int guard = 0;
                    while (p >= 0 && p < boneCount && guard++ < boneCount)
                    {
                        top = p;
                        p = src.Bones[p].Parent;
                    }
                    if (!keep[top])
                    {
                        keep[top] = true;
                        changed = true;
                    }
                }
                if (changed)
                {
                    MarkAncestors(src, keep);
                }
            }

            int[] newIndex = new int[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                newIndex[i] = -1;
            }
            List<int> order = new List<int>();
            // Depth-first from the roots keeps every parent before its children.
            int[] childCount = new int[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                int p = src.Bones[i].Parent;
                if (p >= 0 && p < boneCount)
                {
                    childCount[p]++;
                }
            }
            int[] childCursor = new int[boneCount];
            int[][] children = new int[boneCount][];
            for (int i = 0; i < boneCount; i++)
            {
                children[i] = childCount[i] > 0 ? new int[childCount[i]] : null;
            }
            for (int i = 0; i < boneCount; i++)
            {
                int p = src.Bones[i].Parent;
                if (p >= 0 && p < boneCount && children[p] != null)
                {
                    children[p][childCursor[p]++] = i;
                }
            }

            Stack<int> stack = new Stack<int>();
            for (int i = boneCount - 1; i >= 0; i--)
            {
                int p = src.Bones[i].Parent;
                if (keep[i] && (p < 0 || p >= boneCount || !keep[p]))
                {
                    stack.Push(i);
                }
            }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (newIndex[i] >= 0)
                {
                    continue;
                }
                newIndex[i] = order.Count;
                order.Add(i);
                int[] kids = children[i];
                if (kids != null)
                {
                    for (int k = kids.Length - 1; k >= 0; k--)
                    {
                        if (keep[kids[k]] && newIndex[kids[k]] < 0)
                        {
                            stack.Push(kids[k]);
                        }
                    }
                }
            }

            Dictionary<string, int> keptByName =
                new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < boneCount; i++)
            {
                if (mapped[i] != null && newIndex[i] >= 0)
                {
                    keptByName[mapped[i]] = newIndex[i];
                }
            }

            // Dropped bones are folded into their nearest kept ancestor. Duplicate
            // claims (the "D" copies) start at the bone that owns the game name.
            // MMD helper bones override that with the limb bone they belong to.
            int[] resolve = new int[boneCount];
            string[] foldTargets = new string[boneCount];
            for (int i = 0; i < boneCount; i++) foldTargets[i] = FoldTarget(src.Bones[i].Name);
            for (int i = 0; i < boneCount; i++)
            {
                string fold = foldTargets[i];
                int foldIndex;
                if (fold != null && keptByName.TryGetValue(fold, out foldIndex))
                {
                    resolve[i] = foldIndex;
                    continue;
                }
                int p = keeper[i];
                int guard = 0;
                bool resolvedAlias = false;
                while (p >= 0 && p < boneCount && guard++ <= boneCount)
                {
                    p = keeper[p];
                    if (p < 0 || p >= boneCount)
                    {
                        break;
                    }
                    // Apply a helper alias while walking ancestors too. A sleeve
                    // below 手W/ひじW must follow that limb, not skip all helpers
                    // and land on the first retained shoulder bone.
                    string ancestorFold = foldTargets[p];
                    if (ancestorFold != null && keptByName.TryGetValue(ancestorFold, out foldIndex))
                    {
                        resolve[i] = foldIndex;
                        resolvedAlias = true;
                        break;
                    }
                    if (keep[p]) break;
                    p = src.Bones[p].Parent;
                }
                if (!resolvedAlias) resolve[i] = p >= 0 && p < boneCount ? newIndex[p] : -1;
            }

            ModelData model = new ModelData();
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            for (int n = 0; n < order.Count; n++)
            {
                int i = order[n];
                PmxBone pb = src.Bones[i];
                BoneData b = new BoneData();

                string boneName = mapped[i];
                if (string.IsNullOrEmpty(boneName))
                {
                    // Unmapped bones keep their PMX name, which the hair scan and
                    // the diagnostics rely on.
                    boneName = string.IsNullOrEmpty(pb.Name) ? "pmx_bone_" + i : pb.Name;
                }
                if (!names.Add(boneName))
                {
                    string baseName = boneName;
                    int suffix = 2;
                    while (!names.Add(boneName = baseName + "_" + suffix))
                    {
                        suffix++;
                    }
                }
                b.Name = boneName;
                b.IkControl = pb.Ik;

                int newParent = -1;
                int p = pb.Parent;
                if (p >= 0 && p < boneCount && newIndex[p] >= 0)
                {
                    newParent = newIndex[p];
                }
                b.Parent = newParent;
                if (pb.InheritsRotation && pb.InheritParent >= 0 &&
                    pb.InheritParent < boneCount && newIndex[pb.InheritParent] >= 0)
                {
                    b.InheritsRotation = true;
                    b.InheritParent = newIndex[pb.InheritParent];
                    b.InheritRatio = pb.InheritRatio;
                }

                Vector3 world = ToGame(pb.Position);
                Vector3 parentWorld = newParent >= 0
                    ? ToGame(src.Bones[p].Position)
                    : Vector3.zero;
                b.LocalPos = world - parentWorld;
                b.LocalRot = Quaternion.identity;
                model.Bones.Add(b);
            }

            // ---- materials
            for (int i = 0; i < src.Materials.Count; i++)
            {
                PmxMaterial pm = src.Materials[i];
                MaterialData m = new MaterialData();
                m.Name = pm.Name;
                // Keep the directory part: tex/hair.png and spa/hair.png are
                // different files and MMD resolves them relative to the model.
                m.Texture = TexturePaths.Normalize(pm.Texture);
                m.Cull = pm.NoCull ? 0 : 2;
                model.Materials.Add(m);
            }

            // ---- face -> material assignment
            // PMX stores the per-material index counts, and faces are usually in
            // material order; trust the counts when they add up, otherwise fall
            // back to the flat face list with material 0.
            int triangleCount = src.Indices.Length / 3;
            int[] triangleMaterial = new int[triangleCount];
            int cursor = 0;
            bool countsOk = true;
            int total = 0;
            for (int i = 0; i < src.Materials.Count; i++)
            {
                total += src.Materials[i].SurfaceCount;
            }
            if (total != src.Indices.Length)
            {
                countsOk = false;
            }
            if (countsOk)
            {
                int t = 0;
                for (int mi = 0; mi < src.Materials.Count; mi++)
                {
                    int faces = src.Materials[mi].SurfaceCount / 3;
                    for (int k = 0; k < faces && t < triangleCount; k++)
                    {
                        triangleMaterial[t++] = mi;
                    }
                }
                cursor = t;
            }
            for (; cursor < triangleCount; cursor++)
            {
                triangleMaterial[cursor] = 0;
            }

            // ---- head / body split
            int headPmxBone = -1;
            for (int i = 0; i < boneCount; i++)
            {
                if (string.Equals(mapped[i], "head_rig", StringComparison.Ordinal))
                {
                    headPmxBone = i;
                    break;
                }
            }

            bool[] headBones = new bool[boneCount];
            if (headPmxBone >= 0)
            {
                for (int i = 0; i < boneCount; i++)
                {
                    // Face bones (eyes, jaw, ...) belong to the head mesh. Imported
                    // hair goes there too: a fringe hanging in front of the face would
                    // otherwise stay visible (and block the camera) in first person.
                    if (IsHairName(src.Bones[i].Name))
                    {
                        headBones[i] = true;
                        continue;
                    }
                    int p = i;
                    int guard = 0;
                    while (p >= 0 && p < boneCount && guard++ <= boneCount)
                    {
                        if (p == headPmxBone)
                        {
                            headBones[i] = true;
                            break;
                        }
                        p = src.Bones[p].Parent;
                    }
                }
            }

            bool[] headTriangle = new bool[triangleCount];
            int headFaces = 0;
            if (headPmxBone >= 0)
            {
                for (int t = 0; t < triangleCount; t++)
                {
                    bool all = true;
                    for (int k = 0; k < 3 && all; k++)
                    {
                        int v = src.Indices[t * 3 + k];
                        int dom = DominantBone(src, v);
                        if (dom < 0 || !headBones[dom])
                        {
                            all = false;
                        }
                    }
                    if (all)
                    {
                        headTriangle[t] = true;
                        headFaces++;
                    }
                }
            }

            string safeName = string.IsNullOrEmpty(modelName) ? "Model" : modelName.Trim();
            if (safeName == "Miku")
            {
                safeName = "PMX_Miku";
            }

            MeshData body = BuildMesh(src, resolve, triangleMaterial, headTriangle, false,
                safeName + "_Body", MeshData.RoleBody);
            model.Meshes.Add(body);
            if (headFaces > 0)
            {
                MeshData head = BuildMesh(src, resolve, triangleMaterial, headTriangle, true,
                    safeName + "_Head", MeshData.RoleHead);
                model.Meshes.Add(head);
            }

            return model;
        }

        private static int DominantBone(PmxData src, int vertex)
        {
            int[] bi = src.VertexBones[vertex];
            float[] bw = src.VertexWeights[vertex];
            int best = -1;
            float bestW = 0f;
            for (int k = 0; k < 4; k++)
            {
                if (bw[k] > bestW)
                {
                    bestW = bw[k];
                    best = bi[k];
                }
            }
            return best;
        }

        private static MeshData BuildMesh(PmxData src, int[] resolve, int[] triangleMaterial,
                                          bool[] headTriangle, bool wantHead, string name, int role)
        {
            int triangleCount = triangleMaterial.Length;
            int[] remap = new int[src.Positions.Length];
            for (int i = 0; i < remap.Length; i++)
            {
                remap[i] = -1;
            }

            List<int> used = new List<int>();
            Dictionary<int, List<int>> subFaces = new Dictionary<int, List<int>>();

            for (int t = 0; t < triangleCount; t++)
            {
                if (headTriangle[t] != wantHead)
                {
                    continue;
                }
                int mi = triangleMaterial[t];
                List<int> list;
                if (!subFaces.TryGetValue(mi, out list))
                {
                    list = new List<int>();
                    subFaces[mi] = list;
                }
                for (int k = 0; k < 3; k++)
                {
                    int v = src.Indices[t * 3 + k];
                    if (remap[v] < 0)
                    {
                        remap[v] = used.Count;
                        used.Add(v);
                    }
                    list.Add(remap[v]);
                }
            }

            MeshData md = new MeshData();
            md.Name = name;
            md.Role = role;
            int n = used.Count;
            md.Vertices = new Vector3[n];
            md.Normals = new Vector3[n];
            md.Uvs = new Vector2[n];
            md.Weights = new BoneWeight[n];
            // Four reusable scratch slots instead of two tiny arrays per vertex.
            // Only [0,count) is read; each newly used slot is overwritten.
            int[] idx = new int[4];
            float[] wgt = new float[4];

            for (int i = 0; i < n; i++)
            {
                int v = used[i];
                md.Vertices[i] = ToGame(src.Positions[v]);
                Vector3 nrm = DirToGame(src.Normals[v]);
                md.Normals[i] = nrm.sqrMagnitude > 1e-8f ? nrm.normalized : Vector3.up;
                // PMX UV origin is the top-left corner; Unity's is bottom-left.
                md.Uvs[i] = new Vector2(src.Uvs[v].x, 1f - src.Uvs[v].y);

                int[] bi = src.VertexBones[v];
                float[] bw = src.VertexWeights[v];
                // Several PMX bones can fold into one kept bone, so merge first and
                // then keep the four heaviest influences.
                int count = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (bw[k] <= 1e-5f)
                    {
                        continue;
                    }
                    int nb = resolve[bi[k]];
                    if (nb < 0)
                    {
                        continue;
                    }
                    int found = -1;
                    for (int j = 0; j < count; j++)
                    {
                        if (idx[j] == nb)
                        {
                            found = j;
                            break;
                        }
                    }
                    if (found >= 0)
                    {
                        wgt[found] += bw[k];
                    }
                    else if (count < 4)
                    {
                        idx[count] = nb;
                        wgt[count] = bw[k];
                        count++;
                    }
                    else
                    {
                        int min = 0;
                        for (int j = 1; j < 4; j++)
                        {
                            if (wgt[j] < wgt[min])
                            {
                                min = j;
                            }
                        }
                        if (bw[k] > wgt[min])
                        {
                            idx[min] = nb;
                            wgt[min] = bw[k];
                        }
                    }
                }

                float sum = 0f;
                for (int j = 0; j < count; j++)
                {
                    sum += wgt[j];
                }
                if (sum <= 1e-6f)
                {
                    idx[0] = 0;
                    wgt[0] = 1f;
                    count = 1;
                    sum = 1f;
                }

                BoneWeight w = new BoneWeight();
                for (int j = 0; j < count; j++)
                {
                    float weight = wgt[j] / sum;
                    if (j == 0)
                    {
                        w.boneIndex0 = idx[j]; w.weight0 = weight;
                    }
                    else if (j == 1)
                    {
                        w.boneIndex1 = idx[j]; w.weight1 = weight;
                    }
                    else if (j == 2)
                    {
                        w.boneIndex2 = idx[j]; w.weight2 = weight;
                    }
                    else
                    {
                        w.boneIndex3 = idx[j]; w.weight3 = weight;
                    }
                }
                md.Weights[i] = w;
            }

            List<int> keys = new List<int>(subFaces.Keys);
            keys.Sort();
            md.SubMeshMaterials = new int[keys.Count];
            md.Indices = new int[keys.Count][];
            for (int s = 0; s < keys.Count; s++)
            {
                md.SubMeshMaterials[s] = keys[s];
                md.Indices[s] = subFaces[keys[s]].ToArray();
            }
            return md;
        }
    }

    // ----------------------------------------------------------------------
    // BMP / TGA decoding, for the texture formats Unity's LoadImage rejects.
    // Both return null when the data is not a format they understand.
    // ----------------------------------------------------------------------

    internal static class TextureDecoder
    {
        public static Texture2D Decode(byte[] data)
        {
            if (data == null || data.Length < 26)
            {
                return null;
            }
            if (data[0] == (byte)'B' && data[1] == (byte)'M')
            {
                return DecodeBmp(data);
            }
            return DecodeTga(data);
        }

        private static Texture2D Finish(Color32[] pixels, int width, int height)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        private static Texture2D DecodeBmp(byte[] d)
        {
            try
            {
                int offset = BitConverter.ToInt32(d, 10);
                int headerSize = BitConverter.ToInt32(d, 14);
                if (headerSize < 12)
                {
                    return null;
                }
                int width = BitConverter.ToInt32(d, 18);
                int height = BitConverter.ToInt32(d, 22);
                int bpp = BitConverter.ToInt16(d, 28);
                int compression = BitConverter.ToInt32(d, 30);
                if (width <= 0 || width > 8192 || height == 0 || Math.Abs(height) > 8192)
                {
                    return null;
                }
                if ((bpp != 24 && bpp != 32) || (compression != 0 && compression != 3))
                {
                    return null;
                }

                int h = Math.Abs(height);
                bool bottomUp = height > 0;
                int bytesPerPixel = bpp / 8;
                int stride = ((width * bpp + 31) / 32) * 4;
                if (offset + stride * h > d.Length)
                {
                    return null;
                }

                // Unity texture data is stored bottom-up: data row 0 is the bottom
                // row of the image. A BMP with a positive height also stores its
                // bottom row first, so those rows go straight through; only the
                // top-down (negative height) variant has to be reversed.
                Color32[] pixels = new Color32[width * h];
                for (int y = 0; y < h; y++)
                {
                    int row = bottomUp ? y : h - 1 - y;
                    int srcRow = offset + y * stride;
                    int dstRow = row * width;
                    for (int x = 0; x < width; x++)
                    {
                        int si = srcRow + x * bytesPerPixel;
                        byte b = d[si];
                        byte g = d[si + 1];
                        byte r = d[si + 2];
                        byte a = bytesPerPixel == 4 ? d[si + 3] : (byte)255;
                        pixels[dstRow + x] = new Color32(r, g, b, a);
                    }
                }
                return Finish(pixels, width, h);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Texture2D DecodeTga(byte[] d)
        {
            try
            {
                int idLength = d[0];
                int colorMapType = d[1];
                int imageType = d[2];
                if (colorMapType != 0 || (imageType != 2 && imageType != 10))
                {
                    return null;
                }
                int width = d[12] | (d[13] << 8);
                int height = d[14] | (d[15] << 8);
                int bpp = d[16];
                int descriptor = d[17];
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                {
                    return null;
                }
                if (bpp != 24 && bpp != 32)
                {
                    return null;
                }

                int bytesPerPixel = bpp / 8;
                int total = width * height;
                int offset = 18 + idLength;
                bool topDown = (descriptor & 0x20) != 0;
                // Same orientation rule as the BMP reader: TGA's default is a
                // bottom-left origin, which already matches Unity's bottom-up pixel
                // array, so only the optional top-left origin needs reversing.
                Color32[] pixels = new Color32[total];

                if (imageType == 2)
                {
                    if (offset + total * bytesPerPixel > d.Length)
                    {
                        return null;
                    }
                    for (int i = 0; i < total; i++)
                    {
                        int si = offset + i * bytesPerPixel;
                        int y = i / width;
                        int x = i % width;
                        int row = topDown ? height - 1 - y : y;
                        byte b = d[si];
                        byte g = d[si + 1];
                        byte r = d[si + 2];
                        byte a = bytesPerPixel == 4 ? d[si + 3] : (byte)255;
                        pixels[row * width + x] = new Color32(r, g, b, a);
                    }
                }
                else
                {
                    int i = 0;
                    int p = offset;
                    while (i < total)
                    {
                        if (p >= d.Length)
                        {
                            return null;
                        }
                        int header = d[p++];
                        int count = (header & 0x7F) + 1;
                        if ((header & 0x80) != 0)
                        {
                            if (p + bytesPerPixel > d.Length)
                            {
                                return null;
                            }
                            byte b = d[p];
                            byte g = d[p + 1];
                            byte r = d[p + 2];
                            byte a = bytesPerPixel == 4 ? d[p + 3] : (byte)255;
                            p += bytesPerPixel;
                            for (int k = 0; k < count && i < total; k++, i++)
                            {
                                int y = i / width;
                                int x = i % width;
                                int row = topDown ? height - 1 - y : y;
                                pixels[row * width + x] = new Color32(r, g, b, a);
                            }
                        }
                        else
                        {
                            for (int k = 0; k < count && i < total; k++, i++)
                            {
                                if (p + bytesPerPixel > d.Length)
                                {
                                    return null;
                                }
                                byte b = d[p];
                                byte g = d[p + 1];
                                byte r = d[p + 2];
                                byte a = bytesPerPixel == 4 ? d[p + 3] : (byte)255;
                                p += bytesPerPixel;
                                int y = i / width;
                                int x = i % width;
                                int row = topDown ? height - 1 - y : y;
                                pixels[row * width + x] = new Color32(r, g, b, a);
                            }
                        }
                    }
                }
                return Finish(pixels, width, height);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

}
