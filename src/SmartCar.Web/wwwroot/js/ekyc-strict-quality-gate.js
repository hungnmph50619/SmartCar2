(() => {
    const clickReplay = new WeakSet();
    const submitReplay = new WeakSet();
    const cache = new Map();

    const fingerprint = file => file
        ? `${file.name}|${file.size}|${file.lastModified}|${file.type}`
        : '';

    const configFor = panel => panel?.matches('[data-ekyc-panel="citizen"]')
        ? {
            front: document.getElementById('citizen-front-file'),
            back: document.getElementById('citizen-back-file'),
            label: 'CCCD',
            fileState: '[data-ekyc-file-state]',
            message: '[data-ekyc-message]'
        }
        : {
            front: document.getElementById('license-front-file'),
            back: document.getElementById('license-back-file'),
            label: 'GPLX',
            fileState: '[data-license-file-state]',
            message: '[data-license-message]'
        };

    const normalizeFailure = (message, label) => (message || 'Ảnh chưa đạt yêu cầu.')
        .replace(/CCCD/gi, label)
        .replace(/OCR/gi, 'hệ thống đọc giấy tờ');

    const showMessage = (panel, text, type = 'warning') => {
        const config = configFor(panel);
        const box = panel?.querySelector(config.message);
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = text;
        box.classList.remove('d-none');
    };

    const createHost = panel => {
        let host = panel.querySelector('[data-strict-quality-host]');
        if (host) return host;
        const config = configFor(panel);
        host = document.createElement('div');
        host.dataset.strictQualityHost = '';
        host.className = 'border rounded-3 p-3 mt-3 bg-light';
        host.innerHTML = `
            <div class="d-flex justify-content-between align-items-center gap-2 flex-wrap mb-2">
                <strong>Điều kiện ảnh bắt buộc</strong>
                <span class="badge bg-secondary" data-strict-quality-badge>Chưa kiểm tra</span>
            </div>
            <div class="small text-muted" data-strict-quality-body>
                Hai mặt phải rõ nét, đủ vùng giấy tờ, không lóa và giấy tờ không được quá nhỏ trong ảnh.
            </div>`;
        const fileState = panel.querySelector(config.fileState);
        fileState?.insertAdjacentElement('afterend', host);
        return host;
    };

    const render = (panel, result) => {
        const host = createHost(panel);
        const badge = host.querySelector('[data-strict-quality-badge]');
        const body = host.querySelector('[data-strict-quality-body]');
        if (!badge || !body) return;

        if (result.checking) {
            badge.textContent = 'Đang kiểm tra';
            badge.className = 'badge bg-secondary';
            body.className = 'small text-muted';
            body.textContent = 'Đang kiểm tra độ rõ, ánh sáng, kích thước và vùng giấy tờ của cả hai mặt...';
            return;
        }

        if (result.passed) {
            badge.textContent = 'Đạt';
            badge.className = 'badge bg-success';
            body.className = 'small text-success';
            body.textContent = '✓ Cả hai ảnh đạt điều kiện. SmartCar mới tiếp tục nhận diện giấy tờ và đọc QR/MRZ/OCR.';
            return;
        }

        badge.textContent = 'Chụp lại';
        badge.className = 'badge bg-danger';
        body.className = 'small text-danger';
        body.innerHTML = result.errors.map(error => `<div>✕ ${escapeHtml(error)}</div>`).join('');
    };

    const escapeHtml = value => (value || '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');

    const analyze = async (file, side, label) => {
        const key = fingerprint(file);
        if (cache.has(key)) return cache.get(key);
        const vision = window.SmartCarDocumentVision;
        if (!vision?.analyzeImage) {
            const unavailable = {
                passed: false,
                failures: ['Bộ kiểm tra chất lượng ảnh chưa sẵn sàng. Hãy tải lại trang.']
            };
            cache.set(key, unavailable);
            return unavailable;
        }

        let result;
        try {
            result = await vision.analyzeImage(file);
        } catch (error) {
            result = {
                passed: false,
                failures: [error?.message || 'Không thể kiểm tra chất lượng ảnh.']
            };
        }
        const normalized = {
            passed: result?.passed === true,
            failures: (result?.failures || []).map(message => `${side}: ${normalizeFailure(message, label)}`)
        };
        cache.set(key, normalized);
        return normalized;
    };

    const checkPanel = async panel => {
        const config = configFor(panel);
        const front = config.front?.files?.[0];
        const back = config.back?.files?.[0];
        createHost(panel);

        if (!front || !back) {
            const errors = [];
            if (!front) errors.push(`Mặt trước: chưa chọn ảnh ${config.label}.`);
            if (!back) errors.push(`Mặt sau: chưa chọn ảnh ${config.label}.`);
            const result = { passed: false, errors };
            panel.dataset.strictQuality = 'fail';
            panel.dataset.imageQuality = 'fail';
            render(panel, result);
            return result;
        }

        render(panel, { checking: true });
        const [frontResult, backResult] = await Promise.all([
            analyze(front, 'Mặt trước', config.label),
            analyze(back, 'Mặt sau', config.label)
        ]);
        const errors = [...frontResult.failures, ...backResult.failures];
        const passed = frontResult.passed && backResult.passed;
        panel.dataset.strictQuality = passed ? 'pass' : 'fail';
        panel.dataset.imageQuality = passed ? 'pass' : 'fail';
        const result = { passed, errors };
        render(panel, result);
        return result;
    };

    const actionSelector = '[data-ekyc-ocr], [data-ekyc-manual], [data-license-ocr], [data-license-manual]';

    document.addEventListener('click', event => {
        const action = event.target.closest?.(actionSelector);
        if (!action || clickReplay.has(action)) return;
        const panel = action.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;

        event.preventDefault();
        event.stopImmediatePropagation();

        const oldText = action.textContent;
        const oldDisabled = action.disabled;
        action.disabled = true;
        action.textContent = 'Đang kiểm tra ảnh...';

        void checkPanel(panel).then(result => {
            action.disabled = oldDisabled;
            action.textContent = oldText;
            if (!result.passed) {
                showMessage(panel, 'Ảnh chưa đạt yêu cầu nên SmartCar chưa đọc dữ liệu và chưa cho gửi hồ sơ. Hãy chụp lại ảnh được đánh dấu.', 'danger');
                return;
            }

            clickReplay.add(action);
            action.click();
            clickReplay.delete(action);
        }).catch(error => {
            action.disabled = oldDisabled;
            action.textContent = oldText;
            showMessage(panel, error?.message || 'Không thể kiểm tra ảnh. Hãy thử lại.', 'danger');
        });
    }, true);

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || submitReplay.has(form)) return;
        const panel = form.querySelector('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]') ||
            form.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        void checkPanel(panel).then(result => {
            if (!result.passed) {
                showMessage(panel, 'Không thể gửi hồ sơ vì ảnh giấy tờ chưa đạt yêu cầu. Hãy thay ảnh và thử lại.', 'danger');
                panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
                return;
            }
            submitReplay.add(form);
            form.requestSubmit();
            submitReplay.delete(form);
        });
    }, true);

    document.addEventListener('change', event => {
        const input = event.target;
        if (!(input instanceof HTMLInputElement) || input.type !== 'file') return;
        const panel = input.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;
        panel.dataset.strictQuality = 'pending';
        panel.dataset.imageQuality = 'pending';
        const host = panel.querySelector('[data-strict-quality-host]');
        if (host) {
            const badge = host.querySelector('[data-strict-quality-badge]');
            const body = host.querySelector('[data-strict-quality-body]');
            if (badge) {
                badge.textContent = 'Chưa kiểm tra';
                badge.className = 'badge bg-secondary';
            }
            if (body) {
                body.className = 'small text-muted';
                body.textContent = 'Ảnh mới sẽ được kiểm tra trước khi SmartCar đọc dữ liệu hoặc cho gửi hồ sơ.';
            }
        }
    });

    window.SmartCarStrictQualityGate = { checkPanel };
})();
