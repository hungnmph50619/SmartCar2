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
- Seeder tự tạo role, tài khoản demo, 6 xe, ảnh xe, giấy tờ xe và mã khuyến mãi.

6. TÀI KHOẢN DEMO
Admin:
Email: admin@smartcar.vn
Mật khẩu: SmartCar@123

Customer đã xác minh CCCD/GPLX:
Email: customer@smartcar.vn
Mật khẩu: SmartCar@123

Mã khuyến mãi demo:
WELCOME10

7. LUỒNG DEMO CHÍNH
- Customer đăng nhập, tìm xe theo ngày giờ tương lai và tạo đơn.
- Admin xác nhận đơn.
- Customer áp dụng mã khuyến mãi và thanh toán mô phỏng.
- Admin chuyển đơn sang sẵn sàng giao, lập biên bản giao xe.
- Customer có thể gửi yêu cầu gia hạn.
- Admin lập biên bản trả xe, thêm phụ phí và hoàn tất đơn.
- Customer đánh giá xe sau khi đơn hoàn tất.
- Admin quản lý giấy tờ xe, bảo trì, sự cố, khuyến mãi, báo cáo và audit log.

8. QUY TẮC HỦY VÀ HOÀN TIỀN
- Customer hủy sau khi đã thanh toán: không hoàn tiền.
- Admin hủy trước khi bàn giao: hoàn 100% số tiền đã thanh toán.
- Khách không đến nhận xe sau 30 phút: no-show, không hoàn tiền.

9. KIỂM TRA TỰ ĐỘNG
GitHub Actions thực hiện:
- Restore và build toàn bộ solution .NET 8 Release.
- Chạy unit test quy tắc ngày thuê.
- Áp toàn bộ migration lên SQL Server sạch.
- Kiểm tra EF Model Snapshot không còn thay đổi chưa tạo migration.
