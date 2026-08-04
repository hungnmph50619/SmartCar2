# SmartCar Clean Architecture

```text
SmartCar.Web
   │  Controllers / ViewModels / Views
   ▼
SmartCar.Application
   │  Use-case interfaces / request models / service results
   ▼
SmartCar.Domain
      Entities / Enums / Constants

SmartCar.Infrastructure
   ├─ implements SmartCar.Application interfaces
   ├─ EF Core + SQL Server
   └─ ASP.NET Core Identity
```

## Quy tắc phụ thuộc

- `Domain` không tham chiếu project nào.
- `Application` chỉ tham chiếu `Domain`.
- `Infrastructure` tham chiếu `Application` và `Domain`.
- `Web` là composition root, tham chiếu `Application`, `Domain`, `Infrastructure`.
- Giao diện MVC không chứa truy vấn dữ liệu.
- Controller chỉ kiểm tra model, gọi use case/service và quyết định View/Redirect.

## Vị trí code cho các module tiếp theo

| Module | Application | Infrastructure | Web |
|---|---|---|---|
| Xe | `Features/Vehicles` | repository/EF mapping | `VehiclesController`, Views |
| Đặt xe | `Features/Bookings` | booking repository | `BookingsController`, Views |
| Thanh toán | `Features/Payments` | payment persistence | payment Views |
| Giao/trả | `Features/Handovers`, `Features/Returns` | EF persistence | manager Controllers/Views |
| Bảo trì | `Features/Maintenance` | maintenance repository | manager Controllers/Views |
