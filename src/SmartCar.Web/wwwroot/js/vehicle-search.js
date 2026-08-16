(function () {
    'use strict';

    const forms = document.querySelectorAll('[data-vehicle-search-form]');
    if (forms.length === 0) {
        return;
    }

    function parseVietnameseDateTime(value) {
        const match = /^\s*(\d{2})\/(\d{2})\/(\d{4})\s+([01]\d|2[0-3]):([0-5]\d)\s*$/.exec(value);
        if (!match) {
            return null;
        }

        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const hour = Number(match[4]);
        const minute = Number(match[5]);

        const result = new Date(
            year,
            month - 1,
            day,
            hour,
            minute,
            0,
            0
        );

        if (
            result.getFullYear() !== year ||
            result.getMonth() !== month - 1 ||
            result.getDate() !== day ||
            result.getHours() !== hour ||
            result.getMinutes() !== minute
        ) {
            return null;
        }

        return result;
    }

    function toLocalIso(date) {
        const pad = value => String(value).padStart(2, '0');

        return (
            date.getFullYear() +
            '-' +
            pad(date.getMonth() + 1) +
            '-' +
            pad(date.getDate()) +
            'T' +
            pad(date.getHours()) +
            ':' +
            pad(date.getMinutes())
        );
    }

    function focusSearchResults() {
        if (window.location.hash !== '#vehicle-results') {
            return;
        }

        const resultsSection = document.getElementById('vehicle-results');

        if (!resultsSection) {
            return;
        }

        window.setTimeout(function () {
            resultsSection.scrollIntoView({
                behavior: 'smooth',
                block: 'start'
            });

            resultsSection.focus({
                preventScroll: true
            });
        }, 100);
    }

    forms.forEach(function (form) {
        const pickupDisplay = form.querySelector(
            '[data-vn-datetime="pickup"]'
        );

        const returnDisplay = form.querySelector(
            '[data-vn-datetime="return"]'
        );

        const pickupHidden = form.querySelector(
            '[data-vn-datetime-hidden="pickup"]'
        );

        const returnHidden = form.querySelector(
            '[data-vn-datetime-hidden="return"]'
        );

        const pickupError = form.querySelector(
            '[data-vn-datetime-error="pickup"]'
        );

        const returnError = form.querySelector(
            '[data-vn-datetime-error="return"]'
        );

        const generalError = form.querySelector(
            '[data-search-error]'
        );

        const submitButton = form.querySelector(
            '[data-search-submit]'
        );

        const submitLabel = submitButton?.querySelector(
            '[data-search-submit-label]'
        );

        const submitLoading = submitButton?.querySelector(
            '[data-search-submit-loading]'
        );

        if (
            !pickupDisplay ||
            !returnDisplay ||
            !pickupHidden ||
            !returnHidden ||
            !submitButton
        ) {
            return;
        }

        function setFieldError(input, errorElement, message) {
            const hasError = Boolean(message);

            input.classList.toggle(
                'is-invalid',
                hasError
            );

            input.setAttribute(
                'aria-invalid',
                hasError ? 'true' : 'false'
            );

            if (errorElement) {
                errorElement.textContent = message || '';

                errorElement.classList.toggle(
                    'is-visible',
                    hasError
                );
            }
        }

        function showGeneralError(message) {
            if (!generalError) {
                return;
            }

            generalError.textContent = message || '';

            generalError.classList.toggle(
                'is-visible',
                Boolean(message)
            );
        }

        function clearErrors() {
            setFieldError(
                pickupDisplay,
                pickupError,
                ''
            );

            setFieldError(
                returnDisplay,
                returnError,
                ''
            );

            showGeneralError('');
        }

        function setSubmitting(isSubmitting) {
            submitButton.disabled = isSubmitting;

            submitButton.setAttribute(
                'aria-busy',
                isSubmitting ? 'true' : 'false'
            );

            form.classList.toggle(
                'is-submitting',
                isSubmitting
            );

            if (submitLabel) {
                submitLabel.classList.toggle(
                    'd-none',
                    isSubmitting
                );
            }

            if (submitLoading) {
                submitLoading.classList.toggle(
                    'd-none',
                    !isSubmitting
                );
            }
        }

        function validate() {
            clearErrors();

            const pickup = parseVietnameseDateTime(
                pickupDisplay.value
            );

            const returnDate = parseVietnameseDateTime(
                returnDisplay.value
            );

            if (!pickup) {
                setFieldError(
                    pickupDisplay,
                    pickupError,
                    'Nhập ngày giờ nhận xe theo định dạng dd/MM/yyyy HH:mm.'
                );

                pickupDisplay.focus();

                return null;
            }

            if (!returnDate) {
                setFieldError(
                    returnDisplay,
                    returnError,
                    'Nhập ngày giờ trả xe theo định dạng dd/MM/yyyy HH:mm.'
                );

                returnDisplay.focus();

                return null;
            }

            if (pickup.getTime() <= Date.now()) {
                setFieldError(
                    pickupDisplay,
                    pickupError,
                    'Ngày giờ nhận xe phải sau thời điểm hiện tại.'
                );

                pickupDisplay.focus();

                return null;
            }

            if (
                returnDate.getTime() <= pickup.getTime()
            ) {
                setFieldError(
                    returnDisplay,
                    returnError,
                    'Ngày giờ trả xe phải sau ngày giờ nhận xe.'
                );

                returnDisplay.focus();

                return null;
            }

            return {
                pickup,
                returnDate
            };
        }

        form.addEventListener(
            'submit',
            function (event) {
                if (submitButton.disabled) {
                    event.preventDefault();
                    return;
                }

                const values = validate();

                if (!values) {
                    event.preventDefault();

                    showGeneralError(
                        'Vui lòng kiểm tra lại thông tin được đánh dấu.'
                    );

                    return;
                }

                /*
                 * QUAN TRỌNG:
                 * Luôn cập nhật hidden input theo ngày người dùng
                 * vừa chọn trước khi submit.
                 */
                pickupHidden.value = toLocalIso(
                    values.pickup
                );

                returnHidden.value = toLocalIso(
                    values.returnDate
                );

                setSubmitting(true);
            }
        );

        [
            pickupDisplay,
            returnDisplay
        ].forEach(function (input) {
            input.addEventListener(
                'input',
                function () {
                    if (input === pickupDisplay) {
                        setFieldError(
                            pickupDisplay,
                            pickupError,
                            ''
                        );
                    } else {
                        setFieldError(
                            returnDisplay,
                            returnError,
                            ''
                        );
                    }

                    showGeneralError('');
                }
            );
        });

        window.addEventListener(
            'pageshow',
            function () {
                setSubmitting(false);
            }
        );
    });

    /*
     * =========================================================
     * ĐỒNG BỘ NGÀY CHO FORM BỘ LỌC
     * =========================================================
     *
     * Đây là phần quan trọng để sửa lỗi:
     *
     * - Tìm xe ban đầu theo ngày A -> B
     * - Sau đó người dùng đổi sang ngày C -> D
     * - Khi lọc hãng / loại xe / giá...
     * - Form bộ lọc phải tiếp tục gửi C -> D
     *
     * Không được giữ A -> B từ request ban đầu.
     */
    const filterForms = document.querySelectorAll(
        '[data-vehicle-filter-form]'
    );

    filterForms.forEach(function (filterForm) {
        filterForm.addEventListener(
            'submit',
            function (event) {
                /*
                 * Lấy ngày hiện tại đang hiển thị
                 * trên form tìm kiếm chính.
                 */
                const pickupDisplay =
                    document.querySelector(
                        '[data-vn-datetime="pickup"]'
                    );

                const returnDisplay =
                    document.querySelector(
                        '[data-vn-datetime="return"]'
                    );

                /*
                 * Hidden input nằm trong form filter.
                 */
                const pickupHidden =
                    filterForm.querySelector(
                        '[data-filter-period="pickup"]'
                    );

                const returnHidden =
                    filterForm.querySelector(
                        '[data-filter-period="return"]'
                    );

                if (
                    !pickupDisplay ||
                    !returnDisplay ||
                    !pickupHidden ||
                    !returnHidden
                ) {
                    return;
                }

                const pickup =
                    parseVietnameseDateTime(
                        pickupDisplay.value
                    );

                const returnDate =
                    parseVietnameseDateTime(
                        returnDisplay.value
                    );

                /*
                 * Nếu ngày không hợp lệ,
                 * không cho submit form filter.
                 */
                if (
                    !pickup ||
                    !returnDate ||
                    pickup.getTime() <= Date.now() ||
                    returnDate.getTime() <=
                    pickup.getTime()
                ) {
                    event.preventDefault();

                    const searchForm =
                        document.querySelector(
                            '[data-vehicle-search-form]'
                        );

                    if (
                        searchForm &&
                        typeof searchForm.requestSubmit ===
                        'function'
                    ) {
                        searchForm.requestSubmit();
                    }

                    return;
                }

                /*
                 * QUAN TRỌNG NHẤT:
                 *
                 * Ghi đè ngày cũ A -> B bằng
                 * ngày người dùng đang chọn C -> D.
                 *
                 * Nhờ vậy khi lọc xe:
                 *
                 * PickupDate = C
                 * ReturnDate = D
                 *
                 * thay vì quay lại ngày mặc định A -> B.
                 */
                pickupHidden.value =
                    toLocalIso(pickup);

                returnHidden.value =
                    toLocalIso(returnDate);
            }
        );
    });

    focusSearchResults();
})();