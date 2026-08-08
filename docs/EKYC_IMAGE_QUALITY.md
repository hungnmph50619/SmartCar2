# eKYC Image Quality Gate

SmartCar kiểm tra chất lượng ảnh giấy tờ **ngay trên trình duyệt** trước khi cho OCR hoặc chuyển sang nhập thủ công. Mục tiêu là loại ảnh xấu sớm, tránh xử lý/OCR/request không cần thiết và giúp Admin nhận được ảnh đủ rõ để đối chiếu.

## Các kiểm tra hiện tại

- Độ phân giải tối thiểu: cạnh ngắn >= 400 px và cạnh dài >= 700 px.
- Độ nét: dùng phương sai Laplacian trên ảnh đã thu nhỏ để phát hiện mờ/nhòe.
- Độ sáng: kiểm tra độ sáng trung bình và tỷ lệ pixel quá tối.
- Cháy sáng/lóa: kiểm tra tỷ lệ pixel gần trắng và vùng sáng cục bộ lớn.
- Mức chi tiết: dùng mật độ cạnh làm heuristic để phát hiện ảnh có quá ít nội dung/giấy tờ quá nhỏ trong khung.
- Hai mặt trùng nhau vẫn được chặn bởi `ekyc-guards.js` bằng SHA-256.

Các ngưỡng nằm trong `wwwroot/js/ekyc-image-quality.js` và nên được hiệu chỉnh bằng bộ ảnh test thực tế của đồ án. Đây là heuristic nhằm cải thiện UX, không phải mô hình đánh giá chất lượng ảnh chuẩn ngân hàng.

## Luồng

1. Người dùng chọn mặt trước và mặt sau.
2. Browser phân tích ảnh bằng Canvas; không gọi server/API ngoài.
3. Nếu một ảnh không đạt, OCR và xác minh thủ công đều bị chặn cho tới khi chọn ảnh khác.
4. Nếu hai ảnh đạt, mới cho phép tiếp tục OCR.
5. Sau OCR CCCD local, nếu đọc dưới 4/7 trường hoặc không có số CCCD 12 chữ số, nút sang bước khuôn mặt bị khóa và người dùng được yêu cầu chọn ảnh rõ hơn.

## Lợi ích về request

Quality gate chạy trước handler OCR nên ảnh mờ/tối/lóa/độ phân giải thấp không đi vào `/Ekyc/PreviewCitizenId` hoặc endpoint OCR provider. Khi sau này bật provider tính phí, lớp này giúp giảm request vô ích.

## Lưu ý

- Mật độ cạnh chỉ là heuristic cho việc giấy tờ quá nhỏ/ít chi tiết, không phải nhận dạng chính xác bốn cạnh thẻ.
- Không coi quality gate là xác thực CCCD thật.
- Với đồ án, vẫn ưu tiên CCCD mô phỏng có watermark `SMARTCAR TEST DOCUMENT / KHÔNG CÓ GIÁ TRỊ`.
