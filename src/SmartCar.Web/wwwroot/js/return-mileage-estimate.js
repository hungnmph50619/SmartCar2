(() => {
    const input = document.querySelector('[data-return-mileage]');
    const estimate = document.querySelector('[data-return-mileage-estimate]');
    if (!input || !estimate) return;

    const startingMileage = Number(input.dataset.handoverMileage);
    const allowance = Number(input.dataset.includedKilometers);
    const rate = Number(input.dataset.excessKmRate);
    const format = value => new Intl.NumberFormat('vi-VN').format(value);

    const update = () => {
        const endingMileage = Number(input.value);
        if (!input.value || !Number.isInteger(endingMileage) || endingMileage < startingMileage ||
            !Number.isFinite(allowance) || !Number.isFinite(rate)) {
            estimate.textContent = 'Nhập số km lúc trả hợp lệ để xem phụ phí vượt km tạm tính.';
            return;
        }

        const driven = endingMileage - startingMileage;
        const excess = Math.max(0, driven - allowance);
        estimate.textContent = `Đã đi ${format(driven)} km · định mức ${format(allowance)} km · ` +
            `vượt ${format(excess)} km · phụ phí tạm tính ${format(excess * rate)} đ.`;
    };

    input.addEventListener('input', update);
    update();
})();
