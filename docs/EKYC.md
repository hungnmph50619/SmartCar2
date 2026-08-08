# SmartCar eKYC

SmartCar hỗ trợ luồng eKYC cho khách hàng gồm:

1. Chụp/tải CCCD mặt trước và mặt sau.
2. OCR tự động đọc thông tin CCCD.
3. Quay video selfie khoảng 5 giây.
4. Kiểm tra liveness và face match giữa video với ảnh CCCD mặt trước.
5. Gửi hồ sơ về trạng thái chờ Quản trị viên duyệt lần cuối.
6. GPLX có thể dùng OCR để tự điền thông tin trước khi gửi.

## Chế độ mặc định

`appsettings.json` để `Ekyc:Mode = Auto` và không chứa API key. Khi chưa có API key, hệ thống tự chạy ở **Demo eKYC** để có thể trình diễn UI/UX mà không giả vờ rằng AI thật đã xác minh người dùng.

Ở Demo:

- OCR không tự bịa dữ liệu từ ảnh; người dùng vẫn nhập thông tin thủ công.
- Luồng camera/video hoạt động.
- Kết quả liveness/face match được ghi rõ là mô phỏng.
- Hồ sơ vẫn ở trạng thái chờ Quản trị viên duyệt.

## Bật FPT.AI Reader thật

Không commit API key vào GitHub. Cấu hình bằng biến môi trường hoặc secret trên máy chạy ứng dụng.

PowerShell:

```powershell
$env:Ekyc__Mode="Fpt"
$env:Ekyc__ApiKey="YOUR_FPT_AI_API_KEY"
dotnet run --project src/SmartCar.Web
```

Các endpoint mặc định đang dùng theo tài liệu FPT.AI Reader:

```text
CCCD OCR:  https://api.fpt.ai/vision/idr/vnm/
GPLX OCR:  https://api.fpt.ai/vision/dlr/vnm
Liveness:  https://api.fpt.ai/dmp/liveness/v3
```

Có thể override từng endpoint bằng:

```powershell
$env:Ekyc__CitizenIdOcrUrl="..."
$env:Ekyc__DrivingLicenseOcrUrl="..."
$env:Ekyc__LivenessUrl="..."
```

Luồng liveness gửi `video` selfie cùng ảnh CCCD mặt trước (`cmnd`) để FPT Reader vừa kiểm tra người thật vừa trả kết quả face match.

## Yêu cầu media

SmartCar giới hạn ảnh giấy tờ tối đa 5 MB/ảnh. Video selfie tối đa 10 MB và giao diện quay khoảng 5 giây. Khi dùng provider thật, nên quay đủ sáng, nhìn thẳng, chỉ có một khuôn mặt và để khuôn mặt chiếm phần đáng kể trong khung hình.

## Bảo mật dữ liệu

- Ảnh CCCD/GPLX tiếp tục được lưu trong `App_Data/SecureDocuments`, không nằm trong `wwwroot`.
- Video selfie chỉ được gửi để xác minh trong request hiện tại; SmartCar không lưu file video vào ổ đĩa.
- SmartCar chỉ lưu kết quả eKYC cần cho Admin review (OCR/liveness/face-match/similarity/provider/timestamp) trong vùng private của từng khách hàng.
- Không ghi API key vào log hoặc audit log.
- Kết quả OCR/liveness/face match không được mô tả là “C06 xác nhận” hay “Bộ Công an xác nhận” nếu chưa có tích hợp đối soát chính thức.

## Chính sách duyệt

Phiên bản này **không auto-verify CCCD**. Dù AI đạt, CCCD vẫn được gửi về `Pending` để Quản trị viên xem ảnh, dữ liệu và kết quả eKYC rồi mới bấm xác minh. Đây là lựa chọn chủ động để giảm rủi ro false-positive trong đồ án và giữ luồng manual review hiện có.

## Threshold

`Ekyc:FaceMatchThreshold` mặc định là `80`. Có thể điều chỉnh sau khi test dữ liệu thực tế và đối chiếu khuyến nghị của provider.

## Fallback thủ công

Nếu API eKYC lỗi, camera không hỗ trợ hoặc người dùng không muốn dùng luồng tự động, nút **Dùng xác minh thủ công** giữ nguyên quy trình CCCD/GPLX trước đây. Các validation server hiện có vẫn được áp dụng.
