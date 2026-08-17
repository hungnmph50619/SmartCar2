using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Constants
{
    public static class RentalPolicy
    {
        // Tiền cọc = 300% tiền thuê ban đầu
        public const decimal DepositRate = 3.00m;

        // No-show: sau 30 phút, giữ 70% tiền thuê và hoàn 30% tiền thuê còn lại.
        // Tiền cọc được hoàn theo số cọc còn lại thực tế của đơn.
        public const int NoShowGraceMinutes = 30;
        public const decimal NoShowFeeRate = 0.70m;

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
            "Vi phạm giao thông/phạt nguội phát sinh trong thời gian thuê do Bên B chịu trách nhiệm. Khi SmartCar thông báo, Bên B trực tiếp làm việc với cơ quan có thẩm quyền; Bên A cung cấp hồ sơ thuê xe cần thiết để xác định người điều khiển. Bên B có trách nhiệm phối hợp xử lý.";

        public const string DamageCompensationTerms =
            "Hư hỏng, mất mát hoặc thiếu phụ kiện do Bên B gây ra được bồi thường theo thiệt hại thực tế, hợp lý, có ảnh đối chiếu và chứng từ/báo giá hợp lệ. " +
            "Nếu gia hạn bị từ chối, Bên B phải trả xe đúng hạn; trường hợp cố tình trả muộn làm ảnh hưởng đơn kế tiếp, Bên B chịu phí trả muộn và thiệt hại thực tế có căn cứ. " +
            "Trường hợp bất khả kháng có minh chứng, SmartCar ưu tiên xử lý xe/đơn kế tiếp và không tự động khấu trừ tiền cọc của Bên B chỉ vì phát sinh xung đột lịch. Nếu SmartCar hỗ trợ khách kế tiếp thêm ngoài khoản hoàn tiền đã thu thì đó là chính sách hỗ trợ riêng của SmartCar.";

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
