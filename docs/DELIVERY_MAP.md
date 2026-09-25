# Bản đồ giao xe

Trang chọn điểm giao ưu tiên Google Maps khi `GoogleMaps:BrowserApiKey` được cấu hình. Bật **Maps JavaScript API** và **Geocoding API** trong cùng dự án Google Maps Platform, bật thanh toán, giới hạn khóa trình duyệt theo HTTP referrer của tên miền đang triển khai. Đặt khóa bằng biến môi trường `GoogleMaps__BrowserApiKey`; không commit khóa vào kho mã. Khởi động lại ứng dụng sau khi đặt biến.

Khi không có khóa, giao diện dùng Leaflet 1.9.4 được phục vụ từ chính ứng dụng; ảnh nền dùng OpenStreetMap và tìm địa chỉ dùng Nominatim. Nếu mạng của máy khách chặn hai dịch vụ OpenStreetMap, nền và tên địa chỉ sẽ không tải được. Cấu hình khóa Google để chuyển sang nguồn bản đồ và tìm địa chỉ khác. GPS vẫn lấy tọa độ để tính phí; nhân viên hoặc khách cần nhập tên địa chỉ giao xe khi dịch vụ địa chỉ không phản hồi.
