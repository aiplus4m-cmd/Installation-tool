# Windows Setup Helper (Installation-tool)

Công cụ chạy trên Windows giúp **cài đặt nhanh các ứng dụng cần thiết sau khi cài mới Windows**.

## Tính năng

- Danh sách ~60 ứng dụng cơ bản, chia theo nhóm (trình duyệt, Zalo, bộ gõ tiếng Việt, văn phòng, PDF, tiện ích, đa phương tiện, điều khiển từ xa, runtime...).
- **Tự kiểm tra ứng dụng đã cài**: ứng dụng đã có trên máy được đánh dấu ✔ *Đã cài đặt*, kèm nút **Gỡ bỏ** và **Cài lại**.
- Ứng dụng chưa cài có ô tích chọn và nút **Cài ngay**.
- **Gợi ý ứng dụng** (★ Đề xuất) và nút *Chọn ứng dụng đề xuất* để chọn nhanh.
- **Tìm kiếm** trong danh mục, và **tìm ứng dụng ngoài danh mục** trên kho winget (nhấn Enter hoặc nút *Tìm trên kho winget*).
- Bấm **Cài đặt** → công cụ tự tìm gói, tải xuống, cài đặt im lặng lần lượt từng ứng dụng. Trạng thái từng ứng dụng thay đổi theo thời gian thực:
  *Đang chờ → Đang tìm kiếm → Đang tải xuống xx% → Đang cài đặt → Đã cài đặt* (hoặc báo lỗi).
- Nếu mã gói trong danh mục không còn đúng, công cụ tự tìm theo tên ứng dụng và cài gói phù hợp.
- Nếu máy chưa có winget, công cụ đề nghị cài tự động.
- Nhật ký chi tiết hiển thị trong cửa sổ và lưu tại `%TEMP%\WinSetupHelper.log`.

## Tải về & sử dụng

1. Vào mục **Releases** của repo, tải `WinSetupHelper.exe`.
2. Chạy file (công cụ yêu cầu quyền Administrator để cài đặt không phải bấm xác nhận UAC cho từng ứng dụng).
3. Tích chọn ứng dụng → bấm **Cài đặt**.

Yêu cầu: Windows 10 1809 trở lên hoặc Windows 11 (có sẵn .NET Framework 4.8), có Internet.

> Windows SmartScreen có thể cảnh báo vì file exe chưa được ký số — chọn *More info → Run anyway*.

## Tùy chỉnh danh mục

Danh mục mặc định nằm ở [`src/WinSetupHelper/apps.json`](src/WinSetupHelper/apps.json) (được nhúng vào exe).
Có thể đặt một file `apps.json` cùng thư mục với `WinSetupHelper.exe` để dùng danh mục riêng mà không cần build lại:

```json
[
  { "id": "Google.Chrome", "name": "Google Chrome", "category": "Trình duyệt web",
    "description": "Trình duyệt của Google", "recommended": true }
]
```

`id` là mã gói winget (tra bằng lệnh `winget search <tên>`).

## Build

- Code: C# WPF, .NET Framework 4.8 — thư mục `src/WinSetupHelper`.
- Build cục bộ (trên Windows): `dotnet build src/WinSetupHelper/WinSetupHelper.csproj -c Release`
- GitHub Actions (`.github/workflows/build-release.yml`) tự build trên `windows-latest`:
  - mọi nhánh: build và đính kèm exe vào phần *Artifacts* của lần chạy;
  - nhánh `main`: tự tạo **GitHub Release** `v1.0.<số lần build>` kèm `WinSetupHelper.exe` và file zip.
