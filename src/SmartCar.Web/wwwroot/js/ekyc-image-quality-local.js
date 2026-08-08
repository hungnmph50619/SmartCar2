(() => {
    const thresholds = {
        minCardShortSide: 300,
        minCardLongSide: 500,
        minDocumentAreaRatio: 0.12,
        warnDocumentAreaRatio: 0.22,
        minSharpness: 45,
        warnSharpness: 80,
        minBrightness: 42,
        maxBrightness: 228,
        maxDarkRatio: 0.62,
        maxBrightRatio: 0.40,
        minEdgeDensity: 0.009,
        warnEdgeDensity: 0.016
    };

    const qualityCache = new Map();
    const documentCache = new Map();

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
                reject(new Error('Không đọc được ảnh đã chọn. Hãy chọn lại ảnh khác.'));
            };
            image.src = url;
        });
    };

    const makeCanvas = (bitmap, maxSide = 2600) => {
        const sourceWidth = bitmap.width || bitmap.naturalWidth || 0;
        const sourceHeight = bitmap.height || bitmap.naturalHeight || 0;
        const scale = Math.min(1, maxSide / Math.max(sourceWidth, sourceHeight));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(sourceWidth * scale));
        canvas.height = Math.max(1, Math.round(sourceHeight * scale));
        canvas.getContext('2d', { willReadFrequently: true })
            .drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        return { canvas, sourceWidth, sourceHeight, scale };
    };

    const copyCanvas = source => {
        const canvas = document.createElement('canvas');
        canvas.width = source.width;
        canvas.height = source.height;
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(source, 0, 0);
        return canvas;
    };

    const colorDistance = (r1, g1, b1, r2, g2, b2) =>
        Math.sqrt((r1 - r2) ** 2 + (g1 - g2) ** 2 + (b1 - b2) ** 2);

    const median = values => {
        if (!values.length) return 0;
        const sorted = [...values].sort((a, b) => a - b);
        return sorted[Math.floor(sorted.length / 2)];
    };

    const estimateBackground = (pixels, width, height) => {
        const rs = [];
        const gs = [];
        const bs = [];
        const sample = (x, y) => {
            const i = (y * width + x) * 4;
            rs.push(pixels[i]);
            gs.push(pixels[i + 1]);
            bs.push(pixels[i + 2]);
        };
        const step = Math.max(2, Math.floor(Math.min(width, height) / 80));
        for (let x = 0; x < width; x += step) {
            sample(x, 0);
            sample(x, height - 1);
        }
        for (let y = 0; y < height; y += step) {
            sample(0, y);
            sample(width - 1, y);
        }
        return { r: median(rs), g: median(gs), b: median(bs) };
    };

    const percentile = (values, p) => {
        if (!values.length) return 0;
        const sorted = [...values].sort((a, b) => a - b);
        return sorted[Math.min(sorted.length - 1, Math.floor((sorted.length - 1) * p))];
    };

    const detectDocumentBounds = sourceCanvas => {
        const maxSide = 720;
        const scale = Math.min(1, maxSide / Math.max(sourceCanvas.width, sourceCanvas.height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(sourceCanvas.width * scale));
        canvas.height = Math.max(1, Math.round(sourceCanvas.height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(sourceCanvas, 0, 0, canvas.width, canvas.height);
        const { data } = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const bg = estimateBackground(data, canvas.width, canvas.height);

        const xs = [];
        const ys = [];
        const stride = 2;
        for (let y = 1; y < canvas.height - 1; y += stride) {
            for (let x = 1; x < canvas.width - 1; x += stride) {
                const i = (y * canvas.width + x) * 4;
                const r = data[i];
                const g = data[i + 1];
                const b = data[i + 2];
                const distance = colorDistance(r, g, b, bg.r, bg.g, bg.b);
                const cyanLike = b > r * 0.95 && g > r * 0.95 && (g + b - 2 * r) > 18;
                const lightCard = r > 145 && g > 155 && b > 150 && distance > 28;
                if (distance > 68 || cyanLike || lightCard) {
                    xs.push(x);
                    ys.push(y);
                }
            }
        }

        if (xs.length < (canvas.width * canvas.height) / 180) return null;

        let left = percentile(xs, 0.025);
        let right = percentile(xs, 0.975);
        let top = percentile(ys, 0.025);
        let bottom = percentile(ys, 0.975);
        const detectedWidth = right - left;
        const detectedHeight = bottom - top;
        if (detectedWidth < 80 || detectedHeight < 45) return null;

        const padX = detectedWidth * 0.035;
        const padY = detectedHeight * 0.055;
        left = Math.max(0, left - padX);
        right = Math.min(canvas.width - 1, right + padX);
        top = Math.max(0, top - padY);
        bottom = Math.min(canvas.height - 1, bottom + padY);

        const width = right - left;
        const height = bottom - top;
        const aspect = Math.max(width, height) / Math.max(1, Math.min(width, height));
        if (aspect < 1.20 || aspect > 2.10) return null;

        return {
            x: left / scale,
            y: top / scale,
            width: width / scale,
            height: height / scale,
            areaRatio: (width * height) / (canvas.width * canvas.height)
        };
    };

    const cropDocument = (sourceCanvas, bounds) => {
        const landscape = bounds.width >= bounds.height;
        const outWidth = landscape ? 1400 : 900;
        const outHeight = landscape ? 900 : 1400;
        const canvas = document.createElement('canvas');
        canvas.width = outWidth;
        canvas.height = outHeight;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(
            sourceCanvas,
            Math.max(0, bounds.x),
            Math.max(0, bounds.y),
            Math.min(sourceCanvas.width - bounds.x, bounds.width),
            Math.min(sourceCanvas.height - bounds.y, bounds.height),
            0,
            0,
            outWidth,
            outHeight
        );
        return canvas;
    };

    const extractDocument = async file => {
        const key = fingerprint(file);
        const cached = documentCache.get(key);
        if (cached) return { canvas: copyCanvas(cached.canvas), meta: { ...cached.meta } };

        const bitmap = await loadBitmap(file);
        const { canvas: sourceCanvas, sourceWidth, sourceHeight, scale } = makeCanvas(bitmap);
        bitmap.close?.();

        const aspect = Math.max(sourceCanvas.width, sourceCanvas.height) /
            Math.max(1, Math.min(sourceCanvas.width, sourceCanvas.height));

        if (aspect >= 1.38 && aspect <= 1.82) {
            const meta = {
                fullFrame: true,
                areaRatio: 1,
                sourceWidth,
                sourceHeight,
                cardWidth: sourceWidth,
                cardHeight: sourceHeight,
                perspectiveCorrected: false,
                detector: 'canvas-local'
            };
            documentCache.set(key, { canvas: copyCanvas(sourceCanvas), meta });
            return { canvas: sourceCanvas, meta: { ...meta } };
        }

        const bounds = detectDocumentBounds(sourceCanvas);
        if (!bounds) {
            const error = new Error('Không nhận ra đủ vùng giấy tờ. Hãy đặt CCCD trên nền khác màu, để đủ 4 góc và chụp gần hơn.');
            error.code = 'DOCUMENT_NOT_FOUND';
            throw error;
        }

        const cropped = cropDocument(sourceCanvas, bounds);
        const meta = {
            fullFrame: false,
            areaRatio: bounds.areaRatio,
            sourceWidth,
            sourceHeight,
            cardWidth: bounds.width / Math.max(scale, 0.0001),
            cardHeight: bounds.height / Math.max(scale, 0.0001),
            perspectiveCorrected: false,
            detector: 'canvas-local'
        };
        documentCache.set(key, { canvas: copyCanvas(cropped), meta });
        return { canvas: cropped, meta: { ...meta } };
    };

    const analyzeCanvas = (canvas, meta) => {
        const maxSide = 760;
        const scale = Math.min(1, maxSide / Math.max(canvas.width, canvas.height));
        const width = Math.max(1, Math.round(canvas.width * scale));
        const height = Math.max(1, Math.round(canvas.height * scale));
        const sample = document.createElement('canvas');
        sample.width = width;
        sample.height = height;
        const ctx = sample.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(canvas, 0, 0, width, height);
        const pixels = ctx.getImageData(0, 0, width, height).data;
        const gray = new Float32Array(width * height);

        let brightnessSum = 0;
        let darkCount = 0;
        let brightCount = 0;
        for (let y = 0; y < height; y += 1) {
            for (let x = 0; x < width; x += 1) {
                const i = (y * width + x) * 4;
                const lum = 0.299 * pixels[i] + 0.587 * pixels[i + 1] + 0.114 * pixels[i + 2];
                gray[y * width + x] = lum;
                brightnessSum += lum;
                if (lum < 45) darkCount += 1;
                if (lum > 245) brightCount += 1;
            }
        }

        let lapSum = 0;
        let lapSq = 0;
        let lapCount = 0;
        let edgeCount = 0;
        let edgeTotal = 0;
        for (let y = 1; y < height - 1; y += 2) {
            for (let x = 1; x < width - 1; x += 2) {
                const center = gray[y * width + x];
                const lap = gray[(y - 1) * width + x] + gray[(y + 1) * width + x] +
                    gray[y * width + x - 1] + gray[y * width + x + 1] - 4 * center;
                lapSum += lap;
                lapSq += lap * lap;
                lapCount += 1;
                const gx = gray[y * width + x + 1] - gray[y * width + x - 1];
                const gy = gray[(y + 1) * width + x] - gray[(y - 1) * width + x];
                if (Math.hypot(gx, gy) > 35) edgeCount += 1;
                edgeTotal += 1;
            }
        }

        const brightness = brightnessSum / Math.max(1, width * height);
        const darkRatio = darkCount / Math.max(1, width * height);
        const brightRatio = brightCount / Math.max(1, width * height);
        const lapMean = lapCount ? lapSum / lapCount : 0;
        const sharpness = lapCount ? Math.max(0, lapSq / lapCount - lapMean * lapMean) : 0;
        const edgeDensity = edgeTotal ? edgeCount / edgeTotal : 0;
        const cardShort = Math.min(meta.cardWidth || 0, meta.cardHeight || 0);
        const cardLong = Math.max(meta.cardWidth || 0, meta.cardHeight || 0);

        const failures = [];
        const warnings = [];
        if (cardShort < thresholds.minCardShortSide || cardLong < thresholds.minCardLongSide) {
            failures.push('Giấy tờ ở quá xa nên chữ quá nhỏ. Hãy chụp gần hơn.');
        }
        if (!meta.fullFrame && meta.areaRatio < thresholds.minDocumentAreaRatio) {
            failures.push('Giấy tờ chiếm quá ít khung hình. Hãy đưa CCCD gần camera hơn.');
        } else if (!meta.fullFrame && meta.areaRatio < thresholds.warnDocumentAreaRatio) {
            warnings.push('Giấy tờ hơi nhỏ trong ảnh. Nên chụp gần hơn để OCR chính xác hơn.');
        }
        if (sharpness < thresholds.minSharpness) {
            failures.push('Ảnh bị mờ hoặc rung. Hãy giữ máy ổn định và chụp lại.');
        } else if (sharpness < thresholds.warnSharpness) {
            warnings.push('Ảnh hơi mờ. Nên chụp lại nếu OCR đọc thiếu thông tin.');
        }
        if (brightness < thresholds.minBrightness || darkRatio > thresholds.maxDarkRatio) {
            failures.push('Ảnh quá tối. Hãy chụp ở nơi đủ sáng.');
        }
        if (brightness > thresholds.maxBrightness || brightRatio > thresholds.maxBrightRatio) {
            failures.push('Ảnh bị lóa hoặc cháy sáng. Hãy đổi góc chụp.');
        }
        if (edgeDensity < thresholds.minEdgeDensity) {
            failures.push('Chữ trên giấy tờ chưa đủ rõ để đọc. Hãy chụp gần hơn.');
        } else if (edgeDensity < thresholds.warnEdgeDensity) {
            warnings.push('Chữ hơi nhỏ hoặc chưa rõ. OCR có thể cần bạn kiểm tra lại.');
        }

        return {
            passed: failures.length === 0,
            failures,
            warnings,
            metrics: {
                width: meta.sourceWidth,
                height: meta.sourceHeight,
                cardWidth: Math.round(cardLong),
                cardHeight: Math.round(cardShort),
                areaRatio: meta.areaRatio,
                sharpness,
                brightness,
                darkRatio,
                brightRatio,
                edgeDensity,
                perspectiveCorrected: false,
                detector: 'canvas-local'
            }
        };
    };

    const analyzeImage = async file => {
        const key = fingerprint(file);
        if (qualityCache.has(key)) return qualityCache.get(key);
        try {
            const extracted = await extractDocument(file);
            const result = analyzeCanvas(extracted.canvas, extracted.meta);
            qualityCache.set(key, result);
            return result;
        } catch (error) {
            const result = {
                passed: false,
                failures: [error?.message || 'Không kiểm tra được ảnh. Hãy chọn ảnh khác.'],
                warnings: [],
                metrics: { width: 0, height: 0, cardWidth: 0, cardHeight: 0, areaRatio: 0, sharpness: 0, brightness: 0, edgeDensity: 0 }
            };
            qualityCache.set(key, result);
            return result;
        }
    };

    const prepareForOcr = async file => {
        const extracted = await extractDocument(file);
        return { canvas: copyCanvas(extracted.canvas), meta: { ...extracted.meta } };
    };

    window.SmartCarDocumentVision = {
        analyzeImage,
        extractDocument,
        prepareForOcr,
        thresholds: { ...thresholds },
        mode: 'canvas-local'
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
            <div class="small text-muted" data-image-quality-body>Chọn đủ hai mặt để SmartCar kiểm tra ảnh.</div>`;
        fileState?.insertAdjacentElement('afterend', host);
        return host;
    };

    const renderFace = (label, result) => {
        const tone = result.passed ? 'text-success' : 'text-danger';
        const icon = result.passed ? '✓' : '✕';
        const detail = result.failures.length
            ? result.failures.join(' ')
            : result.warnings.length
                ? result.warnings.join(' ')
                : 'Ảnh đạt yêu cầu.';
        return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white">
            <div class="fw-semibold ${tone}">${icon} ${label}</div>
            <div class="small mt-1">${detail}</div>
        </div></div>`;
    };

    const renderQuality = (host, frontResult, backResult, checking = false) => {
        const badge = host.querySelector('[data-image-quality-badge]');
        const body = host.querySelector('[data-image-quality-body]');
        if (checking) {
            badge.textContent = 'Đang kiểm tra';
            badge.className = 'badge bg-secondary';
            body.textContent = 'Đang kiểm tra độ rõ, ánh sáng và vùng giấy tờ...';
            return;
        }
        const passed = frontResult?.passed && backResult?.passed;
        badge.textContent = passed ? 'Đạt' : 'Chụp lại';
        badge.className = `badge ${passed ? 'bg-success' : 'bg-danger'}`;
        body.innerHTML = `<div class="row g-2">${renderFace('Mặt trước', frontResult)}${renderFace('Mặt sau', backResult)}</div>
            <div class="small ${passed ? 'text-success' : 'text-danger'} fw-semibold mt-2">
                ${passed ? '✓ Ảnh đạt. Bạn có thể tiếp tục.' : '✕ Hãy sửa đúng nguyên nhân ở trên rồi chọn ảnh lại.'}
            </div>`;
    };

    const setMessage = (panel, message, type = 'warning') => {
        const selector = panel.matches('[data-ekyc-panel="citizen"]') ? '[data-ekyc-message]' : '[data-license-message]';
        const box = panel.querySelector(selector);
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const panelConfig = panel => {
        const citizen = panel.matches('[data-ekyc-panel="citizen"]');
        return citizen ? {
            frontInput: document.getElementById('citizen-front-file'),
            backInput: document.getElementById('citizen-back-file'),
            fileState: panel.querySelector('[data-ekyc-file-state]'),
            ocrButton: panel.querySelector('[data-ekyc-ocr]'),
            submitButton: panel.closest('form')?.querySelector('button[type="submit"]'),
            title: 'Kiểm tra ảnh CCCD'
        } : {
            frontInput: document.getElementById('license-front-file'),
            backInput: document.getElementById('license-back-file'),
            fileState: panel.querySelector('[data-license-file-state]'),
            ocrButton: panel.querySelector('[data-license-ocr]'),
            submitButton: panel.closest('form')?.querySelector('button[type="submit"]'),
            title: 'Kiểm tra ảnh GPLX'
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
                host.querySelector('[data-image-quality-badge]').textContent = 'Chưa kiểm tra';
                host.querySelector('[data-image-quality-body]').textContent = 'Chọn đủ hai mặt để SmartCar kiểm tra ảnh.';
            }
            return { passed: false, incomplete: true };
        }

        panel.dataset.imageQuality = 'checking';
        config.ocrButton.disabled = true;
        if (render) renderQuality(host, null, null, true);
        const [frontResult, backResult] = await Promise.all([analyzeImage(front), analyzeImage(back)]);
        const passed = frontResult.passed && backResult.passed;
        panel.dataset.imageQuality = passed ? 'pass' : 'fail';
        if (render) renderQuality(host, frontResult, backResult);
        config.ocrButton.disabled = !passed || panel.dataset.duplicateImages === 'true';
        if (!passed && panel.dataset.manualMode === 'true' && config.submitButton) config.submitButton.disabled = true;
        return { passed, frontResult, backResult };
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
        if (config.frontInput.files?.[0] && config.backInput.files?.[0]) void runPanelCheck(panel);
    };

    const installOcrQualityGate = panel => {
        const state = panel.querySelector('[data-ekyc-ocr-state]');
        const nextButton = panel.querySelector('[data-ekyc-next-face]');
        const documentNumber = panel.querySelector('[name="CitizenIdVerification.DocumentNumber"]');
        if (!state || !nextButton || !documentNumber || state.dataset.ocrQualityObserved === 'true') return;
        state.dataset.ocrQualityObserved = 'true';
        const evaluate = () => {
            const match = state.textContent?.match(/(?:đọc được|đọc hợp lệ)\s+(\d+)\/7/i);
            if (!match) return;
            const fieldCount = Number(match[1]);
            const hasDocumentNumber = /^\d{12}$/.test(documentNumber.value || '');
            const passed = fieldCount >= 4 && hasDocumentNumber;
            panel.dataset.ocrQuality = passed ? 'pass' : 'fail';
            nextButton.disabled = !passed;
            if (!passed) setMessage(panel, 'OCR chưa đọc đủ thông tin. Hãy dùng ảnh rõ hơn, chụp gần và đủ sáng.', 'danger');
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
        button.disabled = true;
        const result = await runPanelCheck(panel);
        if (!result.passed) {
            setMessage(panel, 'Ảnh chưa đạt yêu cầu. Xem nguyên nhân ở mục “Kiểm tra ảnh” và chọn lại ảnh.', 'danger');
            return;
        }
        panel.dataset.imageQualityBypass = 'true';
        button.disabled = false;
        button.click();
    }, true);

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement)) return;
        const panel = form.querySelector('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel || panel.dataset.imageQuality !== 'fail') return;
        event.preventDefault();
        event.stopImmediatePropagation();
        setMessage(panel, 'Chưa thể gửi hồ sơ vì ảnh chưa đạt yêu cầu. Hãy chọn lại ảnh theo nguyên nhân đã hiển thị.', 'danger');
    }, true);

    const install = () => {
        document.querySelectorAll('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]')
            .forEach(panel => {
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
