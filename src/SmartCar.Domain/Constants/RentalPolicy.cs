using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Constants
{
    public static class RentalPolicy
    {
        // Tiền cọc = 300% tiền thuê ban đầu.
        public const decimal DepositRate = 3.00m;

        // Giữ chỗ:
        // - Đơn mới chờ Admin xác nhận tối đa 30 phút.
        // - Sau khi được duyệt, khách có 15 phút để thanh toán/báo đã chuyển khoản.
        // Khi khách đã báo chuyển khoản và payment ở AwaitingConfirmation thì không tự giải phóng lịch
        // cho tới khi SmartCar đối soát giao dịch.
        public const int BookingConfirmationHoldMinutes = 30;
        public const int BookingPaymentHoldMinutes = 15;

        // Khoảng vận hành tối thiểu giữa hai lượt thuê cùng xe: nhận xe trả về, kiểm tra, chụp ảnh,
        // đối chiếu km/nhiên liệu và vệ sinh nhanh trước lượt kế tiếp.
        public const int VehicleTurnaroundMinutes = 30;

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
            "Vi phạm giao thông/phạt nguội phát sinh trong thời gian khách thực tế giữ xe do Bên B chịu trách nhiệm. " +
            "Phạt nguội có thể được cơ quan có thẩm quyền thông báo sau khi chuyến thuê đã kết thúc; khi có thông báo chính thức, " +
            "SmartCar gắn vi phạm với đúng đơn thuê dựa trên thời gian giao xe thực tế và trả xe thực tế, lưu bằng chứng và tạo khoản phải thu tương ứng. " +
            "Bên B có trách nhiệm thanh toán phần nghĩa vụ của mình theo thông báo hợp lệ. Khoản còn nợ không làm tăng tiền cọc ban đầu, " +
            "nhưng tài khoản sẽ không được tạo chuyến thuê mới cho đến khi khoản phải thu được thanh toán hoặc được xác nhận đã xử lý. " +
            "Nếu khách không tiếp tục sử dụng dịch vụ, SmartCar vẫn lưu hồ sơ, hợp đồng và bằng chứng để thực hiện việc thu hồi nghĩa vụ theo quy định áp dụng.";

        public const string DamageCompensationTerms =
            "Hư hỏng, mất mát hoặc thiếu phụ kiện do Bên B gây ra được bồi thường theo thiệt hại thực tế, hợp lý, có ảnh đối chiếu và chứng từ/báo giá hợp lệ. " +
            "Nếu yêu cầu gia hạn thông thường bị từ chối vì xe đã có đơn kế tiếp, Bên B phải trả xe đúng hạn; trường hợp đã được thông báo từ chối nhưng vẫn cố tình không giao/trả xe đúng hạn làm ảnh hưởng đơn kế tiếp, Bên B chịu phí trả muộn và khoản bồi thường bằng giá hợp đồng của đơn thuê bị ảnh hưởng. " +
            "Trường hợp bất khả kháng phải có minh chứng và vị trí hiện tại; SmartCar ưu tiên xử lý đổi xe cho khách kế tiếp. Nếu khách kế tiếp không chấp nhận phương án đổi xe và phải hủy đơn, các khoản khách đó đã thanh toán được hoàn theo chính sách; khoản bồi thường (nếu có) được xác định theo thiệt hại thực tế có căn cứ và khấu trừ từ tiền cọc của khách đang thuê. Khoản bồi thường này được thông báo rõ trước khi duyệt/thanh toán gia hạn và không tự động lấy bằng giá hợp đồng của đơn kế tiếp.";

        public static bool HasTurnaroundConflict(
            DateTime pickupA,
            DateTime returnA,
            DateTime pickupB,
            DateTime returnB)
        {
            var buffer = TimeSpan.FromMinutes(VehicleTurnaroundMinutes);
            return pickupA < returnB.Add(buffer) && returnA.Add(buffer) > pickupB;
        }

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
