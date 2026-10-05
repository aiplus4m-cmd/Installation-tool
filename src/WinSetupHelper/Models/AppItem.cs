using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinSetupHelper.Models
{
    /// <summary>Giai đoạn xử lý hiện tại của một ứng dụng.</summary>
    public enum AppPhase
    {
        None,
        Checking,
        Queued,
        Searching,
        Downloading,
        Installing,
        Uninstalling
    }

    /// <summary>Một dòng trong danh sách ứng dụng (danh mục hoặc kết quả tìm kiếm).</summary>
    public sealed class AppItem : INotifyPropertyChanged
    {
        private string _id;
        private bool _selected;
        private bool _isInstalled;
        private bool _isError;
        private AppPhase _phase = AppPhase.Checking;
        private string _statusText = "Đang kiểm tra...";
        private double _progress;
        private bool _isIndeterminate = true;

        public string Id
        {
            get => _id;
            set { _id = value; OnChanged(); }
        }

        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public bool Recommended { get; set; }

        /// <summary>Mẫu tên hiển thị trong "Programs and Features" để nhận diện đã cài.</summary>
        public string[] MatchNames { get; set; }

        /// <summary>Mã gói winget thực tế đang cài (có thể là biến thể, vd. Google.Chrome.EXE).</summary>
        public string InstalledId { get; set; }

        /// <summary>Tên hiển thị trong "Programs and Features" khi phát hiện qua Registry.</summary>
        public string InstalledName { get; set; }

        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; OnChanged(); }
        }

        public bool IsInstalled
        {
            get => _isInstalled;
            private set { _isInstalled = value; OnChanged(); RaiseDerived(); }
        }

        public bool IsError
        {
            get => _isError;
            private set { _isError = value; OnChanged(); RaiseDerived(); }
        }

        public AppPhase Phase
        {
            get => _phase;
            private set { _phase = value; OnChanged(); RaiseDerived(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnChanged(); }
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set { _isIndeterminate = value; OnChanged(); }
        }

        public bool IsBusy => Phase != AppPhase.None;
        public bool CanSelect => !IsInstalled && !IsBusy;
        public bool CanManage => IsInstalled && !IsBusy;

        public bool ShowProgress =>
            Phase == AppPhase.Searching || Phase == AppPhase.Downloading ||
            Phase == AppPhase.Installing || Phase == AppPhase.Uninstalling;

        /// <summary>Dùng để tô màu trạng thái: ok / busy / error / none.</summary>
        public string StatusTone =>
            IsBusy ? "busy" : IsError ? "error" : IsInstalled ? "ok" : "none";

        public void SetInstalled(bool installed, string text = null)
        {
            if (installed) Selected = false;
            _isError = false;
            _phase = AppPhase.None;
            IsInstalled = installed;
            StatusText = text ?? (installed ? "Đã cài đặt" : "Chưa cài đặt");
            OnChanged(nameof(IsError));
            OnChanged(nameof(Phase));
            RaiseDerived();
        }

        public void SetBusy(AppPhase phase, string text, bool indeterminate = true)
        {
            _isError = false;
            Progress = 0;
            IsIndeterminate = indeterminate;
            StatusText = text;
            Phase = phase;
        }

        public void SetError(string text)
        {
            _isError = true;
            StatusText = text;
            Phase = AppPhase.None;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaiseDerived()
        {
            OnChanged(nameof(IsBusy));
            OnChanged(nameof(CanSelect));
            OnChanged(nameof(CanManage));
            OnChanged(nameof(ShowProgress));
            OnChanged(nameof(StatusTone));
        }

        private void OnChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
