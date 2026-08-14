SMARTCAR - CLEAN ARCHITECTURE 4 PROJECT
=======================================

1. MỞ PROJECT
- Mở file SmartCar.sln bằng Visual Studio 2022.
- Chuột phải SmartCar.Web -> Set as Startup Project.
- Yêu cầu .NET SDK 8 và SQL Server LocalDB hoặc SQL Server 2022.

2. KIẾN TRÚC
- SmartCar.Domain: Entity, Enum, Constant.
- SmartCar.Application: Interface, DTO, Request và quy tắc nghiệp vụ.
- SmartCar.Infrastructure: EF Core, SQL Server, Identity, Service, Migration, SeedData.
- SmartCar.Web: MVC Controller, ViewModel, Razor View và wwwroot.
- SmartCar.Tests: Unit test cho các quy tắc nghiệp vụ quan trọng.

3. CHUỖI KẾT NỐI
Mặc định dùng LocalDB:
Server=(localdb)\\MSSQLLocalDB;Database=SmartCarDb;Trusted_Connection=True;TrustServerCertificate=True

Nếu máy dùng SQL Server instance khác, sửa DefaultConnection tại:
src/SmartCar.Web/appsettings.Development.json

4. TẠO HOẶC CẬP NHẬT DATABASE
Mở Package Manager Console và chạy:

Update-Database -Project SmartCar.Infrastructure -StartupProject SmartCar.Web

Nếu database SmartCarDb cũ không có dữ liệu cần giữ và gây xung đột migration,
xóa database cũ rồi chạy lại lệnh Update-Database.

5. CHẠY PHẦN MỀM
- Build -> Rebuild Solution.
- Nhấn Ctrl + F5.
- Seeder tự tạo role, tài khoản demo, 6 xe demo, ảnh xe và giấy tờ xe.
- Dữ liệu demo chỉ được tạo khi database chưa có xe nên không ghi đè dữ liệu đã nhập.

6. TÀI KHOẢN DEMO
Admin:
Email: admin@smartcar.vn
Mật khẩu: SmartCar@123

Customer đã xác minh CCCD/GPLX:
Email: customer@smartcar.vn
Mật khẩu: SmartCar@123

7. LUỒNG DEMO CHÍNH
- Customer đăng nhập, tìm xe theo ngày giờ tương lai và tạo đơn.
- Admin xác nhận đơn.
- Customer thanh toán mô phỏng.
- Trước khi chuyển đơn sang sẵn sàng giao xe, Admin phải gọi/nhắn khách và chỉ xác nhận khi khách phản hồi sẽ nhận xe đúng giờ, đúng địa điểm.
- Admin đến điểm giao, chỉ lập biên bản khi khách thực sự có mặt; sau bàn giao đơn mới chuyển sang Rented.
- Nếu khách đã xác nhận nhưng sau đó không xuất hiện: chờ tối thiểu 30 phút, liên hệ lại ít nhất 2 lần; với giao tận nơi Admin phải xác nhận đã đến đúng điểm giao trước khi ghi NoShow.
- Customer có thể gửi yêu cầu gia hạn.
- Admin lập biên bản trả xe, thêm phụ phí và hoàn tất đơn.
- Customer đánh giá xe sau khi đơn hoàn tất.
- Admin quản lý giấy tờ xe, bảo trì, sự cố, báo cáo và audit log.

8. QUY TẮC HỦY, NOSHOW VÀ HOÀN TIỀN
- Customer chủ động hủy trước giờ nhận từ 48 giờ trở lên: hoàn 100% số tiền đã thanh toán.
- Customer chủ động hủy trước giờ nhận từ 24 đến dưới 48 giờ: hoàn 50% số tiền đã thanh toán.
- Customer chủ động hủy trước giờ nhận dưới 24 giờ: không hoàn tiền.
- Admin hủy trước khi bàn giao: hoàn 100% số tiền đã thanh toán.
- NoShow: chỉ áp dụng sau ít nhất 30 phút kể từ giờ nhận, có xác nhận khách trước khi Admin xuất phát, có ít nhất 2 lần liên hệ lại; đơn giao tận nơi còn phải xác nhận Admin đã đến đúng điểm giao.
- Phí NoShow: giữ 40% tiền thuê + phí lượt giao xe thực tế đã phát sinh (nếu có); phần còn lại được tạo thành khoản hoàn tiền cho khách.
- NoShow không tạo biên bản bàn giao, không chuyển xe sang Rented và xe được giải phóng theo lịch vận hành.

9. KIỂM TRA TỰ ĐỘNG
GitHub Actions thực hiện:
- Restore và build toàn bộ solution .NET 8 Release.
- Chạy unit test quy tắc ngày thuê.
- Áp toàn bộ migration lên SQL Server sạch.
- Kiểm tra EF Model Snapshot không còn thay đổi chưa tạo migration.
