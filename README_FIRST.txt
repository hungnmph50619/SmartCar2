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
- Hệ thống tạo 2 cấu phần kế toán cho thanh toán ban đầu:
  + Rental = tiền thuê + phí giao nhận.
  + Deposit = cọc bảo đảm 5.000.000 đồng.
- Trên giao diện Customer, hai cấu phần này được cộng thành MỘT số tiền cần thanh toán. Customer chỉ chuyển khoản một lần/một QR.
- Khi thanh toán mô phỏng hoặc Admin xác nhận QR, Rental và Deposit được ghi Paid cùng thời điểm và cùng mã giao dịch; Booking chuyển sang Paid.
- Cọc bảo đảm không cộng vào Booking.TotalAmount và không tính vào doanh thu.
- Admin kiểm tra/vệ sinh/chuẩn bị xe và ghi nhận "Xe đã chuẩn bị xong". Customer nhận thông báo nhưng đơn vẫn ở Paid.
- Sau đó Admin gọi/nhắn khách; chỉ khi khách phản hồi sẽ nhận xe đúng giờ, đúng địa điểm thì đơn mới chuyển sang ReadyForPickup.
- Admin đến điểm giao, chỉ lập biên bản khi khách thực sự có mặt. Handover chỉ kiểm tra cọc đã được thanh toán trước đó, KHÔNG thu cọc lần hai.
- Sau bàn giao thực tế đơn mới chuyển sang Rented.
- Nếu khách đã xác nhận nhưng sau đó không xuất hiện: chờ tối thiểu 30 phút, liên hệ lại ít nhất 2 lần; với giao tận nơi Admin phải xác nhận đã đến đúng điểm giao trước khi ghi NoShow.
- Customer có thể gửi yêu cầu gia hạn.
- Admin lập biên bản trả xe; xe chuyển sang PendingInspection để kiểm tra, thêm phụ phí và quyết toán cọc.
- Phụ phí được đối trừ với cọc trước. Chỉ phần vượt quá số cọc mới yêu cầu Customer thanh toán thêm.
- Sau khi hoàn tất kiểm tra, số cọc còn dư được tạo thành khoản DepositRefund chờ Admin chuyển trả cho khách.
- Customer đánh giá xe sau khi đơn hoàn tất.
- Admin quản lý giấy tờ xe, bảo trì, sự cố, báo cáo và audit log.

8. QUY TẮC HỦY, NOSHOW VÀ HOÀN TIỀN
- Chính sách hủy chỉ áp dụng trên phần tiền thuê/phí giao nhận; cọc bảo đảm được xử lý riêng.
- Customer chủ động hủy trước giờ nhận từ 48 giờ trở lên: hoàn 100% phần tiền thuê/phí giao nhận + hoàn 100% cọc vì xe chưa bàn giao.
- Customer chủ động hủy trước giờ nhận từ 24 đến dưới 48 giờ: hoàn 50% phần tiền thuê/phí giao nhận + hoàn 100% cọc.
- Customer chủ động hủy trước giờ nhận dưới 24 giờ: không hoàn phần tiền thuê/phí giao nhận nhưng vẫn hoàn 100% cọc vì xe chưa bàn giao.
- Admin hủy trước khi bàn giao: hoàn 100% phần tiền thuê/phí giao nhận + hoàn 100% cọc.
- NoShow: chỉ áp dụng sau ít nhất 30 phút kể từ giờ nhận, có xác nhận khách trước khi Admin xuất phát, có ít nhất 2 lần liên hệ lại; đơn giao tận nơi còn phải xác nhận Admin đã đến đúng điểm giao.
- Phí NoShow: giữ 40% tiền thuê + phí lượt giao xe thực tế đã phát sinh (nếu có) từ phần tiền chuyến; phần tiền chuyến còn lại được hoàn.
- Với NoShow, cọc bảo đảm được hoàn 100% vì xe chưa được bàn giao.
- NoShow không tạo biên bản bàn giao, không chuyển xe sang Rented và xe được giải phóng theo lịch vận hành.
- Refund và DepositRefund được lưu riêng để báo cáo không nhầm tiền cọc là doanh thu/chi phí hoạt động.

9. CỌC BẢO ĐẢM
- Mức cọc hiện tại của SmartCar: 5.000.000 đồng/đơn.
- Cọc được thanh toán CÙNG tiền thuê/phí giao nhận trong một giao dịch ban đầu; Customer không phải chuyển khoản cọc lần thứ hai khi nhận xe.
- Trong database, Rental và Deposit vẫn là hai Payment riêng nhưng dùng chung thời điểm/mã giao dịch thanh toán ban đầu.
- Tiền cọc không cộng vào TotalAmount của chuyến thuê và không tính vào doanh thu.
- Khi khách trả xe, hệ thống đối trừ phụ phí có căn cứ (trả muộn, nhiên liệu, vệ sinh, hư hỏng, thiếu phụ kiện...) với cọc.
- Nếu phụ phí nhỏ hơn cọc: tạo DepositRefund bằng phần cọc còn dư.
- Nếu phụ phí bằng cọc: không còn số dư cọc phải hoàn.
- Nếu phụ phí lớn hơn cọc: dùng hết cọc và chỉ yêu cầu khách thanh toán phần vượt cọc.
- Admin thực hiện chuyển khoản hoàn cọc trong màn Quản lý thanh toán và nhập mã giao dịch để lưu Audit Log.

10. KIỂM TRA TỰ ĐỘNG
GitHub Actions thực hiện:
- Restore và build toàn bộ solution .NET 8 Release.
- Chạy unit test quy tắc ngày thuê.
- Áp toàn bộ migration lên SQL Server sạch.
- Kiểm tra EF Model Snapshot không còn thay đổi chưa tạo migration.
