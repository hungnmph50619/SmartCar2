# Kiểm tra bản sửa quản lý nhân viên

Phạm vi: số điện thoại, khóa/mở tài khoản, mật khẩu lần đầu, validation và dữ liệu rỗng.
Không thay đổi cấu hình giữ cọc hoặc cơ chế chống ghi đè hồ sơ.

## Chính sách số điện thoại

Giữ unique trên toàn bộ AspNetUsers để khớp database hiện có. Staff, Customer và Admin không dùng chung số giữa hai tài khoản. Chuẩn hóa +84 về 0; khi kiểm tra vẫn nhận diện cả dữ liệu cũ dùng +84. Đăng ký Customer cũng kiểm tra cùng chính sách để tránh lỗi theo chiều ngược lại. Không cần migration cho bản sửa này.

## Chạy kiểm thử

```sh
dotnet test tests/SmartCar.Tests/SmartCar.Tests.csproj
dotnet run --project src/SmartCar.Web/SmartCar.Web.csproj
```

Các regression test mới kiểm tra khóa/mở lặp, mật khẩu tạm không đổi, form tạo thiếu dữ liệu, chuẩn hóa điện thoại và giới hạn hồ sơ. Chưa chạy được trong môi trường soạn bản sửa do thiếu .NET SDK; cần chạy trên máy có .NET 8.

## Kiểm thử với SQL Server và trình duyệt

- Tạo Staff dùng số của Customer: báo trùng, không HTTP 500; thử chiều Customer dùng số của Staff.
- Sửa Staff hoặc hồ sơ cá nhân sang số đã có: báo trùng. Giữ nguyên số của chính tài khoản: lưu được.
- Thử 0901234567 và +84901234567: nhận diện cùng số, lưu dạng 0.
- Hai yêu cầu tạo/sửa cùng một số: database không cho trùng; lỗi unique trả thông báo thân thiện.
- Bỏ trống tên/email/điện thoại/CCCD ở form tạo và sửa, kể cả gửi trực tiếp POST: hiển thị validation.
- Hai tab cùng mở tài khoản đang hoạt động; lần lượt bấm Khóa ở cả hai: tài khoản vẫn khóa. Làm tương tự với Mở khóa.
- Staff bị khóa không tiếp tục thao tác; Customer/Staff không gọi được action quản lý nhân viên. POST thiếu antiforgery token bị từ chối.
- Đăng nhập với mật khẩu tạm rồi nhập lại nó làm mật khẩu mới: bị từ chối, vẫn bắt buộc đổi mật khẩu. Mật khẩu mới khác và đủ mạnh: đăng nhập lại được.
- Tên 101 ký tự, tên có số, địa chỉ 251 ký tự: bị từ chối; tên hợp lệ 100 ký tự và địa chỉ 250 ký tự: không vượt schema.
- Xác nhận audit vẫn ghi đúng hành động Khóa/Mở, tạo và sửa hồ sơ.

Trang cũ đang mở trước khi cập nhật có form ToggleStatus: tải lại trang để dùng action Khóa/Mở mới.
