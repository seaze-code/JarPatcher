using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace JarPatcher.Engine
{
    public class ConstantStringItem : INotifyPropertyChanged
    {
        private string _currentValue;

        public string EntryPath { get; set; }
        public string ClassName { get; set; }
        public string ShortClassName { get; set; }
        public int StringCpIndex { get; set; }
        public int Utf8CpIndex { get; set; }
        public string OriginalValue { get; set; }
        public string Category { get; set; }

        public string CurrentValue
        {
            get { return _currentValue; }
            set
            {
                if (_currentValue != value)
                {
                    _currentValue = value;
                    OnPropertyChanged("CurrentValue");
                    OnPropertyChanged("IsModified");
                    OnPropertyChanged("StatusBadge");
                    OnPropertyChanged("ByteLength");
                }
            }
        }

        public bool IsModified
        {
            get { return !string.Equals(OriginalValue, _currentValue, StringComparison.Ordinal); }
        }

        public string StatusBadge
        {
            get { return IsModified ? "MODIFIED" : "ORIGINAL"; }
        }

        public int ByteLength
        {
            get { return JvmModifiedUtf8.GetByteCount(_currentValue ?? ""); }
        }

        public string CpSlotDisplay
        {
            get { return string.Format("#{0} -> #{1}", StringCpIndex, Utf8CpIndex); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propName));
            }
        }
    }

    public class ClassMemberSummary
    {
        public string Kind { get; set; }
        public string AccessFlags { get; set; }
        public string Name { get; set; }
        public string Descriptor { get; set; }
    }

    public class ParsedClassMetadata
    {
        public bool IsValid { get; set; }
        public string EntryPath { get; set; }
        public int MinorVersion { get; set; }
        public int MajorVersion { get; set; }
        public string JavaVersionDisplay { get; set; }
        public string AccessFlagsDisplay { get; set; }
        public string ThisClassName { get; set; }
        public string SuperClassName { get; set; }
        public List<string> Interfaces { get; set; }
        public List<ClassMemberSummary> Fields { get; set; }
        public List<ClassMemberSummary> Methods { get; set; }
        public List<ConstantStringItem> StringLiterals { get; set; }
        public int ConstantPoolCount { get; set; }

        public ParsedClassMetadata()
        {
            Interfaces = new List<string>();
            Fields = new List<ClassMemberSummary>();
            Methods = new List<ClassMemberSummary>();
            StringLiterals = new List<ConstantStringItem>();
        }
    }

    internal class RawCpSlot
    {
        public byte Tag;
        public byte[] RawPayload;
        public string Utf8Text;
        public int RefIndex1;
        public int RefIndex2;
    }

    public static class ClassBytecodePatcher
    {
        public static string FormatJavaVersion(int major, int minor)
        {
            if (major >= 45)
            {
                int javaVer = major - 44;
                if (major <= 52)
                {
                    return string.Format("Java 1.{0} ({1}.{2})", javaVer, major, minor);
                }
                return string.Format("Java {0} ({1}.{2})", javaVer, major, minor);
            }
            return string.Format("Bytecode {0}.{1}", major, minor);
        }

        public static string ClassifyString(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Empty";

            if (value.IndexOf('§') >= 0 ||
                (value.IndexOf('&') >= 0 && HasMinecraftColorCode(value)) ||
                value.IndexOf("<gradient:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Minecraft Text";
            }

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
                value.IndexOf("discord.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("minecraft.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("mojang.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "URL / Endpoint";
            }

            if (value.StartsWith("/", StringComparison.Ordinal) && value.Length > 1 && value.Length < 64 && value.IndexOf('/') == 0 ||
                value.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (value.IndexOf('.') > 0 && value.IndexOf(' ') < 0 && value.ToLowerInvariant() == value && value.Length >= 5))
            {
                return "Command / Key";
            }

            if (value.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".properties", StringComparison.OrdinalIgnoreCase))
            {
                return "Resource Path";
            }

            return "General Text";
        }

        private static bool HasMinecraftColorCode(string s)
        {
            for (int i = 0; i < s.Length - 1; i++)
            {
                if (s[i] == '&' || s[i] == '§')
                {
                    char next = char.ToLowerInvariant(s[i + 1]);
                    if ((next >= '0' && next <= '9') ||
                        (next >= 'a' && next <= 'f') ||
                        (next >= 'k' && next <= 'o') ||
                        next == 'r' || next == '#')
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static ParsedClassMetadata InspectClass(byte[] classBytes, string entryPath)
        {
            ParsedClassMetadata meta = new ParsedClassMetadata
            {
                EntryPath = entryPath
            };

            if (classBytes == null || classBytes.Length < 16) return meta;

            try
            {
                using (MemoryStream ms = new MemoryStream(classBytes, false))
                using (BinaryReader br = new BinaryReader(ms))
                {
                    uint magic = ReadUInt32BE(br);
                    if (magic != 0xCAFEBABEu) return meta;

                    meta.MinorVersion = ReadUInt16BE(br);
                    meta.MajorVersion = ReadUInt16BE(br);
                    meta.JavaVersionDisplay = FormatJavaVersion(meta.MajorVersion, meta.MinorVersion);

                    int cpCount = ReadUInt16BE(br);
                    meta.ConstantPoolCount = cpCount;
                    RawCpSlot[] slots = ReadConstantPool(br, ms, cpCount);
                    if (slots == null) return meta;

                    meta.IsValid = true;

                    // Extract all CONSTANT_String entries (Tag 8)
                    string shortName = entryPath;
                    int lastSlash = shortName.LastIndexOf('/');
                    if (lastSlash >= 0) shortName = shortName.Substring(lastSlash + 1);
                    if (shortName.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
                    {
                        shortName = shortName.Substring(0, shortName.Length - 6);
                    }

                    string fullClassName = entryPath.EndsWith(".class", StringComparison.OrdinalIgnoreCase)
                        ? entryPath.Substring(0, entryPath.Length - 6)
                        : entryPath;

                    for (int i = 1; i < cpCount; i++)
                    {
                        RawCpSlot slot = slots[i];
                        if (slot != null && slot.Tag == 8)
                        {
                            int utf8Idx = slot.RefIndex1;
                            if (utf8Idx > 0 && utf8Idx < cpCount && slots[utf8Idx] != null && slots[utf8Idx].Tag == 1)
                            {
                                string text = slots[utf8Idx].Utf8Text ?? "";
                                meta.StringLiterals.Add(new ConstantStringItem
                                {
                                    EntryPath = entryPath,
                                    ClassName = fullClassName,
                                    ShortClassName = shortName,
                                    StringCpIndex = i,
                                    Utf8CpIndex = utf8Idx,
                                    OriginalValue = text,
                                    CurrentValue = text,
                                    Category = ClassifyString(text)
                                });
                            }
                        }
                    }

                    // Parse class header & members if stream has remaining bytes
                    if (ms.Position + 8 <= ms.Length)
                    {
                        ushort accessFlags = ReadUInt16BE(br);
                        meta.AccessFlagsDisplay = FormatClassAccessFlags(accessFlags);

                        ushort thisClassIdx = ReadUInt16BE(br);
                        ushort superClassIdx = ReadUInt16BE(br);
                        meta.ThisClassName = ResolveClassName(slots, cpCount, thisClassIdx);
                        meta.SuperClassName = ResolveClassName(slots, cpCount, superClassIdx);

                        ushort interfacesCount = ReadUInt16BE(br);
                        for (int i = 0; i < interfacesCount && ms.Position + 2 <= ms.Length; i++)
                        {
                            ushort ifaceIdx = ReadUInt16BE(br);
                            meta.Interfaces.Add(ResolveClassName(slots, cpCount, ifaceIdx));
                        }

                        // Fields
                        if (ms.Position + 2 <= ms.Length)
                        {
                            ushort fieldsCount = ReadUInt16BE(br);
                            for (int i = 0; i < fieldsCount && ms.Position + 8 <= ms.Length; i++)
                            {
                                ushort fAcc = ReadUInt16BE(br);
                                ushort fNameIdx = ReadUInt16BE(br);
                                ushort fDescIdx = ReadUInt16BE(br);
                                ushort attrCount = ReadUInt16BE(br);

                                meta.Fields.Add(new ClassMemberSummary
                                {
                                    Kind = "Field",
                                    AccessFlags = FormatMemberAccessFlags(fAcc),
                                    Name = ResolveUtf8(slots, cpCount, fNameIdx),
                                    Descriptor = ResolveUtf8(slots, cpCount, fDescIdx)
                                });

                                SkipAttributes(br, ms, attrCount);
                            }
                        }

                        // Methods
                        if (ms.Position + 2 <= ms.Length)
                        {
                            ushort methodsCount = ReadUInt16BE(br);
                            for (int i = 0; i < methodsCount && ms.Position + 8 <= ms.Length; i++)
                            {
                                ushort mAcc = ReadUInt16BE(br);
                                ushort mNameIdx = ReadUInt16BE(br);
                                ushort mDescIdx = ReadUInt16BE(br);
                                ushort attrCount = ReadUInt16BE(br);

                                meta.Methods.Add(new ClassMemberSummary
                                {
                                    Kind = "Method",
                                    AccessFlags = FormatMemberAccessFlags(mAcc),
                                    Name = ResolveUtf8(slots, cpCount, mNameIdx),
                                    Descriptor = ResolveUtf8(slots, cpCount, mDescIdx)
                                });

                                SkipAttributes(br, ms, attrCount);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Return partial metadata safely
            }

            return meta;
        }

        /// <summary>
        /// Binary-safe Constant Pool patcher.
        /// Replaces modified CONSTANT_String values. If a CONSTANT_Utf8 slot is exclusively owned by a single CONSTANT_String,
        /// it is updated in-place. If the CONSTANT_Utf8 slot is shared with any class/method/field/descriptor or another CONSTANT_String,
        /// a new CONSTANT_Utf8 entry is appended at the end of the Constant Pool and the CONSTANT_String is repointed to it.
        /// </summary>
        public static byte[] PatchClassStrings(byte[] originalBytes, Dictionary<int, string> modifiedByStringCpIndex)
        {
            if (originalBytes == null || modifiedByStringCpIndex == null || modifiedByStringCpIndex.Count == 0)
            {
                return originalBytes;
            }

            using (MemoryStream ms = new MemoryStream(originalBytes, false))
            using (BinaryReader br = new BinaryReader(ms))
            {
                uint magic = ReadUInt32BE(br);
                if (magic != 0xCAFEBABEu) return originalBytes;

                ushort minor = ReadUInt16BE(br);
                ushort major = ReadUInt16BE(br);
                int cpCount = ReadUInt16BE(br);

                RawCpSlot[] slots = ReadConstantPool(br, ms, cpCount);
                if (slots == null) return originalBytes;

                long bodyStartOffset = ms.Position;

                // Determine how many times each CONSTANT_Utf8 index is referenced across the Constant Pool and Class Body
                int[] utf8RefCounts = new int[cpCount];
                for (int i = 1; i < cpCount; i++)
                {
                    RawCpSlot s = slots[i];
                    if (s == null) continue;
                    if (s.Tag == 7 || s.Tag == 8 || s.Tag == 16 || s.Tag == 19 || s.Tag == 20)
                    {
                        if (s.RefIndex1 > 0 && s.RefIndex1 < cpCount) utf8RefCounts[s.RefIndex1]++;
                    }
                    else if (s.Tag == 12)
                    {
                        if (s.RefIndex1 > 0 && s.RefIndex1 < cpCount) utf8RefCounts[s.RefIndex1]++;
                        if (s.RefIndex2 > 0 && s.RefIndex2 < cpCount) utf8RefCounts[s.RefIndex2]++;
                    }
                }

                // Also mark any CONSTANT_Utf8 referenced directly as field/method/attribute names in the class body
                MarkBodyUtf8References(br, ms, cpCount, utf8RefCounts);

                List<RawCpSlot> appendedSlots = new List<RawCpSlot>();

                foreach (KeyValuePair<int, string> patch in modifiedByStringCpIndex)
                {
                    int strCpIdx = patch.Key;
                    string newText = patch.Value ?? "";

                    if (strCpIdx <= 0 || strCpIdx >= cpCount) continue;
                    RawCpSlot strSlot = slots[strCpIdx];
                    if (strSlot == null || strSlot.Tag != 8) continue;

                    byte[] encoded = JvmModifiedUtf8.Encode(newText);
                    if (encoded.Length > 65535)
                    {
                        throw new InvalidOperationException(string.Format(
                            "String at CP #{0} exceeds JVM 65,535-byte CONSTANT_Utf8 limit ({1} bytes).",
                            strCpIdx, encoded.Length));
                    }

                    int targetUtf8Idx = strSlot.RefIndex1;
                    if (targetUtf8Idx > 0 && targetUtf8Idx < cpCount &&
                        slots[targetUtf8Idx] != null && slots[targetUtf8Idx].Tag == 1 &&
                        utf8RefCounts[targetUtf8Idx] == 1)
                    {
                        // Exclusively used by this CONSTANT_String: safe to update in-place
                        slots[targetUtf8Idx].Utf8Text = newText;
                        slots[targetUtf8Idx].RawPayload = encoded;
                    }
                    else
                    {
                        // Shared with an identifier/descriptor/attribute or another string: append a new CONSTANT_Utf8 slot
                        int newUtf8Index = cpCount + appendedSlots.Count;
                        if (newUtf8Index > 65534)
                        {
                            // Fallback to in-place if Constant Pool is already at 65535 limit
                            slots[targetUtf8Idx].Utf8Text = newText;
                            slots[targetUtf8Idx].RawPayload = encoded;
                        }
                        else
                        {
                            appendedSlots.Add(new RawCpSlot
                            {
                                Tag = 1,
                                Utf8Text = newText,
                                RawPayload = encoded
                            });
                            strSlot.RefIndex1 = newUtf8Index;
                            strSlot.RawPayload = new byte[]
                            {
                                (byte)((newUtf8Index >> 8) & 0xFF),
                                (byte)(newUtf8Index & 0xFF)
                            };
                        }
                    }
                }

                int finalCpCount = cpCount + appendedSlots.Count;

                using (MemoryStream outMs = new MemoryStream(originalBytes.Length + 256))
                using (BinaryWriter bw = new BinaryWriter(outMs))
                {
                    WriteUInt32BE(bw, 0xCAFEBABEu);
                    WriteUInt16BE(bw, minor);
                    WriteUInt16BE(bw, major);
                    WriteUInt16BE(bw, (ushort)finalCpCount);

                    for (int i = 1; i < cpCount; i++)
                    {
                        RawCpSlot slot = slots[i];
                        if (slot == null) continue;
                        WriteCpSlot(bw, slot);
                    }

                    for (int i = 0; i < appendedSlots.Count; i++)
                    {
                        WriteCpSlot(bw, appendedSlots[i]);
                    }

                    // Write remainder of .class file (access_flags, this_class, super_class, interfaces, fields, methods, attributes)
                    int remaining = (int)(originalBytes.Length - bodyStartOffset);
                    if (remaining > 0)
                    {
                        bw.Write(originalBytes, (int)bodyStartOffset, remaining);
                    }

                    return outMs.ToArray();
                }
            }
        }

        private static void WriteCpSlot(BinaryWriter bw, RawCpSlot slot)
        {
            bw.Write(slot.Tag);
            if (slot.Tag == 1)
            {
                byte[] utf8Bytes = slot.RawPayload ?? new byte[0];
                WriteUInt16BE(bw, (ushort)utf8Bytes.Length);
                bw.Write(utf8Bytes);
            }
            else if (slot.RawPayload != null)
            {
                bw.Write(slot.RawPayload);
            }
        }

        private static RawCpSlot[] ReadConstantPool(BinaryReader br, MemoryStream ms, int cpCount)
        {
            if (cpCount <= 0 || cpCount > 65535) return null;
            RawCpSlot[] slots = new RawCpSlot[cpCount];

            for (int i = 1; i < cpCount; i++)
            {
                if (ms.Position >= ms.Length) return null;
                byte tag = br.ReadByte();
                RawCpSlot slot = new RawCpSlot { Tag = tag };

                switch (tag)
                {
                    case 1: // CONSTANT_Utf8
                        int len = ReadUInt16BE(br);
                        if (ms.Position + len > ms.Length) return null;
                        byte[] utf8Bytes = br.ReadBytes(len);
                        slot.RawPayload = utf8Bytes;
                        slot.Utf8Text = JvmModifiedUtf8.Decode(utf8Bytes);
                        slots[i] = slot;
                        break;

                    case 3: // CONSTANT_Integer
                    case 4: // CONSTANT_Float
                        if (ms.Position + 4 > ms.Length) return null;
                        slot.RawPayload = br.ReadBytes(4);
                        slots[i] = slot;
                        break;

                    case 5: // CONSTANT_Long
                    case 6: // CONSTANT_Double
                        if (ms.Position + 8 > ms.Length) return null;
                        slot.RawPayload = br.ReadBytes(8);
                        slots[i] = slot;
                        i++; // 8-byte constants take 2 CP slots
                        break;

                    case 7:  // CONSTANT_Class
                    case 8:  // CONSTANT_String
                    case 16: // CONSTANT_MethodType
                    case 19: // CONSTANT_Module
                    case 20: // CONSTANT_Package
                        if (ms.Position + 2 > ms.Length) return null;
                        byte[] b2 = br.ReadBytes(2);
                        slot.RawPayload = b2;
                        slot.RefIndex1 = (b2[0] << 8) | b2[1];
                        slots[i] = slot;
                        break;

                    case 9:  // CONSTANT_Fieldref
                    case 10: // CONSTANT_Methodref
                    case 11: // CONSTANT_InterfaceMethodref
                    case 12: // CONSTANT_NameAndType
                    case 17: // CONSTANT_Dynamic
                    case 18: // CONSTANT_InvokeDynamic
                        if (ms.Position + 4 > ms.Length) return null;
                        byte[] b4 = br.ReadBytes(4);
                        slot.RawPayload = b4;
                        slot.RefIndex1 = (b4[0] << 8) | b4[1];
                        slot.RefIndex2 = (b4[2] << 8) | b4[3];
                        slots[i] = slot;
                        break;

                    case 15: // CONSTANT_MethodHandle
                        if (ms.Position + 3 > ms.Length) return null;
                        slot.RawPayload = br.ReadBytes(3);
                        slots[i] = slot;
                        break;

                    default:
                        return null;
                }
            }

            return slots;
        }

        private static void MarkBodyUtf8References(BinaryReader br, MemoryStream ms, int cpCount, int[] utf8RefCounts)
        {
            long savedPos = ms.Position;
            try
            {
                if (ms.Position + 8 > ms.Length) return;
                br.ReadUInt16(); // access_flags
                br.ReadUInt16(); // this_class
                br.ReadUInt16(); // super_class
                ushort ifaces = ReadUInt16BE(br);
                ms.Position += ifaces * 2;

                // Fields
                if (ms.Position + 2 <= ms.Length)
                {
                    ushort fields = ReadUInt16BE(br);
                    for (int i = 0; i < fields && ms.Position + 8 <= ms.Length; i++)
                    {
                        br.ReadUInt16();
                        ushort nameIdx = ReadUInt16BE(br);
                        ushort descIdx = ReadUInt16BE(br);
                        if (nameIdx > 0 && nameIdx < cpCount) utf8RefCounts[nameIdx]++;
                        if (descIdx > 0 && descIdx < cpCount) utf8RefCounts[descIdx]++;

                        ushort attrs = ReadUInt16BE(br);
                        for (int a = 0; a < attrs && ms.Position + 6 <= ms.Length; a++)
                        {
                            ushort attrNameIdx = ReadUInt16BE(br);
                            if (attrNameIdx > 0 && attrNameIdx < cpCount) utf8RefCounts[attrNameIdx]++;
                            uint attrLen = ReadUInt32BE(br);
                            ms.Position += attrLen;
                        }
                    }
                }

                // Methods
                if (ms.Position + 2 <= ms.Length)
                {
                    ushort methods = ReadUInt16BE(br);
                    for (int i = 0; i < methods && ms.Position + 8 <= ms.Length; i++)
                    {
                        br.ReadUInt16();
                        ushort nameIdx = ReadUInt16BE(br);
                        ushort descIdx = ReadUInt16BE(br);
                        if (nameIdx > 0 && nameIdx < cpCount) utf8RefCounts[nameIdx]++;
                        if (descIdx > 0 && descIdx < cpCount) utf8RefCounts[descIdx]++;

                        ushort attrs = ReadUInt16BE(br);
                        for (int a = 0; a < attrs && ms.Position + 6 <= ms.Length; a++)
                        {
                            ushort attrNameIdx = ReadUInt16BE(br);
                            if (attrNameIdx > 0 && attrNameIdx < cpCount) utf8RefCounts[attrNameIdx]++;
                            uint attrLen = ReadUInt32BE(br);
                            ms.Position += attrLen;
                        }
                    }
                }
            }
            catch
            {
                // Ignore truncated trailing attributes
            }
            finally
            {
                ms.Position = savedPos;
            }
        }

        private static void SkipAttributes(BinaryReader br, MemoryStream ms, ushort attrCount)
        {
            for (int a = 0; a < attrCount && ms.Position + 6 <= ms.Length; a++)
            {
                br.ReadUInt16(); // attribute_name_index
                uint len = ReadUInt32BE(br);
                if (ms.Position + len > ms.Length)
                {
                    ms.Position = ms.Length;
                    return;
                }
                ms.Position += len;
            }
        }

        private static string ResolveClassName(RawCpSlot[] slots, int cpCount, int classIdx)
        {
            if (classIdx > 0 && classIdx < cpCount && slots[classIdx] != null && slots[classIdx].Tag == 7)
            {
                return ResolveUtf8(slots, cpCount, slots[classIdx].RefIndex1);
            }
            return "";
        }

        private static string ResolveUtf8(RawCpSlot[] slots, int cpCount, int utf8Idx)
        {
            if (utf8Idx > 0 && utf8Idx < cpCount && slots[utf8Idx] != null && slots[utf8Idx].Tag == 1)
            {
                return slots[utf8Idx].Utf8Text ?? "";
            }
            return "";
        }

        private static string FormatClassAccessFlags(ushort flags)
        {
            List<string> parts = new List<string>();
            if ((flags & 0x0001) != 0) parts.Add("public");
            if ((flags & 0x0010) != 0) parts.Add("final");
            if ((flags & 0x0200) != 0) parts.Add("interface");
            else if ((flags & 0x0400) != 0) parts.Add("abstract");
            if ((flags & 0x4000) != 0) parts.Add("enum");
            if ((flags & 0x1000) != 0) parts.Add("synthetic");
            return string.Join(" ", parts);
        }

        private static string FormatMemberAccessFlags(ushort flags)
        {
            List<string> parts = new List<string>();
            if ((flags & 0x0001) != 0) parts.Add("public");
            if ((flags & 0x0002) != 0) parts.Add("private");
            if ((flags & 0x0004) != 0) parts.Add("protected");
            if ((flags & 0x0008) != 0) parts.Add("static");
            if ((flags & 0x0010) != 0) parts.Add("final");
            if ((flags & 0x0020) != 0) parts.Add("synchronized");
            if ((flags & 0x0100) != 0) parts.Add("native");
            if ((flags & 0x0400) != 0) parts.Add("abstract");
            return string.Join(" ", parts);
        }

        private static ushort ReadUInt16BE(BinaryReader br)
        {
            byte b1 = br.ReadByte();
            byte b2 = br.ReadByte();
            return (ushort)((b1 << 8) | b2);
        }

        private static uint ReadUInt32BE(BinaryReader br)
        {
            byte b1 = br.ReadByte();
            byte b2 = br.ReadByte();
            byte b3 = br.ReadByte();
            byte b4 = br.ReadByte();
            return ((uint)b1 << 24) | ((uint)b2 << 16) | ((uint)b3 << 8) | b4;
        }

        private static void WriteUInt16BE(BinaryWriter bw, ushort val)
        {
            bw.Write((byte)((val >> 8) & 0xFF));
            bw.Write((byte)(val & 0xFF));
        }

        private static void WriteUInt32BE(BinaryWriter bw, uint val)
        {
            bw.Write((byte)((val >> 24) & 0xFF));
            bw.Write((byte)((val >> 16) & 0xFF));
            bw.Write((byte)((val >> 8) & 0xFF));
            bw.Write((byte)(val & 0xFF));
        }
    }
}
