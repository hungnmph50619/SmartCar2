# eKYC Image Quality Gate

SmartCar kiểm tra chất lượng ảnh giấy tờ **ngay trên trình duyệt** trước khi cho OCR hoặc chuyển sang nhập thủ công. Mục tiêu là loại ảnh xấu sớm, tránh OCR/request không cần thiết và giúp OCR chỉ xử lý phần giấy tờ thay vì toàn bộ nền ảnh.

## Pipeline hiện tại

1. Người dùng chọn mặt trước và mặt sau.
2. Nếu ảnh đã là ảnh thẻ trực tiếp với tỉ lệ gần ID-1, SmartCar dùng toàn bộ ảnh.
3. Nếu ảnh là ảnh chụp có nền, SmartCar tải OpenCV.js từ tài liệu chính thức của OpenCV và chạy **ngay trong browser** để tìm vùng hình chữ nhật giống thẻ ID.
4. Hệ thống cắt nền, chỉnh phối cảnh và đưa riêng vùng giấy tờ về khung chuẩn trước khi đánh giá chất lượng.
5. Chỉ khi vùng giấy tờ đạt chất lượng mới cho OCR chạy.
6. Tesseract.js OCR trên ảnh đã cắt/chỉnh. Nếu còn thiếu dữ liệu, SmartCar có thể OCR thêm một số vùng chữ quan trọng như thông tin mặt trước, ngày cấp và MRZ. Các lần đọc bổ sung này vẫn chạy local, không gọi API OCR tính phí.
7. Parser kiểm tra tính hợp lý của từng trường trước khi điền form; dữ liệu nghi ngờ được để trống thay vì coi là OCR thành công.

## Các kiểm tra ảnh

- Tìm được vùng giấy tờ/4 cạnh đủ rõ.
- Số pixel thực tế của vùng thẻ đủ lớn: cạnh ngắn khoảng >= 320 px, cạnh dài >= 520 px.
- Giấy tờ phải chiếm đủ diện tích ảnh: dưới khoảng 14% bị chặn; dưới khoảng 26% có cảnh báo chụp gần hơn.
- Độ nét được tính **trên vùng thẻ đã cắt**, không còn bị nền gỗ/vân bàn làm chỉ số sắc nét cao giả.
- Độ sáng, tỷ lệ pixel quá tối, cháy sáng và lóa được tính trên vùng thẻ.
- Mật độ cạnh được tính trên vùng thẻ để đánh giá chữ/chi tiết có đủ rõ cho OCR hay không.
- Hai mặt trùng nhau vẫn được chặn bởi `ekyc-guards.js` bằng SHA-256.

Các ngưỡng nằm trong `wwwroot/js/ekyc-image-quality.js` và cần được tune bằng bộ ảnh test của đồ án. Đây là heuristic phục vụ UX/đồ án, không phải bộ tiêu chuẩn eKYC ngân hàng.

## Kiểm tra dữ liệu sau OCR

- Số CCCD phải đúng 12 chữ số.
- Họ tên phải là chuỗi tên hợp lý, không nhận một ký tự/số như `3` thành họ tên.
- Ngày sinh phải là ngày hợp lệ và phù hợp độ tuổi khách thuê.
- Ngày cấp không được ở tương lai.
- Ngày hết hạn phải là ngày hợp lệ và sau ngày cấp.
- Địa chỉ phải là chuỗi văn bản hợp lý, không lấy MRZ hoặc nhãn `Có giá trị đến` làm địa chỉ.
- MRZ mặt sau được kiểm tra check digit theo trọng số 7-3-1 trước khi dùng để đối chiếu ngày sinh/ngày hết hạn. Nhờ đó lỗi OCR kiểu đọc `2032` thành `2052` sẽ bị loại nếu check digit không khớp.

UI dùng cụm `OCR cục bộ đọc hợp lệ X/7 trường`; X là số trường vượt qua validation, không chỉ là số giá trị Tesseract đã trả về.

## Lợi ích về request

Ảnh xấu bị chặn trước handler OCR/provider. Khi sau này bật FPT/VNPT hoặc provider tính phí, lớp này giúp giảm request vô ích. OpenCV.js và Tesseract.js có thể được tải từ CDN, nhưng **ảnh giấy tờ không được gửi tới OpenCV/Tesseract server**; việc phân tích ảnh diễn ra trong browser.

## Lưu ý

- Không coi quality gate hoặc OCR local là xác thực CCCD với cơ sở dữ liệu nhà nước.
- Với đồ án, vẫn ưu tiên CCCD mô phỏng có watermark `SMARTCAR TEST DOCUMENT / KHÔNG CÓ GIÁ TRỊ`.
- Ảnh chụp nên để đủ 4 cạnh, nền tương phản và giấy tờ chiếm phần lớn khung hình để OCR ổn định nhất.
