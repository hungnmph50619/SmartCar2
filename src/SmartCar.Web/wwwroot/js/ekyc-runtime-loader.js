(() => {
    const runtimeScripts = [
        '/js/ekyc-tesseract-loader.js',
        '/js/ekyc-guards.js',
        '/js/ekyc-gate-click-compat.js',
        '/js/ekyc-manual-attachments.js',
        '/js/ekyc-cccd-qr-mrz.js',
        '/js/ekyc-document-gate.js',
        '/js/ekyc-template-pipeline-v3.js',
        '/js/ekyc-pipeline-compat.js',
        '/js/ekyc-image-quality-local.js',
        '/js/ekyc-gplx-manual-bridge.js',
        '/js/ekyc-gplx-local-ocr.js',
        '/js/ekyc-cccd-field-level.js',
        '/js/ekyc-quality-copy.js',
        '/js/ekyc-cccd-qr-ui.js'
    ];

    let runtimePromise = null;
    let runtimeReady = false;
    const replaying = new WeakSet();

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

    const settleInstallers = async () => {
        await new Promise(resolve => setTimeout(resolve, 0));
        await new Promise(resolve => requestAnimationFrame(() => resolve()));
    };

    const ensureRuntime = () => {
        if (runtimeReady) return Promise.resolve();
        if (!runtimePromise) {
            runtimePromise = (async () => {
                for (const src of runtimeScripts) {
                    await loadScript(src);
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

    const isKycFileInput = element => element instanceof HTMLInputElement &&
        ['citizen-front-file', 'citizen-back-file', 'license-front-file', 'license-back-file'].includes(element.id);

    const isKycAction = element => element?.closest?.(
        '[data-ekyc-ocr], [data-ekyc-manual], [data-license-ocr], [data-license-manual], ' +
        '[data-ekyc-camera-start], [data-ekyc-record], [data-ekyc-video-upload], [data-ekyc-next-face]'
    );

    document.addEventListener('change', event => {
        const input = event.target;
        if (!isKycFileInput(input) || runtimeReady || replaying.has(input)) return;

        // Không chặn preview nhẹ của ekyc.js. Runtime nặng tải song song sau khi người dùng thực sự chọn ảnh.
        void ensureRuntime()
            .then(() => {
                if (!input.files?.length) return;
                replaying.add(input);
                input.dispatchEvent(new Event('change', { bubbles: true }));
                replaying.delete(input);
            })
            .catch(error => console.error('Không thể khởi tạo bộ xác minh giấy tờ:', error));
    }, true);

    document.addEventListener('click', event => {
        if (runtimeReady) return;
        const action = isKycAction(event.target);
        if (!action || replaying.has(action)) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        const wasDisabled = action.disabled;
        action.disabled = true;

        void ensureRuntime()
            .then(() => {
                action.disabled = wasDisabled;
                replaying.add(action);
                action.click();
                replaying.delete(action);
            })
            .catch(error => {
                action.disabled = wasDisabled;
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