(() => {
    const ensureLegacyDocumentGateNodes = panel => {
        const host = panel?.querySelector?.('[data-document-gate-host]');
        if (!host) return;

        let badge = host.querySelector('[data-document-gate-badge]');
        if (!badge) {
            badge = host.querySelector('.badge');
            badge?.setAttribute('data-document-gate-badge', '');
        }

        let body = host.querySelector('[data-document-gate-body]');
        if (!body) {
            body = host.querySelector('[data-pipeline-document-body]');
            if (body) {
                body.setAttribute('data-document-gate-body', '');
            } else {
                body = document.createElement('div');
                body.className = 'small text-muted';
                body.setAttribute('data-document-gate-body', '');
                body.textContent = 'SmartCar sẽ kiểm tra đúng loại giấy tờ, đúng mặt và chiều ảnh.';
                host.appendChild(body);
            }
        }
    };

    document.addEventListener('click', event => {
        const button = event.target.closest(
            '[data-ekyc-ocr], [data-license-ocr], [data-ekyc-manual], [data-license-manual]'
        );
        if (!button) return;

        const panel = button.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;

        // PR #30 dùng chung host với Document Gate cũ nhưng thay phần body bằng
        // data-pipeline-document-body. Khôi phục selector legacy trước khi handler cũ chạy,
        // tránh lỗi "Cannot set properties of null (setting 'textContent')".
        ensureLegacyDocumentGateNodes(panel);

        // Nếu pipeline mới đã xác nhận xong cả giấy tờ lẫn chất lượng ảnh thì không cần
        // chạy lại hai gate cũ khi người dùng bấm OCR hoặc chuyển sang xác minh thủ công.
        if (panel.dataset.documentGate === 'pass' && panel.dataset.imageQuality === 'pass') {
            panel.dataset.documentGateBypass = 'true';
            panel.dataset.imageQualityBypass = 'true';
        }
    }, true);
})();
