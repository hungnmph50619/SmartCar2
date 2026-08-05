SMARTCAR - BAN CLEAN ARCHITECTURE 4 PROJECT
===========================================

1. MO PROJECT
- Mo file SmartCar.sln bang Visual Studio.
- Chuot phai SmartCar.Web -> Set as Startup Project.

2. KIEN TRUC
- SmartCar.Domain: Entity, Enum, Constant; khong phu thuoc project khac.
- SmartCar.Application: Interface, DTO/Request, ket qua nghiep vu; chi phu thuoc Domain.
- SmartCar.Infrastructure: EF Core, SQL Server, Identity, DbContext, SeedData; phu thuoc Application + Domain.
- SmartCar.Web: Controllers, ViewModels, Views, wwwroot; goi Application va dang ky Infrastructure.

3. CHUOI KET NOI
Mac dinh dang dung:
Server=localhost\\HUNG;Database=SmartCarDb;Trusted_Connection=True;TrustServerCertificate=True

Sua tai:
src/SmartCar.Web/appsettings.Development.json

4. TAO HOAC CAP NHAT DATABASE (PACKAGE MANAGER CONSOLE)
Repository da co san cac migration. Chi chay:

Update-Database -Project SmartCar.Infrastructure -StartupProject SmartCar.Web

Neu SmartCarDb cu khong co du lieu can giu va gay xung dot migration, co the xoa database cu roi chay lai lenh Update-Database.

5. CHAY PHAN MEM
- Build -> Rebuild Solution.
- Nhan Ctrl + F5.

6. TAI KHOAN ADMIN DUOC SEED
Email: admin@smartcar.vn
Mat khau: SmartCar@123

7. LUONG THUE XE GIAI DOAN 1
- Admin quan ly hang xe, xe va anh xe.
- Khach tim xe con trong theo ngay gio va gui yeu cau thue.
- Admin xac nhan hoac tu choi don.
- Khach thanh toan mo phong.
- Admin lap bien ban giao xe va tra xe.
- Admin them phu phi, hoan tat don va cap nhat trang thai xe.
- Khach xem lich su don; Dashboard lay so lieu that tu database.
