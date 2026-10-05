using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinSetupHelper.Services
{
    public sealed class ProcResult
    {
        public int ExitCode { get; set; }
        public List<string> Lines { get; } = new List<string>();
    }

    public sealed class PackageInfo
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Version { get; set; }
    }

    /// <summary>Bao bọc lệnh winget (Windows Package Manager).</summary>
    public static class Winget
    {
        public const int NoApplicationsFound = unchecked((int)0x8A150014);
        public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
        public const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
        public const int PackageInUse = unchecked((int)0x8A150101);
        public const int InstallInProgress = unchecked((int)0x8A150102);
        public const int RebootRequiredToFinish = unchecked((int)0x8A150109);

        private const string Agreements = "--accept-source-agreements";

        private static readonly Regex AnsiRegex = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex SizeRegex = new Regex(
            @"([\d.,]+)\s*(B|KB|MB|GB)\s*/\s*([\d.,]+)\s*(B|KB|MB|GB)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PercentRegex = new Regex(@"(\d{1,3})\s*%", RegexOptions.Compiled);

        public static string ExePath { get; private set; }

        public static bool Locate()
        {
            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WindowsApps\winget.exe")
            };
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d.Trim(), "winget.exe")));

            foreach (var c in candidates)
            {
                try
                {
                    if (File.Exists(c)) { ExePath = c; return true; }
                }
                catch { /* đường dẫn không hợp lệ trong PATH */ }
            }
            ExePath = null;
            return false;
        }

        public static async Task<string> GetVersionAsync()
        {
            var r = await RunAsync("--version");
            return r.ExitCode == 0 ? r.Lines.FirstOrDefault() : null;
        }

        public static bool IsSuccess(int code) =>
            code == 0 || code == PackageAlreadyInstalled || code == UpdateNotApplicable ||
            code == RebootRequiredToFinish || code == 3010 || code == 1641;

        public static bool NeedsReboot(int code) =>
            code == RebootRequiredToFinish || code == 3010 || code == 1641;

        public static string DescribeExit(int code)
        {
            switch (code)
            {
                case NoApplicationsFound: return "không tìm thấy gói cài đặt";
                case PackageInUse: return "ứng dụng đang chạy, hãy đóng lại rồi thử lại";
                case InstallInProgress: return "đang có trình cài đặt khác chạy";
                default: return "mã lỗi 0x" + code.ToString("X8");
            }
        }

        public static Task<ProcResult> InstallAsync(string id, Action<string> onLine) =>
            RunAsync($"install --id {Quote(id)} -e --source winget --silent " +
                     $"--accept-package-agreements {Agreements}", onLine);

        public static Task<ProcResult> UninstallAsync(string id, Action<string> onLine) =>
            RunAsync($"uninstall --id {Quote(id)} -e --silent {Agreements}", onLine);

        public static async Task<bool> IsInstalledAsync(string id)
        {
            var r = await RunAsync($"list --id {Quote(id)} -e {Agreements}");
            return r.ExitCode == 0;
        }

        public static async Task<List<PackageInfo>> SearchAsync(string query)
        {
            var r = await RunAsync($"search {Quote(query)} --source winget {Agreements}");
            return ParseTable(r.Lines)
                .Where(c => c.Length >= 2)
                .Select(c => new PackageInfo
                {
                    Name = c[0],
                    Id = FirstToken(c[1]),
                    Version = c.Length > 2 ? FirstToken(c[2]) : ""
                })
                .Where(p => IsValidId(p.Id))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>Lấy danh sách ID các gói winget đã cài trên máy.</summary>
        public static async Task<HashSet<string>> GetInstalledIdsAsync(Action<string> log)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tmp = Path.Combine(Path.GetTempPath(), "WinSetupHelper_export_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await RunAsync($"export -o {Quote(tmp)} --source winget {Agreements}");
                if (File.Exists(tmp))
                {
                    var json = File.ReadAllText(tmp);
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var root = (ExportRoot)new DataContractJsonSerializer(typeof(ExportRoot)).ReadObject(ms);
                        foreach (var src in root?.Sources ?? new List<ExportSource>())
                            foreach (var pkg in src.Packages ?? new List<ExportPackage>())
                                if (!string.IsNullOrEmpty(pkg.PackageIdentifier))
                                    set.Add(pkg.PackageIdentifier);
                    }
                    return set;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Không đọc được kết quả 'winget export': " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            // Dự phòng: đọc bảng của 'winget list'
            log?.Invoke("Dùng 'winget list' để kiểm tra ứng dụng đã cài...");
            var r = await RunAsync($"list {Agreements}");
            foreach (var c in ParseTable(r.Lines).Where(c => c.Length >= 2))
            {
                var id = FirstToken(c[1]);
                if (IsValidId(id)) set.Add(id);
            }
            return set;
        }

        /// <summary>Đọc phần trăm tải xuống từ thanh tiến trình của winget.</summary>
        public static bool TryParseProgress(string line, out double percent)
        {
            percent = 0;
            var m = SizeRegex.Match(line);
            if (m.Success)
            {
                var done = ToBytes(m.Groups[1].Value, m.Groups[2].Value);
                var total = ToBytes(m.Groups[3].Value, m.Groups[4].Value);
                if (total > 0)
                {
                    percent = Math.Max(0, Math.Min(100, done * 100 / total));
                    return true;
                }
            }
            if (line.IndexOf('█') >= 0 || line.IndexOf('▒') >= 0)
            {
                var p = PercentRegex.Match(line);
                if (p.Success)
                {
                    percent = Math.Min(100, double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture));
                    return true;
                }
            }
            return false;
        }

        public static Task<ProcResult> RunAsync(string args, Action<string> onLine = null)
        {
            return Task.Run(() =>
            {
                var result = new ProcResult();
                if (ExePath == null)
                {
                    result.ExitCode = -1;
                    return result;
                }

                var psi = new ProcessStartInfo(ExePath, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var p = new Process { StartInfo = psi })
                {
                    var line = new StringBuilder();
                    void Flush()
                    {
                        var s = Clean(line.ToString());
                        line.Clear();
                        if (s.Trim().Length == 0 || IsSpinner(s)) return;
                        lock (result.Lines) result.Lines.Add(s);
                        onLine?.Invoke(s.Trim());
                    }

                    p.ErrorDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data)) return;
                        var s = Clean(e.Data);
                        onLine?.Invoke(s.Trim());
                    };

                    p.Start();
                    p.BeginErrorReadLine();

                    var buf = new char[2048];
                    int n;
                    while ((n = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (var i = 0; i < n; i++)
                        {
                            var ch = buf[i];
                            if (ch == '\r' || ch == '\n') Flush();
                            else if (ch == '\b') { if (line.Length > 0) line.Length--; }
                            else line.Append(ch);
                        }
                    }
                    Flush();
                    p.WaitForExit();
                    result.ExitCode = p.ExitCode;
                }
                return result;
            });
        }

        /// <summary>
        /// Tách bảng dạng cột của winget (search/list) dựa trên vị trí tiêu đề.
        /// </summary>
        public static List<string[]> ParseTable(List<string> lines)
        {
            var rows = new List<string[]>();
            var sep = lines.FindIndex(l => l.Trim().Length >= 10 && l.Trim().All(c => c == '-'));
            if (sep < 1) return rows;

            var header = lines[sep - 1];
            var starts = new List<int>();
            for (var i = 0; i < header.Length; i++)
                if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                    starts.Add(i);
            if (starts.Count < 2) return rows;

            for (var r = sep + 1; r < lines.Count; r++)
            {
                var row = lines[r];
                if (row.Length <= starts[1]) continue;
                var cells = new string[starts.Count];
                for (var k = 0; k < starts.Count; k++)
                {
                    var s = starts[k];
                    if (s >= row.Length) { cells[k] = ""; continue; }
                    var e = k + 1 < starts.Count ? Math.Min(starts[k + 1], row.Length) : row.Length;
                    cells[k] = row.Substring(s, e - s).Trim();
                }
                rows.Add(cells);
            }
            return rows;
        }

        /// <summary>Cài winget (App Installer) khi máy chưa có.</summary>
        public static async Task<bool> BootstrapAsync(Action<string> log)
        {
            log("Đang đăng ký lại App Installer (winget)...");
            await RunPowerShellAsync(
                "Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe");
            if (Locate() && await GetVersionAsync() != null) return true;

            var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            var dir = Path.Combine(Path.GetTempPath(), "WinSetupHelper_winget");
            Directory.CreateDirectory(dir);
            var files = new[]
            {
                ($"https://aka.ms/Microsoft.VCLibs.{arch}.14.00.Desktop.appx", "VCLibs.appx"),
                ($"https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.{arch}.appx", "UIXaml.appx"),
                ("https://aka.ms/getwinget", "winget.msixbundle")
            };

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            foreach (var (url, name) in files)
            {
                var target = Path.Combine(dir, name);
                log("Đang tải " + name + "...");
                try
                {
                    using (var wc = new WebClient())
                        await wc.DownloadFileTaskAsync(url, target);
                }
                catch (Exception ex)
                {
                    log("Tải " + name + " thất bại: " + ex.Message);
                    continue;
                }
                log("Đang cài " + name + "...");
                await RunPowerShellAsync($"Add-AppxPackage -Path '{target}'");
            }

            return Locate() && await GetVersionAsync() != null;
        }

        private static Task RunPowerShellAsync(string command)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                try
                {
                    using (var p = Process.Start(psi)) p?.WaitForExit();
                }
                catch { /* bỏ qua, kiểm tra lại bằng Locate() */ }
            });
        }

        private static string Clean(string s)
        {
            s = AnsiRegex.Replace(s, "");
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
                if (ch >= ' ' || ch == '\t') sb.Append(ch);
            return sb.ToString().TrimEnd();
        }

        private static bool IsSpinner(string s) => s.Trim().All(ch => ch == '-' || ch == '\\' || ch == '|' || ch == '/');

        private static string FirstToken(string s) =>
            (s ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        private static bool IsValidId(string id) =>
            !string.IsNullOrEmpty(id) && id.IndexOf('…') < 0 && id.Contains(".");

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

        private static double ToBytes(string number, string unit)
        {
            if (!double.TryParse(number.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return 0;
            switch (unit.ToUpperInvariant())
            {
                case "KB": return v * 1024;
                case "MB": return v * 1024 * 1024;
                case "GB": return v * 1024 * 1024 * 1024;
                default: return v;
            }
        }

        [DataContract]
        private sealed class ExportRoot
        {
            [DataMember(Name = "Sources")] public List<ExportSource> Sources { get; set; }
        }

        [DataContract]
        private sealed class ExportSource
        {
            [DataMember(Name = "Packages")] public List<ExportPackage> Packages { get; set; }
        }

        [DataContract]
        private sealed class ExportPackage
        {
            [DataMember(Name = "PackageIdentifier")] public string PackageIdentifier { get; set; }
        }
    }
}
