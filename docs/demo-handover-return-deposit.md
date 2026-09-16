# Demo giao xe, trả xe và giữ cọc

Nhánh demo cho phép giao xe trước giờ nhận dự kiến: bỏ chốt so sánh với PickupDate ở bước lập biên bản và bước xác minh bản ký giao. Thời gian mặc định trên biên bản giao là thời điểm hiện tại. Thay đổi này áp dụng trực tiếp trên nhánh, không có công tắc môi trường; cần khôi phục chốt giao đúng lịch khi kết thúc demo.

Các kiểm tra xe sẵn sàng, thanh toán, danh tính, bản ký, không nhập thời gian giao trong tương lai và giao trước giờ trả vẫn còn ở luồng lập biên bản.

## Demo hoàn cọc ngay

1. Admin đặt Cấu hình nghiệp vụ → giữ cọc 0 ngày **trước khi tạo đơn demo mới**. Đơn đã tạo giữ chính sách cũ.
2. Tạo đơn, duyệt và hoàn thành tiền thuê + cọc. Staff đánh dấu sẵn sàng giao xe.
3. Staff lập biên bản giao, in/ký, quay về chi tiết đơn → tải 1–12 ảnh bản ký, mở bản ký và xác minh để bắt đầu chuyến.
4. Staff lập biên bản trả, in/ký, quay về chi tiết đơn → tải bản ký trả → mở bản ký → xác minh.
5. Chọn Đối chiếu, thêm phí & quyết toán. Trang này so sánh biên bản giao/trả và hiển thị số ngày giữ cọc đã lưu trên đơn cùng mốc được duyệt hoàn.
6. Xử lý phụ phí nếu có; bấm Xác nhận & quyết toán cọc. Nếu còn cọc được hoàn, hệ thống tạo khoản chờ Admin duyệt; chưa phải đã chuyển tiền cho khách.
7. Admin vào Thanh toán → hoàn tiền để duyệt khi đủ điều kiện; Staff vào danh sách hoàn tiền để thực hiện và ghi nhận mã giao dịch. Dùng dữ liệu mô phỏng cho demo, không cần chuyển tiền thật.

## Demo giữ cọc có thời hạn

Đặt 15 ngày rồi tạo một đơn khác. Sau khi trả xe và quyết toán, khoản cọc phải chờ đến thời gian trả thực tế + 15 ngày. Đổi cấu hình về 0 sau đó không làm đơn này được hoàn sớm. Khoản phạt chưa xử lý vẫn chặn duyệt hoàn dù đã đủ ngày.

## Kiểm tra sau khi kéo code

- Chưa đến giờ nhận vẫn lập và xác minh bản ký giao được.
- Bản ký giao/trả có form tải ngay tại chi tiết Staff; không cần vào Inspect để tải.
- Chưa tải bản ký thì chưa xác minh được; chưa xác minh cả giao/trả thì chưa mở quyết toán.
- Đã xác minh thì form thay bản ký bị ẩn; backend vẫn chặn thay bản ký.
- File sai định dạng, trên 8 MB mỗi ảnh hoặc quá 12 trang được backend từ chối.
- 0 ngày và 15 ngày áp dụng theo từng đơn, không theo cấu hình mới nhất khi trả.

Chưa chạy build hoặc kiểm thử .NET/SQL Server trong môi trường chỉnh sửa vì thiếu SDK.
