using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Constants
{
    public static class RentalPolicy
    {
        // Tiền cọc = 300% tiền thuê ban đầu
        public const decimal DepositRate = 3.00m;

        // ============================================================
        // CHÍNH SÁCH GIAO XE THEO KHOẢNG CÁCH
        // ============================================================

        // QUAN TRỌNG:
        // THAY 2 tọa độ này bằng tọa độ THẬT của cửa hàng bạn.
        public const decimal StoreLatitude = 21.0381298m; //vĩ độ
        public const decimal StoreLongitude = 105.7424982m; //kinh độ

        // 3 km đầu = 30.000đ
        public const double IncludedDeliveryDistanceKm = 3d; //số km đầu
        public const decimal BaseDeliveryFee = 30_000m; //giá

        // Sau 3 km, mỗi km bắt đầu tiếp theo = +10.000đ
        public const decimal DeliveryFeePerExtraKm = 10_000m;

        // Khoảng cách giao tối đa
        public const double MaxDeliveryDistanceKm = 50d;

        // Bán kính trung bình Trái Đất
        private const double EarthRadiusKm = 6371.0088d;

        // Mỗi ngày được đi 300 km
        public const int IncludedKilometersPerDay = 300;

        // Đi vượt định mức: 5.000đ/km
        public const decimal ExcessKilometerFee = 5_000m;

        // Trả xe muộn: 1.5 lần giá thuê/ngày
        public const decimal LateReturnFeeMultiplier = 1.50m;

        public const string TrafficFineTerms =
            "Khách hàng chịu toàn bộ tiền phạt giao thông và chi phí phát sinh theo chứng từ/quyết định của cơ quan có thẩm quyền đối với vi phạm xảy ra trong thời gian thuê.";

        public const string DamageCompensationTerms =
            "Hư hỏng, mất mát do khách hàng gây ra được bồi thường theo chi phí sửa chữa/thay thế thực tế có chứng từ hoặc báo giá hợp lệ. " +
            "Nếu yêu cầu gia hạn bị SmartCar từ chối, khách hàng phải trả xe đúng thời hạn đã cam kết. " +
            "Trường hợp khách hàng cố tình không trả xe đúng hạn và việc này làm SmartCar không thể thực hiện đơn thuê kế tiếp, ngoài phí trả muộn khách hàng phải bồi thường khoản tương ứng giá trị tiền thuê và phí giao của đơn thuê kế tiếp bị ảnh hưởng; khoản bồi thường được đối soát theo đơn thực tế và có thể khấu trừ từ tiền cọc. " +
            "Nếu gia hạn do bất khả kháng làm ảnh hưởng đơn kế tiếp, SmartCar sẽ xử lý phương án đổi xe hoặc hủy/hoàn tiền cho khách kế tiếp trước khi duyệt gia hạn; khoản bồi thường phát sinh (nếu có) được ghi nhận riêng và đối soát với tiền cọc của khách đang thuê. " +
            "Các khoản bồi thường được xác định riêng, không tự động gộp với phạt giao thông hoặc phí vượt kilomet.";

        // ============================================================
        // TÍNH KHOẢNG CÁCH
        // ============================================================

        public static double CalculateDeliveryDistanceKm(
            decimal deliveryLatitude,
            decimal deliveryLongitude)
        {
            var storeLat = ToRadians((double)StoreLatitude);
            var storeLng = ToRadians((double)StoreLongitude);

            var deliveryLat =
                ToRadians((double)deliveryLatitude);

            var deliveryLng =
                ToRadians((double)deliveryLongitude);

            var deltaLat =
                deliveryLat - storeLat;

            var deltaLng =
                deliveryLng - storeLng;

            var a =
                Math.Pow(
                    Math.Sin(deltaLat / 2d),
                    2d)
                +
                Math.Cos(storeLat)
                *
                Math.Cos(deliveryLat)
                *
                Math.Pow(
                    Math.Sin(deltaLng / 2d),
                    2d);

            var c =
                2d * Math.Atan2(
                    Math.Sqrt(a),
                    Math.Sqrt(1d - a));

            return Math.Round(
                EarthRadiusKm * c,
                2,
                MidpointRounding.AwayFromZero);
        }

        // ============================================================
        // TÍNH PHÍ GIAO XE
        // ============================================================

        public static decimal CalculateDeliveryFee(
            VehiclePickupMethod pickupMethod,
            decimal? deliveryLatitude,
            decimal? deliveryLongitude)
        {
            // Nhận tại cửa hàng => không có phí giao
            if (pickupMethod !=
                VehiclePickupMethod.Delivery)
            {
                return 0m;
            }

            if (!deliveryLatitude.HasValue ||
                !deliveryLongitude.HasValue)
            {
                return 0m;
            }

            var distanceKm =
                CalculateDeliveryDistanceKm(
                    deliveryLatitude.Value,
                    deliveryLongitude.Value);

            // Số km vượt quá 3 km
            var extraDistanceKm =
                Math.Max(
                    0d,
                    distanceKm -
                    IncludedDeliveryDistanceKm);

            // Ví dụ:
            // vượt 0.1km cũng tính thành 1km
            // vượt 2.2km tính thành 3km
            var extraWholeKilometers =
                (decimal)Math.Ceiling(
                    extraDistanceKm);

            return
                BaseDeliveryFee
                +
                extraWholeKilometers
                *
                DeliveryFeePerExtraKm;
        }

        // ============================================================
        // KIỂM TRA PHẠM VI GIAO
        // ============================================================

        public static bool IsWithinDeliveryRange(
            decimal deliveryLatitude,
            decimal deliveryLongitude)
        {
            return
                CalculateDeliveryDistanceKm(
                    deliveryLatitude,
                    deliveryLongitude)
                <=
                MaxDeliveryDistanceKm;
        }

        public static decimal CalculateDeposit(
            decimal rentalAmount)
        {
            return Math.Round(
                rentalAmount * DepositRate,
                0,
                MidpointRounding.AwayFromZero);
        }

        private static double ToRadians(
            double degrees)
        {
            return degrees *
                   Math.PI /
                   180d;
        }
    }
}