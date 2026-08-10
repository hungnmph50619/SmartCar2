(() => {
    const scriptUrl = 'https://cdn.jsdelivr.net/npm/tesseract.js@6.0.0/dist/tesseract.min.js';
    let loadPromise = null;

    const stub = {
        createWorker: async (...args) => {
            const tesseract = await ensure();
            return tesseract.createWorker(...args);
        }
    };

    const ensure = () => {
        const current = window.Tesseract;
        if (current && current !== stub && typeof current.createWorker === 'function') {
            return Promise.resolve(current);
        }
        if (loadPromise) return loadPromise;

        loadPromise = new Promise((resolve, reject) => {
            let script = document.querySelector('script[data-smartcar-tesseract]');
            const complete = () => {
                const loaded = window.Tesseract;
                if (loaded && loaded !== stub && typeof loaded.createWorker === 'function') {
                    resolve(loaded);
                    return;
                }
                reject(new Error('Không tải được bộ OCR cục bộ. Hãy kiểm tra Internet rồi thử lại.'));
            };

            if (script) {
                script.addEventListener('load', complete, { once: true });
                script.addEventListener('error', () => reject(new Error('Không tải được bộ OCR cục bộ. Hãy kiểm tra Internet rồi thử lại.')), { once: true });
                return;
            }

            script = document.createElement('script');
            script.src = scriptUrl;
            script.async = true;
            script.dataset.smartcarTesseract = 'true';
            script.addEventListener('load', complete, { once: true });
            script.addEventListener('error', () => reject(new Error('Không tải được bộ OCR cục bộ. Hãy kiểm tra Internet rồi thử lại.')), { once: true });
            document.head.appendChild(script);
        }).catch(error => {
            loadPromise = null;
            if (window.Tesseract === stub) delete window.Tesseract;
            throw error;
        });

        return loadPromise;
    };

    if (!window.Tesseract) window.Tesseract = stub;
    window.SmartCarTesseract = { ensure };
})();
