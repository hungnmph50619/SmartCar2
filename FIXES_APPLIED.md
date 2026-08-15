# SmartCar - Các lỗi đã sửa

Bản này được chỉnh trực tiếp từ file ZIP gốc được cung cấp.

## 14 nhóm sửa chính

1. **Brand SetActive**: Chỉ chặn ngừng hãng khi còn xe chưa `Inactive`; kích hoạt lại hãng luôn được phép.
2. **Edit xe với hãng inactive**: Dropdown Edit giữ hãng hiện tại dù hãng đã ngừng, có nhãn `(Đã ngừng hoạt động)`; Create vẫn chỉ hiện hãng active.
3. **Validate hãng khi Edit**: Cho phép xe giữ nguyên hãng inactive cũ, nhưng không cho chuyển sang một hãng inactive khác.
4. **Upload ảnh giấy tờ**: Đổi từ nhập `ImagePath` bằng text sang `IFormFile`, validate ảnh và lưu tại `wwwroot/uploads/documents/`.
5. **Ngày cấp giấy tờ**: Chặn `IssuedDate` ở tương lai; giữ kiểm tra `ExpiryDate >= IssuedDate`.
6. **Trùng giấy tờ còn hiệu lực**: Chặn tạo thêm giấy tờ cùng loại đang còn hiệu lực cho cùng một xe.
7. **So sánh ngày hết hạn**: Dùng `.Date` khi kiểm tra giấy tờ đến ngày trả, tránh sai do phần giờ.
8. **Bao phủ toàn thời gian thuê**: Kiểm tra `IssuedDate <= PickupDate` và `ExpiryDate >= ReturnDate` cho Registration/Inspection/Insurance.
9. **Xe đang Rented hôm nay nhưng thuê tương lai không trùng lịch**: Search không còn loại xe chỉ vì `Rented`; vẫn chặn `Inactive`, `Maintenance`, `Inspection` và kiểm tra overlap booking.
10. **BookingService đồng bộ với Search**: Create booking dùng cùng nguyên tắc trạng thái vật lý + overlap thay vì bắt buộc `Status == Available`.
11. **Đổi trạng thái xe có booking đang hoạt động**: Chặn Admin đổi trạng thái khi có booking thuộc các trạng thái đang chiếm lịch.
12. **Bộ lọc tìm xe**: `FuelType`, `MinDailyPrice`, `MinManufactureYear` được đưa xuống `VehicleSearchRequest` / Service thay vì lọc lại trong Controller.
13. **Thông báo kết quả tìm kiếm rỗng**: Bổ sung nguyên nhân nghiệp vụ thực tế: lịch thuê, bảo trì/kiểm tra, sự cố, giấy tờ.
14. **Cảnh báo xe mới cần giấy tờ**: Sau khi tạo xe và trên trang Edit có nhắc phải bổ sung Registration, Inspection, Insurance để xe đủ điều kiện tìm kiếm.

## Lỗi bổ sung đã sửa

- **Edit xe bắt chọn ảnh lại**: `Images` được để nullable để tránh implicit `[Required]` trên Edit. Create vẫn yêu cầu ít nhất một ảnh như hành vi cũ; Edit không chọn ảnh mới vẫn lưu được và giữ ảnh hiện có.
- **Toggle hãng gửi boolean sai**: hidden field dùng chuỗi `true/false` rõ ràng để tránh request gửi giá trị không hợp lệ.

## Các file chính đã thay đổi

- `src/SmartCar.Infrastructure/Services/BrandService.cs`
- `src/SmartCar.Infrastructure/Services/VehicleService.cs`
- `src/SmartCar.Infrastructure/Services/BookingService.cs`
- `src/SmartCar.Infrastructure/Services/VehicleDocumentService.cs`
- `src/SmartCar.Application/Features/Vehicles/VehicleContracts.cs`
- `src/SmartCar.Web/Controllers/AdminVehiclesController.cs`
- `src/SmartCar.Web/Controllers/VehicleDocumentsController.cs`
- `src/SmartCar.Web/Controllers/VehiclesController.cs`
- `src/SmartCar.Web/ViewModels/RentalFlowViewModels.cs`
- `src/SmartCar.Web/ViewModels/Phase3ViewModels.cs`
- `src/SmartCar.Web/Views/Brands/Index.cshtml`
- `src/SmartCar.Web/Views/AdminVehicles/Edit.cshtml`
- `src/SmartCar.Web/Views/VehicleDocuments/Create.cshtml`
- `src/SmartCar.Web/Views/Vehicles/Index.cshtml`

## Test tay nên chạy sau khi mở Visual Studio

1. Hãng có xe `Available` -> bấm Ngừng dùng -> phải bị chặn.
2. Tất cả xe của hãng `Inactive` -> Ngừng dùng -> thành công -> Kích hoạt lại -> thành công.
3. Edit xe thuộc hãng inactive -> hãng cũ vẫn được chọn -> sửa màu/giá mà không upload ảnh -> lưu thành công.
4. Create xe -> dropdown không có hãng inactive.
5. Upload ảnh giấy tờ JPG/PNG/WEBP hợp lệ -> file được lưu; file giả ảnh / quá 5 MB -> bị chặn.
6. Nhập ngày cấp giấy tờ ở tương lai -> bị chặn.
7. Thêm giấy tờ cùng loại đang còn hiệu lực -> bị chặn.
8. Khách thuê đến đúng ngày giấy tờ hết hạn -> vẫn hợp lệ trong ngày đó.
9. Giấy tờ có ngày cấp sau ngày nhận xe -> xe không xuất hiện.
10. Xe đang `Rented` hôm nay nhưng booking tương lai không overlap -> có thể xuất hiện và đặt; khoảng overlap -> không xuất hiện/không đặt được.
11. Xe có booking `PendingPayment/Paid/ReadyForPickup/Rented/...` -> Admin không được tùy ý đổi trạng thái.
12. Lọc FuelType / giá tối thiểu / năm tối thiểu -> kết quả đúng từ Service.

> Lưu ý: môi trường đóng gói này không có .NET SDK (`dotnet`), nên chưa thể chạy `dotnet build` tại đây. Hãy Rebuild Solution trong Visual Studio sau khi giải nén.
