(() => {
    let mrzWorkerPromise = null;
    let busy = false;

    const citizenPanel = () => document.querySelector('[data-ekyc-panel="citizen"]');
    const citizenForm = panel => panel?.closest('form') || document.querySelector('form[action*="SubmitCitizenId"]');
    const frontInput = () => document.getElementById('citizen-front-file');
    const backInput = () => document.getElementById('citizen-back-file');

    const installGuards = () => {
        const panel = citizenPanel();
        if (!panel) return false;

        // CCCD không còn đi qua classifier/OCR toàn thẻ cũ. GPLX vẫn dùng pipeline cũ.
        panel.dataset.documentGateInstalled = 'true';
        panel.dataset.templatePipelineInstalled = 'true';
        panel.dataset.cccdQrMrz = 'true';

        const button = panel.querySelector('[data-ekyc-ocr]');
        if (button && !busy) button.textContent = 'Đọc QR + MRZ và tiếp tục';

        const provider = panel.querySelector('[data-ekyc-provider]');
        if (provider) {
            provider.textContent = window.SmartCarKycTest?.active === true ? 'KIỂM THỬ GIẤY TỜ · DEVELOPMENT' : 'QR + MRZ · LOCAL';
            provider.className = window.SmartCarKycTest?.active === true
                ? 'badge bg-warning text-dark'
                : 'badge bg-info text-dark';
        }

        const notice = panel.querySelector('[data-ekyc-demo-notice]');
        if (notice) {
            notice.className = window.SmartCarKycTest?.active === true
                ? 'alert alert-warning py-2 small mb-3'
                : 'alert alert-info py-2 small mb-3';
            notice.innerHTML = window.SmartCarKycTest?.active === true
                ? '<strong>🧪 Development Test Mode:</strong> dữ liệu CCCD/QR/MRZ được mô phỏng để nhóm kiểm thử luồng. Đây không phải xác thực giấy tờ thật.'
                : '<strong>Đọc CCCD trên máy:</strong> thông tin được lấy từ mã QR mặt trước và đối chiếu với dòng MRZ mặt sau. Không dùng OCR toàn bộ CCCD và không đối soát với cơ sở dữ liệu nhà nước.';
        }
        return true;
    };

    const showMessage = (panel, message, type = 'info') => {
        const box = panel.querySelector('[data-ekyc-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const clearMessage = panel => {
        const box = panel.querySelector('[data-ekyc-message]');
        if (!box) return;
        box.textContent = '';
        box.classList.add('d-none');
    };

    const getToken = form => form?.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    const jsonRequest = async (url, options = {}) => {
        const response = await fetch(url, { credentials: 'same-origin', ...options });
        let payload = null;
        try { payload = await response.json(); } catch { payload = null; }
        if (!response.ok) {
            const messages = Array.isArray(payload?.errors)
                ? payload.errors
                : [payload?.message || 'Không thể đọc CCCD.'];
            const error = new Error(messages.join(' '));
            error.code = payload?.code || '';
            throw error;
        }
        return payload;
    };

    const toIso = value => {
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec((value || '').trim());
        return match ? `${match[3]}-${match[2]}-${match[1]}` : '';
    };

    const setValue = (selector, value) => {
        if (!value) return;
        const input = document.querySelector(selector);
        if (!input) return;
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const setDate = (displaySelector, hiddenSelector, value) => {
        if (!value) return;
        const display = document.querySelector(displaySelector);
        const hidden = document.querySelector(hiddenSelector);
        if (display) {
            display.value = value;
            display.dispatchEvent(new Event('input', { bubbles: true }));
            display.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (hidden) {
            hidden.value = toIso(value);
            hidden.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };

    const showStep = (panel, step) => {
        panel.querySelectorAll('[data-ekyc-step-pane]').forEach(pane => {
            pane.classList.toggle('d-none', Number(pane.dataset.ekycStepPane) !== step);
        });
        panel.querySelectorAll('[data-ekyc-step-indicator]').forEach(indicator => {
            const number = Number(indicator.dataset.ekycStepIndicator);
            indicator.classList.toggle('is-active', number === step);
            indicator.classList.toggle('is-done', number < step);
        });
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    const rotateCanvas = (source, degrees) => {
        const angle = ((degrees % 360) + 360) % 360;
        const swap = angle === 90 || angle === 270;
        const canvas = document.createElement('canvas');
        canvas.width = swap ? source.height : source.width;
        canvas.height = swap ? source.width : source.height;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.translate(canvas.width / 2, canvas.height / 2);
        ctx.rotate(angle * Math.PI / 180);
        ctx.drawImage(source, -source.width / 2, -source.height / 2);
        return canvas;
    };

    const cropMrzBand = source => {
        const y = Math.max(0, Math.round(source.height * 0.43));
        const height = Math.max(1, source.height - y);
        const scale = Math.min(3.2, Math.max(1.4, 2300 / Math.max(source.width, height)));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(source.width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, y, source.width, height, 0, 0, canvas.width, canvas.height);

        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = pixels.data;
        let sum = 0;
        for (let i = 0; i < data.length; i += 4) {
            sum += 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
        }
        const mean = sum / Math.max(1, data.length / 4);
        for (let i = 0; i < data.length; i += 4) {
            const gray = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
            const value = gray >= mean * 0.88 ? 255 : 0;
            data[i] = value;
            data[i + 1] = value;
            data[i + 2] = value;
        }
        ctx.putImageData(pixels, 0, 0);
        return canvas;
    };

    const getMrzWorker = async () => {
        if (!window.Tesseract?.createWorker) {
            throw new Error('Bộ đọc MRZ chưa sẵn sàng. Hãy tải lại trang hoặc dùng nhập thủ công.');
        }
        if (!mrzWorkerPromise) {
            mrzWorkerPromise = window.Tesseract.createWorker('eng', 1)
                .then(async worker => {
                    try {
                        await worker.setParameters({
                            tessedit_pageseg_mode: '6',
                            preserve_interword_spaces: '1',
                            tessedit_char_whitelist: 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<'
                        });
                    } catch { /* build Tesseract khác nhau vẫn có thể đọc MRZ */ }
                    return worker;
                })
                .catch(error => {
                    mrzWorkerPromise = null;
                    throw error;
                });
        }
        return mrzWorkerPromise;
    };

    const mrzScore = text => {
        const compact = (text || '').toUpperCase().replace(/\s/g, '');
        let score = 0;
        if (compact.includes('VNM')) score += 6;
        if (compact.includes('IDVNM')) score += 5;
        if ((compact.match(/</g) || []).length >= 5) score += 7;
        if (/[0-9OILZSB]{7}[MF][0-9OILZSB]{7}VNM/.test(compact)) score += 14;
        return score;
    };

    const readMrzText = async file => {
        const vision = window.SmartCarDocumentVision;
        let source = null;
        try {
            source = vision?.prepareForOcr ? (await vision.prepareForOcr(file)).canvas : null;
        } catch {
            source = null;
        }

        if (!source) {
            const bitmap = await createImageBitmap(file);
            source = document.createElement('canvas');
            source.width = bitmap.width;
            source.height = bitmap.height;
            source.getContext('2d', { willReadFrequently: true }).drawImage(bitmap, 0, 0);
            bitmap.close?.();
        }

        const worker = await getMrzWorker();
        let best = { score: -1, text: '' };
        for (const degrees of [0, 90, 270, 180]) {
            const rotated = rotateCanvas(source, degrees);
            if (rotated.width < rotated.height * 1.15) continue;
            const band = cropMrzBand(rotated);
            const output = await worker.recognize(band);
            const text = output?.data?.text || '';
            const score = mrzScore(text);
            if (score > best.score) best = { score, text };
            if (score >= 20) break;
        }
        return best.score >= 10 ? best.text : '';
    };

    const basicQualityFallback = async file => {
        const bitmap = await createImageBitmap(file);
        const maxSide = 760;
        const scale = Math.min(1, maxSide / Math.max(bitmap.width, bitmap.height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(bitmap.width * scale));
        canvas.height = Math.max(1, Math.round(bitmap.height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        bitmap.close?.();
        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
        let brightness = 0;
        let bright = 0;
        for (let i = 0; i < pixels.length; i += 4) {
            const lum = 0.299 * pixels[i] + 0.587 * pixels[i + 1] + 0.114 * pixels[i + 2];
            brightness += lum;
            if (lum > 245) bright += 1;
        }
        brightness /= Math.max(1, pixels.length / 4);
        const brightRatio = bright / Math.max(1, pixels.length / 4);
        const shortSide = Math.min(canvas.width, canvas.height);
        const longSide = Math.max(canvas.width, canvas.height);
        return shortSide >= 300 && longSide >= 500 && brightness >= 38 && brightness <= 232 && brightRatio < 0.42;
    };

    const qualityOk = async file => {
        const vision = window.SmartCarDocumentVision;
        if (!vision?.analyzeImage) return basicQualityFallback(file);
        const result = await vision.analyzeImage(file);
        if (result?.passed) return true;
        const boundsOnly = result?.failures?.length && result.failures.every(message => /không nhận ra đủ vùng giấy tờ/i.test(message));
        return boundsOnly ? basicQualityFallback(file) : false;
    };

    const renderQrMrzStatus = (panel, mode, detail) => {
        let host = panel.querySelector('[data-cccd-qr-mrz-host]');
        if (!host) {
            const fileState = panel.querySelector('[data-ekyc-file-state]');
            host = document.createElement('div');
            host.dataset.cccdQrMrzHost = '';
            host.className = 'border rounded-3 p-3 mt-3 bg-light';
            fileState?.insertAdjacentElement('afterend', host);
        }
        const success = mode === 'success';
        const danger = mode === 'danger';
        const testMode = window.SmartCarKycTest?.active === true;
        host.innerHTML = `<div class="d-flex justify-content-between align-items-center gap-2 flex-wrap">
            <strong>Đọc dữ liệu CCCD</strong>
            <span class="badge ${success ? (testMode ? 'bg-warning text-dark' : 'bg-success') : danger ? 'bg-danger' : 'bg-secondary'}">${success ? (testMode ? 'TEST DATA' : 'QR + MRZ khớp') : danger ? 'Không đọc được' : 'Đang đọc'}</span>
        </div><div class="small mt-2 ${success ? (testMode ? 'text-warning' : 'text-success') : danger ? 'text-danger' : 'text-muted'}">${detail}</div>`;
    };

    const runQrMrz = async (panel, button) => {
        const front = frontInput()?.files?.[0];
        const back = backInput()?.files?.[0];
        const form = citizenForm(panel);
        if (!front || !back || !form) {
            showMessage(panel, 'Vui lòng chọn đủ ảnh CCCD mặt trước và mặt sau.', 'warning');
            return;
        }

        const testMode = window.SmartCarKycTest?.active === true;
        busy = true;
        button.disabled = true;
        const oldText = button.textContent;
        button.textContent = testMode ? 'Đang nạp CCCD mẫu...' : 'Đang đọc QR + MRZ...';
        clearMessage(panel);
        renderQrMrzStatus(
            panel,
            'loading',
            testMode
                ? 'Đang nạp dữ liệu CCCD mẫu Development...'
                : 'Đang giải mã QR mặt trước và đọc riêng vùng MRZ mặt sau...'
        );

        try {
            const [frontQuality, backQuality] = testMode
                ? [true, true]
                : await Promise.all([qualityOk(front), qualityOk(back)]);
            if (!frontQuality || !backQuality) {
                throw new Error('Ảnh chưa đủ rõ để đọc QR/MRZ. Hãy chụp gần hơn, tránh lóa và để trọn 4 góc giấy tờ.');
            }

            const mrzText = testMode ? 'SMARTCAR_TEST_MRZ' : await readMrzText(back);
            if (!mrzText) {
                const error = new Error('Không đọc được MRZ mặt sau. Hãy chụp gần hơn để 3 dòng ký tự ở cuối thẻ rõ nét; nếu vẫn không đọc được, hãy nhập thủ công.');
                error.code = 'MRZ_NOT_READABLE';
                throw error;
            }

            const data = new FormData();
            const token = getToken(form);
            if (token) data.append('__RequestVerificationToken', token);
            data.append('frontImage', front);
            data.append('backImage', back);
            data.append('mrzText', mrzText);

            const result = await jsonRequest('/Ekyc/PreviewCitizenQrMrz', { method: 'POST', body: data });
            const session = panel.querySelector('[data-ekyc-session]');
            if (session) session.value = result.sessionId || '';

            setValue('[name="CitizenIdVerification.FullNameOnDocument"]', result.fullName);
            setValue('[name="CitizenIdVerification.DocumentNumber"]', result.documentNumber);
            setValue('[name="CitizenIdVerification.Gender"]', result.gender);
            setValue('[name="CitizenIdVerification.PermanentAddress"]', result.address);
            setDate('#citizen-birth-display', '#citizen-birth-value', result.dateOfBirth);
            setDate('#citizen-issued-display', '#citizen-issued-value', result.issuedDate);
            setDate('#citizen-expiry-display', '#citizen-expiry-value', result.expiryDate);

            const state = panel.querySelector('[data-ekyc-ocr-state]');
            if (state) {
                state.textContent = result.testMode
                    ? '🧪 Đã nạp dữ liệu CCCD mẫu Development. Hãy tiếp tục để kiểm thử luồng.'
                    : '✓ Đã lấy thông tin từ QR mặt trước và đối chiếu MRZ mặt sau. Hãy kiểm tra lại trước khi tiếp tục.';
                state.className = `small ${result.testMode ? 'text-warning' : 'text-success'} mb-3`;
            }
            renderQrMrzStatus(
                panel,
                'success',
                result.testMode
                    ? '🧪 Dữ liệu CCCD mẫu đã được nạp để kiểm thử luồng Development. Không phải kết quả xác thực giấy tờ thật.'
                    : 'QR mặt trước và MRZ mặt sau có thông tin khớp nhau. SmartCar đã tự điền dữ liệu có cấu trúc; hồ sơ vẫn cần Quản trị viên duyệt.'
            );
            showMessage(panel, result.message || 'Đã đọc QR + MRZ. Vui lòng kiểm tra thông tin.', result.testMode ? 'warning' : 'success');
            showStep(panel, 2);
        } catch (error) {
            const session = panel.querySelector('[data-ekyc-session]');
            if (session) session.value = '';
            renderQrMrzStatus(panel, 'danger', error?.message || 'Không thể đọc QR/MRZ.');
            showMessage(panel, error?.message || 'Không thể đọc QR/MRZ. Hãy nhập thủ công.', 'warning');
        } finally {
            busy = false;
            button.disabled = !(frontInput()?.files?.[0] && backInput()?.files?.[0]);
            button.textContent = oldText || 'Đọc QR + MRZ và tiếp tục';
        }
    };

    // Window capture chạy trước các document-capture handler OCR cũ.
    window.addEventListener('click', event => {
        const automatic = event.target.closest?.('[data-ekyc-ocr]');
        if (automatic) {
            const panel = automatic.closest('[data-ekyc-panel="citizen"]');
            if (!panel) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            if (!busy) void runQrMrz(panel, automatic);
            return;
        }

        const manual = event.target.closest?.('[data-ekyc-manual]');
        if (manual) {
            const panel = manual.closest('[data-ekyc-panel="citizen"]');
            if (!panel) return;
            // Manual không cần chạy classifier OCR giấy tờ cũ; quality gate vẫn được giữ ở Normal Mode.
            panel.dataset.documentGateBypass = 'true';
        }
    }, true);

    const install = () => {
        if (!installGuards()) return;
        const panel = citizenPanel();
        const button = panel?.querySelector('[data-ekyc-ocr]');
        const front = frontInput();
        const back = backInput();
        if (!panel || !button || !front || !back) return;

        const keepButtonUsable = () => {
            if (busy) return;
            button.disabled = !(front.files?.[0] && back.files?.[0]) || panel.dataset.duplicateImages === 'true';
        };
        front.addEventListener('change', () => window.setTimeout(keepButtonUsable, 700));
        back.addEventListener('change', () => window.setTimeout(keepButtonUsable, 700));
        new MutationObserver(keepButtonUsable).observe(button, { attributes: true, attributeFilter: ['disabled'] });
        keepButtonUsable();
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.setTimeout(install, 0), { once: true });
    } else {
        window.setTimeout(install, 0);
    }
})();
