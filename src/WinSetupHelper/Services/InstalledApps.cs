using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinSetupHelper.Services
{
    /// <summary>
    /// Tổng hợp các ứng dụng đã cài: mã gói winget (export + list) và tên hiển thị
    /// trong "Programs and Features" (Registry Uninstall).
    /// </summary>
    public sealed class InstalledIndex
    {
        private readonly HashSet<string> _ids;
        private readonly List<string> _names;

        public InstalledIndex(IEnumerable<string> ids, IEnumerable<string> names)
        {
            _ids = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _names = (names ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static InstalledIndex Empty { get; } = new InstalledIndex(null, null);

        public int IdCount => _ids.Count;
        public int NameCount => _names.Count;

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
    }
}
