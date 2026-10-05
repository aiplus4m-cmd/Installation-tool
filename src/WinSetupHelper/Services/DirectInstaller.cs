using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace WinSetupHelper.Services
{
    /// <summary>
    /// Phương án dự phòng khi winget không tải được bộ cài: tự tải file theo URL trong
    /// manifest winget (dùng proxy/kết nối của hệ thống), kiểm tra SHA256 rồi cài im lặng.
    /// </summary>
    public static class DirectInstaller
    {
        public sealed class Result
        {
            public bool Success { get; set; }
            public bool NeedsReboot { get; set; }
            public string InstalledPath { get; set; }
            public string Error { get; set; }
        }

        public static async Task<Result> InstallAsync(string id, string name, string[] exeNames, InstallerInfo info,
            Action<double> onProgress, Action<string> onStatus, Action<string> log)
        {
            var dir = Path.Combine(Path.GetTempPath(), "WinSetupHelper", Sanitize(id));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, FileNameFor(info));

            // 1. Tải xuống
            log($"Tải trực tiếp: {info.Url}");
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinSetupHelper";
                    wc.Proxy = WebRequest.GetSystemWebProxy();
                    wc.Proxy.Credentials = CredentialCache.DefaultCredentials;
                    wc.DownloadProgressChanged += (s, e) =>
                    {
                        if (e.TotalBytesToReceive > 0) onProgress(e.BytesReceived * 100.0 / e.TotalBytesToReceive);
                    };
                    await wc.DownloadFileTaskAsync(new Uri(info.Url), file);
                }
            }
            catch (Exception ex)
            {
                var host = SafeHost(info.Url);
                return Fail($"không kết nối được máy chủ tải xuống {host} ({Inner(ex).Message})");
            }

            // 2. Kiểm tra SHA256
            if (!string.IsNullOrEmpty(info.Sha256))
            {
                onStatus("Đang kiểm tra file tải về...");
                var hash = await Task.Run(() => Sha256(file));
                if (!string.Equals(hash, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                    return Fail("file tải về không khớp mã kiểm tra SHA256");
                }
            }

            // 3. Cài đặt
            onStatus("Đang cài đặt...");
            var type = info.Type ?? "exe";
            log($"Cài đặt kiểu '{type}': {file}");
            try
            {
                switch (type)
                {
                    case "msi":
                    case "wix":
                        return ExitResult(await RunAsync("msiexec.exe", $"/i \"{file}\" /qn /norestart"));
                    case "inno":
                        return ExitResult(await RunAsync(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"));
                    case "nullsoft":
                        return ExitResult(await RunAsync(file, "/S"));
                    case "burn":
                        return ExitResult(await RunAsync(file, "/quiet /norestart"));
                    case "msix":
                    case "appx":
                        return ExitResult(await RunAsync("powershell.exe",
                            $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{file}'\""));
                    case "portable":
                        return InstallPortable(name, exeNames, file, null);
                    case "zip":
                        return await InstallZipAsync(name, exeNames, file, info.NestedType);
                    default:
                        return ExitResult(await RunAsync(file, "/S"));
                }
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static async Task<Result> InstallZipAsync(string name, string[] exeNames, string zip, string nestedType)
        {
            var extractDir = Path.Combine(Path.GetDirectoryName(zip), "extracted");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, extractDir));

            // Bộ cài nằm trong file zip
            var msi = Directory.GetFiles(extractDir, "*.msi", SearchOption.AllDirectories).FirstOrDefault();
            if (nestedType != null && nestedType != "portable" && msi != null)
                return ExitResult(await RunAsync("msiexec.exe", $"/i \"{msi}\" /qn /norestart"));

            return InstallPortable(name, exeNames, null, extractDir);
        }

        /// <summary>Chép ứng dụng portable vào Program Files và tạo shortcut ở Desktop + Start Menu.</summary>
        private static Result InstallPortable(string name, string[] exeNames, string exeFile, string folder)
        {
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PortableApps", Sanitize(name));
            Directory.CreateDirectory(target);

            if (folder != null) CopyDirectory(folder, target);
            else File.Copy(exeFile, Path.Combine(target, Path.GetFileName(exeFile)), true);

            var exes = Directory.GetFiles(target, "*.exe", SearchOption.AllDirectories);
            var main = exes.FirstOrDefault(e => exeNames != null &&
                           exeNames.Any(n => string.Equals(n, Path.GetFileName(e), StringComparison.OrdinalIgnoreCase)))
                       ?? exes.OrderByDescending(e => new FileInfo(e).Length).FirstOrDefault();
            if (main == null) return Fail("không tìm thấy file chạy trong gói tải về");

            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                     })
            {
                try { Shortcut.Create(Path.Combine(dir, Sanitize(name) + ".lnk"), main); }
                catch { /* không tạo được shortcut cũng không sao */ }
            }

            return new Result { Success = true, InstalledPath = main };
        }

        private static void CopyDirectory(string from, string to)
        {
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(from, to));
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(from, to), true);
        }

        private static Result ExitResult(int code)
        {
            if (code == 0) return new Result { Success = true };
            if (code == 3010 || code == 1641) return new Result { Success = true, NeedsReboot = true };
            return Fail("trình cài đặt trả về mã lỗi " + code);
        }

        private static Task<int> RunAsync(string file, string args)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    if (!p.WaitForExit((int)TimeSpan.FromMinutes(30).TotalMilliseconds))
                    {
                        try { p.Kill(); } catch { }
                        return -2;
                    }
                    return p.ExitCode;
                }
            });
        }

        private static string FileNameFor(InstallerInfo info)
        {
            string name = null;
            try { name = Path.GetFileName(new Uri(info.Url).LocalPath); } catch { }
            name = Sanitize(string.IsNullOrEmpty(name) ? "installer" : name);

            string ext;
            switch (info.Type)
            {
                case "msi":
                case "wix": ext = ".msi"; break;
                case "zip": ext = ".zip"; break;
                case "msix": ext = ".msix"; break;
                case "appx": ext = ".appx"; break;
                default: ext = ".exe"; break;
            }
            if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) &&
                !(ext == ".msix" && name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)))
                name += ext;
            return name;
        }

        private static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static string SafeHost(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }

        private static Exception Inner(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        private static void TryDelete(string f)
        {
            try { File.Delete(f); } catch { }
        }

        private static Result Fail(string error) => new Result { Success = false, Error = error };
    }
}
