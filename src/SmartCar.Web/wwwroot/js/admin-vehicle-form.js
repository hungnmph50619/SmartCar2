(() => {
    const parseDisplayDate = (value) => {
        const match = /^\s*(\d{2})\/(\d{2})\/(\d{4})\s*$/.exec(value || '');
        if (!match) return null;
        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const date = new Date(year, month - 1, day);
        if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return null;
        return { day, month, year, iso: `${year.toString().padStart(4,'0')}-${month.toString().padStart(2,'0')}-${day.toString().padStart(2,'0')}`, date };
    };

    const vehicleModels = {
        toyota: ['Vios', 'Yaris Cross', 'Corolla Cross', 'Camry', 'Innova', 'Innova Cross', 'Fortuner', 'Raize', 'Hilux'],
        honda: ['City', 'Civic', 'Accord', 'HR-V', 'CR-V', 'BR-V'],
        ford: ['Territory', 'Everest', 'Ranger', 'Explorer'],
        hyundai: ['Grand i10', 'Accent', 'Elantra', 'Venue', 'Creta', 'Tucson', 'Santa Fe', 'Stargazer'],
        kia: ['Morning', 'Soluto', 'K3', 'K5', 'Sonet', 'Seltos', 'Sportage', 'Sorento', 'Carnival'],
        mazda: ['Mazda2', 'Mazda3', 'Mazda6', 'CX-3', 'CX-30', 'CX-5', 'CX-8'],
        mitsubishi: ['Attrage', 'Xforce', 'Xpander', 'Outlander', 'Triton'],
        nissan: ['Almera', 'Kicks', 'Navara', 'Terra'],
        suzuki: ['Swift', 'Ciaz', 'Ertiga', 'XL7', 'Jimny'],
        vinfast: ['VF 3', 'VF 5', 'VF 6', 'VF 7', 'VF 8', 'VF 9'],
        mercedesbenz: ['C-Class', 'E-Class', 'S-Class', 'GLA', 'GLC', 'GLE', 'GLS'],
        bmw: ['3 Series', '5 Series', '7 Series', 'X1', 'X3', 'X5', 'X7'],
        lexus: ['ES', 'LS', 'NX', 'RX', 'GX', 'LX'],
        peugeot: ['2008', '3008', '5008'],
        isuzu: ['mu-X', 'D-Max']
    };

    const normalizeBrand = (value) => (value || '')
        .replace(/\([^)]*\)/g, '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .toLowerCase()
        .replace(/[^a-z0-9]/g, '');

    const vehicleForm = document.querySelector('[data-vehicle-form]');
    if (vehicleForm) {
        const priceDisplay = document.getElementById('dailyPriceDisplay');
        const priceValue = document.getElementById('DailyPrice');
        const imageInput = document.getElementById('Images');
        const imageSelection = document.getElementById('vehicle-image-selection');
        const brandSelect = document.getElementById('BrandId');
        const modelValue = document.getElementById('VehicleModel');
        const modelPreset = document.querySelector('[data-model-preset]');
        const modelCustom = document.querySelector('[data-model-custom]');
        const colorValue = document.getElementById('Color');
        const colorPreset = document.querySelector('[data-color-preset]');
        const colorCustom = document.querySelector('[data-color-custom]');
        const manufactureYearInput = document.getElementById('ManufactureYear');
        const seatsInput = document.getElementById('Seats');
        const mileageInput = document.getElementById('CurrentMileage');
        const currentYear = new Date().getFullYear();
        const allowedSeats = [2, 4, 5, 7, 8, 9, 16];
        const maxMileage = 2000000;
        const maxDailyPrice = Number(priceDisplay?.dataset.maxPrice || 100000000);
        let dirty = false;
        let submitting = false;

        const validationMessageFor = (fieldName) =>
            vehicleForm.querySelector(`[data-valmsg-for="${fieldName}"]`);

        const setFieldError = (input, fieldName, message = '') => {
            const target = validationMessageFor(fieldName);
            if (input) input.classList.toggle('is-invalid', Boolean(message));
            if (target) {
                target.textContent = message;
                target.classList.toggle('field-validation-error', Boolean(message));
                target.classList.toggle('field-validation-valid', !message);
            }
            return !message;
        };

        const validateManufactureYear = () => {
            if (!manufactureYearInput) return true;
            const value = Number(manufactureYearInput.value);
            const valid = Number.isInteger(value) && value >= 1980 && value <= currentYear;
            return setFieldError(
                manufactureYearInput,
                'ManufactureYear',
                valid ? '' : `Năm sản xuất phải từ 1980 đến ${currentYear}.`);
        };

        const validateSeats = () => {
            if (!seatsInput) return true;
            const value = Number(seatsInput.value);
            const valid = Number.isInteger(value) && allowedSeats.includes(value);
            return setFieldError(
                seatsInput,
                'Seats',
                valid ? '' : 'Vui lòng chọn số chỗ trong danh sách cho phép.');
        };

        const validatePrice = () => {
            syncPrice();
            if (!priceValue) return true;
            const value = Number(priceValue.value);
            const valid = Number.isFinite(value) && value >= 1 && value <= maxDailyPrice;
            return setFieldError(
                priceDisplay,
                'DailyPrice',
                valid ? '' : 'Giá thuê/ngày phải từ 1 đến 100.000.000 đồng.');
        };

        const validateMileage = () => {
            if (!mileageInput) return true;
            const value = Number(mileageInput.value);
            const valid = Number.isInteger(value) && value >= 0 && value <= maxMileage;
            return setFieldError(
                mileageInput,
                'CurrentMileage',
                valid ? '' : 'Số km hiện tại phải từ 0 đến 2.000.000 km.');
        };

        const setPriceFromHidden = () => {
            if (!priceDisplay || !priceValue) return;
            const normalized = String(priceValue.value || '').replace(',', '.');
            const number = Number(normalized);
            if (Number.isFinite(number) && number > 0) {
                priceDisplay.value = Math.trunc(number).toLocaleString('vi-VN');
            }
        };
        const syncPrice = () => {
            if (!priceDisplay || !priceValue) return;
            const digits = priceDisplay.value.replace(/\D/g, '');
            priceValue.value = digits || '0';
            priceDisplay.value = digits ? Number(digits).toLocaleString('vi-VN') : '';
        };
        setPriceFromHidden();
        priceDisplay?.addEventListener('input', () => {
            syncPrice();
            validatePrice();
            dirty = true;
        });
        priceDisplay?.addEventListener('blur', validatePrice);
        manufactureYearInput?.addEventListener('input', validateManufactureYear);
        manufactureYearInput?.addEventListener('blur', validateManufactureYear);
        seatsInput?.addEventListener('input', validateSeats);
        seatsInput?.addEventListener('blur', validateSeats);
        mileageInput?.addEventListener('input', validateMileage);
        mileageInput?.addEventListener('blur', validateMileage);

        const populateModels = (preserveCurrent = true) => {
            if (!modelPreset || !modelValue) return;
            const existing = preserveCurrent ? String(modelValue.value || '').trim() : '';
            const brandText = brandSelect?.selectedOptions?.[0]?.textContent || '';
            const models = vehicleModels[normalizeBrand(brandText)] || [];

            modelPreset.innerHTML = '<option value="">Chọn dòng xe</option>';
            models.forEach((model) => {
                const option = document.createElement('option');
                option.value = model;
                option.textContent = model;
                modelPreset.appendChild(option);
            });

            if (existing && models.includes(existing)) {
                modelPreset.value = existing;
                modelCustom?.classList.add('d-none');
                if (modelCustom) modelCustom.value = '';
            } else if (existing) {
                const currentOption = document.createElement('option');
                currentOption.value = existing;
                currentOption.textContent = `${existing} (giá trị hiện tại)`;
                modelPreset.appendChild(currentOption);
                modelPreset.value = existing;
                modelCustom?.classList.add('d-none');
                if (modelCustom) modelCustom.value = '';
            } else {
                modelPreset.value = '';
            }

            const other = document.createElement('option');
            other.value = '__other__';
            other.textContent = 'Khác...';
            modelPreset.appendChild(other);
        };

        populateModels(true);

        brandSelect?.addEventListener('change', () => {
            if (modelValue) modelValue.value = '';
            if (modelCustom) { modelCustom.value = ''; modelCustom.classList.add('d-none'); }
            populateModels(false);
            dirty = true;
        });

        modelPreset?.addEventListener('change', () => {
            dirty = true;
            if (!modelValue || !modelCustom) return;
            if (modelPreset.value === '__other__') {
                modelValue.value = '';
                modelCustom.classList.remove('d-none');
                modelCustom.focus();
            } else {
                modelCustom.classList.add('d-none');
                modelCustom.value = '';
                modelValue.value = modelPreset.value;
            }
        });

        modelCustom?.addEventListener('input', () => {
            if (modelValue) modelValue.value = modelCustom.value.trimStart();
            dirty = true;
        });

        const commonColors = ['Trắng', 'Đen', 'Bạc', 'Xám', 'Đỏ', 'Xanh dương', 'Xanh lá', 'Nâu', 'Vàng', 'Cam'];
        const initializeColor = () => {
            if (!colorPreset || !colorValue || !colorCustom) return;
            const existing = String(colorValue.value || '').trim();
            if (!existing) {
                colorPreset.value = '';
                colorCustom.classList.add('d-none');
            } else if (commonColors.includes(existing)) {
                colorPreset.value = existing;
                colorCustom.classList.add('d-none');
            } else {
                colorPreset.value = '__other__';
                colorCustom.value = existing;
                colorCustom.classList.remove('d-none');
            }
        };
        initializeColor();

        colorPreset?.addEventListener('change', () => {
            dirty = true;
            if (!colorValue || !colorCustom) return;
            if (colorPreset.value === '__other__') {
                colorValue.value = colorCustom.value.trim();
                colorCustom.classList.remove('d-none');
                colorCustom.focus();
            } else {
                colorCustom.classList.add('d-none');
                colorCustom.value = '';
                colorValue.value = colorPreset.value;
            }
        });

        colorCustom?.addEventListener('input', () => {
            if (colorValue) colorValue.value = colorCustom.value.trimStart();
            dirty = true;
        });

        imageInput?.addEventListener('change', () => {
            dirty = true;
            if (!imageSelection) return;
            const count = imageInput.files?.length || 0;
            imageSelection.textContent = count === 0 ? 'Chưa chọn ảnh mới' : `Đã chọn ${count} ảnh`;
        });

        vehicleForm.querySelectorAll('input, select, textarea').forEach((element) => {
            if (element === priceDisplay || element === imageInput || element === modelPreset || element === modelCustom || element === colorPreset || element === colorCustom) return;
            element.addEventListener('change', () => { dirty = true; });
            element.addEventListener('input', () => { dirty = true; });
        });

        vehicleForm.addEventListener('submit', (event) => {
            if (modelValue) modelValue.value = modelValue.value.trim();
            if (colorValue) colorValue.value = colorValue.value.trim();

            const modelValid = !(modelPreset?.value === '__other__' && modelCustom && !modelCustom.value.trim());
            setFieldError(
                modelCustom || modelValue,
                'VehicleModel',
                modelValid ? '' : 'Vui lòng nhập dòng xe thực tế hoặc chọn một dòng xe trong danh sách.');

            const colorValid = !(colorPreset?.value === '__other__' && colorCustom && !colorCustom.value.trim());
            setFieldError(
                colorCustom || colorValue,
                'Color',
                colorValid ? '' : 'Vui lòng nhập màu xe thực tế hoặc chọn một màu phổ biến.');

            const numericValid = [
                validateManufactureYear(),
                validateSeats(),
                validatePrice(),
                validateMileage()
            ].every(Boolean);

            if (!modelValid || !colorValid || !numericValid) {
                event.preventDefault();
                const firstInvalid = vehicleForm.querySelector('.is-invalid');
                firstInvalid?.focus();
                return;
            }

            submitting = true;
        });

        document.querySelectorAll('[data-vehicle-back]').forEach((link) => {
            link.addEventListener('click', (event) => {
                if (dirty && !submitting && !window.confirm('Bạn có thay đổi chưa lưu. Bạn có chắc muốn rời trang?')) {
                    event.preventDefault();
                }
            });
        });

        window.addEventListener('beforeunload', (event) => {
            if (!dirty || submitting) return;
            event.preventDefault();
            event.returnValue = '';
        });
    }

    const rentalForm = document.querySelector('[data-rental-date-form]');
    if (rentalForm) {
        // Lỗi 5: cho phép chọn ngày bằng lịch (native date picker) bên cạnh việc gõ tay
        // dd/MM/yyyy. Nút 📅 mở input[type=date] ẩn; khi chọn xong, đồng bộ ngược lại
        // ô hiển thị dd/MM/yyyy để không phá vỡ định dạng và validate hiện có.
        rentalForm.querySelectorAll('[data-calendar-trigger]').forEach((button) => {
            const nativeInput = document.getElementById(button.dataset.calendarTrigger);
            if (!nativeInput) return;
            button.addEventListener('click', () => {
                if (typeof nativeInput.showPicker === 'function') {
                    nativeInput.showPicker();
                } else {
                    nativeInput.focus();
                    nativeInput.click();
                }
            });
        });
        rentalForm.querySelectorAll('[data-calendar-sync]').forEach((nativeInput) => {
            const displayInput = document.getElementById(nativeInput.dataset.calendarSync);
            if (!displayInput) return;
            nativeInput.addEventListener('change', () => {
                if (!nativeInput.value) return;
                const [year, month, day] = nativeInput.value.split('-');
                displayInput.value = `${day}/${month}/${year}`;
            });
        });

        rentalForm.addEventListener('submit', (event) => {
            const pickupDisplay = document.getElementById('pickupDateDisplay');
            const returnDisplay = document.getElementById('returnDateDisplay');
            const pickupValue = document.getElementById('pickupDateValue');
            const returnValue = document.getElementById('returnDateValue');
            const errorBox = rentalForm.querySelector('[data-rental-date-error]');
            const pickup = parseDisplayDate(pickupDisplay?.value);
            const returned = parseDisplayDate(returnDisplay?.value);
            let message = '';
            if (!pickup) message = 'Ngày nhận không hợp lệ. Vui lòng nhập theo định dạng dd/MM/yyyy.';
            else if (!returned) message = 'Ngày trả không hợp lệ. Vui lòng nhập theo định dạng dd/MM/yyyy.';
            else if (returned.date <= pickup.date) message = 'Ngày trả phải sau ngày nhận.';
            if (message) {
                event.preventDefault();
                if (errorBox) { errorBox.textContent = message; errorBox.classList.remove('d-none'); }
                return;
            }
            if (errorBox) errorBox.classList.add('d-none');
            if (pickupValue) pickupValue.value = pickup.iso;
            if (returnValue) returnValue.value = returned.iso;
        });
    }
})();
