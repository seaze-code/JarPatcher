using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace JarPatcher.Engine
{
    public static class VineflowerRunner
    {
        private static string _javaExe;
        private static string _vineflowerJar;
        private static string _jsaArchive;
        private static bool? _resolved;

        public static bool IsAvailable
        {
            get
            {
                if (!_resolved.HasValue)
                {
                    _resolved = ResolveToolchain();
                }
                return _resolved.Value;
            }
        }

        private static bool ResolveToolchain()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] javaPaths = new string[]
            {
                @"D:\JarDecompiler\jre\bin\java.exe",
                Path.Combine(baseDir, "jre", "bin", "java.exe"),
                Path.Combine(baseDir, "..", "JarDecompiler", "jre", "bin", "java.exe")
            };

            string[] vfPaths = new string[]
            {
                @"D:\JarDecompiler\vineflower.jar",
                Path.Combine(baseDir, "vineflower.jar"),
                Path.Combine(baseDir, "..", "JarDecompiler", "vineflower.jar")
            };

            foreach (string jp in javaPaths)
            {
                try
                {
                    if (File.Exists(jp))
                    {
                        _javaExe = Path.GetFullPath(jp);
                        break;
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(_javaExe))
            {
                string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrEmpty(javaHome))
                {
                    string candidate = Path.Combine(javaHome, "bin", "java.exe");
                    if (File.Exists(candidate)) _javaExe = candidate;
                }
            }

            foreach (string vp in vfPaths)
            {
                try
                {
                    if (File.Exists(vp))
                    {
                        _vineflowerJar = Path.GetFullPath(vp);
                        break;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(_javaExe))
            {
                try
                {
                    string binDir = Path.GetDirectoryName(_javaExe) ?? "";
                    string candidateJsa = Path.Combine(binDir, "server", "vineflower.jsa");
                    if (File.Exists(candidateJsa))
                    {
                        _jsaArchive = candidateJsa;
                    }
                }
                catch { }
            }

            return !string.IsNullOrEmpty(_javaExe) && !string.IsNullOrEmpty(_vineflowerJar);
        }

        public static string DecompileClassBytes(string classEntryPath, byte[] mainClassBytes, Dictionary<string, byte[]> innerClassesBytes)
        {
            if (!IsAvailable)
            {
                return "// Vineflower decompiler runtime not found.\n// Place 'vineflower.jar' and 'jre/' in D:\\JarDecompiler or application directory.";
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "JarPatcher_Decomp_" + Guid.NewGuid().ToString("N"));
            string inDir = Path.Combine(tempRoot, "in");
            string outDir = Path.Combine(tempRoot, "out");

            try
            {
                Directory.CreateDirectory(inDir);
                Directory.CreateDirectory(outDir);

                string mainFileName = Path.GetFileName(classEntryPath);
                File.WriteAllBytes(Path.Combine(inDir, mainFileName), mainClassBytes);

                if (innerClassesBytes != null)
                {
                    foreach (KeyValuePair<string, byte[]> kvp in innerClassesBytes)
                    {
                        string innerName = Path.GetFileName(kvp.Key);
                        if (!string.IsNullOrEmpty(innerName))
                        {
                            File.WriteAllBytes(Path.Combine(inDir, innerName), kvp.Value);
                        }
                    }
                }

                string cdsArg = (!string.IsNullOrEmpty(_jsaArchive) && File.Exists(_jsaArchive))
                    ? string.Format("-Xshare:auto -XX:SharedArchiveFile=\"{0}\" ", _jsaArchive)
                    : "";

                string args = string.Format(
                    "{0}-XX:TieredStopAtLevel=1 -XX:+UseParallelGC -Xms128M -Xmx2G -jar \"{1}\" " +
                    "-s --folder " +
                    "--decompile-generics=true --decompile-enums=true --decompile-finally=true " +
                    "--decompile-switch-expressions=true --pattern-matching=true " +
                    "--simplify-stack=true --use-lvt-names=true --use-method-parameters=true " +
                    "--ascii-strings=false --indent-string=\"    \" " +
                    "\"{2}\" \"{3}\"",
                    cdsArg,
                    _vineflowerJar,
                    inDir,
                    outDir
                );

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = _javaExe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                };

                using (Process proc = Process.Start(psi))
                {
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(20000))
                    {
                        try { proc.Kill(); } catch { }
                        return "// Decompilation timed out.";
                    }
                }

                string[] files = Directory.GetFiles(outDir, "*.*", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    return File.ReadAllText(files[0], Encoding.UTF8);
                }

                return "// No decompiled output produced by Vineflower.";
            }
            catch (Exception ex)
            {
                return "// Decompilation error: " + ex.Message;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                }
                catch { }
            }
        }
    }
}
