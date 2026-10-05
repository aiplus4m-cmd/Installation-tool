using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinSetupHelper.Services
{
    /// <summary>
    /// Tổng hợp các ứng dụng đã cài: mã gói winget (export + list), tên hiển thị trong
    /// "Programs and Features", gói Store/MSIX (Appx) và file exe của bản portable.
    /// </summary>
    public sealed class InstalledIndex
    {
        private readonly HashSet<string> _ids;
        private readonly List<string> _names;
        private readonly HashSet<string> _appx;
        private readonly Dictionary<string, string> _exes;

        public InstalledIndex(IEnumerable<string> ids, IEnumerable<string> names,
            IEnumerable<string> appx = null, IDictionary<string, string> exes = null)
        {
            _ids = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _names = (names ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _appx = new HashSet<string>(appx ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _exes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (exes != null)
                foreach (var kv in exes) _exes[kv.Key] = kv.Value;
        }

        public static InstalledIndex Empty { get; } = new InstalledIndex(null, null);

        public int IdCount => _ids.Count;
        public int NameCount => _names.Count;
        public int AppxCount => _appx.Count;

        public void AddId(string id)
        {
            if (!string.IsNullOrEmpty(id)) _ids.Add(id);
        }

        public void RemoveId(string id)
        {
            if (!string.IsNullOrEmpty(id)) _ids.Remove(id);
        }

        /// <summary>
        /// Tìm mã gói đã cài khớp với mã trong danh mục: khớp chính xác, hoặc biến thể
        /// như "Google.Chrome.EXE", "Mozilla.Firefox.vi".
        /// </summary>
        public string FindId(string catalogId)
        {
            if (string.IsNullOrEmpty(catalogId)) return null;
            if (_ids.Contains(catalogId)) return _ids.First(i => string.Equals(i, catalogId, StringComparison.OrdinalIgnoreCase));
            var prefix = catalogId + ".";
            return _ids.Where(i => i.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                       .OrderBy(i => i.Length)
                       .FirstOrDefault();
        }

        /// <summary>Tìm tên hiển thị trong Registry khớp với một trong các mẫu.</summary>
        public string FindName(IEnumerable<string> patterns)
        {
            foreach (var p in patterns ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var match = _names.FirstOrDefault(n => NameMatches(n, p.Trim()));
                if (match != null) return match;
            }
            return null;
        }

        /// <summary>Tìm gói Store/MSIX đã cài theo tên gói (vd. Microsoft.WindowsTerminal).</summary>
        public string FindAppx(IEnumerable<string> names) =>
            (names ?? Enumerable.Empty<string>()).FirstOrDefault(n => !string.IsNullOrEmpty(n) && _appx.Contains(n));

        /// <summary>Tìm đường dẫn file exe (bản portable) theo tên file.</summary>
        public string FindExe(IEnumerable<string> exeNames)
        {
            foreach (var e in exeNames ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(e) && _exes.TryGetValue(e, out var path))
                    return path;
            return null;
        }

        /// <summary>
        /// Mẫu có dấu * được hiểu là ký tự đại diện. Mẫu thường khớp khi tên bằng mẫu,
        /// hoặc bắt đầu bằng mẫu và ký tự kế tiếp không phải chữ/số
        /// ("Mozilla Firefox (x64 vi)" khớp "Mozilla Firefox", còn "GitHub Desktop" không khớp "Git").
        /// </summary>
        public static bool NameMatches(string displayName, string pattern)
        {
            if (pattern.IndexOf('*') >= 0)
            {
                var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(displayName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            if (!displayName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return false;
            return displayName.Length == pattern.Length || !char.IsLetterOrDigit(displayName[pattern.Length]);
        }
    }

    public static class InstalledApps
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string AppxRepositoryKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "WindowsApps", "$Recycle.Bin", "System Volume Information", "ProgramData",
            "node_modules", ".git", "Temp", "Packages", "Microsoft.NET", "WinSxS", "Installer",
            "Windows Defender", "Windows Kits", "Reference Assemblies", "dotnet", "Common Files",
            "Recovery", "PerfLogs", "Config.Msi", "Users", "Program Files", "Program Files (x86)"
        };

        /// <summary>Đọc tên các ứng dụng trong "Programs and Features".</summary>
        public static List<string> ReadDisplayNames()
        {
            var names = new List<string>();
            var sources = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
                (RegistryHive.CurrentUser, RegistryView.Registry32)
            };

            foreach (var (hive, view) in sources)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(UninstallKey))
                    {
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var app = key.OpenSubKey(sub))
                                {
                                    var name = app?.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(name)) continue;
                                    if (app.GetValue("ParentKeyName") != null) continue; // bản cập nhật con
                                    names.Add(name.Trim());
                                }
                            }
                            catch { /* bỏ qua khóa không đọc được */ }
                        }
                    }
                }
                catch { /* hive/view không khả dụng */ }
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Đọc tên các gói Store/MSIX (Appx) đã cài cho người dùng hiện tại,
        /// vd. "Microsoft.WindowsTerminal" (Windows Terminal có sẵn trong Windows 11).
        /// </summary>
        public static HashSet<string> ReadAppxNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AppxRepositoryKey))
                {
                    if (key == null) return set;
                    foreach (var full in key.GetSubKeyNames())
                    {
                        // PackageFullName = Name_Version_Arch_ResourceId_PublisherId
                        var idx = full.IndexOf('_');
                        set.Add(idx > 0 ? full.Substring(0, idx) : full);
                    }
                }
            }
            catch { /* không đọc được */ }
            return set;
        }

        /// <summary>
        /// Tìm file exe của ứng dụng (kể cả bản portable không đăng ký với Windows) qua:
        /// tiến trình đang chạy, mục khởi động cùng Windows, App Paths, shortcut ở
        /// Desktop/Start Menu, và quét các thư mục thường dùng.
        /// Trả về: tên exe → đường dẫn đầy đủ.
        /// </summary>
        public static Dictionary<string, string> FindExecutables(IEnumerable<string> exeNames, Action<string> log)
        {
            var wanted = new HashSet<string>(exeNames.Where(e => !string.IsNullOrWhiteSpace(e)), StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return found;

            void Consider(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                    var name = Path.GetFileName(path);
                    if (!wanted.Contains(name) || found.ContainsKey(name)) return;
                    if (File.Exists(path)) found[name] = path;
                }
                catch { /* đường dẫn không hợp lệ */ }
            }

            // 1. Tiến trình đang chạy
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (wanted.Contains(p.ProcessName + ".exe")) Consider(p.MainModule?.FileName);
                }
                catch { /* tiến trình hệ thống, không có quyền */ }
                finally { p.Dispose(); }
            }

            // 2. Mục khởi động cùng Windows và App Paths
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var runKey in new[]
                         {
                             @"Software\Microsoft\Windows\CurrentVersion\Run",
                             @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
                         })
                {
                    try
                    {
                        using (var key = hive.OpenSubKey(runKey))
                        {
                            if (key == null) continue;
                            foreach (var v in key.GetValueNames())
                                Consider(ExtractExePath(key.GetValue(v) as string));
                        }
                    }
                    catch { }
                }

                foreach (var exe in wanted.ToList())
                {
                    try
                    {
                        using (var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                            Consider(key?.GetValue(null) as string);
                    }
                    catch { }
                }
            }

            // 3. Shortcut ở Desktop / Start Menu
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                     })
            {
                foreach (var lnk in SafeFiles(dir, "*.lnk", 3))
                    Consider(Shortcut.GetTarget(lnk));
            }

            // 4. Quét thư mục thường dùng
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<(string, int)>
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 3),
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), 3),
                (Path.Combine(local, "Programs"), 3),
                (Path.Combine(local, @"Microsoft\WinGet\Packages"), 3),
                (Path.Combine(local, @"Microsoft\WinGet\Links"), 0),
                (local, 2),
                (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 2),
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 3),
                (Path.Combine(profile, "Downloads"), 3),
                (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 2)
            };
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        roots.Add((drive.RootDirectory.FullName, 3));
                }
                catch { }
            }

            foreach (var (root, depth) in roots)
            {
                if (found.Count == wanted.Count) break;
                foreach (var exe in SafeFiles(root, "*.exe", depth))
                    Consider(exe);
            }

            return found;
        }

        /// <summary>Lấy đường dẫn exe từ một dòng lệnh (có thể có ngoặc kép và tham số).</summary>
        private static string ExtractExePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : null;
            }
            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? command.Substring(0, idx + 4) : command;
        }

        /// <summary>Liệt kê file theo mẫu, giới hạn độ sâu và bỏ qua thư mục hệ thống / không có quyền.</summary>
        private static IEnumerable<string> SafeFiles(string root, string pattern, int maxDepth)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;

            var stack = new Stack<(string, int)>();
            stack.Push((root, 0));
            var visited = 0;
            while (stack.Count > 0 && visited < 30000)
            {
                var (dir, depth) = stack.Pop();
                visited++;

                string[] files;
                try { files = Directory.GetFiles(dir, pattern); }
                catch { continue; }
                foreach (var f in files) yield return f;

                if (depth >= maxDepth) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var s in subs)
                {
                    var name = Path.GetFileName(s);
                    if (SkipDirs.Contains(name)) continue;
                    try
                    {
                        if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push((s, depth + 1));
                }
            }
        }
    }

    /// <summary>Đọc / tạo shortcut (.lnk) qua WScript.Shell.</summary>
    public static class Shortcut
    {
        public static string GetTarget(string lnkPath)
        {
            try
            {
                var shell = CreateShell();
                var sc = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                return sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
            }
            catch
            {
                return null;
            }
        }

        public static void Create(string lnkPath, string target)
        {
            var shell = CreateShell();
            var sc = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            var t = sc.GetType();
            t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
        }

        [ThreadStatic] private static object _shell;

        private static object CreateShell() =>
            _shell ?? (_shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")));
    }
}
