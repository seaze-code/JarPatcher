using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace JarPatcher.Engine
{
    public enum JarEntryKind
    {
        Folder,
        JavaClass,
        ConfigText,
        ImageAsset,
        BinaryResource
    }

    public class JarEntryRecord
    {
        public string FullPath { get; set; }
        public string Name { get; set; }
        public JarEntryKind Kind { get; set; }
        public byte[] OriginalBytes { get; set; }
        public byte[] ModifiedBytes { get; set; }
        public DateTimeOffset LastWriteTime { get; set; }

        public byte[] ActiveBytes
        {
            get { return ModifiedBytes ?? OriginalBytes; }
        }

        public bool IsResourceModified
        {
            get { return ModifiedBytes != null; }
        }
    }

    public class ArchiveTreeNode : INotifyPropertyChanged
    {
        private bool _isModified;
        private bool _isExpanded;

        public string Name { get; set; }
        public string FullPath { get; set; }
        public JarEntryKind Kind { get; set; }
        public List<ArchiveTreeNode> Children { get; set; }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged("IsExpanded");
                }
            }
        }

        public bool IsModified
        {
            get { return _isModified; }
            set
            {
                if (_isModified != value)
                {
                    _isModified = value;
                    OnPropertyChanged("IsModified");
                }
            }
        }

        public string IconBadge
        {
            get
            {
                switch (Kind)
                {
                    case JarEntryKind.Folder: return "PKG";
                    case JarEntryKind.JavaClass: return "CLS";
                    case JarEntryKind.ConfigText: return "CFG";
                    case JarEntryKind.ImageAsset: return "IMG";
                    default: return "BIN";
                }
            }
        }

        public string BadgeColor
        {
            get
            {
                switch (Kind)
                {
                    case JarEntryKind.Folder: return "#8B92A5";
                    case JarEntryKind.JavaClass: return "#3B82F6";
                    case JarEntryKind.ConfigText: return "#10B981";
                    case JarEntryKind.ImageAsset: return "#A855F7";
                    default: return "#64748B";
                }
            }
        }

        public ArchiveTreeNode()
        {
            Children = new List<ArchiveTreeNode>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propName));
        }
    }

    public class JarWorkspace
    {
        public string JarFilePath { get; private set; }
        public string JarFileName { get; private set; }
        public long JarFileSize { get; private set; }
        public long LoadTimeMs { get; private set; }

        public Dictionary<string, JarEntryRecord> Entries { get; private set; }
        public Dictionary<string, ParsedClassMetadata> Classes { get; private set; }
        public List<ConstantStringItem> AllStrings { get; private set; }
        public List<ArchiveTreeNode> TreeRoots { get; private set; }
        public Dictionary<string, ArchiveTreeNode> NodeByPath { get; private set; }

        public JarWorkspace()
        {
            Entries = new Dictionary<string, JarEntryRecord>(StringComparer.Ordinal);
            Classes = new Dictionary<string, ParsedClassMetadata>(StringComparer.Ordinal);
            AllStrings = new List<ConstantStringItem>();
            TreeRoots = new List<ArchiveTreeNode>();
            NodeByPath = new Dictionary<string, ArchiveTreeNode>(StringComparer.Ordinal);
        }

        public static JarEntryKind ClassifyEntry(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.EndsWith(".class")) return JarEntryKind.JavaClass;

            if (lower.EndsWith(".yml") || lower.EndsWith(".yaml") ||
                lower.EndsWith(".json") || lower.EndsWith(".mcmeta") ||
                lower.EndsWith(".properties") || lower.EndsWith(".toml") ||
                lower.EndsWith(".txt") || lower.EndsWith(".mf") ||
                lower.EndsWith(".cfg") || lower.EndsWith(".conf") ||
                lower.EndsWith(".xml") || lower.EndsWith(".lang"))
            {
                return JarEntryKind.ConfigText;
            }

            if (lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".gif"))
            {
                return JarEntryKind.ImageAsset;
            }

            return JarEntryKind.BinaryResource;
        }

        public static JarWorkspace Load(string jarPath)
        {
            Stopwatch sw = Stopwatch.StartNew();
            JarWorkspace ws = new JarWorkspace();
            FileInfo fi = new FileInfo(jarPath);
            ws.JarFilePath = fi.FullName;
            ws.JarFileName = fi.Name;
            ws.JarFileSize = fi.Length;

            using (FileStream fs = File.OpenRead(fi.FullName))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string fullPath = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(fullPath) || fullPath.EndsWith("/"))
                        continue;

                    byte[] bytes;
                    using (Stream s = entry.Open())
                    using (MemoryStream ms = new MemoryStream((int)Math.Max(0, entry.Length)))
                    {
                        s.CopyTo(ms);
                        bytes = ms.ToArray();
                    }

                    JarEntryKind kind = ClassifyEntry(fullPath);
                    JarEntryRecord rec = new JarEntryRecord
                    {
                        FullPath = fullPath,
                        Name = Path.GetFileName(fullPath),
                        Kind = kind,
                        OriginalBytes = bytes,
                        ModifiedBytes = null,
                        LastWriteTime = entry.LastWriteTime
                    };

                    ws.Entries[fullPath] = rec;

                    if (kind == JarEntryKind.JavaClass)
                    {
                        ParsedClassMetadata meta = ClassBytecodePatcher.InspectClass(bytes, fullPath);
                        if (meta.IsValid)
                        {
                            ws.Classes[fullPath] = meta;
                            ws.AllStrings.AddRange(meta.StringLiterals);
                        }
                    }
                }
            }

            ws.BuildTree();
            sw.Stop();
            ws.LoadTimeMs = sw.ElapsedMilliseconds;
            return ws;
        }

        private void BuildTree()
        {
            TreeRoots.Clear();
            NodeByPath.Clear();

            Dictionary<string, ArchiveTreeNode> folderMap = new Dictionary<string, ArchiveTreeNode>(StringComparer.Ordinal);
            List<string> sortedPaths = new List<string>(Entries.Keys);
            sortedPaths.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string fullPath in sortedPaths)
            {
                JarEntryRecord rec = Entries[fullPath];
                string[] parts = fullPath.Split('/');
                string currentFolderPath = "";
                ArchiveTreeNode parentFolder = null;

                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string part = parts[i];
                    currentFolderPath = i == 0 ? part : (currentFolderPath + "/" + part);

                    ArchiveTreeNode folderNode;
                    if (!folderMap.TryGetValue(currentFolderPath, out folderNode))
                    {
                        folderNode = new ArchiveTreeNode
                        {
                            Name = part,
                            FullPath = currentFolderPath,
                            Kind = JarEntryKind.Folder,
                            IsExpanded = (i < 2)
                        };
                        folderMap[currentFolderPath] = folderNode;

                        if (parentFolder == null)
                        {
                            TreeRoots.Add(folderNode);
                        }
                        else
                        {
                            parentFolder.Children.Add(folderNode);
                        }
                    }
                    parentFolder = folderNode;
                }

                ArchiveTreeNode leaf = new ArchiveTreeNode
                {
                    Name = parts[parts.Length - 1],
                    FullPath = fullPath,
                    Kind = rec.Kind
                };
                NodeByPath[fullPath] = leaf;

                if (parentFolder == null)
                {
                    TreeRoots.Add(leaf);
                }
                else
                {
                    parentFolder.Children.Add(leaf);
                }
            }

            SortTreeNodes(TreeRoots);
        }

        private void SortTreeNodes(List<ArchiveTreeNode> nodes)
        {
            nodes.Sort((a, b) =>
            {
                bool aFolder = a.Kind == JarEntryKind.Folder;
                bool bFolder = b.Kind == JarEntryKind.Folder;
                if (aFolder != bFolder) return aFolder ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            foreach (ArchiveTreeNode n in nodes)
            {
                if (n.Children.Count > 0) SortTreeNodes(n.Children);
            }
        }

        public void RefreshEntryModifiedState(string entryPath)
        {
            ArchiveTreeNode node;
            if (!NodeByPath.TryGetValue(entryPath, out node)) return;

            bool mod = false;
            JarEntryRecord rec;
            if (Entries.TryGetValue(entryPath, out rec) && rec.IsResourceModified)
            {
                mod = true;
            }

            ParsedClassMetadata meta;
            if (!mod && Classes.TryGetValue(entryPath, out meta))
            {
                foreach (ConstantStringItem s in meta.StringLiterals)
                {
                    if (s.IsModified)
                    {
                        mod = true;
                        break;
                    }
                }
            }

            node.IsModified = mod;
        }

        public int GetModifiedStringsCount()
        {
            int count = 0;
            for (int i = 0; i < AllStrings.Count; i++)
            {
                if (AllStrings[i].IsModified) count++;
            }
            return count;
        }

        public int GetModifiedResourcesCount()
        {
            int count = 0;
            foreach (JarEntryRecord rec in Entries.Values)
            {
                if (rec.IsResourceModified) count++;
            }
            return count;
        }

        public int BatchReplaceStrings(string findText, string replaceText, bool matchCase, bool useRegex, IEnumerable<ConstantStringItem> targetScope)
        {
            if (string.IsNullOrEmpty(findText)) return 0;
            string replacement = replaceText ?? "";
            int replacedItems = 0;
            HashSet<string> touchedClasses = new HashSet<string>(StringComparer.Ordinal);

            Regex regex = null;
            if (useRegex)
            {
                RegexOptions opts = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(findText, opts);
            }

            foreach (ConstantStringItem item in targetScope)
            {
                string current = item.CurrentValue ?? "";
                string updated = current;

                if (useRegex)
                {
                    if (regex.IsMatch(current))
                    {
                        updated = regex.Replace(current, replacement);
                    }
                }
                else
                {
                    StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    int idx = current.IndexOf(findText, cmp);
                    if (idx >= 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        int pos = 0;
                        while (idx >= 0)
                        {
                            sb.Append(current, pos, idx - pos);
                            sb.Append(replacement);
                            pos = idx + findText.Length;
                            idx = current.IndexOf(findText, pos, cmp);
                        }
                        sb.Append(current, pos, current.Length - pos);
                        updated = sb.ToString();
                    }
                }

                if (!string.Equals(current, updated, StringComparison.Ordinal) &&
                    JvmModifiedUtf8.GetByteCount(updated) <= 65535)
                {
                    item.CurrentValue = updated;
                    replacedItems++;
                    touchedClasses.Add(item.EntryPath);
                }
            }

            foreach (string cls in touchedClasses)
            {
                RefreshEntryModifiedState(cls);
            }

            return replacedItems;
        }

        public byte[] GetEffectiveClassBytes(string classEntryPath)
        {
            JarEntryRecord rec;
            if (!Entries.TryGetValue(classEntryPath, out rec)) return null;

            ParsedClassMetadata meta;
            if (!Classes.TryGetValue(classEntryPath, out meta))
            {
                return rec.ActiveBytes;
            }

            Dictionary<int, string> patches = new Dictionary<int, string>();
            foreach (ConstantStringItem item in meta.StringLiterals)
            {
                if (item.IsModified)
                {
                    patches[item.StringCpIndex] = item.CurrentValue;
                }
            }

            if (patches.Count == 0)
            {
                return rec.ActiveBytes;
            }

            return ClassBytecodePatcher.PatchClassStrings(rec.OriginalBytes, patches);
        }

        public Dictionary<string, byte[]> GetInnerClassBytesFor(string classEntryPath)
        {
            Dictionary<string, byte[]> result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (!classEntryPath.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) return result;

            string prefix = classEntryPath.Substring(0, classEntryPath.Length - 6) + "$";
            foreach (string path in Entries.Keys)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
                {
                    result[path] = GetEffectiveClassBytes(path);
                }
            }

            return result;
        }

        public void SavePatchedJar(string destinationPath, out int patchedClassesCount, out int patchedResourcesCount)
        {
            patchedClassesCount = 0;
            patchedResourcesCount = 0;

            bool anyModified = (GetModifiedStringsCount() > 0 || GetModifiedResourcesCount() > 0);
            string tempFile = destinationPath + ".tmp_" + Guid.NewGuid().ToString("N");

            try
            {
                using (FileStream fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (KeyValuePair<string, JarEntryRecord> kvp in Entries)
                    {
                        string entryPath = kvp.Key;
                        JarEntryRecord rec = kvp.Value;

                        // Strip signature block files if any modification was made to prevent JVM SecurityException
                        if (anyModified && IsJarSignatureFile(entryPath))
                        {
                            continue;
                        }

                        byte[] outputBytes = rec.ActiveBytes;

                        if (rec.Kind == JarEntryKind.JavaClass)
                        {
                            ParsedClassMetadata meta;
                            if (Classes.TryGetValue(entryPath, out meta))
                            {
                                Dictionary<int, string> classPatches = new Dictionary<int, string>();
                                foreach (ConstantStringItem s in meta.StringLiterals)
                                {
                                    if (s.IsModified)
                                    {
                                        classPatches[s.StringCpIndex] = s.CurrentValue;
                                    }
                                }

                                if (classPatches.Count > 0)
                                {
                                    outputBytes = ClassBytecodePatcher.PatchClassStrings(rec.OriginalBytes, classPatches);
                                    patchedClassesCount++;
                                }
                            }
                        }
                        else if (rec.IsResourceModified)
                        {
                            patchedResourcesCount++;
                        }

                        ZipArchiveEntry outEntry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
                        try
                        {
                            outEntry.LastWriteTime = rec.LastWriteTime;
                        }
                        catch { }

                        using (Stream entryStream = outEntry.Open())
                        {
                            if (outputBytes != null && outputBytes.Length > 0)
                            {
                                entryStream.Write(outputBytes, 0, outputBytes.Length);
                            }
                        }
                    }
                }

                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
                File.Move(tempFile, destinationPath);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private static bool IsJarSignatureFile(string path)
        {
            if (!path.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return false;
            string upper = path.ToUpperInvariant();
            return upper.EndsWith(".SF") || upper.EndsWith(".RSA") || upper.EndsWith(".DSA") || upper.EndsWith(".EC");
        }
    }
}
