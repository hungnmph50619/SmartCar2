(() => {
    document.querySelectorAll('[data-report-date-filter]').forEach(form => {
        const from = form.querySelector('[name="fromDate"]');
        const to = form.querySelector('[name="toDate"]');
        if (!from || !to) return;
        const validate = () => {
            [from, to].forEach(input => {
                input.setCustomValidity('');
                if (!input.value) input.setCustomValidity('Vui lòng chọn ngày hợp lệ.');
                else if (input.validity.rangeOverflow)
                    input.setCustomValidity('Không được chọn ngày trong tương lai (giờ Việt Nam).');
                else if (input.validity.rangeUnderflow)
                    input.setCustomValidity('Ngày nằm ngoài phạm vi báo cáo hỗ trợ.');
            });
            if (from.validity.valid && to.validity.valid && from.value > to.value)
                to.setCustomValidity('Đến ngày phải bằng hoặc sau Từ ngày.');
        };
        [from, to].forEach(input => input.addEventListener('input', validate));
        form.addEventListener('submit', event => {
            validate();
            if (!form.checkValidity()) {
                event.preventDefault();
                form.reportValidity();
            }
        });
        validate();
    });
})();
