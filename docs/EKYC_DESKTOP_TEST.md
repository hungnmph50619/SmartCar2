# SmartCar eKYC - chế độ test trên máy tính

Mục tiêu của chế độ này là phục vụ đồ án SmartCar khi chỉ chạy trên máy tính và không sử dụng CCCD thật.

## Phạm vi

- Dùng ảnh CCCD mô phỏng, có watermark rõ như `SMARTCAR TEST DOCUMENT` hoặc `KHÔNG CÓ GIÁ TRỊ`.
- OCR chạy ngay trong trình duyệt bằng Tesseract.js, không gọi FPT/VNPT và không cần API key trả phí.
- Web dùng webcam máy tính cho bước khuôn mặt.
- Ở chế độ Demo hiện tại, kết quả liveness/face match vẫn là mô phỏng và phải được ghi rõ là mô phỏng.
- Hồ sơ vẫn về `Pending` để Admin duyệt cuối cùng.
- Không được mô tả kết quả này là xác minh C06/Bộ Công an hoặc xác nhận CCCD tồn tại trong CSDL dân cư.

## Mẫu CCCD mô phỏng nên dùng

Để OCR test ổn định, ảnh test nên có chữ lớn, tương phản cao và các nhãn sau:

### Mặt trước

```text
SMARTCAR TEST DOCUMENT - KHÔNG CÓ GIÁ TRỊ
CĂN CƯỚC CÔNG DÂN MÔ PHỎNG
Số / No.: 012345678901
Họ và tên / Full name: NGUYỄN VĂN TEST
Ngày sinh / Date of birth: 15/09/1998
Giới tính / Sex: Nam
Nơi cư trú / Place of residence: 123 Đường Test, Ninh Bình
Có giá trị đến / Date of expiry: 15/09/2040
```

### Mặt sau

```text
SMARTCAR TEST DOCUMENT - KHÔNG CÓ GIÁ TRỊ
Ngày cấp / Date of issue: 01/01/2025
```

Có thể thêm ảnh chân dung giả lập hoặc ảnh của chính người kiểm thử nếu muốn trình diễn bước webcam, nhưng không dùng dữ liệu cá nhân thật không cần thiết.

## Cách test

1. Chạy SmartCar ở cấu hình mặc định không có API key FPT.
2. Vào Hồ sơ -> Xác minh giấy tờ.
3. Badge phải hiện `OCR cục bộ · TEST`.
4. Chọn hai ảnh CCCD mô phỏng khác nhau.
5. Bấm `Đọc CCCD và tiếp tục`.
6. Lần đầu trình duyệt sẽ tải bộ OCR Tesseract.js và dữ liệu ngôn ngữ; các lần sau thường nhanh hơn nhờ cache.
7. Kiểm tra các trường được tự điền. Trường OCR thiếu hoặc sai vẫn được phép sửa trước khi tiếp tục.
8. Bật webcam và hoàn thành bước khuôn mặt theo luồng hiện tại.

## Lưu ý

- OCR này đọc chữ thật từ ảnh test, nhưng không xác thực giấy tờ với bất kỳ CSDL nhà nước nào.
- Nếu không tải được Tesseract.js hoặc dữ liệu ngôn ngữ, kiểm tra Internet rồi thử lại; vẫn có thể chuyển sang nhập thủ công.
- Không đưa ảnh CCCD thật vào repository, thư mục public hoặc dữ liệu mẫu của đồ án.
