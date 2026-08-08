# KYC Development Test Mode

SmartCar có một chế độ kiểm thử KYC dành riêng cho môi trường `Development` để nhóm có thể test đầy đủ luồng CCCD/GPLX mà không cần nhiều giấy tờ thật.

## Phạm vi an toàn

Chế độ này chỉ khả dụng khi đồng thời thỏa cả hai điều kiện:

1. ASP.NET Core đang chạy trong môi trường `Development`.
2. `KycTesting:Enabled` được đặt thành `true` trong cấu hình Development.

`appsettings.Development.json` của dự án bật khả năng này để phục vụ đồ án. Ở các môi trường khác, `KycTestingService.Available` luôn trả về `false`, vì vậy cookie hoặc request thủ công cũng không thể bật Test Mode.

## Cách sử dụng

1. Mở trang **Hồ sơ > Xác minh CCCD/GPLX** bằng tài khoản Customer.
2. Ở banner **Chế độ kiểm thử KYC**, bấm **Bật chế độ test**.
3. Dùng **Dùng bộ CCCD mẫu** hoặc **Dùng bộ GPLX mẫu** để tạo ảnh synthetic ngay trong trình duyệt.
4. Tiếp tục các bước KYC như bình thường.
5. Đăng nhập Admin, mở thông báo KYC và duyệt hồ sơ để test phần còn lại của quy trình.
6. Khi cần kiểm tra thuật toán thật, bấm **Tắt chế độ test**.

## Những gì được mô phỏng

Khi Test Mode đang bật:

- kiểm tra chất lượng ảnh ở client/server được bypass;
- phân loại CCCD/GPLX và mặt trước/mặt sau được mô phỏng là hợp lệ;
- dữ liệu QR/MRZ/OCR được trả về từ bộ dữ liệu test cố định;
- ảnh mẫu có watermark rõ `SMARTCAR DEMO — KHÔNG CÓ GIÁ TRỊ` và `DEMO / SAMPLE`;
- dữ liệu mẫu không phải dữ liệu của công dân thật.

## Những gì vẫn chạy thật

Test Mode không bỏ qua các phần nghiệp vụ chính:

- hai ảnh mặt trước/mặt sau vẫn phải được chọn và gửi cùng form;
- validation dữ liệu form (định dạng, ngày cấp, ngày hết hạn, tuổi, hạng GPLX...) vẫn chạy;
- hồ sơ vẫn được lưu qua `IDocumentService` và `ISecureDocumentStorage`;
- trạng thái Pending/Verified/Rejected vẫn hoạt động;
- logic gom CCCD + GPLX thành một hồ sơ KYC vẫn hoạt động;
- notification cho Admin vẫn được tạo khi đủ hồ sơ;
- Admin vẫn phải mở hồ sơ và duyệt KYC;
- quyền truy cập ảnh và các kiểm tra authorization vẫn giữ nguyên.

## Dữ liệu mẫu hiện tại

CCCD mẫu:

- Số: `099999999999`
- Họ tên: `NGUYỄN VĂN TEST`
- Ngày sinh: `15/05/1998`
- Giới tính: `Nam`
- Ngày cấp: `01/01/2025`
- Ngày hết hạn: `15/05/2040`
- Nơi cư trú: `123 Đường Test, TP. Ninh Bình, Ninh Bình`

GPLX mẫu:

- Số: `TESTB123456`
- Họ tên: `NGUYỄN VĂN TEST`
- Hạng: `B`
- Ngày cấp: `02/01/2025`
- Ngày hết hạn: `15/05/2035`

## Nguyên tắc

Test Mode chỉ phục vụ kiểm thử quy trình trong đồ án. Không dùng trạng thái Test Mode để khẳng định giấy tờ thật, không coi dữ liệu mẫu là kết quả xác thực với cơ sở dữ liệu nhà nước, và không bật cơ chế bypass này trong môi trường production.
