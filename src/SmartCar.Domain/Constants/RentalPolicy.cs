using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Constants
{
    public static class RentalPolicy
    {
        // Tiền cọc = 300% tiền thuê ban đầu.
        public const decimal DepositRate = 3.00m;

        // Giữ chỗ:
        // - Đơn mới có tối đa 60 phút cho bước nhân viên kiểm tra và quản trị viên duyệt.
        // - Sau khi được quản trị viên duyệt, khách có 30 phút để thanh toán/báo đã chuyển khoản.
        // - Sau khi báo chuyển khoản, SmartCar có tối đa 120 phút để đối soát; không giữ lịch vô hạn.
        public const int BookingConfirmationHoldMinutes = 60;
        public const int BookingPaymentHoldMinutes = 30;
        public const int BookingTransferReconciliationHoldMinutes = 120;

        // Khoảng vận hành tối thiểu giữa hai lượt thuê cùng xe: nhận xe trả về, kiểm tra, chụp ảnh,
        // đối chiếu km/nhiên liệu và vệ sinh nhanh trước lượt kế tiếp.
        public const int VehicleTurnaroundMinutes = 60;

        // Khi lượt kế tiếp là giao tận nơi, hệ thống cần thêm thời gian chuẩn bị/di chuyển.
        public const int DeliveryLeadMinutes = 30;

        // No-show: sau thời gian chờ, không hoàn phần tiền thuê. Tiền cọc và phí giao chưa thực hiện
        // vẫn được hoàn theo số thực tế còn lại của đơn.
        public const int NoShowGraceMinutes = 30;
        public const decimal NoShowFeeRate = 1.00m;

        public const decimal StoreLatitude = 21.0381298m;
        public const decimal StoreLongitude = 105.7424982m;

        public const double IncludedDeliveryDistanceKm = 3d;
        public const decimal BaseDeliveryFee = 30_000m;
        public const decimal DeliveryFeePerExtraKm = 10_000m;
        public const double MaxDeliveryDistanceKm = 50d;
        private const double EarthRadiusKm = 6371.0088d;

        public const int IncludedKilometersPerDay = 300;
        public const decimal ExcessKilometerFee = 5_000m;
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

        public static double CalculateDeliveryDistanceKm(
            decimal deliveryLatitude,
            decimal deliveryLongitude)
        {
            var storeLat = ToRadians((double)StoreLatitude);
            var storeLng = ToRadians((double)StoreLongitude);
            var deliveryLat = ToRadians((double)deliveryLatitude);
            var deliveryLng = ToRadians((double)deliveryLongitude);
            var deltaLat = deliveryLat - storeLat;
            var deltaLng = deliveryLng - storeLng;

            var a =
                Math.Pow(Math.Sin(deltaLat / 2d), 2d) +
                Math.Cos(storeLat) * Math.Cos(deliveryLat) *
                Math.Pow(Math.Sin(deltaLng / 2d), 2d);

            var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));

            return Math.Round(
                EarthRadiusKm * c,
                2,
                MidpointRounding.AwayFromZero);
        }

        public static decimal CalculateDeliveryFee(
            VehiclePickupMethod pickupMethod,
            decimal? deliveryLatitude,
            decimal? deliveryLongitude)
        {
            if (pickupMethod != VehiclePickupMethod.Delivery)
            {
                return 0m;
            }

            if (!deliveryLatitude.HasValue || !deliveryLongitude.HasValue)
            {
                return 0m;
            }

            var distanceKm = CalculateDeliveryDistanceKm(
                deliveryLatitude.Value,
                deliveryLongitude.Value);

            var extraDistanceKm = Math.Max(0d, distanceKm - IncludedDeliveryDistanceKm);
            var extraWholeKilometers = (decimal)Math.Ceiling(extraDistanceKm);

            return BaseDeliveryFee + extraWholeKilometers * DeliveryFeePerExtraKm;
        }

        public static bool IsWithinDeliveryRange(
            decimal deliveryLatitude,
            decimal deliveryLongitude)
        {
            return CalculateDeliveryDistanceKm(
                       deliveryLatitude,
                       deliveryLongitude)
                   <= MaxDeliveryDistanceKm;
        }

        public static decimal CalculateDeposit(decimal rentalAmount)
        {
            return Math.Round(
                rentalAmount * DepositRate,
                0,
                MidpointRounding.AwayFromZero);
        }

        private static double ToRadians(double degrees)
        {
            return degrees * Math.PI / 180d;
        }
    }
}
