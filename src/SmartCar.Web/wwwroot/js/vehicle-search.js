(function () {
    'use strict';

    const forms = document.querySelectorAll('[data-vehicle-search-form]');
    if (forms.length === 0) return;

    function parseVietnameseDateTime(value) {
        const match = /^\s*(\d{2})\/(\d{2})\/(\d{4})\s+([01]\d|2[0-3]):([0-5]\d)\s*$/.exec(value || '');
        if (!match) return null;
        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const hour = Number(match[4]);
        const minute = Number(match[5]);
        const result = new Date(year, month - 1, day, hour, minute, 0, 0);
        if (result.getFullYear() !== year || result.getMonth() !== month - 1 || result.getDate() !== day || result.getHours() !== hour || result.getMinutes() !== minute) return null;
        return result;
    }

    function parseLocalIso(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})T([01]\d|2[0-3]):([0-5]\d)$/.exec(value || '');
        if (!match) return null;
        const result = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]), Number(match[4]), Number(match[5]), 0, 0);
        return Number.isNaN(result.getTime()) ? null : result;
    }

    function toLocalIso(date) {
        const pad = value => String(value).padStart(2, '0');
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) + 'T' + pad(date.getHours()) + ':' + pad(date.getMinutes());
    }

    function formatVietnameseDateTime(date) {
        const pad = value => String(value).padStart(2, '0');
        return pad(date.getDate()) + '/' + pad(date.getMonth() + 1) + '/' + date.getFullYear() + ' ' + pad(date.getHours()) + ':' + pad(date.getMinutes());
    }

    function ceilToMinute(date) { return new Date(Math.ceil(date.getTime() / 60000) * 60000); }
    function addMinutes(date, minutes) { return new Date(date.getTime() + minutes * 60000); }
    function addDays(date, days) { const result = new Date(date.getTime()); result.setDate(result.getDate() + days); return result; }

    function getCurrentPeriod() {
        const pickupDisplay = document.querySelector('[data-vn-datetime="pickup"]');
        const returnDisplay = document.querySelector('[data-vn-datetime="return"]');
        if (!pickupDisplay || !returnDisplay) return null;
        const pickup = parseVietnameseDateTime(pickupDisplay.value);
        const returnDate = parseVietnameseDateTime(returnDisplay.value);
        if (!pickup || !returnDate || returnDate.getTime() <= pickup.getTime()) return null;
        return { pickup, returnDate };
    }

    function focusSearchResults() {
        if (window.location.hash !== '#vehicle-results') return;
        const resultsSection = document.getElementById('vehicle-results');
        if (!resultsSection) return;
        window.setTimeout(function () {
            resultsSection.scrollIntoView({ behavior: 'smooth', block: 'start' });
            resultsSection.focus({ preventScroll: true });
        }, 100);
    }

    forms.forEach(function (form) {
        const pickupDisplay = form.querySelector('[data-vn-datetime="pickup"]');
        const returnDisplay = form.querySelector('[data-vn-datetime="return"]');
        const pickupHidden = form.querySelector('[data-vn-datetime-hidden="pickup"]');
        const returnHidden = form.querySelector('[data-vn-datetime-hidden="return"]');
        const pickupError = form.querySelector('[data-vn-datetime-error="pickup"]');
        const returnError = form.querySelector('[data-vn-datetime-error="return"]');
        const generalError = form.querySelector('[data-search-error]');
        const submitButton = form.querySelector('[data-search-submit]');
        const submitLabel = submitButton?.querySelector('[data-search-submit-label]');
        const submitLoading = submitButton?.querySelector('[data-search-submit-loading]');
        if (!pickupDisplay || !returnDisplay || !pickupHidden || !returnHidden || !submitButton) return;

        function setFieldError(input, errorElement, message) {
            const hasError = Boolean(message);
            input.classList.toggle('is-invalid', hasError);
            input.setAttribute('aria-invalid', hasError ? 'true' : 'false');
            if (errorElement) {
                errorElement.textContent = message || '';
                errorElement.classList.toggle('is-visible', hasError);
            }
        }

        function showGeneralError(message) {
            if (!generalError) return;
            generalError.textContent = message || '';
            generalError.classList.toggle('is-visible', Boolean(message));
        }

        function setSubmitting(isSubmitting) {
            submitButton.disabled = isSubmitting;
            submitButton.setAttribute('aria-busy', isSubmitting ? 'true' : 'false');
            form.classList.toggle('is-submitting', isSubmitting);
            submitLabel?.classList.toggle('d-none', isSubmitting);
            submitLoading?.classList.toggle('d-none', !isSubmitting);
        }

        function getDisplayDate(display, hidden) {
            return parseVietnameseDateTime(display.value) || parseLocalIso(hidden.value);
        }

        function createNativePicker(display, hidden, label, getMinimumDate, onPicked) {
            const control = display.closest('.vn-datetime-control');
            if (!control) return null;
            const nativePicker = document.createElement('input');
            nativePicker.type = 'datetime-local';
            nativePicker.step = '60';
            nativePicker.tabIndex = -1;
            nativePicker.setAttribute('aria-hidden', 'true');
            nativePicker.style.cssText = 'position:absolute;width:1px;height:1px;right:0;bottom:0;opacity:0;pointer-events:none;border:0;padding:0';
            control.appendChild(nativePicker);
            const icon = control.querySelector('.vn-datetime-icon');
            if (icon) {
                icon.style.pointerEvents = 'auto';
                icon.style.cursor = 'pointer';
                icon.setAttribute('role', 'button');
                icon.setAttribute('tabindex', '0');
                icon.setAttribute('aria-label', label);
            }
            function sync() {
                const minimumDate = getMinimumDate();
                if (minimumDate) nativePicker.min = toLocalIso(minimumDate); else nativePicker.removeAttribute('min');
                const currentDate = getDisplayDate(display, hidden);
                if (currentDate) nativePicker.value = toLocalIso(currentDate);
            }
            function openPicker() {
                sync();
                try { if (typeof nativePicker.showPicker === 'function') nativePicker.showPicker(); else { nativePicker.focus(); nativePicker.click(); } }
                catch { nativePicker.focus(); }
            }
            display.addEventListener('click', openPicker);
            if (icon) {
                icon.addEventListener('click', function (event) { event.preventDefault(); event.stopPropagation(); openPicker(); });
                icon.addEventListener('keydown', function (event) { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); openPicker(); } });
            }
            nativePicker.addEventListener('change', function () {
                const selectedDate = parseLocalIso(nativePicker.value);
                if (!selectedDate) return;
                display.value = formatVietnameseDateTime(selectedDate);
                hidden.value = toLocalIso(selectedDate);
                onPicked(selectedDate);
            });
            return { sync, setValue: date => { nativePicker.value = toLocalIso(date); } };
        }

        let pickupPicker = null;
        let returnPicker = null;
        pickupPicker = createNativePicker(pickupDisplay, pickupHidden, 'Mở lịch chọn ngày giờ nhận xe', function () {
            return addMinutes(ceilToMinute(new Date()), 1);
        }, function (selectedPickup) {
            setFieldError(pickupDisplay, pickupError, '');
            showGeneralError('');
            const suggestedReturn = addDays(selectedPickup, 1);
            returnDisplay.value = formatVietnameseDateTime(suggestedReturn);
            returnHidden.value = toLocalIso(suggestedReturn);
            setFieldError(returnDisplay, returnError, '');
            returnPicker?.setValue(suggestedReturn);
            returnPicker?.sync();
        });
        returnPicker = createNativePicker(returnDisplay, returnHidden, 'Mở lịch chọn ngày giờ trả xe', function () {
            const pickup = getDisplayDate(pickupDisplay, pickupHidden);
            return pickup ? addMinutes(pickup, 1) : addMinutes(ceilToMinute(new Date()), 1);
        }, function (selectedReturn) {
            const pickup = getDisplayDate(pickupDisplay, pickupHidden);
            if (pickup && selectedReturn.getTime() <= pickup.getTime()) {
                setFieldError(returnDisplay, returnError, 'Ngày giờ trả xe phải sau ngày giờ nhận xe.');
                return;
            }
            setFieldError(returnDisplay, returnError, '');
            showGeneralError('');
        });
        pickupPicker?.sync();
        returnPicker?.sync();

        function validate() {
            setFieldError(pickupDisplay, pickupError, '');
            setFieldError(returnDisplay, returnError, '');
            showGeneralError('');
            const pickup = parseVietnameseDateTime(pickupDisplay.value);
            const returnDate = parseVietnameseDateTime(returnDisplay.value);
            if (!pickup) { setFieldError(pickupDisplay, pickupError, 'Nhập ngày giờ nhận xe theo định dạng dd/MM/yyyy HH:mm.'); pickupDisplay.focus(); return null; }
            if (!returnDate) { setFieldError(returnDisplay, returnError, 'Nhập ngày giờ trả xe theo định dạng dd/MM/yyyy HH:mm.'); returnDisplay.focus(); return null; }
            if (pickup.getTime() <= Date.now()) { setFieldError(pickupDisplay, pickupError, 'Ngày giờ nhận xe phải sau thời điểm hiện tại.'); pickupDisplay.focus(); return null; }
            if (returnDate.getTime() <= pickup.getTime()) { setFieldError(returnDisplay, returnError, 'Ngày giờ trả xe phải sau ngày giờ nhận xe.'); returnDisplay.focus(); return null; }
            return { pickup, returnDate };
        }

        form.addEventListener('submit', function (event) {
            if (submitButton.disabled) { event.preventDefault(); return; }
            const values = validate();
            if (!values) { event.preventDefault(); showGeneralError('Vui lòng kiểm tra lại thông tin được đánh dấu.'); return; }
            pickupHidden.value = toLocalIso(values.pickup);
            returnHidden.value = toLocalIso(values.returnDate);
            setSubmitting(true);
        });

        [pickupDisplay, returnDisplay].forEach(function (input) {
            input.addEventListener('input', function () {
                const isPickup = input === pickupDisplay;
                const hidden = isPickup ? pickupHidden : returnHidden;
                const picker = isPickup ? pickupPicker : returnPicker;
                const parsed = parseVietnameseDateTime(input.value);
                if (parsed) {
                    hidden.value = toLocalIso(parsed);
                    picker?.setValue(parsed);
                    if (isPickup) returnPicker?.sync();
                }
                setFieldError(input, isPickup ? pickupError : returnError, '');
                showGeneralError('');
            });
        });

        window.addEventListener('pageshow', function () {
            setSubmitting(false);
            pickupPicker?.sync();
            returnPicker?.sync();
        });
    });

    // Keep the currently displayed rental period when applying advanced filters.
    document.querySelectorAll('#advancedFilters form, [data-vehicle-filter-form]').forEach(function (filterForm) {
        filterForm.addEventListener('submit', function (event) {
            const period = getCurrentPeriod();
            if (!period || period.pickup.getTime() <= Date.now()) {
                event.preventDefault();
                const searchForm = document.querySelector('[data-vehicle-search-form]');
                if (searchForm && typeof searchForm.requestSubmit === 'function') searchForm.requestSubmit();
                return;
            }
            const pickupHidden = filterForm.querySelector('input[name="PickupDate"]');
            const returnHidden = filterForm.querySelector('input[name="ReturnDate"]');
            if (pickupHidden) pickupHidden.value = toLocalIso(period.pickup);
            if (returnHidden) returnHidden.value = toLocalIso(period.returnDate);
        });
    });

    // Normalize the period passed to Details. This avoids culture-dependent DateTime URLs
    // and guarantees Details receives exactly the dates currently shown to the user.
    document.querySelectorAll('#vehicle-results a[href*="Details"]').forEach(function (link) {
        link.addEventListener('click', function () {
            const period = getCurrentPeriod();
            if (!period) return;
            try {
                const url = new URL(link.href, window.location.origin);
                url.searchParams.set('pickupDate', toLocalIso(period.pickup));
                url.searchParams.set('returnDate', toLocalIso(period.returnDate));
                link.href = url.toString();
            } catch { }
        });
    });

    focusSearchResults();
})();