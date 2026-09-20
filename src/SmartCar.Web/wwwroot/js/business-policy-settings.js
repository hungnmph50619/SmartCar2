(() => {
    const root = document.getElementById('policy-groups');
    if (!root) return;

    const conflict = root.dataset.conflict === 'true';
    const cards = Array.from(root.querySelectorAll('[data-policy-group]'));
    const transientErrors = new WeakMap();
    let submitting = false;

    const inputs = card => Array.from(card.querySelectorAll('[data-policy-input]'));

    const normalizeText = value => (value ?? '').replace(/\r\n/g, '\n');

    const changed = input => input.type === 'number'
        ? input.value.trim() === '' || Number(input.value) !== Number(input.dataset.current)
        : normalizeText(input.value) !== normalizeText(input.dataset.current);

    const dirty = card => inputs(card).some(changed);

    const format = (value, unit) => {
        const number = Number(value);
        if (!Number.isFinite(number)) return value;
        const formatted = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 2 }).format(number);
        return unit ? `${formatted} ${unit}` : formatted;
    };

    const errorNodeFor = input => {
        const wrapper = input.closest('.col-md-6, .col-12') ?? input.parentElement;
        let node = Array.from(wrapper.querySelectorAll('.policy-client-error'))
            .find(item => item.dataset.for === input.name);

        if (!node) {
            node = document.createElement('div');
            node.className = 'policy-client-error';
            node.dataset.for = input.name;
            node.setAttribute('role', 'alert');
            node.setAttribute('aria-live', 'polite');
            node.hidden = true;
            wrapper.append(node);
        }

        return node;
    };

    const setFieldError = (input, message) => {
        const node = errorNodeFor(input);
        node.textContent = message ?? '';
        node.hidden = !message;
        input.classList.toggle('is-invalid', Boolean(message));

        if (message) {
            input.setAttribute('aria-invalid', 'true');
        } else {
            input.removeAttribute('aria-invalid');
        }
    };

    const numberError = input => {
        const transient = transientErrors.get(input);
        if (transient) return transient;

        const raw = input.value.trim();
        const label = input.dataset.label || 'Giá trị';
        const unit = input.dataset.unit || '';

        if (raw === '') return `Vui lòng nhập ${label.toLowerCase()}.`;

        const normalized = raw.replace(',', '.');
        if (/[eE]/.test(normalized)) {
            return 'Vui lòng nhập số thông thường, không dùng ký hiệu e.';
        }

        const value = Number(normalized);
        if (!Number.isFinite(value)) return 'Giá trị không hợp lệ.';

        if (value < 0) return 'Không được nhập số âm.';

        const min = input.min === '' ? null : Number(input.min);
        const max = input.max === '' ? null : Number(input.max);

        if (min !== null && Number.isFinite(min) && value < min) {
            return min === 0
                ? 'Không được nhập số âm.'
                : `Giá trị tối thiểu là ${format(min, unit)}.`;
        }

        if (max !== null && Number.isFinite(max) && value > max) {
            return `Giá trị tối đa là ${format(max, unit)}.`;
        }

        const step = input.step === '' || input.step === 'any' ? null : Number(input.step);
        if (step === 1 && !Number.isInteger(value)) {
            return 'Chỉ được nhập số nguyên.';
        }

        if (step !== null && Number.isFinite(step) && step > 0 && step < 1) {
            const decimalPart = normalized.split('.')[1] ?? '';
            const allowedDecimals = Math.max(0, (String(step).split('.')[1] ?? '').length);
            if (decimalPart.length > allowedDecimals) {
                return `Chỉ được nhập tối đa ${allowedDecimals} chữ số thập phân.`;
            }
        }

        return null;
    };

    const textError = input => {
        const value = input.value ?? '';
        if (input.required && value.trim() === '') {
            return `Vui lòng nhập ${(input.dataset.label || 'nội dung').toLowerCase()}.`;
        }

        if (input.maxLength > 0 && value.length > input.maxLength) {
            return `Nội dung tối đa ${input.maxLength.toLocaleString('vi-VN')} ký tự.`;
        }

        return null;
    };

    const validateCard = card => {
        const fields = inputs(card);
        const errors = new Map();

        fields.forEach(input => {
            const message = input.type === 'number' ? numberError(input) : textError(input);
            if (message) errors.set(input, message);
        });

        if (card.dataset.policyGroup === 'delivery') {
            const included = fields.find(input => input.name === 'IncludedDeliveryDistanceKm');
            const maximum = fields.find(input => input.name === 'MaxDeliveryDistanceKm');

            if (included && maximum && !errors.has(included) && !errors.has(maximum)) {
                const includedValue = Number(included.value);
                const maximumValue = Number(maximum.value);

                if (Number.isFinite(includedValue) &&
                    Number.isFinite(maximumValue) &&
                    includedValue > maximumValue) {
                    errors.set(
                        included,
                        'Quãng đường trong phí cơ bản không được lớn hơn phạm vi giao tối đa.'
                    );
                }
            }
        }

        fields.forEach(input => setFieldError(input, errors.get(input) ?? null));
        return errors.size === 0;
    };

    const renderChanges = (card, changes) => {
        const output = card.querySelector('[data-changes]');
        output.replaceChildren();
        output.hidden = changes.length === 0;

        if (!changes.length) return;

        const heading = document.createElement('strong');
        heading.textContent = 'Thay đổi chưa lưu';
        output.append(heading);

        changes.forEach(input => {
            const row = document.createElement('div');
            row.className = 'small mt-1';
            row.textContent = input.type === 'number'
                ? `${input.dataset.label}: ${format(input.dataset.current, input.dataset.unit)} → ${input.value === '' ? 'Chưa nhập' : format(input.value, input.dataset.unit)}`
                : `${input.dataset.label}: đã chỉnh sửa nội dung.`;
            output.append(row);
        });
    };

    const render = card => {
        const changes = inputs(card).filter(changed);
        const valid = validateCard(card);
        renderChanges(card, changes);

        const save = card.querySelector('[data-save]');
        save.disabled = conflict || changes.length === 0 || !valid;
        save.title = !valid
            ? 'Hãy sửa các trường đang báo lỗi trước khi lưu.'
            : changes.length === 0
                ? 'Chưa có thay đổi để lưu.'
                : '';
    };

    const resetValidation = card => {
        inputs(card).forEach(input => {
            transientErrors.delete(input);
            input.classList.remove('is-invalid');
            input.removeAttribute('aria-invalid');
            const node = errorNodeFor(input);
            node.textContent = '';
            node.hidden = true;
        });
    };

    const close = card => {
        inputs(card).forEach(input => {
            input.value = input.dataset.current;
        });
        resetValidation(card);
        card.querySelector('[data-policy-form]').hidden = true;
        card.querySelector('[data-edit]').setAttribute('aria-expanded', 'false');
        render(card);
    };

    const showBlockedNegative = (card, input) => {
        transientErrors.set(input, 'Không được nhập số âm.');
        render(card);
    };

    cards.forEach(card => {
        const form = card.querySelector('[data-policy-form]');
        form.noValidate = true;

        card.querySelector('[data-edit]').addEventListener('click', () => {
            if (conflict || !form.hidden) return;

            const active = cards.find(other =>
                other !== card && !other.querySelector('[data-policy-form]').hidden
            );

            if (active &&
                dirty(active) &&
                !window.confirm('Nhóm đang sửa có thay đổi chưa lưu. Bỏ thay đổi để mở nhóm khác?')) {
                return;
            }

            if (active) close(active);

            form.hidden = false;
            card.querySelector('[data-edit]').setAttribute('aria-expanded', 'true');
            render(card);
            inputs(card)[0]?.focus();
        });

        card.querySelector('[data-cancel]').addEventListener('click', () => {
            if (dirty(card) &&
                !window.confirm('Bỏ các thay đổi chưa lưu của nhóm này?')) {
                return;
            }

            close(card);
            card.querySelector('[data-edit]').focus();
        });

        inputs(card).forEach(input => {
            if (input.type === 'number') {
                input.addEventListener('keydown', event => {
                    if (event.key === '-' || event.key === 'Subtract') {
                        event.preventDefault();
                        showBlockedNegative(card, input);
                    }
                });

                input.addEventListener('beforeinput', event => {
                    if (event.data === '-') {
                        event.preventDefault();
                        showBlockedNegative(card, input);
                    }
                });

                input.addEventListener('paste', event => {
                    const pasted = event.clipboardData?.getData('text')?.trim() ?? '';
                    const normalized = pasted.replace(',', '.');
                    if (normalized.startsWith('-') || Number(normalized) < 0) {
                        event.preventDefault();
                        showBlockedNegative(card, input);
                    }
                });
            }

            input.addEventListener('input', () => {
                transientErrors.delete(input);
                render(card);
            });

            input.addEventListener('blur', () => render(card));
        });

        form.addEventListener('submit', event => {
            const valid = validateCard(card);

            if (conflict || submitting || !dirty(card) || !valid) {
                event.preventDefault();

                if (!valid) {
                    card.querySelector('.is-invalid')?.focus();
                }
                return;
            }

            submitting = true;
            const save = card.querySelector('[data-save]');
            save.disabled = true;
            save.textContent = 'Đang lưu…';
        });

        render(card);
    });

    window.addEventListener('beforeunload', event => {
        if (!submitting && cards.some(dirty)) {
            event.preventDefault();
            event.returnValue = '';
        }
    });

    window.addEventListener('pageshow', () => {
        submitting = false;
        cards.forEach(card => {
            const save = card.querySelector('[data-save]');
            save.textContent = 'Lưu thay đổi';
            render(card);
        });
    });
})();
