(() => {
    const thresholds = {
        minShortSide: 400,
        minLongSide: 700,
        minSharpness: 45,
        warnSharpness: 85,
        minBrightness: 45,
        maxBrightness: 225,
        maxDarkRatio: 0.60,
        maxBrightRatio: 0.38,
        glareBrightRatio: 0.25,
        glareBlockRatio: 0.985,
        minEdgeDensity: 0.006,
        warnEdgeDensity: 0.012
    };

    const resultCache = new Map();

    const fingerprint = file => file
        ? `${file.name}|${file.size}|${file.lastModified}|${file.type}`
        : '';

    const loadBitmap = async file => {
        if (window.createImageBitmap) return window.createImageBitmap(file);
        return new Promise((resolve, reject) => {
            const image = new Image();
            const url = URL.createObjectURL(file);
            image.onload = () => {
                URL.revokeObjectURL(url);
                resolve(image);
            };
            image.onerror = () => {
                URL.revokeObjectURL(url);
                reject(new Error('Không đọc được ảnh đã chọn.'));
            };
            image.src = url;
        });
    };

    const analyzeImage = async file => {
        const key = fingerprint(file);
        if (resultCache.has(key)) return resultCache.get(key);

        const bitmap = await loadBitmap(file);
        const originalWidth = bitmap.width || bitmap.naturalWidth || 0;
        const originalHeight = bitmap.height || bitmap.naturalHeight || 0;
        const maxSide = 720;
        const scale = Math.min(1, maxSide / Math.max(originalWidth, originalHeight));
        const width = Math.max(1, Math.round(originalWidth * scale));
        const height = Math.max(1, Math.round(originalHeight * scale));

        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(bitmap, 0, 0, width, height);
        bitmap.close?.();

        const pixels = ctx.getImageData(0, 0, width, height).data;
        const gray = new Float32Array(width * height);
        let brightnessSum = 0;
        let darkCount = 0;
        let brightCount = 0;

        const gridSize = 6;
        const blockBright = new Uint32Array(gridSize * gridSize);
        const blockTotal = new Uint32Array(gridSize * gridSize);

        for (let y = 0; y < height; y += 1) {
            const blockY = Math.min(gridSize - 1, Math.floor((y / height) * gridSize));
            for (let x = 0; x < width; x += 1) {
                const pixelIndex = (y * width + x) * 4;
                const luminance = 0.299 * pixels[pixelIndex] +
                    0.587 * pixels[pixelIndex + 1] +
                    0.114 * pixels[pixelIndex + 2];
                gray[y * width + x] = luminance;
                brightnessSum += luminance;
                if (luminance < 45) darkCount += 1;
                if (luminance > 245) brightCount += 1;

                const blockX = Math.min(gridSize - 1, Math.floor((x / width) * gridSize));
                const blockIndex = blockY * gridSize + blockX;
                blockTotal[blockIndex] += 1;
                if (luminance > 245) blockBright[blockIndex] += 1;
            }
        }

        const totalPixels = Math.max(1, width * height);
        const brightness = brightnessSum / totalPixels;
        const darkRatio = darkCount / totalPixels;
        const brightRatio = brightCount / totalPixels;
        let maxBrightBlockRatio = 0;
        for (let index = 0; index < blockTotal.length; index += 1) {
            if (!blockTotal[index]) continue;
            maxBrightBlockRatio = Math.max(maxBrightBlockRatio, blockBright[index] / blockTotal[index]);
        }

        let laplacianSum = 0;
        let laplacianSquareSum = 0;
        let laplacianCount = 0;
        let edgeCount = 0;
        let edgeTotal = 0;

        for (let y = 1; y < height - 1; y += 2) {
            for (let x = 1; x < width - 1; x += 2) {
                const center = gray[y * width + x];
                const laplacian = gray[(y - 1) * width + x] +
                    gray[(y + 1) * width + x] +
                    gray[y * width + x - 1] +
                    gray[y * width + x + 1] -
                    4 * center;
                laplacianSum += laplacian;
                laplacianSquareSum += laplacian * laplacian;
                laplacianCount += 1;

                const gx = gray[y * width + x + 1] - gray[y * width + x - 1];
                const gy = gray[(y + 1) * width + x] - gray[(y - 1) * width + x];
                const magnitude = Math.sqrt(gx * gx + gy * gy);
                if (magnitude > 35) edgeCount += 1;
                edgeTotal += 1;
            }
        }

        const laplacianMean = laplacianCount ? laplacianSum / laplacianCount : 0;
        const sharpness = laplacianCount
            ? Math.max(0, laplacianSquareSum / laplacianCount - laplacianMean * laplacianMean)
            : 0;
        const edgeDensity = edgeTotal ? edgeCount / edgeTotal : 0;

        const failures = [];
        const warnings = [];
        const shortSide = Math.min(originalWidth, originalHeight);
        const longSide = Math.max(originalWidth, originalHeight);

        if (shortSide < thresholds.minShortSide || longSide < thresholds.minLongSide) {
            failures.push('Độ phân giải quá thấp. Hãy chụp lại gần hơn.');
        }
        if (sharpness < thresholds.minSharpness) {
            failures.push('Ảnh bị mờ hoặc nhòe. Hãy giữ camera ổn định và chụp lại.');
        } else if (sharpness < thresholds.warnSharpness) {
            warnings.push('Ảnh hơi mềm; nên giữ máy chắc hơn để chữ rõ hơn.');
        }
        if (brightness < thresholds.minBrightness || darkRatio > thresholds.maxDarkRatio) {
            failures.push('Ảnh quá tối. Hãy chụp ở nơi đủ sáng.');
        }
        if (brightness > thresholds.maxBrightness || brightRatio > thresholds.maxBrightRatio) {
            failures.push('Ảnh bị cháy sáng. Hãy giảm ánh sáng trực tiếp và chụp lại.');
        }
        if (brightRatio > thresholds.glareBrightRatio && maxBrightBlockRatio > thresholds.glareBlockRatio) {
            failures.push('Ảnh có vùng lóa lớn che thông tin. Hãy đổi góc chụp.');
        }
        if (edgeDensity < thresholds.minEdgeDensity) {
            failures.push('Giấy tờ có quá ít chi tiết trong khung hình. Hãy đưa CCCD/GPLX gần camera hơn.');
        } else if (edgeDensity < thresholds.warnEdgeDensity) {
            warnings.push('Giấy tờ có vẻ hơi nhỏ trong ảnh; nên chụp gần hơn.');
        }

        const result = {
            passed: failures.length === 0,
            failures,
            warnings,
            metrics: {
                width: originalWidth,
                height: originalHeight,
                sharpness,
                brightness,
                darkRatio,
                brightRatio,
                edgeDensity
            }
        };
        resultCache.set(key, result);
        return result;
    };

    const createQualityHost = (panel, fileState, title) => {
        let host = panel.querySelector('[data-image-quality-host]');
        if (host) return host;
        host = document.createElement('div');
        host.dataset.imageQualityHost = '';
        host.className = 'border rounded-3 p-3 mt-3 bg-light';
        host.innerHTML = `
            <div class="d-flex justify-content-between align-items-center gap-2 flex-wrap mb-2">
                <strong>${title}</strong>
                <span class="badge bg-secondary" data-image-quality-badge>Chưa kiểm tra</span>
            </div>
            <div class="small text-muted" data-image-quality-body>Chọn đủ hai mặt giấy tờ để kiểm tra độ nét, ánh sáng và chất lượng trước khi OCR.</div>`;
        fileState?.insertAdjacentElement('afterend', host);
        return host;
    };

    const renderFace = (label, result) => {
        const icon = result.passed ? '✓' : '✕';
        const tone = result.passed ? 'text-success' : 'text-danger';
        const detail = result.failures.length
            ? result.failures.join(' ')
            : result.warnings.length
                ? result.warnings.join(' ')
                : 'Ảnh đạt yêu cầu cơ bản.';
        return `
            <div class="col-md-6">
                <div class="border rounded-3 p-2 h-100 bg-white">
                    <div class="fw-semibold ${tone}">${icon} ${label}</div>
                    <div class="small mt-1">${detail}</div>
                    <div class="small text-muted mt-1">${result.metrics.width}×${result.metrics.height}px · độ nét ${Math.round(result.metrics.sharpness)} · sáng ${Math.round(result.metrics.brightness)}</div>
                </div>
            </div>`;
    };

    const renderQuality = (host, frontResult, backResult, checking = false) => {
        const badge = host.querySelector('[data-image-quality-badge]');
        const body = host.querySelector('[data-image-quality-body]');
        if (checking) {
            badge.textContent = 'Đang kiểm tra';
            badge.className = 'badge bg-secondary';
            body.innerHTML = 'Đang phân tích ảnh ngay trên máy tính, không gửi request ra ngoài...';
            return;
        }

        const passed = frontResult?.passed && backResult?.passed;
        badge.textContent = passed ? 'Ảnh đạt yêu cầu' : 'Cần chụp lại';
        badge.className = `badge ${passed ? 'bg-success' : 'bg-danger'}`;
        body.innerHTML = `
            <div class="row g-2">
                ${renderFace('Mặt trước', frontResult)}
                ${renderFace('Mặt sau', backResult)}
            </div>
            <div class="small ${passed ? 'text-success' : 'text-danger'} fw-semibold mt-2">
                ${passed
                    ? '✓ Chất lượng ảnh đạt. Có thể tiếp tục OCR.'
                    : '✕ Hãy chọn/chụp lại ảnh chưa đạt trước khi tiếp tục.'}
            </div>`;
    };

    const setMessage = (panel, message, type = 'warning') => {
        const selector = panel.matches('[data-ekyc-panel="citizen"]')
            ? '[data-ekyc-message]'
            : '[data-license-message]';
        const box = panel.querySelector(selector);
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const panelConfig = panel => {
        const citizen = panel.matches('[data-ekyc-panel="citizen"]');
        return citizen
            ? {
                frontInput: document.getElementById('citizen-front-file'),
                backInput: document.getElementById('citizen-back-file'),
                fileState: panel.querySelector('[data-ekyc-file-state]'),
                ocrButton: panel.querySelector('[data-ekyc-ocr]'),
                manualButton: panel.querySelector('[data-ekyc-manual]'),
                submitButton: panel.closest('form')?.querySelector('button[type="submit"]'),
                title: 'Kiểm tra chất lượng ảnh CCCD'
            }
            : {
                frontInput: document.getElementById('license-front-file'),
                backInput: document.getElementById('license-back-file'),
                fileState: panel.querySelector('[data-license-file-state]'),
                ocrButton: panel.querySelector('[data-license-ocr]'),
                manualButton: panel.querySelector('[data-license-manual]'),
                submitButton: panel.closest('form')?.querySelector('button[type="submit"]'),
                title: 'Kiểm tra chất lượng ảnh GPLX'
            };
    };

    const runPanelCheck = async (panel, { render = true } = {}) => {
        const config = panelConfig(panel);
        const front = config.frontInput?.files?.[0];
        const back = config.backInput?.files?.[0];
        const host = createQualityHost(panel, config.fileState, config.title);

        if (!front || !back) {
            panel.dataset.imageQuality = 'pending';
            if (render) {
                const badge = host.querySelector('[data-image-quality-badge]');
                const body = host.querySelector('[data-image-quality-body]');
                badge.textContent = 'Chưa kiểm tra';
                badge.className = 'badge bg-secondary';
                body.textContent = 'Chọn đủ hai mặt giấy tờ để kiểm tra chất lượng trước khi OCR.';
            }
            return { passed: false, incomplete: true };
        }

        panel.dataset.imageQuality = 'checking';
        config.ocrButton.disabled = true;
        if (render) renderQuality(host, null, null, true);

        try {
            const [frontResult, backResult] = await Promise.all([
                analyzeImage(front),
                analyzeImage(back)
            ]);
            const passed = frontResult.passed && backResult.passed;
            panel.dataset.imageQuality = passed ? 'pass' : 'fail';
            if (render) renderQuality(host, frontResult, backResult, false);
            config.ocrButton.disabled = !passed || panel.dataset.duplicateImages === 'true';
            if (!passed && panel.dataset.manualMode === 'true' && config.submitButton) {
                config.submitButton.disabled = true;
            }
            return { passed, frontResult, backResult };
        } catch (error) {
            panel.dataset.imageQuality = 'fail';
            config.ocrButton.disabled = true;
            if (render) {
                const badge = host.querySelector('[data-image-quality-badge]');
                const body = host.querySelector('[data-image-quality-body]');
                badge.textContent = 'Không kiểm tra được';
                badge.className = 'badge bg-danger';
                body.textContent = error?.message || 'Không thể phân tích ảnh. Hãy chọn ảnh khác.';
            }
            return { passed: false, error };
        }
    };

    const installPanel = panel => {
        if (!panel || panel.dataset.imageQualityInstalled === 'true') return;
        panel.dataset.imageQualityInstalled = 'true';
        const config = panelConfig(panel);
        if (!config.frontInput || !config.backInput || !config.ocrButton) return;

        createQualityHost(panel, config.fileState, config.title);
        const onChange = () => {
            panel.dataset.imageQuality = 'pending';
            void runPanelCheck(panel);
        };
        config.frontInput.addEventListener('change', onChange);
        config.backInput.addEventListener('change', onChange);
        if (config.frontInput.files?.[0] && config.backInput.files?.[0]) {
            void runPanelCheck(panel);
        }
    };

    const installOcrQualityGate = panel => {
        const state = panel.querySelector('[data-ekyc-ocr-state]');
        const nextButton = panel.querySelector('[data-ekyc-next-face]');
        const documentNumber = panel.querySelector('[name="CitizenIdVerification.DocumentNumber"]');
        if (!state || !nextButton || !documentNumber || state.dataset.ocrQualityObserved === 'true') return;
        state.dataset.ocrQualityObserved = 'true';

        const evaluate = () => {
            const match = state.textContent?.match(/đọc được\s+(\d+)\/7/i);
            if (!match) return;
            const fieldCount = Number(match[1]);
            const hasDocumentNumber = /^\d{12}$/.test(documentNumber.value || '');
            const passed = fieldCount >= 4 && hasDocumentNumber;
            panel.dataset.ocrQuality = passed ? 'pass' : 'fail';
            nextButton.disabled = !passed;
            if (!passed) {
                setMessage(
                    panel,
                    'OCR đọc được quá ít thông tin hoặc chưa đọc được số CCCD 12 chữ số. Hãy quay lại và chọn ảnh rõ hơn thay vì nhập tay toàn bộ.',
                    'danger');
            }
        };

        new MutationObserver(evaluate).observe(state, { childList: true, characterData: true, subtree: true });
    };

    document.addEventListener('click', async event => {
        const button = event.target.closest('[data-ekyc-ocr], [data-license-ocr], [data-ekyc-manual], [data-license-manual]');
        if (!button) return;
        const panel = button.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;

        if (panel.dataset.imageQualityBypass === 'true') {
            panel.dataset.imageQualityBypass = 'false';
            return;
        }

        const config = panelConfig(panel);
        if (!config.frontInput?.files?.[0] || !config.backInput?.files?.[0]) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        const originalDisabled = button.disabled;
        button.disabled = true;

        const result = await runPanelCheck(panel);
        if (!result.passed) {
            setMessage(
                panel,
                'Ảnh giấy tờ chưa đạt yêu cầu chất lượng. Hãy chụp/chọn lại ảnh theo các cảnh báo bên dưới trước khi tiếp tục.',
                'danger');
            return;
        }

        panel.dataset.imageQualityBypass = 'true';
        button.disabled = originalDisabled && button !== config.ocrButton ? true : false;
        if (!button.disabled) button.click();
    }, true);

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement)) return;
        const panel = form.querySelector('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel || panel.dataset.imageQuality !== 'fail') return;
        event.preventDefault();
        event.stopImmediatePropagation();
        setMessage(panel, 'Không thể gửi hồ sơ vì ảnh giấy tờ chưa đạt yêu cầu chất lượng.', 'danger');
    }, true);

    const install = () => {
        document.querySelectorAll('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]').forEach(panel => {
            installPanel(panel);
            if (panel.matches('[data-ekyc-panel="citizen"]')) installOcrQualityGate(panel);
        });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(install, 0), { once: true });
    } else {
        setTimeout(install, 0);
    }
})();
