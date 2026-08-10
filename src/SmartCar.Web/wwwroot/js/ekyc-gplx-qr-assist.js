(() => {
    const panelSelector = '[data-ekyc-panel="license"]';

    const getToken = form => form?.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    const setMessage = (panel, text, type = 'info') => {
        const box = panel.querySelector('[data-license-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = text;
        box.classList.remove('d-none');
    };

    const statusHost = panel => {
        let host = panel.querySelector('[data-license-qr-host]');
        if (host) return host;
        host = document.createElement('div');
        host.dataset.licenseQrHost = '';
        host.className = 'border rounded-3 p-3 mt-3 bg-light';
        host.innerHTML = `
            <div class="d-flex justify-content-between align-items-center gap-2 flex-wrap">
                <strong>Đọc QR GPLX</strong>
                <span class="badge bg-secondary" data-license-qr-badge>Chưa đọc</span>
            </div>
            <div class="small text-muted mt-2" data-license-qr-body>
                SmartCar ưu tiên QR; OCR cục bộ chỉ bổ sung trường QR không cung cấp.
            </div>`;
        const state = panel.querySelector('[data-license-file-state]');
        state?.insertAdjacentElement('afterend', host);
        return host;
    };

    const renderStatus = (panel, mode, text) => {
        const host = statusHost(panel);
        const badge = host.querySelector('[data-license-qr-badge]');
        const body = host.querySelector('[data-license-qr-body]');
        if (!badge || !body) return;
        const success = mode === 'success';
        const warning = mode === 'warning';
        badge.className = `badge ${success ? 'bg-success' : warning ? 'bg-warning text-dark' : 'bg-secondary'}`;
        badge.textContent = success ? 'Đã đọc QR' : warning ? 'OCR bổ sung' : 'Đang đọc';
        body.className = `small mt-2 ${success ? 'text-success' : warning ? 'text-warning' : 'text-muted'}`;
        body.textContent = text;
    };

    const setInput = (selector, value) => {
        if (!value) return;
        const input = document.querySelector(selector);
        if (!input) return;
        input.value = value;
        input.dataset.qrValue = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const setDate = (displaySelector, hiddenSelector, value) => {
        if (!value) return;
        const display = document.querySelector(displaySelector);
        const hidden = document.querySelector(hiddenSelector);
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value);
        if (display) {
            display.value = value;
            display.dataset.qrValue = value;
            display.dispatchEvent(new Event('input', { bubbles: true }));
            display.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (hidden && match) {
            hidden.value = `${match[3]}-${match[2]}-${match[1]}`;
            hidden.dataset.qrValue = hidden.value;
            hidden.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };

    const applyQrValues = result => {
        setInput('[name="DrivingLicenseVerification.FullNameOnDocument"]', result.fullName);
        setInput('[name="DrivingLicenseVerification.DocumentNumber"]', result.documentNumber);
        setInput('[name="DrivingLicenseVerification.LicenseClass"]', result.licenseClass);
        setDate('#license-issued-display', '#license-issued-value', result.issuedDate);
        setDate('#license-expiry-display', '#license-expiry-value', result.expiryDate);
    };

    const restoreQrValues = result => {
        // OCR là fallback. Trường đã đọc chắc chắn từ QR không được để OCR ghi đè bằng dữ liệu đoán.
        applyQrValues(result);
    };

    const hasAllRequiredQrFields = result => Boolean(
        result.fullName && result.documentNumber && result.licenseClass && result.issuedDate && result.expiryDate
    );

    const showStepTwo = panel => {
        panel.querySelectorAll('[data-license-step-pane]').forEach(pane => {
            pane.classList.toggle('d-none', Number(pane.dataset.licenseStepPane) !== 2);
        });
        panel.querySelectorAll('[data-license-step-indicator]').forEach(indicator => {
            const step = Number(indicator.dataset.licenseStepIndicator);
            indicator.classList.toggle('is-active', step === 2);
            indicator.classList.toggle('is-done', step < 2);
        });
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    const waitAndRestoreAfterOcr = (button, result) => {
        let sawBusy = false;
        const started = Date.now();
        const timer = window.setInterval(() => {
            if (button.disabled) sawBusy = true;
            if ((sawBusy && !button.disabled) || Date.now() - started > 35000) {
                window.clearInterval(timer);
                restoreQrValues(result);
            }
        }, 180);
    };

    const requestQr = async (panel, front, back) => {
        const form = panel.closest('form');
        const data = new FormData();
        const token = getToken(form);
        if (token) data.append('__RequestVerificationToken', token);
        data.append('frontImage', front);
        data.append('backImage', back);
        const response = await fetch('/Ekyc/PreviewLicenseQr', {
            method: 'POST',
            credentials: 'same-origin',
            body: data
        });
        let payload = null;
        try { payload = await response.json(); } catch { payload = null; }
        if (!response.ok) {
            const message = Array.isArray(payload?.errors)
                ? payload.errors.join(' ')
                : payload?.message || 'Không thể kiểm tra QR GPLX.';
            const error = new Error(message);
            error.code = payload?.code || '';
            throw error;
        }
        return payload || {};
    };

    const fallbackToOcr = (panel, button, qrResult, oldText) => {
        renderStatus(
            panel,
            'warning',
            qrResult.qrDecoded
                ? (qrResult.message || 'QR chưa đủ trường. SmartCar đang dùng OCR cục bộ để bổ sung phần còn thiếu.')
                : 'Không đọc được QR GPLX. SmartCar chuyển sang OCR cục bộ để hỗ trợ điền thông tin.'
        );
        panel.dataset.documentGateBypass = 'true';
        panel.dataset.licenseQrBypass = 'true';
        button.disabled = false;
        button.textContent = oldText;
        if (qrResult.qrDecoded) waitAndRestoreAfterOcr(button, qrResult);
        button.click();
    };

    document.addEventListener('click', event => {
        const button = event.target.closest?.('[data-license-ocr]');
        if (!button) return;
        const panel = button.closest(panelSelector);
        if (!panel) return;

        if (panel.dataset.licenseQrBypass === 'true') {
            panel.dataset.licenseQrBypass = 'false';
            return;
        }

        // Document Gate đứng trước script này. Chỉ bắt đầu QR khi loại/mặt giấy tờ đã đúng.
        if (panel.dataset.documentGate !== 'pass' || panel.dataset.strictQuality !== 'pass') {
            return;
        }

        const front = document.getElementById('license-front-file')?.files?.[0];
        const back = document.getElementById('license-back-file')?.files?.[0];
        if (!front || !back) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        const oldText = button.textContent;
        button.disabled = true;
        button.textContent = 'Đang đọc QR GPLX...';
        renderStatus(panel, 'loading', 'Đang tìm QR trên hai mặt GPLX...');

        void requestQr(panel, front, back).then(result => {
            if (result.qrDecoded) {
                applyQrValues(result);
                if (hasAllRequiredQrFields(result)) {
                    button.disabled = false;
                    button.textContent = oldText;
                    renderStatus(panel, 'success', '✓ QR đã cung cấp đủ 5 trường cần thiết. Không cần chạy OCR cục bộ. Hãy kiểm tra lại thông tin trước khi gửi.');
                    const state = panel.querySelector('[data-license-ocr-state]');
                    if (state) state.textContent = '✓ Đã tự điền đầy đủ từ QR GPLX. Hãy kiểm tra lại trước khi gửi.';
                    showStepTwo(panel);
                    return;
                }
            }
            fallbackToOcr(panel, button, result, oldText);
        }).catch(error => {
            button.disabled = false;
            button.textContent = oldText;
            if (error.code === 'IMAGE_QUALITY_FAILED') {
                renderStatus(panel, 'warning', 'Ảnh chưa đạt yêu cầu nên SmartCar không đọc QR/OCR. Hãy chụp lại ảnh trước khi tiếp tục.');
                setMessage(panel, error.message, 'danger');
                return;
            }
            // Lỗi QR không làm mất đường OCR fallback, nhưng không được bypass Quality Gate.
            fallbackToOcr(panel, button, { qrDecoded: false, message: error.message }, oldText);
        });
    }, true);

    const install = () => {
        const panel = document.querySelector(panelSelector);
        if (!panel) return;
        const button = panel.querySelector('[data-license-ocr]');
        if (button && !button.disabled) button.textContent = 'Đọc QR / OCR và tiếp tục';
        statusHost(panel);
    };

    install();
    new MutationObserver(install).observe(document.documentElement, { childList: true, subtree: true });
})();
