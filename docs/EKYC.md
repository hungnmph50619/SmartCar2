# SmartCar eKYC

SmartCar hỗ trợ luồng eKYC cho khách hàng gồm:

1. Chụp/tải CCCD mặt trước và mặt sau.
2. OCR tự động đọc thông tin CCCD.
3. Quay video selfie khoảng 5 giây.
4. Kiểm tra liveness và face match với khuôn mặt trên CCCD.
5. Gửi hồ sơ về trạng thái chờ Quản trị viên duyệt lần cuối.
6. GPLX có thể dùng OCR để tự điền thông tin trước khi gửi.

## Chế độ mặc định

`appsettings.json` để `Ekyc:Mode = Auto` và không chứa API key. Khi chưa có API key, hệ thống tự chạy ở **Demo eKYC** để có thể trình diễn UI/UX mà không giả vờ rằng AI thật đã xác minh người dùng.

Ở Demo:

- OCR không tự bịa dữ liệu từ ảnh; người dùng vẫn nhập thông tin thủ công.
- Luồng camera/video hoạt động.
- Kết quả liveness/face match được ghi rõ là mô phỏng.
- Hồ sơ vẫn ở trạng thái chờ Quản trị viên duyệt.

## Bật FPT.AI eKYC thật

Không commit API key vào GitHub. Cấu hình bằng biến môi trường hoặc secret trên máy chạy ứng dụng.

PowerShell:

```powershell
$env:Ekyc__Mode="Fpt"
$env:Ekyc__ApiKey="YOUR_FPT_AI_API_KEY"
$env:Ekyc__BaseUrl="https://api.fpt.ai/vision/ekyc/be-stag"
dotnet run --project src/SmartCar.Web
```

Staging mặc định:

```text
https://api.fpt.ai/vision/ekyc/be-stag
```

Khi được cấp quyền production, đổi BaseUrl theo tài liệu/tài khoản của nhà cung cấp. Không tự chuyển production trước khi credential được cấp đúng môi trường.

## Bảo mật dữ liệu

- Ảnh CCCD/GPLX tiếp tục được lưu trong `App_Data/SecureDocuments`, không nằm trong `wwwroot`.
- Video selfie chỉ được gửi để xác minh trong request hiện tại; SmartCar không lưu file video vào ổ đĩa.
- SmartCar chỉ lưu kết quả eKYC cần cho Admin review (OCR/liveness/face-match/similarity/provider/timestamp) trong vùng private của từng khách hàng.
- Không ghi API key vào log hoặc audit log.
- Kết quả OCR/liveness/face match không được mô tả là “C06 xác nhận” hay “Bộ Công an xác nhận” nếu chưa có tích hợp đối soát chính thức.

## Chính sách duyệt

Phiên bản này **không auto-verify CCCD**. Dù AI đạt, CCCD vẫn được gửi về `Pending` để Quản trị viên xem ảnh, dữ liệu và kết quả eKYC rồi mới bấm xác minh. Đây là lựa chọn chủ động để giảm rủi ro false-positive trong đồ án và giữ luồng manual review hiện có.

## Threshold

`Ekyc:FaceMatchThreshold` mặc định là `80`. Có thể điều chỉnh theo tài liệu/khuyến nghị của provider sau khi test dữ liệu thực tế.

## Fallback thủ công

Nếu API eKYC lỗi, camera không hỗ trợ hoặc người dùng không muốn dùng luồng tự động, nút **Dùng xác minh thủ công** giữ nguyên quy trình CCCD/GPLX trước đây. Các validation server hiện có vẫn được áp dụng.
