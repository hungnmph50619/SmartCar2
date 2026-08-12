(() => {
    const avatarInput = document.querySelector('[data-avatar-input]');
    const avatarSubmit = document.querySelector('[data-avatar-submit]');
    const avatarFileName = document.querySelector('[data-avatar-file-name]');

    if (avatarInput && avatarSubmit && avatarFileName) {
        avatarInput.addEventListener('change', () => {
            const file = avatarInput.files && avatarInput.files.length > 0
                ? avatarInput.files[0]
                : null;

            avatarSubmit.disabled = !file;
            avatarFileName.textContent = file
                ? file.name
                : 'JPG, PNG, WEBP · tối đa 2 MB';

            if (!file) return;

            const avatar = document.querySelector('.profile-header .profile-avatar');
            if (!avatar) return;

            const objectUrl = URL.createObjectURL(file);
            if (avatar.tagName === 'IMG') {
                avatar.src = objectUrl;
            } else {
                avatar.textContent = '';
                avatar.style.backgroundImage = `url("${objectUrl}")`;
                avatar.style.backgroundSize = 'cover';
                avatar.style.backgroundPosition = 'center';
            }
        });
    }

    const packageForm = document.querySelector('[data-kyc-package-form]');
    const packageSubmit = packageForm?.querySelector('[data-kyc-submit]') ?? null;
    const submitHint = packageForm?.querySelector('[data-submit-hint]') ?? null;
    const maxDocumentImageBytes = 5 * 1024 * 1024;
    const allowedImageTypes = new Set(['image/jpeg', 'image/png', 'image/webp']);
    const allowedImageExtensions = new Set(['jpg', 'jpeg', 'png', 'webp']);
    const personNamePattern = /^[A-Za-zÀ-ÖØ-öø-ÿĂăĐđĨĩŨũƠơƯưẠ-ỹ]+(?:(?: +|['’\-])[A-Za-zÀ-ÖØ-öø-ÿĂăĐđĨĩŨũƠơƯưẠ-ỹ]+)*$/u;

    function getClientErrorElement(input) {
        const errorId = input?.dataset?.clientErrorId;
        return errorId ? document.getElementById(errorId) : null;
    }

    function setClientError(input, message) {
        if (!input) return false;

        input.setCustomValidity(message || '');
        input.classList.toggle('is-invalid', Boolean(message));

        const error = getClientErrorElement(input);
        if (error) {
            error.textContent = message || '';
            error.classList.toggle('d-none', !message);
        }

        return !message;
    }

    function resetUploadCard(input) {
        const card = input.closest('[data-upload-card]');
        if (!card) return;

        const preview = card.querySelector('[data-upload-preview]');
        const placeholder = card.querySelector('[data-upload-placeholder]');
        const name = card.querySelector('[data-upload-name]');

        if (preview) {
            preview.removeAttribute('src');
            preview.classList.remove('is-visible');
        }
        if (placeholder) placeholder.classList.remove('d-none');
        if (name) name.textContent = 'Chọn ảnh';
        card.classList.remove('has-file', 'has-error');
    }

    function validateUpload(input, showPreview = true) {
        const file = input.files && input.files.length > 0 ? input.files[0] : null;
        const card = input.closest('[data-upload-card]');

        if (!file) {
            resetUploadCard(input);
            return setClientError(input, 'Vui lòng chọn ảnh giấy tờ.');
        }

        const extension = file.name.includes('.')
            ? file.name.split('.').pop().toLowerCase()
            : '';

        let error = '';
        if (file.size > maxDocumentImageBytes) {
            error = 'Ảnh không được vượt quá 5 MB.';
        } else if (!allowedImageExtensions.has(extension) || !allowedImageTypes.has(file.type)) {
            error = 'Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.';
        }

        if (error) {
            input.value = '';
            resetUploadCard(input);
            card?.classList.add('has-error');
            setClientError(input, error);
            return false;
        }

        setClientError(input, '');
        card?.classList.remove('has-error');

        if (!showPreview || !card) return true;

        const preview = card.querySelector('[data-upload-preview]');
        const placeholder = card.querySelector('[data-upload-placeholder]');
        const name = card.querySelector('[data-upload-name]');
        const objectUrl = URL.createObjectURL(file);

        if (preview) {
            preview.src = objectUrl;
            preview.classList.add('is-visible');
        }
        if (placeholder) placeholder.classList.add('d-none');
        if (name) name.textContent = file.name;
        card.classList.add('has-file');
        return true;
    }

    function normalizeDateInput(input) {
        const digits = input.value.replace(/\D/g, '').slice(0, 8);
        const parts = [];
        if (digits.length > 0) parts.push(digits.slice(0, 2));
        if (digits.length > 2) parts.push(digits.slice(2, 4));
        if (digits.length > 4) parts.push(digits.slice(4, 8));
        input.value = parts.join('/');
    }

    function convertVietnameseDateToDate(value) {
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value.trim());
        if (!match) return null;

        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const date = new Date(year, month - 1, day);

        if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) {
            return null;
        }

        return date;
    }

    function toIsoDate(date) {
        return `${date.getFullYear().toString().padStart(4, '0')}-${(date.getMonth() + 1).toString().padStart(2, '0')}-${date.getDate().toString().padStart(2, '0')}`;
    }

    function parseIsoDate(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})/.exec((value || '').trim());
        if (!match) return null;

        const year = Number(match[1]);
        const month = Number(match[2]);
        const day = Number(match[3]);
        const date = new Date(year, month - 1, day);
        return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
            ? date
            : null;
    }

    function startOfToday() {
        const now = new Date();
        return new Date(now.getFullYear(), now.getMonth(), now.getDate());
    }

    function validateDateInput(input) {
        const targetId = input.dataset.dateTarget;
        const target = targetId ? document.getElementById(targetId) : null;
        const date = convertVietnameseDateToDate(input.value);
        let error = '';

        if (!date) {
            error = input.value.trim()
                ? 'Ngày không hợp lệ. Vui lòng nhập theo định dạng dd/mm/yyyy.'
                : 'Vui lòng nhập ngày.';
        } else if (input.dataset.dateRole === 'dob') {
            const today = startOfToday();
            const adultCutoff = new Date(today.getFullYear() - 18, today.getMonth(), today.getDate());
            if (date > adultCutoff) {
                error = 'Khách thuê xe phải đủ 18 tuổi.';
            }
        } else if (input.dataset.dateRole === 'expiry') {
            const today = startOfToday();
            const documentLabel = input.dataset.documentLabel || 'Giấy tờ';
            if (date < today) {
                error = `${documentLabel} đã hết hạn.`;
            } else {
                const returnDateValue = packageForm?.querySelector('input[name="returnDate"]')?.value;
                const requiredThrough = parseIsoDate(returnDateValue);
                if (requiredThrough && date < requiredThrough) {
                    error = `${documentLabel} phải còn hiệu lực ít nhất đến ngày trả xe.`;
                }
            }
        }

        if (target) target.value = date && !error ? toIsoDate(date) : '';
        return setClientError(input, error);
    }

    function validatePersonName(input) {
        const value = input.value.trim().replace(/\s+/g, ' ');
        let error = '';

        if (!value) {
            error = 'Vui lòng nhập họ và tên trên CCCD.';
        } else if (value.length < 2 || value.length > 100) {
            error = 'Họ và tên phải có từ 2 đến 100 ký tự.';
        } else if (!personNamePattern.test(value)) {
            error = 'Họ và tên chỉ được chứa chữ cái, khoảng trắng, dấu nháy hoặc dấu gạch nối.';
        }

        if (!error && input.value !== value) input.value = value;
        return setClientError(input, error);
    }

    function validateCitizenNumber(input) {
        input.value = input.value.replace(/\D/g, '').slice(0, 12);
        const error = input.value.length === 0
            ? 'Vui lòng nhập số CCCD.'
            : input.value.length !== 12
                ? 'Số CCCD phải gồm đúng 12 chữ số.'
                : '';
        return setClientError(input, error);
    }

    function validateLicenseNumber(input) {
        input.value = input.value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 12);
        const error = input.value.length === 0
            ? 'Vui lòng nhập số GPLX.'
            : input.value.length < 8 || input.value.length > 12
                ? 'Số GPLX phải gồm từ 8 đến 12 ký tự chữ hoặc số.'
                : '';
        return setClientError(input, error);
    }

    function validateAddress(input) {
        const value = input.value.trim().replace(/\s+/g, ' ');
        const error = value.length === 0
            ? 'Vui lòng nhập nơi cư trú trên CCCD.'
            : value.length < 5
                ? 'Nơi cư trú phải có ít nhất 5 ký tự.'
                : value.length > 500
                    ? 'Nơi cư trú không được vượt quá 500 ký tự.'
                    : '';
        if (!error && input.value !== value) input.value = value;
        return setClientError(input, error);
    }

    function validateSelect(input) {
        return setClientError(input, input.value ? '' : 'Vui lòng chọn hạng GPLX.');
    }

    function validateConfirmation(input) {
        return setClientError(input, input.checked ? '' : 'Bạn cần xác nhận CCCD và GPLX thuộc cùng một người.');
    }

    function updateSubmitState() {
        if (!packageForm || !packageSubmit) return;

        const requiredControls = Array.from(packageForm.querySelectorAll('[required]'));
        const ready = requiredControls.every(control => control.checkValidity());
        packageSubmit.disabled = !ready;

        if (submitHint) {
            submitHint.textContent = ready
                ? 'Hồ sơ đã đủ thông tin cơ bản và sẵn sàng gửi.'
                : 'Vui lòng hoàn tất thông tin, chọn đủ 4 ảnh và tích xác nhận.';
            submitHint.classList.toggle('text-success', ready);
            submitHint.classList.toggle('text-muted', !ready);
        }
    }

    document.querySelectorAll('[data-upload-input]').forEach(input => {
        input.addEventListener('change', () => {
            validateUpload(input, true);
            updateSubmitState();
        });
    });

    document.querySelectorAll('[data-vn-date]').forEach(input => {
        input.addEventListener('input', () => {
            normalizeDateInput(input);
            validateDateInput(input);
            updateSubmitState();
        });
        input.addEventListener('blur', () => {
            validateDateInput(input);
            updateSubmitState();
        });
    });

    packageForm?.querySelectorAll('[data-person-name]').forEach(input => {
        input.addEventListener('input', () => {
            validatePersonName(input);
            updateSubmitState();
        });
        input.addEventListener('blur', () => {
            validatePersonName(input);
            updateSubmitState();
        });
    });

    packageForm?.querySelectorAll('[data-citizen-number]').forEach(input => {
        input.addEventListener('input', () => {
            validateCitizenNumber(input);
            updateSubmitState();
        });
        input.addEventListener('blur', () => {
            validateCitizenNumber(input);
            updateSubmitState();
        });
    });

    packageForm?.querySelectorAll('[data-license-number]').forEach(input => {
        input.addEventListener('input', () => {
            validateLicenseNumber(input);
            updateSubmitState();
        });
        input.addEventListener('blur', () => {
            validateLicenseNumber(input);
            updateSubmitState();
        });
    });

    packageForm?.querySelectorAll('[data-address-field]').forEach(input => {
        input.addEventListener('input', () => {
            validateAddress(input);
            updateSubmitState();
        });
        input.addEventListener('blur', () => {
            validateAddress(input);
            updateSubmitState();
        });
    });

    packageForm?.querySelectorAll('[data-license-class]').forEach(input => {
        input.addEventListener('change', () => {
            validateSelect(input);
            updateSubmitState();
        });
    });

    const confirmation = packageForm?.querySelector('[data-confirm-same-person]');
    confirmation?.addEventListener('change', () => {
        validateConfirmation(confirmation);
        updateSubmitState();
    });

    if (packageForm) {
        packageForm.addEventListener('invalid', event => {
            const target = event.target;
            const scrollTarget = target.closest?.('[data-upload-card]') || target;
            setTimeout(() => scrollTarget?.scrollIntoView?.({ behavior: 'smooth', block: 'center' }), 0);
        }, true);

        packageForm.addEventListener('submit', event => {
            const validations = [];

            packageForm.querySelectorAll('[data-person-name]').forEach(input => validations.push([input, validatePersonName(input)]));
            packageForm.querySelectorAll('[data-citizen-number]').forEach(input => validations.push([input, validateCitizenNumber(input)]));
            packageForm.querySelectorAll('[data-license-number]').forEach(input => validations.push([input, validateLicenseNumber(input)]));
            packageForm.querySelectorAll('[data-address-field]').forEach(input => validations.push([input, validateAddress(input)]));
            packageForm.querySelectorAll('[data-license-class]').forEach(input => validations.push([input, validateSelect(input)]));
            packageForm.querySelectorAll('[data-vn-date]').forEach(input => validations.push([input, validateDateInput(input)]));
            packageForm.querySelectorAll('[data-upload-input]').forEach(input => validations.push([input, validateUpload(input, false)]));
            if (confirmation) validations.push([confirmation, validateConfirmation(confirmation)]);

            const firstInvalid = validations.find(([, valid]) => !valid)?.[0] ||
                packageForm.querySelector(':invalid');

            if (firstInvalid) {
                event.preventDefault();
                event.stopImmediatePropagation();
                const scrollTarget = firstInvalid.closest?.('[data-upload-card]') || firstInvalid;
                scrollTarget?.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
                if (firstInvalid.type !== 'file' && typeof firstInvalid.focus === 'function') {
                    firstInvalid.focus({ preventScroll: true });
                }
                updateSubmitState();
            }
        }, true);

        packageForm.querySelectorAll('[data-person-name]').forEach(input => {
            if (input.value.trim()) validatePersonName(input);
        });
        packageForm.querySelectorAll('[data-citizen-number]').forEach(input => {
            if (input.value.trim()) validateCitizenNumber(input);
        });
        packageForm.querySelectorAll('[data-license-number]').forEach(input => {
            if (input.value.trim()) validateLicenseNumber(input);
        });
        packageForm.querySelectorAll('[data-address-field]').forEach(input => {
            if (input.value.trim()) validateAddress(input);
        });
        packageForm.querySelectorAll('[data-license-class]').forEach(input => {
            if (input.value) validateSelect(input);
        });
        packageForm.querySelectorAll('[data-vn-date]').forEach(input => {
            if (input.value.trim()) validateDateInput(input);
        });
        updateSubmitState();
    }

    document.querySelectorAll('[data-scroll-to]').forEach(button => {
        button.addEventListener('click', () => {
            const selector = button.dataset.scrollTo;
            const target = selector ? document.querySelector(selector) : null;
            target?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
    });
})();
