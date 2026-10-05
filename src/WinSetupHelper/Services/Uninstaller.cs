using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinSetupHelper.Services
{
    /// <summary>
    /// Hỗ trợ gỡ cài đặt: đóng ứng dụng đang chạy và chạy trình gỡ cài đặt của ứng dụng
    /// (lấy từ "Programs and Features") ở chế độ im lặng khi winget gỡ không thành công.
    /// </summary>
    public static class Uninstaller
    {
        private static readonly Regex GuidRegex = new Regex(@"\{[0-9A-Fa-f\-]{36}\}", RegexOptions.Compiled);

        /// <summary>Tìm mục trong "Programs and Features" theo tên hiển thị hoặc mẫu tên.</summary>
        public static UninstallEntry FindEntry(string installedName, IEnumerable<string> patterns)
        {
            var entries = InstalledApps.ReadUninstallEntries();
            if (!string.IsNullOrEmpty(installedName))
            {
                var exact = entries.FirstOrDefault(e => string.Equals(e.DisplayName, installedName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            foreach (var p in patterns ?? Enumerable.Empty<string>())
            {
                var match = entries.FirstOrDefault(e => InstalledIndex.NameMatches(e.DisplayName, p));
                if (match != null) return match;
            }
            return null;
        }

        public static bool EntryExists(UninstallEntry entry) =>
            entry != null && InstalledApps.ReadUninstallEntries().Any(e =>
                string.Equals(e.KeyName, entry.KeyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.DisplayName, entry.DisplayName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Thư mục cài đặt của ứng dụng (dùng để tìm tiến trình cần đóng).</summary>
        public static string InstallDirOf(UninstallEntry entry)
        {
            if (entry == null) return null;
            var candidates = new List<string> { entry.InstallLocation };
            var icon = entry.DisplayIcon?.Split(',')[0].Trim().Trim('"');
            if (!string.IsNullOrEmpty(icon)) candidates.Add(SafeDir(icon));
            var uninst = InstalledApps.ExtractExePath(entry.UninstallString);
            if (!string.IsNullOrEmpty(uninst) && uninst.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) < 0)
                candidates.Add(SafeDir(uninst));

            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var dir = c.Trim().Trim('"').TrimEnd('\\');
                if (IsSafeAppDir(dir) && Directory.Exists(dir)) return dir;
            }
            return null;
        }

        /// <summary>
        /// Đóng các tiến trình của ứng dụng (theo tên exe hoặc nằm trong thư mục cài đặt),
        /// vì trình gỡ cài đặt thường lỗi khi ứng dụng đang chạy (vd. Greenshot ở khay hệ thống).
        /// </summary>
        public static Task<int> CloseProcessesAsync(IEnumerable<string> exeNames, string installDir, Action<string> log)
        {
            var names = new HashSet<string>((exeNames ?? Enumerable.Empty<string>())
                .Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            var self = Process.GetCurrentProcess().Id;

            return Task.Run(() =>
            {
                var closed = 0;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == self) continue;
                        var match = names.Contains(p.ProcessName);
                        if (!match && installDir != null)
                        {
                            string path = null;
                            try { path = p.MainModule?.FileName; } catch { }
                            match = path != null && path.StartsWith(installDir + "\\", StringComparison.OrdinalIgnoreCase);
                        }
                        if (!match) continue;

                        log($"   Đóng ứng dụng đang chạy: {p.ProcessName} (PID {p.Id})");
                        try
                        {
                            if (p.CloseMainWindow() && p.WaitForExit(3000)) { closed++; continue; }
                        }
                        catch { }
                        p.Kill();
                        p.WaitForExit(5000);
                        closed++;
                    }
                    catch (Exception ex)
                    {
                        log($"   Không đóng được {SafeName(p)}: {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
                return closed;
            });
        }

        /// <summary>
        /// Chạy trình gỡ cài đặt của ứng dụng ở chế độ im lặng.
        /// Trả về mã thoát, hoặc null nếu không có lệnh gỡ cài đặt.
        /// </summary>
        public static Task<int?> RunAsync(UninstallEntry entry, Action<string> log)
        {
            return Task.Run<int?>(() =>
            {
                var (file, args) = BuildSilentCommand(entry);
                if (file == null) return null;

                log($"   Chạy trình gỡ cài đặt: {file} {args}");
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
                if (File.Exists(file)) psi.WorkingDirectory = Path.GetDirectoryName(file);
                using (var p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    if (!p.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds))
                    {
                        try { p.Kill(); } catch { }
                        return -2;
                    }
                    return p.ExitCode;
                }
            });
        }

        /// <summary>
        /// Chờ mục gỡ cài đặt biến mất khỏi Registry. Trình gỡ của Inno Setup / NSIS tự chép
        /// sang thư mục tạm rồi thoát ngay, nên cần chờ thêm.
        /// </summary>
        public static async Task WaitForRemovalAsync(UninstallEntry entry, TimeSpan timeout)
        {
            var until = DateTime.Now + timeout;
            while (DateTime.Now < until)
            {
                if (!await Task.Run(() => EntryExists(entry))) return;
                await Task.Delay(2000);
            }
        }

        private static (string file, string args) BuildSilentCommand(UninstallEntry entry)
        {
            // 1. MSI: msiexec /x {GUID} /qn
            var guid = GuidRegex.Match(entry.KeyName ?? "");
            if (!guid.Success && (entry.UninstallString ?? "").IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                guid = GuidRegex.Match(entry.UninstallString);
            if (guid.Success && (entry.IsMsi || (entry.UninstallString ?? "").IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0))
                return ("msiexec.exe", $"/x {guid.Value} /qn /norestart");

            // 2. Lệnh gỡ im lặng do chính ứng dụng cung cấp
            if (!string.IsNullOrWhiteSpace(entry.QuietUninstallString))
                return Split(entry.QuietUninstallString);

            if (string.IsNullOrWhiteSpace(entry.UninstallString)) return (null, null);
            var (file, args) = Split(entry.UninstallString);

            // 3. Thêm tham số im lặng theo loại trình gỡ
            var kind = DetectInstallerKind(file);
            if (kind == "inno" && args.IndexOf("/VERYSILENT", StringComparison.OrdinalIgnoreCase) < 0)
                args = (args + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim();
            else if (kind == "nsis" && !Regex.IsMatch(args, @"(^|\s)/S(\s|$)"))
                args = (args + " /S").Trim();

            return (file, args);
        }

        private static (string file, string args) Split(string command)
        {
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            var file = InstalledApps.ExtractExePath(command);
            if (string.IsNullOrEmpty(file)) return (null, null);

            var rest = command.StartsWith("\"")
                ? command.Substring(Math.Min(command.Length, file.Length + 2))
                : command.Substring(Math.Min(command.Length, file.Length));
            return (file, rest.Trim());
        }

        /// <summary>Nhận diện trình gỡ Inno Setup / NSIS qua tên file và chữ ký trong file exe.</summary>
        private static string DetectInstallerKind(string file)
        {
            var name = Path.GetFileName(file) ?? "";
            if (Regex.IsMatch(name, @"^unins\d+\.exe$", RegexOptions.IgnoreCase)) return "inno";
            try
            {
                if (!File.Exists(file)) return null;
                var buffer = new byte[Math.Min(new FileInfo(file).Length, 4 * 1024 * 1024)];
                using (var fs = File.OpenRead(file))
                {
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = fs.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                var text = Encoding.ASCII.GetString(buffer);
                if (text.Contains("Nullsoft") || text.Contains("NullsoftInst")) return "nsis";
                if (text.Contains("Inno Setup")) return "inno";
            }
            catch { /* không đọc được file */ }
            return null;
        }

        private static bool IsSafeAppDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || dir.Length < 4) return false;
            var forbidden = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.GetTempPath()
            };
            if (forbidden.Any(f => !string.IsNullOrEmpty(f) &&
                                   string.Equals(f.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)))
                return false;
            // Không nhận thư mục gốc ổ đĩa (vd. C:\)
            return Path.GetPathRoot(dir)?.TrimEnd('\\') != dir;
        }

        private static string SafeDir(string path)
        {
            try { return Path.GetDirectoryName(path); } catch { return null; }
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; } catch { return "?"; }
        }
    }
}
