SMARTCAR - BẢN CLEAN ARCHITECTURE 4 PROJECT
===========================================

1. MỞ PROJECT
- Giải nén file ZIP.
- Mở file SmartCar.sln bằng Visual Studio.
- Chuột phải SmartCar.Web -> Set as Startup Project.

2. KIẾN TRÚC
- SmartCar.Domain: Entity, Enum, Constant; không phụ thuộc project khác.
- SmartCar.Application: Interface, DTO/Request, kết quả nghiệp vụ; chỉ phụ thuộc Domain.
- SmartCar.Infrastructure: EF Core, SQL Server, Identity, DbContext, SeedData; phụ thuộc Application + Domain.
- SmartCar.Web: Controllers, ViewModels, Views, wwwroot; gọi Application và đăng ký Infrastructure.

3. CHUỖI KẾT NỐI
Mặc định đang dùng:
Server=localhost\\HUNG;Database=SmartCarDb;Trusted_Connection=True;TrustServerCertificate=True

Sửa tại:
src/SmartCar.Web/appsettings.Development.json

4. TẠO DATABASE (PACKAGE MANAGER CONSOLE)
Chạy lần lượt:

Add-Migration InitialCreate -Project SmartCar.Infrastructure -StartupProject SmartCar.Web -OutputDir Persistence/Migrations
Update-Database -Project SmartCar.Infrastructure -StartupProject SmartCar.Web

Nếu SmartCarDb cũ không có dữ liệu cần giữ và gây xung đột migration, có thể xóa database cũ rồi chạy lại hai lệnh trên.

5. CHẠY PHẦN MỀM
- Build -> Rebuild Solution.
- Nhấn Ctrl + F5.

6. TÀI KHOẢN QUẢN LÝ ĐƯỢC SEED
Email: manager@smartcar.vn
Mật khẩu: SmartCar@123

7. LƯU Ý
- Không cần tạo thêm Web API ở giai đoạn này.
- Controller không truy cập EF Core/Identity trực tiếp; AccountController gọi IAccountService.
- Các module xe, đặt xe, thanh toán, giao/trả, bảo trì tiếp tục đặt nghiệp vụ trong Application và triển khai dữ liệu trong Infrastructure.
