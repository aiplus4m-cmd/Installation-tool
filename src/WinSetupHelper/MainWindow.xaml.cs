using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WinSetupHelper.Models;
using WinSetupHelper.Services;

namespace WinSetupHelper
{
    public partial class MainWindow : Window
    {
        private const string AllCategories = "Tất cả danh mục";
        private const string RecommendedCategory = "★ Ứng dụng đề xuất";

        private enum JobKind { Install, Uninstall, Reinstall }

        private sealed class Job
        {
            public JobKind Kind;
            public AppItem Item;
        }

        private readonly ObservableCollection<AppItem> _catalog = new ObservableCollection<AppItem>();
        private readonly ObservableCollection<AppItem> _searchResults = new ObservableCollection<AppItem>();
        private readonly Queue<Job> _queue = new Queue<Job>();
        private readonly string _logFile = Path.Combine(Path.GetTempPath(), "WinSetupHelper.log");

        private InstalledIndex _installed = InstalledIndex.Empty;
        private ICollectionView _catalogView;
        private bool _wingetReady;
        private bool _processing;
        private bool _refreshing;
        private int _jobsTotal, _jobsDone, _jobsOk, _jobsFailed;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await InitializeAsync();
            Closing += MainWindow_Closing;
        }

        // ───────────────────────────── Khởi tạo ─────────────────────────────

        private async Task InitializeAsync()
        {
            Log("Windows Setup Helper v" + typeof(MainWindow).Assembly.GetName().Version);

            foreach (var e in Catalog.Load(Log))
            {
                var item = new AppItem
                {
                    Id = e.Id,
                    Name = e.Name,
                    Description = e.Description,
                    Category = e.Category,
                    Recommended = e.Recommended,
                    MatchNames = e.Match,
                    ExeNames = e.Exe,
                    AppxNames = e.Appx
                };
                item.PropertyChanged += Item_PropertyChanged;
                _catalog.Add(item);
            }

            _catalogView = CollectionViewSource.GetDefaultView(_catalog);
            _catalogView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppItem.Category)));
            _catalogView.Filter = CatalogFilter;

            CategoryBox.Items.Add(AllCategories);
            CategoryBox.Items.Add(RecommendedCategory);
            foreach (var c in _catalog.Select(i => i.Category).Distinct())
                CategoryBox.Items.Add(c);
            CategoryBox.SelectedIndex = 0;

            ShowCatalog();
            await EnsureWingetAsync();
        }

        private async Task EnsureWingetAsync()
        {
            SetOverall("Đang kiểm tra winget...");
            string version = null;
            if (Winget.Locate()) version = await Winget.GetVersionAsync();

            _wingetReady = version != null;
            WingetBanner.Visibility = _wingetReady ? Visibility.Collapsed : Visibility.Visible;
            UpdateSelectionInfo();

            if (!_wingetReady)
            {
                Log("Không tìm thấy winget trên máy.");
                foreach (var i in _catalog) i.SetInstalled(false, "Chưa xác định");
                SetOverall("Cần cài winget trước khi sử dụng.");
                return;
            }

            Log("winget " + version + " tại " + Winget.ExePath);
            await RefreshInstalledAsync();
        }

        private async Task RefreshInstalledAsync()
        {
            if (!_wingetReady || _refreshing) return;
            _refreshing = true;
            BtnRefresh.IsEnabled = false;
            try
            {
                SetOverall("Đang quét các ứng dụng đã cài trên máy...");
                foreach (var i in AllItems().Where(i => !i.IsBusy || i.Phase == AppPhase.Checking))
                    i.SetBusy(AppPhase.Checking, "Đang kiểm tra...");

                var ids = await Winget.GetInstalledIdsAsync(Log);
                var (names, appx) = await Task.Run(() => (InstalledApps.ReadDisplayNames(), InstalledApps.ReadAppxNames()));
                var baseIndex = new InstalledIndex(ids, names, appx);
                Log($"Tìm thấy {baseIndex.IdCount} gói winget, {baseIndex.NameCount} ứng dụng trong Programs and Features, " +
                    $"{baseIndex.AppxCount} gói Store/MSIX.");

                // Ứng dụng chưa nhận diện được → tìm bản portable theo tên file exe
                var checking = AllItems().Where(i => i.Phase == AppPhase.Checking).ToList();
                var wantedExe = checking.Where(i => !IsDetected(baseIndex, i)).SelectMany(ExeOf).ToList();
                var exes = new Dictionary<string, string>();
                if (wantedExe.Count > 0)
                {
                    SetOverall("Đang tìm các ứng dụng portable (UniKey, CPU-Z...)...");
                    exes = await Task.Run(() => InstalledApps.FindExecutables(wantedExe, Log));
                }
                _installed = new InstalledIndex(ids, names, appx, exes);

                foreach (var i in checking)
                    ApplyInstalledState(i, true);

                var installedCount = _catalog.Count(i => i.IsInstalled);
                SetOverall($"Đã cài {installedCount}/{_catalog.Count} ứng dụng trong danh mục.");
            }
            finally
            {
                _refreshing = false;
                BtnRefresh.IsEnabled = true;
                UpdateSelectionInfo();
            }
        }

        // ───────────────────────────── Danh sách & lọc ─────────────────────────────

        private IEnumerable<AppItem> AllItems() => _catalog.Concat(_searchResults).Distinct();

        private bool CatalogFilter(object o)
        {
            var item = (AppItem)o;
            var cat = CategoryBox.SelectedItem as string;
            if (cat == RecommendedCategory && !item.Recommended) return false;
            if (cat != null && cat != AllCategories && cat != RecommendedCategory && item.Category != cat) return false;

            var q = SearchBox.Text.Trim();
            if (q.Length == 0) return true;
            return Contains(item.Name, q) || Contains(item.Description, q) || Contains(item.Id, q) || Contains(item.Category, q);
        }

        private static bool Contains(string s, string q) =>
            s != null && s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;

        private void ShowCatalog()
        {
            AppGrid.ItemsSource = _catalogView;
            UpdateEmptyText();
        }

        private void ShowSearchResults()
        {
            AppGrid.ItemsSource = _searchResults;
            UpdateEmptyText();
        }

        private void UpdateEmptyText(string text = null)
        {
            var isCatalog = TabCatalog.IsChecked == true;
            var empty = isCatalog ? _catalogView != null && _catalogView.IsEmpty : _searchResults.Count == 0;
            EmptyText.Text = text ?? (isCatalog
                ? "Không có ứng dụng nào khớp. Nhấn Enter hoặc nút \"Tìm trên kho winget\" để tìm ứng dụng ngoài danh mục."
                : "Nhập tên ứng dụng vào ô tìm kiếm rồi nhấn Enter để tìm trên kho winget.");
            EmptyText.Visibility = empty || text != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _catalogView?.Refresh();
            if (TabCatalog.IsChecked == true) UpdateEmptyText();
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await SearchOnlineAsync();
        }

        private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _catalogView?.Refresh();
            if (TabCatalog.IsChecked == true) UpdateEmptyText();
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (AppGrid == null) return;
            if (sender == TabCatalog) ShowCatalog();
            else ShowSearchResults();
        }

        private async void SearchOnline_Click(object sender, RoutedEventArgs e) => await SearchOnlineAsync();

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (!_wingetReady) await EnsureWingetAsync();
            else await RefreshInstalledAsync();
        }

        private async Task SearchOnlineAsync()
        {
            var q = SearchBox.Text.Trim();
            if (q.Length < 2)
            {
                MessageBox.Show("Hãy nhập ít nhất 2 ký tự tên ứng dụng cần tìm.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                SearchBox.Focus();
                return;
            }
            if (!_wingetReady)
            {
                MessageBox.Show("Cần cài winget trước khi tìm kiếm.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TabSearch.IsChecked = true;
            BtnSearchOnline.IsEnabled = false;
            foreach (var old in _searchResults.Where(i => !_catalog.Contains(i)))
                old.PropertyChanged -= Item_PropertyChanged;
            _searchResults.Clear();
            UpdateEmptyText($"Đang tìm \"{q}\" trên kho winget...");
            Log($"Tìm kiếm: {q}");

            try
            {
                var results = await Winget.SearchAsync(q);
                foreach (var r in results)
                {
                    var existing = _catalog.FirstOrDefault(c => string.Equals(c.Id, r.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        _searchResults.Add(existing);
                        continue;
                    }

                    var item = new AppItem
                    {
                        Id = r.Id,
                        Name = r.Name,
                        Description = string.IsNullOrEmpty(r.Version) ? "Ứng dụng từ kho winget" : "Phiên bản mới nhất: " + r.Version,
                        Category = "Kết quả tìm kiếm"
                    };
                    ApplyInstalledState(item, false);
                    item.PropertyChanged += Item_PropertyChanged;
                    _searchResults.Add(item);
                }

                Log($"Tìm thấy {results.Count} kết quả.");
                UpdateEmptyText(results.Count == 0 ? $"Không tìm thấy ứng dụng nào cho \"{q}\". Hãy thử từ khóa khác (ví dụ tên tiếng Anh)." : null);
                SetOverall($"Tìm thấy {results.Count} ứng dụng cho \"{q}\".");
            }
            finally
            {
                BtnSearchOnline.IsEnabled = true;
            }
        }

        // ───────────────────────────── Chọn ứng dụng ─────────────────────────────

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppItem.Selected) || e.PropertyName == nameof(AppItem.CanSelect))
            {
                if (Dispatcher.CheckAccess()) UpdateSelectionInfo();
                else Dispatcher.BeginInvoke(new Action(UpdateSelectionInfo));
            }
        }

        private List<AppItem> SelectedItems() => AllItems().Where(i => i.Selected && i.CanSelect).ToList();

        private void UpdateSelectionInfo()
        {
            var n = SelectedItems().Count;
            BtnInstall.Content = $"Cài đặt ({n})";
            BtnInstall.IsEnabled = n > 0 && _wingetReady;
        }

        private void SelectRecommended_Click(object sender, RoutedEventArgs e)
        {
            var count = 0;
            foreach (var i in _catalog.Where(i => i.Recommended && i.CanSelect))
            {
                i.Selected = true;
                count++;
            }
            if (count == 0)
                SetOverall("Tất cả ứng dụng đề xuất đều đã được cài đặt.");
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in AllItems()) i.Selected = false;
        }

        private void InstallSelected_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in SelectedItems())
            {
                i.Selected = false;
                Enqueue(JobKind.Install, i);
            }
        }

        private static AppItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as AppItem;

        private void RowInstall_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null || !_wingetReady) return;
            item.Selected = false;
            Enqueue(JobKind.Install, item);
        }

        private void RowUninstall_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;

            if (item.IsPortable)
            {
                if (MessageBox.Show($"{item.Name} là bản portable (không cài vào Windows):\n{item.InstalledPath}\n\n" +
                                    "Mở thư mục chứa ứng dụng để xóa thủ công?", Title,
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    try { Process.Start("explorer.exe", $"/select,\"{item.InstalledPath}\""); } catch { }
                }
                return;
            }

            if (MessageBox.Show($"Bạn có chắc muốn gỡ bỏ {item.Name}?", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Enqueue(JobKind.Uninstall, item);
        }

        private void RowReinstall_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            var question = item.IsPortable
                ? $"Cài đặt {item.Name} bản chính thức qua winget?"
                : $"Gỡ bỏ và cài đặt lại {item.Name}?";
            if (MessageBox.Show(question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Enqueue(JobKind.Reinstall, item);
        }

        // ───────────────────────────── Hàng đợi cài đặt ─────────────────────────────

        private void Enqueue(JobKind kind, AppItem item)
        {
            if (item.IsBusy) return;
            item.SetBusy(AppPhase.Queued, "Đang chờ...");
            _queue.Enqueue(new Job { Kind = kind, Item = item });
            _jobsTotal++;
            UpdateOverallProgress();
            if (!_processing) _ = ProcessQueueAsync();
        }

        private async Task ProcessQueueAsync()
        {
            _processing = true;
            BtnRefresh.IsEnabled = false;
            OverallProgress.Visibility = Visibility.Visible;
            try
            {
                while (_queue.Count > 0)
                {
                    var job = _queue.Dequeue();
                    bool ok;
                    try
                    {
                        switch (job.Kind)
                        {
                            case JobKind.Uninstall:
                                ok = await UninstallAsync(job.Item);
                                break;
                            case JobKind.Reinstall:
                                ok = job.Item.IsPortable
                                    ? await InstallAsync(job.Item, true)
                                    : await UninstallAsync(job.Item) && await InstallAsync(job.Item);
                                break;
                            default:
                                ok = await InstallAsync(job.Item);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Lỗi: " + ex.Message);
                        job.Item.SetError("Lỗi: " + ex.Message);
                        ok = false;
                    }

                    _jobsDone++;
                    if (ok) _jobsOk++; else _jobsFailed++;
                    UpdateOverallProgress();
                }

                var summary = $"Hoàn tất: {_jobsOk} thành công" + (_jobsFailed > 0 ? $", {_jobsFailed} lỗi (xem Nhật ký)." : ".");
                Log(summary);
                SetOverall(summary);
            }
            finally
            {
                _processing = false;
                _jobsTotal = _jobsDone = _jobsOk = _jobsFailed = 0;
                OverallProgress.Visibility = Visibility.Collapsed;
                BtnRefresh.IsEnabled = true;
                UpdateSelectionInfo();
            }
        }

        private void UpdateOverallProgress()
        {
            if (_jobsTotal == 0) return;
            OverallProgress.Maximum = _jobsTotal;
            OverallProgress.Value = _jobsDone;
            if (_jobsDone < _jobsTotal)
                SetOverall($"Đang xử lý {_jobsDone + 1}/{_jobsTotal} ứng dụng...");
        }

        /// <summary>
        /// Hiển thị thời gian đã chờ khi winget không báo phần trăm,
        /// để người dùng biết công cụ vẫn đang chạy.
        /// </summary>
        private DispatcherTimer StartElapsedTimer(AppItem item)
        {
            var phase = item.Phase;
            var started = DateTime.Now;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                if (item.Phase != phase)
                {
                    phase = item.Phase;
                    started = DateTime.Now;
                }
                if (!item.IsIndeterminate) return; // đã có phần trăm

                var secs = (int)(DateTime.Now - started).TotalSeconds;
                if (secs < 3) return;
                string text;
                switch (phase)
                {
                    case AppPhase.Searching: text = "Đang tìm kiếm gói cài đặt..."; break;
                    case AppPhase.Downloading: text = "Đang tải xuống..."; break;
                    case AppPhase.Installing: text = "Đang cài đặt..."; break;
                    default: return;
                }
                item.StatusText = $"{text} ({FormatSeconds(secs)})";
            };
            timer.Start();
            return timer;
        }

        private static string FormatSeconds(int secs) =>
            secs < 60 ? $"{secs} giây" : $"{secs / 60} phút {secs % 60:00} giây";

        private async Task<bool> InstallAsync(AppItem item, bool force = false)
        {
            Log($"▶ Cài đặt {item.Name} ({item.Id})");
            item.SetBusy(AppPhase.Searching, "Đang tìm kiếm gói cài đặt...");
            var timer = StartElapsedTimer(item);
            try
            {
                var r = await Winget.InstallAsync(item.Id, line => OnWingetOutput(item, line), force);

                if (r.ExitCode == Winget.NoApplicationsFound)
                {
                    // ID không còn đúng (gói đổi tên...) → tự tìm theo tên ứng dụng
                    Log($"Không tìm thấy gói '{item.Id}', đang tìm theo tên \"{item.Name}\"...");
                    item.SetBusy(AppPhase.Searching, "Đang tìm gói phù hợp theo tên...");
                    var results = await Winget.SearchAsync(item.Name);
                    var best = results.FirstOrDefault(p => string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                               ?? results.FirstOrDefault();
                    if (best != null)
                    {
                        Log($"Dùng gói thay thế: {best.Name} ({best.Id})");
                        item.Id = best.Id;
                        item.SetBusy(AppPhase.Searching, "Đã tìm thấy: " + best.Id);
                        r = await Winget.InstallAsync(best.Id, line => OnWingetOutput(item, line), force);
                    }
                }

                if (Winget.IsSuccess(r.ExitCode))
                {
                    var text = Winget.NeedsReboot(r.ExitCode) ? "Đã cài đặt (cần khởi động lại)" : "Đã cài đặt";
                    MarkInstalled(item, text, null);
                    return true;
                }

                var reason = Winget.DescribeExit(r.ExitCode);

                // winget không tải được bộ cài (máy chủ chặn, lỗi mạng/proxy...) → tự tải trực tiếp
                if (Winget.IsNetworkError(r.ExitCode))
                {
                    Log($"winget không tải được ({reason}). Thử tải trực tiếp...");
                    item.SetBusy(AppPhase.Searching, "Đang thử tải bằng cách khác...");
                    var info = await Winget.ShowAsync(item.Id);
                    if (info != null)
                    {
                        item.SetBusy(AppPhase.Downloading, "Đang tải xuống...");
                        var res = await DirectInstaller.InstallAsync(item.Id, item.Name, item.ExeNames, info,
                            pct =>
                            {
                                item.IsIndeterminate = false;
                                item.Progress = pct;
                                item.StatusText = $"Đang tải xuống... {pct:0}%";
                            },
                            status => item.SetBusy(AppPhase.Installing, status),
                            Log);
                        if (res.Success)
                        {
                            MarkInstalled(item, res.NeedsReboot ? "Đã cài đặt (cần khởi động lại)" : "Đã cài đặt", res.InstalledPath);
                            return true;
                        }
                        reason = res.Error;
                    }
                    else
                    {
                        Log("Không lấy được địa chỉ tải bộ cài từ winget.");
                    }
                }

                item.SetError("Cài đặt thất bại: " + reason);
                Log($"✖ {item.Name}: cài đặt thất bại ({reason})");
                return false;
            }
            finally
            {
                timer.Stop();
            }
        }

        private void MarkInstalled(AppItem item, string text, string path)
        {
            _installed.AddId(item.Id);
            item.InstalledId = path == null ? item.Id : null;
            item.InstalledName = null;
            item.InstalledAppx = null;
            item.InstalledPath = path;
            item.SetInstalled(true, text);
            Log($"✔ {item.Name}: {text}" + (path != null ? $" — {path}" : ""));
        }

        private async Task<bool> UninstallAsync(AppItem item)
        {
            var target = item.InstalledId ?? item.InstalledName ?? item.InstalledAppx ?? item.Id;
            Log($"▶ Gỡ bỏ {item.Name} ({target})");
            item.SetBusy(AppPhase.Uninstalling, "Đang gỡ bỏ...");

            ProcResult r;
            if (item.InstalledId == null && item.InstalledName != null)
                r = await Winget.UninstallByNameAsync(item.InstalledName, line => Log("   " + line));
            else
                r = await Winget.UninstallAsync(item.InstalledId ?? item.Id, line => Log("   " + line));

            // Ứng dụng Store/MSIX: gỡ trực tiếp nếu winget không gỡ được
            if (item.InstalledAppx != null && await StillInstalledAsync(item))
            {
                Log($"Gỡ gói Store/MSIX {item.InstalledAppx}...");
                await RunPowerShellAsync($"Get-AppxPackage -Name '{item.InstalledAppx}' | Remove-AppxPackage");
            }

            if (!await StillInstalledAsync(item))
            {
                _installed.RemoveId(item.InstalledId);
                _installed.RemoveId(item.Id);
                item.InstalledId = null;
                item.InstalledName = null;
                item.InstalledAppx = null;
                item.InstalledPath = null;
                item.SetInstalled(false, "Đã gỡ bỏ");
                Log($"✔ Đã gỡ bỏ {item.Name}");
                return true;
            }

            var reason = Winget.DescribeExit(r.ExitCode);
            item.SetInstalled(true, "Gỡ bỏ thất bại: " + reason);
            Log($"✖ {item.Name}: gỡ bỏ thất bại ({reason})");
            return false;
        }

        private static Task RunPowerShellAsync(string command) => Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"")
                { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p?.WaitForExit();
            }
            catch { /* kiểm tra lại sau */ }
        });

        /// <summary>Kiểm tra lại một ứng dụng sau khi gỡ (Registry, Store/MSIX, winget list).</summary>
        private async Task<bool> StillInstalledAsync(AppItem item)
        {
            var (names, appx) = await Task.Run(() => (InstalledApps.ReadDisplayNames(), InstalledApps.ReadAppxNames()));
            var index = new InstalledIndex(null, names, appx);
            if (index.FindName(item.InstalledName != null ? new[] { item.InstalledName } : PatternsOf(item)) != null)
                return true;
            if (item.InstalledAppx != null && index.FindAppx(new[] { item.InstalledAppx }) != null)
                return true;
            var id = item.InstalledId ?? item.Id;
            return await Winget.IsInstalledAsync(id);
        }

        private static string[] PatternsOf(AppItem item) =>
            item.MatchNames != null && item.MatchNames.Length > 0 ? item.MatchNames : new[] { item.Name };

        private static string[] AppxOf(AppItem item) =>
            item.AppxNames != null && item.AppxNames.Length > 0 ? item.AppxNames.Concat(new[] { item.Id }).ToArray() : new[] { item.Id };

        private static string[] ExeOf(AppItem item) => item.ExeNames ?? new string[0];

        private static bool IsDetected(InstalledIndex index, AppItem item) =>
            index.FindId(item.Id) != null || index.FindName(PatternsOf(item)) != null || index.FindAppx(AppxOf(item)) != null;

        /// <summary>
        /// Đánh dấu đã cài / chưa cài dựa trên: mã gói winget, tên trong Programs and Features,
        /// gói Store/MSIX, hoặc file exe của bản portable.
        /// </summary>
        private void ApplyInstalledState(AppItem item, bool log)
        {
            var id = _installed.FindId(item.Id);
            var name = id == null ? _installed.FindName(PatternsOf(item)) : null;
            var appx = id == null && name == null ? _installed.FindAppx(AppxOf(item)) : null;
            var exe = id == null && name == null && appx == null ? _installed.FindExe(ExeOf(item)) : null;

            item.InstalledId = id;
            item.InstalledName = name;
            item.InstalledAppx = appx;
            item.InstalledPath = exe;

            if (exe != null) item.SetInstalled(true, "Đã có (bản portable)");
            else item.SetInstalled(id != null || name != null || appx != null);

            if (!log) return;
            if (id != null && !string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase))
                Log($"   {item.Name}: đã cài (gói {id})");
            else if (name != null)
                Log($"   {item.Name}: đã cài (Programs and Features: \"{name}\")");
            else if (appx != null)
                Log($"   {item.Name}: đã cài (Store/MSIX: {appx})");
            else if (exe != null)
                Log($"   {item.Name}: bản portable tại {exe}");
        }

        /// <summary>Cập nhật trạng thái từ đầu ra của winget (chạy trên luồng nền).</summary>
        private void OnWingetOutput(AppItem item, string line)
        {
            if (Winget.TryParseProgress(line, out var pct))
            {
                if (item.Phase == AppPhase.Searching || item.Phase == AppPhase.Downloading)
                {
                    if (item.Phase != AppPhase.Downloading) item.SetBusy(AppPhase.Downloading, "Đang tải xuống...");
                    item.IsIndeterminate = false;
                    item.Progress = pct;
                    item.StatusText = $"Đang tải xuống... {pct:0}%";
                }
                return;
            }

            if (line.StartsWith("Found ", StringComparison.OrdinalIgnoreCase) && item.Phase == AppPhase.Searching)
                item.StatusText = "Đã tìm thấy, chuẩn bị tải...";
            else if (line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase))
                item.SetBusy(AppPhase.Downloading, "Đang tải xuống...");
            else if (line.IndexOf("installer hash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("Starting package install", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("Extracting", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (item.Phase != AppPhase.Installing) item.SetBusy(AppPhase.Installing, "Đang cài đặt...");
            }

            Log("   " + line);
        }

        // ───────────────────────────── winget ─────────────────────────────

        private async void InstallWinget_Click(object sender, RoutedEventArgs e)
        {
            BtnInstallWinget.IsEnabled = false;
            try
            {
                var ok = await Winget.BootstrapAsync(msg =>
                {
                    Log(msg);
                    Dispatcher.BeginInvoke(new Action(() => WingetBannerText.Text = msg));
                });
                if (ok)
                {
                    await EnsureWingetAsync();
                    return;
                }

                WingetBannerText.Text = "Không thể tự cài winget. Hãy cài \"App Installer\" từ Microsoft Store rồi bấm \"Kiểm tra lại\".";
                if (MessageBox.Show("Không thể tự cài winget.\nMở Microsoft Store để cài \"App Installer\"?", Title,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try { Process.Start("ms-windows-store://pdp/?productid=9NBLGGH4NNS1"); }
                    catch { Process.Start("https://aka.ms/getwinget"); }
                }
            }
            finally
            {
                BtnInstallWinget.IsEnabled = true;
            }
        }

        // ───────────────────────────── Tiện ích ─────────────────────────────

        private void SetOverall(string text) => OverallText.Text = text;

        private void Log(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(message)));
                return;
            }

            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_processing) return;
            if (MessageBox.Show("Đang cài đặt ứng dụng. Bạn có chắc muốn thoát?", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        }
    }
}
