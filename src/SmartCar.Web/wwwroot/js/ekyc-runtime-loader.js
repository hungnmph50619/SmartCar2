(() => {
    const runtimeScripts = [
        '/js/ekyc-tesseract-loader.js',
        '/js/ekyc-guards.js',
        '/js/ekyc-gate-click-compat.js',
        '/js/ekyc-manual-attachments.js',
        // Chất lượng ảnh là cổng đầu tiên: ảnh không đạt thì không chạy classifier/QR/MRZ/OCR.
        '/js/ekyc-image-quality-local.js',
        '/js/ekyc-strict-quality-gate.js',
        // CCCD dùng QR + MRZ sau khi Quality Gate đã pass.
        '/js/ekyc-cccd-qr-mrz.js',
        // Document Gate đứng sau Quality Gate để tránh OCR/classifier ảnh rác.
        '/js/ekyc-document-gate.js',
        '/js/ekyc-template-pipeline-v3.js',
        '/js/ekyc-pipeline-compat.js',
        '/js/ekyc-gplx-manual-bridge.js',
        // GPLX: QR trước, OCR chỉ bổ sung trường còn thiếu.
        '/js/ekyc-gplx-qr-assist.js',
        '/js/ekyc-gplx-local-ocr.js',
        '/js/ekyc-cccd-field-level.js',
        '/js/ekyc-quality-copy.js',
        '/js/ekyc-cccd-qr-ui.js'
    ];

    let runtimePromise = null;
    let runtimeReady = false;
    const replaying = new WeakSet();

    const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));

    const loadScript = src => new Promise((resolve, reject) => {
        const existing = document.querySelector(`script[data-ekyc-runtime-src="${src}"]`);
        if (existing?.dataset.loaded === 'true') {
            resolve();
            return;
        }
        if (existing) {
            existing.addEventListener('load', resolve, { once: true });
            existing.addEventListener('error', reject, { once: true });
            return;
        }

        const script = document.createElement('script');
        script.src = src;
        script.async = false;
        script.dataset.ekycRuntimeSrc = src;
        script.addEventListener('load', () => {
            script.dataset.loaded = 'true';
            resolve();
        }, { once: true });
        script.addEventListener('error', () => reject(new Error(`Không tải được ${src}`)), { once: true });
        document.body.appendChild(script);
    });

    // Hướng dẫn luồng KYC là script nhẹ, tải ngay để người dùng hiểu quy trình trước khi
    // khởi tạo Tesseract/classifier/quality runtime nặng.
    void loadScript('/js/kyc-page-ux.js').catch(error => {
        console.warn('Không tải được hướng dẫn KYC:', error);
    });

    const settleInstallers = async () => {
        await new Promise(resolve => setTimeout(resolve, 0));
        await nextFrame();
    };

    const ensureRuntime = () => {
        if (runtimeReady) return Promise.resolve();
        if (!runtimePromise) {
            runtimePromise = (async () => {
                for (const src of runtimeScripts) {
                    await loadScript(src);
                    // Nhường main thread giữa các module để trình duyệt vẫn có cơ hội vẽ UI.
                    await nextFrame();
                }
                await settleInstallers();
                runtimeReady = true;
                document.documentElement.dataset.ekycRuntimeReady = 'true';
            })().catch(error => {
                runtimePromise = null;
                throw error;
            });
        }
        return runtimePromise;
    };

    const isKycAction = element => element?.closest?.(
        '[data-ekyc-ocr], [data-ekyc-manual], [data-license-ocr], [data-license-manual], ' +
        '[data-ekyc-camera-start], [data-ekyc-record], [data-ekyc-video-upload], [data-ekyc-next-face]'
    );

    const preparingLabel = action => {
        if (action.matches('[data-ekyc-ocr]')) return 'Đang chuẩn bị đọc CCCD...';
        if (action.matches('[data-license-ocr]')) return 'Đang chuẩn bị đọc GPLX...';
        if (action.matches('[data-ekyc-manual], [data-license-manual]')) return 'Đang mở nhập thủ công...';
        return 'Đang chuẩn bị...';
    };

    // Quan trọng: KHÔNG khởi tạo OCR/quality/classifier khi người dùng chỉ chọn ảnh.
    // ekyc.js vẫn xử lý tên file + preview ngay lập tức. Runtime nặng chỉ được tải
    // khi người dùng chủ động bấm đọc/xác minh hoặc chuyển sang luồng cần runtime.
    document.addEventListener('click', event => {
        if (runtimeReady) return;
        const action = isKycAction(event.target);
        if (!action || replaying.has(action)) return;

        event.preventDefault();
        event.stopImmediatePropagation();

        const wasDisabled = action.disabled;
        const oldText = action.textContent;
        action.disabled = true;
        action.textContent = preparingLabel(action);

        // Cho browser render trạng thái "đang chuẩn bị" trước khi nạp các module xử lý ảnh.
        void nextFrame()
            .then(() => ensureRuntime())
            .then(() => {
                action.disabled = wasDisabled;
                action.textContent = oldText;
                replaying.add(action);
                action.click();
                replaying.delete(action);
            })
            .catch(error => {
                action.disabled = wasDisabled;
                action.textContent = oldText;
                console.error('Không thể khởi tạo bộ xác minh giấy tờ:', error);
                const panel = action.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
                const message = panel?.querySelector('[data-ekyc-message], [data-license-message]');
                if (message) {
                    message.className = 'alert alert-danger ekyc-message mb-3';
                    message.textContent = 'Không tải được bộ xác minh giấy tờ. Hãy tải lại trang hoặc dùng nhập thủ công.';
                    message.classList.remove('d-none');
                }
            });
    }, true);

    window.SmartCarEkycRuntime = { ensure: ensureRuntime, get ready() { return runtimeReady; } };
})();
