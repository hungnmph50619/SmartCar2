(() => {
    const panelConfig = panel => {
        const citizen = panel.matches('[data-ekyc-panel="citizen"]');
        return citizen ? {
            frontInput: document.getElementById('citizen-front-file'),
            backInput: document.getElementById('citizen-back-file'),
            frontKind: 'cccdFront',
            backKind: 'cccdBack',
            label: 'CCCD',
            fieldsHost: panel.querySelector('[data-ekyc-form-fields]'),
            backButton: panel.querySelector('[data-ekyc-back="1"]')
        } : {
            frontInput: document.getElementById('license-front-file'),
            backInput: document.getElementById('license-back-file'),
            frontKind: 'licenseFront',
            backKind: 'licenseBack',
            label: 'GPLX',
            fieldsHost: panel.querySelector('[data-license-form-fields]'),
            backButton: panel.querySelector('[data-license-back]')
        };
    };

    const readAsDataUrl = file => new Promise(resolve => {
        if (!file) return resolve('');
        const reader = new FileReader();
        reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : '');
        reader.onerror = () => resolve('');
        reader.readAsDataURL(file);
    });

    const ensureHost = (panel, cfg) => {
        let host = panel.querySelector('[data-attached-document-summary]');
        if (host) return host;
        host = document.createElement('div');
        host.dataset.attachedDocumentSummary = '';
        host.className = 'border rounded-3 p-3 mb-3 bg-light';
        host.innerHTML = `
            <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap mb-2">
                <div>
                    <strong>Ảnh ${cfg.label} đã đính kèm</strong>
                    <div class="small text-muted">Hai ảnh này vẫn được gửi cùng hồ sơ để Quản trị viên đối chiếu.</div>
                </div>
                <span class="badge bg-secondary" data-attachment-badge>Chưa đủ ảnh</span>
            </div>
            <div class="row g-2">
                <div class="col-md-6">
                    <div class="border rounded-3 p-2 bg-white h-100">
                        <div class="small fw-semibold mb-2">Mặt trước</div>
                        <div class="ratio ratio-16x9 bg-light rounded overflow-hidden">
                            <img data-attachment-front alt="Ảnh mặt trước" class="w-100 h-100" style="object-fit:contain;" />
                        </div>
                        <div class="small text-muted mt-2 text-truncate" data-attachment-front-name>Chưa chọn ảnh</div>
                    </div>
                </div>
                <div class="col-md-6">
                    <div class="border rounded-3 p-2 bg-white h-100">
                        <div class="small fw-semibold mb-2">Mặt sau</div>
                        <div class="ratio ratio-16x9 bg-light rounded overflow-hidden">
                            <img data-attachment-back alt="Ảnh mặt sau" class="w-100 h-100" style="object-fit:contain;" />
                        </div>
                        <div class="small text-muted mt-2 text-truncate" data-attachment-back-name>Chưa chọn ảnh</div>
                    </div>
                </div>
            </div>
            <div class="d-flex justify-content-between align-items-center gap-2 flex-wrap mt-2">
                <div class="small" data-attachment-mode-note></div>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-attachment-change>Thay ảnh</button>
            </div>`;
        cfg.fieldsHost?.insertAdjacentElement('beforebegin', host);
        host.querySelector('[data-attachment-change]')?.addEventListener('click', () => cfg.backButton?.click());
        return host;
    };

    const render = async panel => {
        const cfg = panelConfig(panel);
        const host = ensureHost(panel, cfg);
        if (!host) return;
        const front = cfg.frontInput?.files?.[0];
        const back = cfg.backInput?.files?.[0];
        const badge = host.querySelector('[data-attachment-badge]');
        const modeNote = host.querySelector('[data-attachment-mode-note]');
        const manual = panel.dataset.manualMode === 'true' || panel.dataset.gplxManualMode === 'true';

        badge.textContent = front && back ? '✓ Đủ 2 mặt' : 'Chưa đủ ảnh';
        badge.className = `badge ${front && back ? 'bg-success' : 'bg-warning text-dark'}`;
        modeNote.textContent = manual
            ? 'Nhập thủ công chỉ thay cách điền thông tin; ảnh hai mặt vẫn bắt buộc.'
            : 'Bạn có thể đối chiếu lại ảnh trước khi gửi.';
        modeNote.className = `small ${manual ? 'text-primary fw-semibold' : 'text-muted'}`;

        const [frontUrl, backUrl] = await Promise.all([readAsDataUrl(front), readAsDataUrl(back)]);
        const frontImg = host.querySelector('[data-attachment-front]');
        const backImg = host.querySelector('[data-attachment-back]');
        if (frontImg) {
            frontImg.src = frontUrl || '';
            frontImg.classList.toggle('d-none', !frontUrl);
        }
        if (backImg) {
            backImg.src = backUrl || '';
            backImg.classList.toggle('d-none', !backUrl);
        }
        const frontName = host.querySelector('[data-attachment-front-name]');
        const backName = host.querySelector('[data-attachment-back-name]');
        if (frontName) frontName.textContent = front?.name || 'Chưa chọn ảnh';
        if (backName) backName.textContent = back?.name || 'Chưa chọn ảnh';
    };

    const allowManualFallbackWhenUnknown = panel => {
        const cfg = panelConfig(panel);
        const front = cfg.frontInput?.files?.[0];
        const back = cfg.backInput?.files?.[0];
        if (!front || !back) return;

        panel.dataset.manualMode = 'true';
        if (!panel.matches('[data-ekyc-panel="citizen"]')) panel.dataset.gplxManualMode = 'true';
        void render(panel);

        if (panel.dataset.documentGate !== 'fail') return;
        const frontKind = panel.dataset.frontDocumentKind || 'unknown';
        const backKind = panel.dataset.backDocumentKind || 'unknown';
        const explicitWrong = (frontKind !== 'unknown' && frontKind !== '' && frontKind !== cfg.frontKind) ||
            (backKind !== 'unknown' && backKind !== '' && backKind !== cfg.backKind);

        // Không chắc chắn thì được nhập thủ công; đã nhận ra rõ sai loại/sai mặt thì vẫn chặn.
        if (!explicitWrong) panel.dataset.documentGateBypass = 'true';
    };

    const installPanel = panel => {
        const cfg = panelConfig(panel);
        if (!cfg.frontInput || !cfg.backInput || !cfg.fieldsHost) return;
        ensureHost(panel, cfg);
        cfg.frontInput.addEventListener('change', () => void render(panel));
        cfg.backInput.addEventListener('change', () => void render(panel));
        void render(panel);
    };

    // Script này được đặt trước ekyc-document-gate.js để manual fallback có thể bypass
    // trường hợp "không xác định", nhưng không bypass trường hợp sai giấy tờ rõ ràng.
    document.addEventListener('click', event => {
        const manual = event.target.closest('[data-ekyc-manual], [data-license-manual]');
        if (!manual) return;
        const panel = manual.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;
        allowManualFallbackWhenUnknown(panel);
    }, true);

    const install = () => document
        .querySelectorAll('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]')
        .forEach(installPanel);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', install, { once: true });
    } else {
        install();
    }
})();