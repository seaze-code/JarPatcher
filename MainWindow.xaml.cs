using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using JarPatcher.Engine;

namespace JarPatcher
{
    public partial class MainWindow : Window
    {
        private JarWorkspace _workspace;
        private List<ConstantStringItem> _currentFilteredStrings = new List<ConstantStringItem>();
        private ConstantStringItem _selectedStringItem;
        private string _scopedPathPrefix;
        private string _activeClassEntryPath;
        private string _activeResourceEntryPath;
        private bool _suppressStringTextChanged;

        public MainWindow()
        {
            // Support CLI headless verification / batch mode:
            // JarPatcher.exe --cli-patch <in.jar> <out.jar> <find> <replace>
            string[] cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs != null && cmdArgs.Length >= 6 &&
                string.Equals(cmdArgs[1], "--cli-patch", StringComparison.OrdinalIgnoreCase))
            {
                RunHeadlessCliPatch(cmdArgs[2], cmdArgs[3], cmdArgs[4], cmdArgs[5]);
                Environment.Exit(0);
                return;
            }

            InitializeComponent();
            Loaded += MainWindow_Loaded;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        private static void RunHeadlessCliPatch(string inJar, string outJar, string findStr, string replaceStr)
        {
            JarWorkspace ws = JarWorkspace.Load(inJar);
            int replaced = ws.BatchReplaceStrings(findStr, replaceStr, false, false, ws.AllStrings);
            int patchedClasses;
            int patchedResources;
            ws.SavePatchedJar(outJar, out patchedClasses, out patchedResources);
            Console.WriteLine(string.Format(
                "CLI_PATCH_OK|Classes={0}|Strings={1}|Replaced={2}|PatchedClasses={3}",
                ws.Classes.Count, ws.AllStrings.Count, replaced, patchedClasses));
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (VineflowerRunner.IsAvailable)
            {
                TxtStatsDecompiler.Text = "DECOMPILER: VINEFLOWER 1.12 READY";
                TxtStatsDecompiler.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                TxtStatsDecompiler.Text = "DECOMPILER: OFFLINE";
                TxtStatsDecompiler.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }

            string[] cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs != null && cmdArgs.Length >= 2 &&
                File.Exists(cmdArgs[1]) &&
                cmdArgs[1].EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                OpenJarArchive(cmdArgs[1]);
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.O)
                {
                    BtnOpenJar_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.S)
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        BtnSaveAsJar_Click(this, new RoutedEventArgs());
                    else
                        BtnSaveJar_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.F)
                {
                    SwitchToStringPoolView();
                    TxtSearchStrings.Focus();
                    TxtSearchStrings.SelectAll();
                    e.Handled = true;
                }
                else if (e.Key == Key.H)
                {
                    BtnToggleReplaceBar_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
        }

        #region Window Chrome & Drag/Drop

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace != null && (_workspace.GetModifiedStringsCount() > 0 || _workspace.GetModifiedResourcesCount() > 0))
            {
                MessageBoxResult res = MessageBox.Show(
                    "You have unsaved bytecode/resource modifications. Close without saving?",
                    "Unsaved Modifications",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            Close();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            string file = files[0];
            if (File.Exists(file) && file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                OpenJarArchive(file);
            }
            else
            {
                SetStatus("Only Java Archive (.jar) files can be loaded into the workspace.", true);
            }
        }

        #endregion

        #region Loading & Saving JAR Archives

        private void BtnOpenJar_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Title = "Open Java Archive (.JAR)",
                Filter = "Java Archives (*.jar)|*.jar|All Files (*.*)|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                OpenJarArchive(ofd.FileName);
            }
        }

        public void OpenJarArchive(string jarPath)
        {
            SetStatus("Loading and indexing constant pools from " + Path.GetFileName(jarPath) + "...", false);

            Thread worker = new Thread(() =>
            {
                try
                {
                    JarWorkspace ws = JarWorkspace.Load(jarPath);
                    Dispatcher.Invoke(new Action(() =>
                    {
                        BindWorkspace(ws);
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        SetStatus("Failed to load archive: " + ex.Message, true);
                        MessageBox.Show("Could not open .JAR archive:\n" + ex.Message, "Archive Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }));
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void BindWorkspace(JarWorkspace ws)
        {
            _workspace = ws;
            _scopedPathPrefix = null;
            _activeClassEntryPath = null;
            _activeResourceEntryPath = null;
            _selectedStringItem = null;

            OverlayEmptyState.Visibility = Visibility.Collapsed;
            BtnSaveJar.IsEnabled = true;
            BtnSaveAsJar.IsEnabled = true;
            BtnClearClassScope.Visibility = Visibility.Collapsed;
            BtnDecompileSelectedClass.IsEnabled = false;

            double sizeMb = ws.JarFileSize / (1024.0 * 1024.0);
            TxtActiveArchiveTitle.Text = string.Format("{0} ({1:N2} MB)", ws.JarFileName, sizeMb);
            TxtTreeEntryCount.Text = string.Format("{0} entries", ws.Entries.Count);
            TxtStatsClasses.Text = string.Format("CLASSES: {0:N0}", ws.Classes.Count);
            TxtStatsStrings.Text = string.Format("STRINGS: {0:N0}", ws.AllStrings.Count);

            TreeArchive.ItemsSource = ws.TreeRoots;
            SwitchToStringPoolView();
            ApplyStringFilter();
            UpdatePendingCounterUI();

            SetStatus(string.Format(
                "Loaded {0} — Indexed {1:N0} classes and {2:N0} CONSTANT_String literals in {3}ms.",
                ws.JarFileName, ws.Classes.Count, ws.AllStrings.Count, ws.LoadTimeMs), false);
        }

        private void BtnSaveJar_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null) return;
            SaveArchiveTo(_workspace.JarFilePath);
        }

        private void BtnSaveAsJar_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null) return;

            SaveFileDialog sfd = new SaveFileDialog
            {
                Title = "Export Patched Java Archive (.JAR)",
                Filter = "Java Archive (*.jar)|*.jar",
                FileName = Path.GetFileNameWithoutExtension(_workspace.JarFileName) + "-patched.jar",
                InitialDirectory = Path.GetDirectoryName(_workspace.JarFilePath)
            };

            if (sfd.ShowDialog() == true)
            {
                SaveArchiveTo(sfd.FileName);
            }
        }

        private void SaveArchiveTo(string targetPath)
        {
            if (_workspace == null) return;

            // Commit any open config text edit if user is currently on ViewResourceEditor
            if (ViewResourceEditor.Visibility == Visibility.Visible &&
                !string.IsNullOrEmpty(_activeResourceEntryPath) &&
                TxtResourceContent.Visibility == Visibility.Visible)
            {
                CommitCurrentTextResource(false);
            }

            try
            {
                int patchedClasses;
                int patchedResources;
                _workspace.SavePatchedJar(targetPath, out patchedClasses, out patchedResources);

                SetStatus(string.Format(
                    "Saved patched archive to '{0}' ({1} bytecode classes patched, {2} resources updated).",
                    Path.GetFileName(targetPath), patchedClasses, patchedResources), false);
            }
            catch (Exception ex)
            {
                SetStatus("Save failed: " + ex.Message, true);
                MessageBox.Show("Failed to write patched .JAR archive:\n" + ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Tree Navigation & View Switching

        private void TxtTreeFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_workspace == null) return;
            string q = (TxtTreeFilter.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q))
            {
                TreeArchive.ItemsSource = _workspace.TreeRoots;
                return;
            }

            List<ArchiveTreeNode> matches = new List<ArchiveTreeNode>();
            foreach (KeyValuePair<string, ArchiveTreeNode> kvp in _workspace.NodeByPath)
            {
                if (kvp.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matches.Add(kvp.Value);
                }
            }
            TreeArchive.ItemsSource = matches;
        }

        private void TreeArchive_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_workspace == null) return;
            ArchiveTreeNode node = TreeArchive.SelectedItem as ArchiveTreeNode;
            if (node == null) return;

            if (node.Kind == JarEntryKind.Folder)
            {
                _scopedPathPrefix = node.FullPath + "/";
                _activeClassEntryPath = null;
                BtnDecompileSelectedClass.IsEnabled = false;
                BtnClearClassScope.Content = "Package: " + node.Name + " (Reset)";
                BtnClearClassScope.Visibility = Visibility.Visible;
                SwitchToStringPoolView();
                ApplyStringFilter();
                return;
            }

            if (node.Kind == JarEntryKind.JavaClass)
            {
                _scopedPathPrefix = node.FullPath;
                _activeClassEntryPath = node.FullPath;
                BtnDecompileSelectedClass.IsEnabled = true;
                BtnClearClassScope.Content = "Class: " + node.Name + " (Reset)";
                BtnClearClassScope.Visibility = Visibility.Visible;

                if (ViewClassDecompiler.Visibility == Visibility.Visible)
                {
                    DecompileAndShowClass(node.FullPath);
                }
                else
                {
                    SwitchToStringPoolView();
                    ApplyStringFilter();
                }
                return;
            }

            if (node.Kind == JarEntryKind.ConfigText || node.Kind == JarEntryKind.ImageAsset || node.Kind == JarEntryKind.BinaryResource)
            {
                OpenResourceInEditor(node.FullPath);
            }
        }

        private void BtnViewGlobalStrings_Click(object sender, RoutedEventArgs e)
        {
            _scopedPathPrefix = null;
            BtnClearClassScope.Visibility = Visibility.Collapsed;
            SwitchToStringPoolView();
            ApplyStringFilter();
        }

        private void BtnClearClassScope_Click(object sender, RoutedEventArgs e)
        {
            _scopedPathPrefix = null;
            BtnClearClassScope.Visibility = Visibility.Collapsed;
            ApplyStringFilter();
        }

        private void SwitchToStringPoolView()
        {
            ViewStringPool.Visibility = Visibility.Visible;
            ViewClassDecompiler.Visibility = Visibility.Collapsed;
            ViewResourceEditor.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region String Filtering, Editing & Batch Replace

        private void FilterCategory_Changed(object sender, RoutedEventArgs e)
        {
            ApplyStringFilter();
        }

        private void TxtSearchStrings_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyStringFilter();
        }

        private void SearchOption_Changed(object sender, RoutedEventArgs e)
        {
            ApplyStringFilter();
        }

        private void ApplyStringFilter()
        {
            if (_workspace == null || GridStrings == null) return;

            string query = (TxtSearchStrings.Text ?? "").Trim();
            bool matchCase = (ChkMatchCase.IsChecked == true);
            bool useRegex = (ChkRegex.IsChecked == true);

            bool onlyMc = (FilterMinecraft.IsChecked == true);
            bool onlyUrls = (FilterUrls.IsChecked == true);
            bool onlyCmds = (FilterCommands.IsChecked == true);
            bool onlyMod = (FilterModified.IsChecked == true);

            Regex rx = null;
            if (useRegex && !string.IsNullOrEmpty(query))
            {
                try
                {
                    rx = new Regex(query, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                }
                catch
                {
                    rx = null;
                }
            }

            StringComparison strCmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            List<ConstantStringItem> filtered = new List<ConstantStringItem>(_workspace.AllStrings.Count);

            for (int i = 0; i < _workspace.AllStrings.Count; i++)
            {
                ConstantStringItem item = _workspace.AllStrings[i];

                if (!string.IsNullOrEmpty(_scopedPathPrefix))
                {
                    if (_scopedPathPrefix.EndsWith("/", StringComparison.Ordinal))
                    {
                        if (!item.EntryPath.StartsWith(_scopedPathPrefix, StringComparison.Ordinal)) continue;
                    }
                    else
                    {
                        if (!string.Equals(item.EntryPath, _scopedPathPrefix, StringComparison.Ordinal)) continue;
                    }
                }

                if (onlyMc && !string.Equals(item.Category, "Minecraft Text", StringComparison.Ordinal)) continue;
                if (onlyUrls && !string.Equals(item.Category, "URL / Endpoint", StringComparison.Ordinal)) continue;
                if (onlyCmds && !string.Equals(item.Category, "Command / Key", StringComparison.Ordinal)) continue;
                if (onlyMod && !item.IsModified) continue;

                if (!string.IsNullOrEmpty(query))
                {
                    if (useRegex)
                    {
                        if (rx == null) continue;
                        if (!rx.IsMatch(item.CurrentValue ?? "") &&
                            !rx.IsMatch(item.OriginalValue ?? "") &&
                            !rx.IsMatch(item.ClassName ?? ""))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        bool hit = (item.CurrentValue != null && item.CurrentValue.IndexOf(query, strCmp) >= 0) ||
                                   (item.OriginalValue != null && item.OriginalValue.IndexOf(query, strCmp) >= 0) ||
                                   (item.ClassName != null && item.ClassName.IndexOf(query, strCmp) >= 0);
                        if (!hit) continue;
                    }
                }

                filtered.Add(item);
            }

            _currentFilteredStrings = filtered;
            GridStrings.ItemsSource = _currentFilteredStrings;
            TxtStatsStrings.Text = string.Format("STRINGS: {0:N0} / {1:N0}", filtered.Count, _workspace.AllStrings.Count);
        }

        private void GridStrings_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ConstantStringItem item = GridStrings.SelectedItem as ConstantStringItem;
            _selectedStringItem = item;

            if (item == null)
            {
                _suppressStringTextChanged = true;
                TxtStringPatchValue.Text = "";
                TxtStringPatchValue.IsEnabled = false;
                _suppressStringTextChanged = false;

                TxtInspectorSlotInfo.Text = " — Select a string row above to edit";
                TxtInspectorByteCount.Text = "0 / 65,535 UTF-8 bytes";
                TxtOriginalValueRef.Text = "Original: -";
                TxtMinecraftPreview.Inlines.Clear();
                BtnRevertSingleString.IsEnabled = false;
                return;
            }

            _activeClassEntryPath = item.EntryPath;
            BtnDecompileSelectedClass.IsEnabled = true;

            _suppressStringTextChanged = true;
            TxtStringPatchValue.IsEnabled = true;
            TxtStringPatchValue.Text = item.CurrentValue;
            _suppressStringTextChanged = false;

            TxtInspectorSlotInfo.Text = string.Format(" — {0}.class [CP #{1} -> Utf8 #{2}]", item.ClassName, item.StringCpIndex, item.Utf8CpIndex);
            TxtOriginalValueRef.Text = "Original: " + item.OriginalValue;
            BtnRevertSingleString.IsEnabled = item.IsModified;

            UpdateStringByteCounterAndPreview(item.CurrentValue);
        }

        private void TxtStringPatchValue_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressStringTextChanged || _selectedStringItem == null || _workspace == null) return;

            string newValue = TxtStringPatchValue.Text ?? "";
            int byteCount = JvmModifiedUtf8.GetByteCount(newValue);

            if (byteCount <= 65535)
            {
                _selectedStringItem.CurrentValue = newValue;
                _workspace.RefreshEntryModifiedState(_selectedStringItem.EntryPath);
                BtnRevertSingleString.IsEnabled = _selectedStringItem.IsModified;
                UpdatePendingCounterUI();
            }

            UpdateStringByteCounterAndPreview(newValue);
        }

        private void UpdateStringByteCounterAndPreview(string text)
        {
            int bytes = JvmModifiedUtf8.GetByteCount(text ?? "");
            TxtInspectorByteCount.Text = string.Format("{0:N0} / 65,535 UTF-8 bytes", bytes);
            TxtInspectorByteCount.Foreground = bytes > 65535
                ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
                : new SolidColorBrush(Color.FromRgb(100, 116, 139));

            RenderMinecraftTextToInlines(TxtMinecraftPreview, text ?? "");
        }

        private void BtnInsertSectionSign_Click(object sender, RoutedEventArgs e)
        {
            InsertIntoStringEditor("§");
        }

        private void BtnColorCode_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            if (b != null && b.Tag != null)
            {
                InsertIntoStringEditor(b.Tag.ToString());
            }
        }

        private void InsertIntoStringEditor(string token)
        {
            if (!TxtStringPatchValue.IsEnabled) return;
            int caret = TxtStringPatchValue.CaretIndex;
            string current = TxtStringPatchValue.Text ?? "";
            TxtStringPatchValue.Text = current.Insert(caret, token);
            TxtStringPatchValue.CaretIndex = caret + token.Length;
            TxtStringPatchValue.Focus();
        }

        private void BtnConvertSelectedAmpersand_Click(object sender, RoutedEventArgs e)
        {
            if (!TxtStringPatchValue.IsEnabled) return;
            TxtStringPatchValue.Text = ConvertAmpersandColorCodes(TxtStringPatchValue.Text ?? "");
        }

        private void BtnRevertSingleString_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedStringItem == null || _workspace == null) return;
            _selectedStringItem.CurrentValue = _selectedStringItem.OriginalValue;
            _suppressStringTextChanged = true;
            TxtStringPatchValue.Text = _selectedStringItem.OriginalValue;
            _suppressStringTextChanged = false;
            _workspace.RefreshEntryModifiedState(_selectedStringItem.EntryPath);
            BtnRevertSingleString.IsEnabled = false;
            UpdateStringByteCounterAndPreview(_selectedStringItem.CurrentValue);
            UpdatePendingCounterUI();
        }

        private void BtnToggleReplaceBar_Click(object sender, RoutedEventArgs e)
        {
            SwitchToStringPoolView();
            PanelBatchReplace.Visibility = (PanelBatchReplace.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (PanelBatchReplace.Visibility == Visibility.Visible)
            {
                TxtBatchFind.Focus();
            }
        }

        private void BtnExecuteBatchReplace_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null) return;
            string findText = TxtBatchFind.Text ?? "";
            string replaceText = TxtBatchReplace.Text ?? "";

            if (string.IsNullOrEmpty(findText))
            {
                SetStatus("Enter a search phrase in the FIND box before running batch replace.", true);
                return;
            }

            try
            {
                int count = _workspace.BatchReplaceStrings(
                    findText,
                    replaceText,
                    ChkMatchCase.IsChecked == true,
                    ChkRegex.IsChecked == true,
                    _currentFilteredStrings);

                if (_selectedStringItem != null)
                {
                    _suppressStringTextChanged = true;
                    TxtStringPatchValue.Text = _selectedStringItem.CurrentValue;
                    _suppressStringTextChanged = false;
                    UpdateStringByteCounterAndPreview(_selectedStringItem.CurrentValue);
                }

                UpdatePendingCounterUI();
                SetStatus(string.Format("Batch replace updated {0:N0} CONSTANT_String entries in current view.", count), false);
            }
            catch (Exception ex)
            {
                SetStatus("Batch replace error: " + ex.Message, true);
            }
        }

        private void BtnBatchConvertAmpersand_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null) return;
            int updatedCount = 0;
            HashSet<string> touched = new HashSet<string>(StringComparer.Ordinal);

            foreach (ConstantStringItem item in _currentFilteredStrings)
            {
                string conv = ConvertAmpersandColorCodes(item.CurrentValue ?? "");
                if (!string.Equals(conv, item.CurrentValue, StringComparison.Ordinal))
                {
                    item.CurrentValue = conv;
                    updatedCount++;
                    touched.Add(item.EntryPath);
                }
            }

            foreach (string p in touched) _workspace.RefreshEntryModifiedState(p);

            if (_selectedStringItem != null)
            {
                _suppressStringTextChanged = true;
                TxtStringPatchValue.Text = _selectedStringItem.CurrentValue;
                _suppressStringTextChanged = false;
                UpdateStringByteCounterAndPreview(_selectedStringItem.CurrentValue);
            }

            UpdatePendingCounterUI();
            SetStatus(string.Format("Converted legacy '&' color codes to '§' across {0:N0} strings.", updatedCount), false);
        }

        public static string ConvertAmpersandColorCodes(string input)
        {
            if (string.IsNullOrEmpty(input) || input.IndexOf('&') < 0) return input;
            char[] chars = input.ToCharArray();
            for (int i = 0; i < chars.Length - 1; i++)
            {
                if (chars[i] == '&')
                {
                    char c = char.ToLowerInvariant(chars[i + 1]);
                    if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'k' && c <= 'o') || c == 'r' || c == 'x')
                    {
                        chars[i] = '§';
                    }
                }
            }
            return new string(chars);
        }

        private void BtnRevertAll_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null) return;
            MessageBoxResult res = MessageBox.Show(
                "Revert all modified strings and edited resources back to their original archive state?",
                "Confirm Revert All",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;

            foreach (ConstantStringItem item in _workspace.AllStrings)
            {
                if (item.IsModified) item.CurrentValue = item.OriginalValue;
            }

            foreach (JarEntryRecord rec in _workspace.Entries.Values)
            {
                rec.ModifiedBytes = null;
            }

            foreach (ArchiveTreeNode node in _workspace.NodeByPath.Values)
            {
                node.IsModified = false;
            }

            if (_selectedStringItem != null)
            {
                _suppressStringTextChanged = true;
                TxtStringPatchValue.Text = _selectedStringItem.CurrentValue;
                _suppressStringTextChanged = false;
                UpdateStringByteCounterAndPreview(_selectedStringItem.CurrentValue);
                BtnRevertSingleString.IsEnabled = false;
            }

            if (!string.IsNullOrEmpty(_activeResourceEntryPath) && ViewResourceEditor.Visibility == Visibility.Visible)
            {
                OpenResourceInEditor(_activeResourceEntryPath);
            }

            ApplyStringFilter();
            UpdatePendingCounterUI();
            SetStatus("All pending modifications reverted to original state.", false);
        }

        #endregion

        #region Class Decompiler View (Vineflower Live Preview)

        private void BtnDecompileSelectedClass_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || string.IsNullOrEmpty(_activeClassEntryPath)) return;
            DecompileAndShowClass(_activeClassEntryPath);
        }

        private void BtnRefreshDecompile_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || string.IsNullOrEmpty(_activeClassEntryPath)) return;
            DecompileAndShowClass(_activeClassEntryPath);
        }

        private void BtnBackToStrings_Click(object sender, RoutedEventArgs e)
        {
            SwitchToStringPoolView();
        }

        private void DecompileAndShowClass(string classEntryPath)
        {
            if (_workspace == null) return;

            ViewStringPool.Visibility = Visibility.Collapsed;
            ViewResourceEditor.Visibility = Visibility.Collapsed;
            ViewClassDecompiler.Visibility = Visibility.Visible;

            TxtDecompClassName.Text = classEntryPath;
            ParsedClassMetadata meta;
            if (_workspace.Classes.TryGetValue(classEntryPath, out meta))
            {
                TxtClassMetadataSummary.Text = string.Format(
                    "{0}  |  Access: {1}  |  Class: {2}  |  Super: {3}  |  CP Slots: {4}  |  Fields: {5}  |  Methods: {6}",
                    meta.JavaVersionDisplay,
                    string.IsNullOrEmpty(meta.AccessFlagsDisplay) ? "default" : meta.AccessFlagsDisplay,
                    meta.ThisClassName,
                    meta.SuperClassName,
                    meta.ConstantPoolCount,
                    meta.Fields.Count,
                    meta.Methods.Count);
            }

            TxtDecompiledCode.Text = "// Decompiling patched bytecode with Vineflower 1.12.0...";
            SetStatus("Decompiling " + Path.GetFileName(classEntryPath) + " (including live Constant Pool patches)...", false);

            byte[] effectiveBytes = _workspace.GetEffectiveClassBytes(classEntryPath);
            Dictionary<string, byte[]> innerBytes = _workspace.GetInnerClassBytesFor(classEntryPath);

            Thread t = new Thread(() =>
            {
                string javaSource = VineflowerRunner.DecompileClassBytes(classEntryPath, effectiveBytes, innerBytes);
                Dispatcher.Invoke(new Action(() =>
                {
                    TxtDecompiledCode.Text = javaSource;
                    SetStatus("Decompiled " + Path.GetFileName(classEntryPath) + " with live Constant Pool state.", false);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        #endregion

        #region Config Text & Image Asset Editor

        private void OpenResourceInEditor(string entryPath)
        {
            if (_workspace == null) return;
            JarEntryRecord rec;
            if (!_workspace.Entries.TryGetValue(entryPath, out rec)) return;

            _activeResourceEntryPath = entryPath;
            ViewStringPool.Visibility = Visibility.Collapsed;
            ViewClassDecompiler.Visibility = Visibility.Collapsed;
            ViewResourceEditor.Visibility = Visibility.Visible;

            byte[] activeBytes = rec.ActiveBytes ?? new byte[0];
            TxtResourcePath.Text = entryPath;
            TxtResourceInfo.Text = string.Format(" ({0:N0} bytes{1})", activeBytes.Length, rec.IsResourceModified ? " • MODIFIED" : "");

            if (rec.Kind == JarEntryKind.ImageAsset)
            {
                TxtResourceContent.Visibility = Visibility.Collapsed;
                PanelImagePreview.Visibility = Visibility.Visible;
                BtnApplyTextResource.Visibility = Visibility.Collapsed;
                BtnReplaceImageAsset.Visibility = Visibility.Visible;

                try
                {
                    using (MemoryStream ms = new MemoryStream(activeBytes))
                    {
                        BitmapImage bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        ImgAssetPreview.Source = bmp;
                        TxtImageDimensions.Text = string.Format("{0} x {1} px  •  {2:N0} bytes", bmp.PixelWidth, bmp.PixelHeight, activeBytes.Length);
                    }
                }
                catch (Exception ex)
                {
                    ImgAssetPreview.Source = null;
                    TxtImageDimensions.Text = "Image preview unavailable: " + ex.Message;
                }
            }
            else if (rec.Kind == JarEntryKind.ConfigText)
            {
                PanelImagePreview.Visibility = Visibility.Collapsed;
                TxtResourceContent.Visibility = Visibility.Visible;
                BtnReplaceImageAsset.Visibility = Visibility.Collapsed;
                BtnApplyTextResource.Visibility = Visibility.Visible;
                TxtResourceContent.IsReadOnly = false;
                TxtResourceContent.Text = Encoding.UTF8.GetString(activeBytes);
            }
            else
            {
                PanelImagePreview.Visibility = Visibility.Collapsed;
                TxtResourceContent.Visibility = Visibility.Visible;
                BtnReplaceImageAsset.Visibility = Visibility.Collapsed;
                BtnApplyTextResource.Visibility = Visibility.Collapsed;
                TxtResourceContent.IsReadOnly = true;
                TxtResourceContent.Text = FormatHexPreview(activeBytes, 2048);
            }
        }

        private static string FormatHexPreview(byte[] data, int maxBytes)
        {
            if (data == null || data.Length == 0) return "// Empty binary entry";
            int len = Math.Min(data.Length, maxBytes);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("// Binary resource ({0:N0} bytes total, showing first {1:N0} bytes)", data.Length, len));
            sb.AppendLine();

            for (int i = 0; i < len; i += 16)
            {
                sb.Append(i.ToString("X6")).Append("  ");
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < len) sb.Append(data[i + j].ToString("X2")).Append(' ');
                    else sb.Append("   ");
                }
                sb.Append(" |");
                for (int j = 0; j < 16 && i + j < len; j++)
                {
                    byte b = data[i + j];
                    sb.Append((b >= 32 && b <= 126) ? (char)b : '.');
                }
                sb.AppendLine("|");
            }
            return sb.ToString();
        }

        private void BtnApplyTextResource_Click(object sender, RoutedEventArgs e)
        {
            CommitCurrentTextResource(true);
        }

        private void CommitCurrentTextResource(bool notifyStatus)
        {
            if (_workspace == null || string.IsNullOrEmpty(_activeResourceEntryPath)) return;
            JarEntryRecord rec;
            if (!_workspace.Entries.TryGetValue(_activeResourceEntryPath, out rec)) return;
            if (rec.Kind != JarEntryKind.ConfigText) return;

            byte[] updatedBytes = Encoding.UTF8.GetBytes(TxtResourceContent.Text ?? "");
            string origText = Encoding.UTF8.GetString(rec.OriginalBytes ?? new byte[0]);

            if (string.Equals(origText, TxtResourceContent.Text ?? "", StringComparison.Ordinal))
            {
                rec.ModifiedBytes = null;
            }
            else
            {
                rec.ModifiedBytes = updatedBytes;
            }

            _workspace.RefreshEntryModifiedState(_activeResourceEntryPath);
            TxtResourceInfo.Text = string.Format(" ({0:N0} bytes{1})", rec.ActiveBytes.Length, rec.IsResourceModified ? " • MODIFIED" : "");
            UpdatePendingCounterUI();

            if (notifyStatus)
            {
                SetStatus("Applied changes to resource '" + _activeResourceEntryPath + "' in memory.", false);
            }
        }

        private void BtnReplaceImageAsset_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || string.IsNullOrEmpty(_activeResourceEntryPath)) return;
            JarEntryRecord rec;
            if (!_workspace.Entries.TryGetValue(_activeResourceEntryPath, out rec)) return;

            OpenFileDialog ofd = new OpenFileDialog
            {
                Title = "Select Replacement Image File",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif|All Files (*.*)|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                rec.ModifiedBytes = File.ReadAllBytes(ofd.FileName);
                _workspace.RefreshEntryModifiedState(_activeResourceEntryPath);
                OpenResourceInEditor(_activeResourceEntryPath);
                UpdatePendingCounterUI();
                SetStatus("Replaced image asset '" + _activeResourceEntryPath + "' with '" + Path.GetFileName(ofd.FileName) + "'.", false);
            }
        }

        private void BtnRevertResource_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || string.IsNullOrEmpty(_activeResourceEntryPath)) return;
            JarEntryRecord rec;
            if (!_workspace.Entries.TryGetValue(_activeResourceEntryPath, out rec)) return;

            rec.ModifiedBytes = null;
            _workspace.RefreshEntryModifiedState(_activeResourceEntryPath);
            OpenResourceInEditor(_activeResourceEntryPath);
            UpdatePendingCounterUI();
            SetStatus("Reverted resource '" + _activeResourceEntryPath + "' to original archive bytes.", false);
        }

        #endregion

        #region Live Minecraft Color & Hex Renderer

        private static void RenderMinecraftTextToInlines(TextBlock target, string raw)
        {
            target.Inlines.Clear();
            if (string.IsNullOrEmpty(raw)) return;

            Brush currentBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            bool bold = false;
            bool italic = false;
            bool underline = false;
            bool strikethrough = false;

            StringBuilder chunk = new StringBuilder();
            int i = 0;
            int len = raw.Length;

            Action flushChunk = () =>
            {
                if (chunk.Length == 0) return;
                Run run = new Run(chunk.ToString())
                {
                    Foreground = currentBrush,
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = italic ? FontStyles.Italic : FontStyles.Normal
                };
                if (underline || strikethrough)
                {
                    TextDecorationCollection decs = new TextDecorationCollection();
                    if (underline) decs.Add(TextDecorations.Underline[0]);
                    if (strikethrough) decs.Add(TextDecorations.Strikethrough[0]);
                    run.TextDecorations = decs;
                }
                target.Inlines.Add(run);
                chunk.Clear();
            };

            while (i < len)
            {
                char c = raw[i];

                // Check &#RRGGBB or §#RRGGBB hex code
                if ((c == '§' || c == '&') && i + 7 < len && raw[i + 1] == '#')
                {
                    string hex = raw.Substring(i + 2, 6);
                    Color parsedColor;
                    if (TryParseHexColor(hex, out parsedColor))
                    {
                        flushChunk();
                        currentBrush = new SolidColorBrush(parsedColor);
                        bold = italic = underline = strikethrough = false;
                        i += 8;
                        continue;
                    }
                }

                // Standard § / & formatting code
                if ((c == '§' || c == '&') && i + 1 < len)
                {
                    char code = char.ToLowerInvariant(raw[i + 1]);
                    Color stdColor;
                    if (TryGetMinecraftColor(code, out stdColor))
                    {
                        flushChunk();
                        currentBrush = new SolidColorBrush(stdColor);
                        bold = italic = underline = strikethrough = false;
                        i += 2;
                        continue;
                    }
                    else if (code == 'l') { flushChunk(); bold = true; i += 2; continue; }
                    else if (code == 'o') { flushChunk(); italic = true; i += 2; continue; }
                    else if (code == 'n') { flushChunk(); underline = true; i += 2; continue; }
                    else if (code == 'm') { flushChunk(); strikethrough = true; i += 2; continue; }
                    else if (code == 'r')
                    {
                        flushChunk();
                        currentBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                        bold = italic = underline = strikethrough = false;
                        i += 2;
                        continue;
                    }
                }

                chunk.Append(c);
                i++;
            }

            flushChunk();
        }

        private static bool TryParseHexColor(string hex6, out Color color)
        {
            color = Colors.White;
            if (string.IsNullOrEmpty(hex6) || hex6.Length != 6) return false;
            try
            {
                byte r = Convert.ToByte(hex6.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex6.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex6.Substring(4, 2), 16);
                color = Color.FromRgb(r, g, b);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetMinecraftColor(char code, out Color color)
        {
            switch (code)
            {
                case '0': color = Color.FromRgb(0, 0, 0); return true;
                case '1': color = Color.FromRgb(0, 0, 170); return true;
                case '2': color = Color.FromRgb(0, 170, 0); return true;
                case '3': color = Color.FromRgb(0, 170, 170); return true;
                case '4': color = Color.FromRgb(170, 0, 0); return true;
                case '5': color = Color.FromRgb(170, 0, 170); return true;
                case '6': color = Color.FromRgb(255, 170, 0); return true;
                case '7': color = Color.FromRgb(170, 170, 170); return true;
                case '8': color = Color.FromRgb(85, 85, 85); return true;
                case '9': color = Color.FromRgb(85, 85, 255); return true;
                case 'a': color = Color.FromRgb(85, 255, 85); return true;
                case 'b': color = Color.FromRgb(85, 255, 255); return true;
                case 'c': color = Color.FromRgb(255, 85, 85); return true;
                case 'd': color = Color.FromRgb(255, 85, 255); return true;
                case 'e': color = Color.FromRgb(255, 255, 85); return true;
                case 'f': color = Color.FromRgb(255, 255, 255); return true;
                default:
                    color = Colors.White;
                    return false;
            }
        }

        #endregion

        #region Status & Counter Helpers

        private void UpdatePendingCounterUI()
        {
            if (_workspace == null) return;
            int modStrings = _workspace.GetModifiedStringsCount();
            int modRes = _workspace.GetModifiedResourcesCount();
            int totalMod = modStrings + modRes;

            BtnRevertAll.IsEnabled = (totalMod > 0);

            if (totalMod > 0)
            {
                DotPendingIndicator.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                TxtPendingChanges.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                TxtPendingChanges.Text = string.Format("{0} string(s), {1} file(s) modified", modStrings, modRes);
                BadgePendingChanges.BorderBrush = new SolidColorBrush(Color.FromRgb(120, 75, 20));
            }
            else
            {
                DotPendingIndicator.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                TxtPendingChanges.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                TxtPendingChanges.Text = "0 pending modifications";
                BadgePendingChanges.BorderBrush = new SolidColorBrush(Color.FromRgb(40, 48, 68));
            }
        }

        private void SetStatus(string message, bool isWarning)
        {
            TxtStatusBar.Text = message;
            DotStatusBar.Fill = isWarning
                ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                : new SolidColorBrush(Color.FromRgb(59, 130, 246));
        }

        #endregion
    }
}
