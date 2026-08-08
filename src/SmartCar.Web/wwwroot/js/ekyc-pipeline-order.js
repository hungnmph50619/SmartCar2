(() => {
    let workerPromise = null;
    const state = new WeakMap();

    const normalize = value => (value || '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/đ/g, 'd')
        .replace(/Đ/g, 'D')
        .toUpperCase()
        .replace(/\s+/g, ' ')
        .trim();

    const labels = {
        cccdFront: 'CCCD mặt trước',
        cccdBack: 'CCCD mặt sau',
        licenseFront: 'GPLX mặt trước',
        licenseBack: 'GPLX mặt sau'
    };

    const configFor = panel => panel.matches('[data-ekyc-panel="citizen"]') ? {
        frontInput: document.getElementById('citizen-front-file'),
        backInput: document.getElementById('citizen-back-file'),
        ocrButton: panel.querySelector('[data-ekyc-ocr]'),
        frontKind: 'cccdFront',
        backKind: 'cccdBack',
        documentTitle: 'Kiểm tra đúng CCCD',
        qualityTitle: 'Kiểm tra ảnh CCCD'
    } : {
        frontInput: document.getElementById('license-front-file'),
        backInput: document.getElementById('license-back-file'),
        ocrButton: panel.querySelector('[data-license-ocr]'),
        frontKind: 'licenseFront',
        backKind: 'licenseBack',
        documentTitle: 'Kiểm tra đúng GPLX',
        qualityTitle: 'Kiểm tra ảnh GPLX'
    };

    const getWorker = async () => {
        if (!window.Tesseract?.createWorker) {
            throw new Error('Bộ đọc giấy tờ chưa sẵn sàng. Hãy tải lại trang.');
        }
        if (!workerPromise) {
            workerPromise = window.Tesseract.createWorker(['vie', 'eng'], 1)
                .then(async worker => {
                    try {
                        await worker.setParameters({ preserve_interword_spaces: '1', tessedit_pageseg_mode: '11' });
                    } catch { }
                    return worker;
                })
                .catch(error => {
                    workerPromise = null;
                    throw error;
                });
        }
        return workerPromise;
    };

    const loadCanvas = async file => {
        const bitmap = window.createImageBitmap
            ? await window.createImageBitmap(file)
            : await new Promise((resolve, reject) => {
                const image = new Image();
                const url = URL.createObjectURL(file);
                image.onload = () => { URL.revokeObjectURL(url); resolve(image); };
                image.onerror = () => { URL.revokeObjectURL(url); reject(new Error('Không đọc được ảnh.')); };
                image.src = url;
            });
        const width = bitmap.width || bitmap.naturalWidth;
        const height = bitmap.height || bitmap.naturalHeight;
        const scale = Math.min(1, 1800 / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        bitmap.close?.();
        return canvas;
    };

    const rotateCanvas = (source, degrees) => {
        const angle = ((degrees % 360) + 360) % 360;
        if (!angle) {
            const copy = document.createElement('canvas');
            copy.width = source.width;
            copy.height = source.height;
            copy.getContext('2d').drawImage(source, 0, 0);
            return copy;
        }
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

    const prepOcr = source => {
        const maxSide = 1450;
        const scale = Math.min(1, maxSide / Math.max(source.width, source.height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(source.width * scale));
        canvas.height = Math.max(1, Math.round(source.height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, 0, canvas.width, canvas.height);
        const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
        for (let i = 0; i < image.data.length; i += 4) {
            const gray = .299 * image.data[i] + .587 * image.data[i + 1] + .114 * image.data[i + 2];
            const value = Math.max(0, Math.min(255, (gray - 128) * 1.2 + 128));
            image.data[i] = value;
            image.data[i + 1] = value;
            image.data[i + 2] = value;
        }
        ctx.putImageData(image, 0, 0);
        return canvas;
    };

    const scoreText = raw => {
        const text = normalize(raw);
        const scores = { cccdFront: 0, cccdBack: 0, licenseFront: 0, licenseBack: 0 };
        const add = (kind, points, words) => { if (words.some(word => text.includes(word))) scores[kind] += points; };

        add('cccdFront', 9, ['CAN CUOC CONG DAN', 'CITIZEN IDENTITY CARD', 'CAN CUOC']);
        add('cccdFront', 4, ['HO VA TEN', 'FULL NAME']);
        add('cccdFront', 3, ['NGAY SINH', 'DATE OF BIRTH']);
        add('cccdFront', 3, ['GIOI TINH', 'SEX']);
        add('cccdFront', 3, ['NOI THUONG TRU', 'NOI CU TRU', 'PLACE OF RESIDENCE']);
        add('cccdFront', 2, ['QUOC TICH', 'NATIONALITY']);

        add('cccdBack', 12, ['IDVNM']);
        add('cccdBack', 7, ['DAC DIEM NHAN DANG', 'PERSONAL IDENTIFICATION']);
        add('cccdBack', 5, ['NGON TRO TRAI', 'LEFT INDEX', 'NGON TRO PHAI', 'RIGHT INDEX']);
        add('cccdBack', 4, ['DATE MONTH YEAR', 'NGAY THANG NAM']);

        add('licenseFront', 12, ['GIAY PHEP LAI XE', 'DRIVERS LICENSE', 'DRIVER LICENSE', 'DRIVING LICENSE']);
        add('licenseFront', 4, ['HANG CLASS', 'HANG/CLASS', 'CLASS']);
        add('licenseFront', 3, ['HO VA TEN', 'FULL NAME']);
        add('licenseFront', 2, ['NGAY SINH', 'DATE OF BIRTH']);

        add('licenseBack', 10, ['CAC LOAI XE CO GIOI', 'VEHICLES PERMITTED', 'CATEGORIES OF VEHICLES']);
        add('licenseBack', 6, ['CAC HANG GIAY PHEP', 'HANG GIAY PHEP']);
        add('licenseBack', 4, ['DIEU KIEN HAN CHE', 'NOTES', 'CHU Y']);

        if (/\bIDVNM[A-Z0-9<]{12,}/.test(text.replace(/\s/g, ''))) scores.cccdBack += 10;
        if (text.includes('GIAY PHEP LAI XE') || text.includes('DRIVER LICENSE')) {
            scores.cccdFront -= 8; scores.cccdBack -= 8;
        }
        if (text.includes('CAN CUOC') || text.includes('IDVNM')) {
            scores.licenseFront -= 8; scores.licenseBack -= 8;
        }

        const ranked = Object.entries(scores).sort((a, b) => b[1] - a[1]);
        const [kind, points] = ranked[0];
        const margin = points - (ranked[1]?.[1] ?? -999);
        return { kind, points, margin, confident: points >= 7 && (margin >= 2 || points >= 12), scores };
    };

    const classify = async (file, expectedKind) => {
        const source = await loadCanvas(file);
        const worker = await getWorker();
        const attempts = [];
        for (const degrees of [0, 90, 270, 180]) {
            const rotated = rotateCanvas(source, degrees);
            const output = await worker.recognize(prepOcr(rotated));
            const text = output?.data?.text || '';
            const confidence = Number(output?.data?.confidence || 0);
            const score = scoreText(text);
            const expectedScore = score.scores[expectedKind] ?? -99;
            attempts.push({ degrees, confidence, score, source, rank: Math.max(score.points, expectedScore) + Math.min(3, confidence / 30) });
            if (score.confident && score.points >= 16) break;
        }
        attempts.sort((a, b) => b.rank - a.rank);
        const best = attempts[0];
        return {
            kind: best.score.confident ? best.score.kind : 'unknown',
            label: best.score.confident ? labels[best.score.kind] : 'Không xác định',
            degrees: best.degrees,
            confidence: best.confidence,
            source: best.source
        };
    };

    const canvasToFile = async (source, degrees, original) => {
        if (!degrees) return original;
        const rotated = rotateCanvas(source, degrees);
        const mime = ['image/jpeg', 'image/png', 'image/webp'].includes(original.type) ? original.type : 'image/jpeg';
        const blob = await new Promise((resolve, reject) => rotated.toBlob(
            value => value ? resolve(value) : reject(new Error('Không thể tự xoay ảnh.')),
            mime,
            .94));
        const extension = mime === 'image/png' ? '.png' : mime === 'image/webp' ? '.webp' : '.jpg';
        return new File([blob], `${original.name.replace(/\.[^.]+$/, '')}-smartcar${extension}`, { type: mime, lastModified: Date.now() });
    };

    const replaceFile = (input, file) => {
        if (!window.DataTransfer || !input || !file) return;
        const transfer = new DataTransfer();
        transfer.items.add(file);
        input.files = transfer.files;
    };

    const ensureHost = (panel, selector, title, attr) => {
        let host = panel.querySelector(selector);
        if (host) return host;
        const fileState = panel.querySelector(panel.matches('[data-ekyc-panel="citizen"]') ? '[data-ekyc-file-state]' : '[data-license-file-state]');
        host = document.createElement('div');
        host.setAttribute(attr, '');
        host.className = 'border rounded-3 p-3 mt-3 bg-light';
        host.innerHTML = `<div class="d-flex justify-content-between align-items-center gap-2 flex-wrap mb-2"><strong>${title}</strong><span class="badge bg-secondary">Chưa kiểm tra</span></div><div class="small text-muted">Chọn đủ hai mặt để SmartCar kiểm tra.</div>`;
        fileState?.insertAdjacentElement('afterend', host);
        return host;
    };

    const renderDocument = (panel, front, back, cfg) => {
        const host = ensureHost(panel, '[data-document-gate-host]', cfg.documentTitle, 'data-document-gate-host');
        const badge = host.querySelector('.badge');
        const ok = front.kind === cfg.frontKind && back.kind === cfg.backKind;
        badge.textContent = ok ? 'Đúng giấy tờ' : 'Sai giấy tờ';
        badge.className = `badge ${ok ? 'bg-success' : 'bg-danger'}`;
        const item = (name, result, expected) => {
            if (result.kind === 'unknown') return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold text-danger">✕ ${name}</div><div class="small mt-1">Không xác định được loại hoặc mặt giấy tờ. Hãy đặt thẻ nằm ngang, đủ 4 góc và chụp gần hơn.</div></div></div>`;
            if (result.kind !== expected) return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold text-danger">✕ ${name}</div><div class="small mt-1">Ảnh này là <strong>${result.label}</strong>. Vui lòng chọn đúng <strong>${labels[expected]}</strong>.</div></div></div>`;
            const rotated = result.degrees ? ` · đã tự xoay ${result.degrees}°` : '';
            return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold text-success">✓ ${name}</div><div class="small mt-1">Đúng ${result.label}${rotated}.</div></div></div>`;
        };
        host.querySelector('div.small')?.remove();
        const bodies = host.querySelectorAll(':scope > div');
        let body = host.querySelector('[data-pipeline-document-body]');
        if (!body) {
            body = document.createElement('div');
            body.dataset.pipelineDocumentBody = '';
            host.appendChild(body);
        }
        body.innerHTML = `<div class="row g-2">${item('Mặt trước', front, cfg.frontKind)}${item('Mặt sau', back, cfg.backKind)}</div><div class="small ${ok ? 'text-success' : 'text-danger'} fw-semibold mt-2">${ok ? '✓ Đúng loại giấy tờ và đúng hai mặt.' : '✕ Hãy chọn lại đúng ảnh theo hướng dẫn.'}</div>`;
        return ok;
    };

    const showDocumentChecking = (panel, cfg) => {
        const host = ensureHost(panel, '[data-document-gate-host]', cfg.documentTitle, 'data-document-gate-host');
        const badge = host.querySelector('.badge');
        badge.textContent = 'Đang nhận diện';
        badge.className = 'badge bg-secondary';
        const existing = host.querySelector('[data-pipeline-document-body]') || host.querySelector('div.small');
        if (existing) existing.textContent = 'Đang kiểm tra loại giấy tờ, mặt trước/mặt sau và chiều ảnh...';
    };

    const showQualityWaiting = (panel, cfg, message = 'SmartCar sẽ kiểm tra độ rõ sau khi xác nhận đúng loại giấy tờ và đúng hai mặt.') => {
        const host = ensureHost(panel, '[data-image-quality-host]', cfg.qualityTitle, 'data-image-quality-host');
        const badge = host.querySelector('.badge');
        badge.textContent = 'Chờ kiểm tra';
        badge.className = 'badge bg-secondary';
        const body = host.querySelector('[data-image-quality-body]') || host.querySelector('div.small');
        if (body) body.textContent = message;
        panel.dataset.imageQuality = 'pending';
    };

    const basicQuality = async file => {
        const canvas = await loadCanvas(file);
        const maxSide = 720;
        const scale = Math.min(1, maxSide / Math.max(canvas.width, canvas.height));
        const sample = document.createElement('canvas');
        sample.width = Math.max(1, Math.round(canvas.width * scale));
        sample.height = Math.max(1, Math.round(canvas.height * scale));
        const ctx = sample.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(canvas, 0, 0, sample.width, sample.height);
        const pixels = ctx.getImageData(0, 0, sample.width, sample.height).data;
        const gray = new Float32Array(sample.width * sample.height);
        let brightness = 0;
        for (let i = 0, p = 0; i < pixels.length; i += 4, p += 1) {
            const lum = .299 * pixels[i] + .587 * pixels[i + 1] + .114 * pixels[i + 2];
            gray[p] = lum;
            brightness += lum;
        }
        brightness /= Math.max(1, gray.length);
        let sum = 0, sq = 0, count = 0;
        for (let y = 1; y < sample.height - 1; y += 2) {
            for (let x = 1; x < sample.width - 1; x += 2) {
                const c = gray[y * sample.width + x];
                const lap = gray[(y - 1) * sample.width + x] + gray[(y + 1) * sample.width + x] + gray[y * sample.width + x - 1] + gray[y * sample.width + x + 1] - 4 * c;
                sum += lap; sq += lap * lap; count += 1;
            }
        }
        const mean = count ? sum / count : 0;
        const sharpness = count ? Math.max(0, sq / count - mean * mean) : 0;
        const shortSide = Math.min(canvas.width, canvas.height);
        const longSide = Math.max(canvas.width, canvas.height);
        const failures = [];
        if (shortSide < 430 || longSide < 760) failures.push('Ảnh quá nhỏ. Hãy chụp gần hơn.');
        if (sharpness < 35) failures.push('Ảnh bị mờ hoặc rung. Hãy giữ máy ổn định và chụp lại.');
        if (brightness < 40) failures.push('Ảnh quá tối. Hãy chụp ở nơi đủ sáng.');
        if (brightness > 225) failures.push('Ảnh quá sáng hoặc bị lóa. Hãy đổi góc chụp.');
        return { passed: failures.length === 0, failures, warnings: failures.length ? [] : ['Nền ảnh phức tạp nên SmartCar đánh giá độ rõ trực tiếp trên toàn ảnh.'] };
    };

    const renderQuality = (panel, cfg, front, back) => {
        const host = ensureHost(panel, '[data-image-quality-host]', cfg.qualityTitle, 'data-image-quality-host');
        const badge = host.querySelector('.badge');
        const passed = front.passed && back.passed;
        badge.textContent = passed ? 'Đạt' : 'Chụp lại';
        badge.className = `badge ${passed ? 'bg-success' : 'bg-danger'}`;
        const detail = (name, result) => `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold ${result.passed ? 'text-success' : 'text-danger'}">${result.passed ? '✓' : '✕'} ${name}</div><div class="small mt-1">${result.failures?.length ? result.failures.join(' ') : result.warnings?.length ? result.warnings.join(' ') : 'Ảnh đạt yêu cầu.'}</div></div></div>`;
        let body = host.querySelector('[data-image-quality-body]');
        if (!body) {
            body = document.createElement('div');
            body.dataset.imageQualityBody = '';
            host.appendChild(body);
        }
        body.innerHTML = `<div class="row g-2">${detail('Mặt trước', front)}${detail('Mặt sau', back)}</div><div class="small ${passed ? 'text-success' : 'text-danger'} fw-semibold mt-2">${passed ? '✓ Ảnh đạt. Bạn có thể tiếp tục.' : '✕ Hãy chụp/chọn lại ảnh theo nguyên nhân ở trên.'}</div>`;
        panel.dataset.imageQuality = passed ? 'pass' : 'fail';
        return passed;
    };

    const qualityFor = async file => {
        const vision = window.SmartCarDocumentVision;
        if (!vision?.analyzeImage) return basicQuality(file);
        const result = await vision.analyzeImage(file);
        const onlyBoundsFailure = !result.passed && result.failures?.length && result.failures.every(message => /không nhận ra đủ vùng giấy tờ/i.test(message));
        return onlyBoundsFailure ? basicQuality(file) : result;
    };

    const runPipeline = async panel => {
        const cfg = configFor(panel);
        const frontFile = cfg.frontInput?.files?.[0];
        const backFile = cfg.backInput?.files?.[0];
        const version = (state.get(panel)?.version || 0) + 1;
        state.set(panel, { version });
        panel.dataset.documentGateBypass = 'false';
        panel.dataset.imageQualityBypass = 'false';
        cfg.ocrButton.disabled = true;

        if (!frontFile || !backFile) {
            panel.dataset.documentGate = 'pending';
            showQualityWaiting(panel, cfg, 'Chọn đủ hai mặt để SmartCar bắt đầu kiểm tra.');
            return;
        }
        if (panel.dataset.duplicateImages === 'true') return;

        showDocumentChecking(panel, cfg);
        showQualityWaiting(panel, cfg);
        panel.dataset.documentGate = 'checking';

        try {
            const [front, back] = await Promise.all([
                classify(frontFile, cfg.frontKind),
                classify(backFile, cfg.backKind)
            ]);
            if (state.get(panel)?.version !== version) return;

            const documentOk = renderDocument(panel, front, back, cfg);
            if (!documentOk) {
                panel.dataset.documentGate = 'fail';
                showQualityWaiting(panel, cfg, 'Chưa kiểm tra chất lượng vì loại giấy tờ hoặc mặt trước/mặt sau chưa đúng.');
                return;
            }

            const normalizedFront = await canvasToFile(front.source, front.degrees, frontFile);
            const normalizedBack = await canvasToFile(back.source, back.degrees, backFile);
            replaceFile(cfg.frontInput, normalizedFront);
            replaceFile(cfg.backInput, normalizedBack);
            panel.dataset.documentGate = 'pass';

            const host = ensureHost(panel, '[data-image-quality-host]', cfg.qualityTitle, 'data-image-quality-host');
            const badge = host.querySelector('.badge');
            badge.textContent = 'Đang kiểm tra';
            badge.className = 'badge bg-secondary';
            const body = host.querySelector('[data-image-quality-body]') || host.querySelector('div.small');
            if (body) body.textContent = 'Đang kiểm tra độ rõ, ánh sáng và kích thước ảnh...';

            const [frontQuality, backQuality] = await Promise.all([
                qualityFor(normalizedFront),
                qualityFor(normalizedBack)
            ]);
            if (state.get(panel)?.version !== version) return;
            const qualityOk = renderQuality(panel, cfg, frontQuality, backQuality);
            cfg.ocrButton.disabled = !qualityOk || panel.dataset.duplicateImages === 'true';
            if (qualityOk) {
                panel.dataset.documentGateBypass = 'true';
                panel.dataset.imageQualityBypass = 'true';
            }
        } catch (error) {
            if (state.get(panel)?.version !== version) return;
            panel.dataset.documentGate = 'fail';
            panel.dataset.imageQuality = 'pending';
            cfg.ocrButton.disabled = true;
            const host = ensureHost(panel, '[data-document-gate-host]', cfg.documentTitle, 'data-document-gate-host');
            const badge = host.querySelector('.badge');
            badge.textContent = 'Không nhận diện được';
            badge.className = 'badge bg-danger';
            let body = host.querySelector('[data-pipeline-document-body]') || host.querySelector('div.small');
            if (body) body.textContent = error?.message || 'Không thể nhận diện giấy tờ. Hãy chọn ảnh rõ hơn.';
            showQualityWaiting(panel, cfg, 'Chưa kiểm tra chất lượng vì SmartCar chưa nhận diện được giấy tờ.');
        }
    };

    const installPanel = panel => {
        if (!panel || panel.dataset.pipelineOrderInstalled === 'true') return;
        panel.dataset.pipelineOrderInstalled = 'true';
        const cfg = configFor(panel);
        if (!cfg.frontInput || !cfg.backInput || !cfg.ocrButton) return;

        const onChange = event => {
            // Các listener đã đăng ký trước (preview, chống ảnh trùng, reset document gate)
            // vẫn chạy; chặn riêng quality gate cũ chạy sau chúng ta.
            event.stopImmediatePropagation();
            void runPipeline(panel);
        };
        cfg.frontInput.addEventListener('change', onChange);
        cfg.backInput.addEventListener('change', onChange);
    };

    const install = () => document
        .querySelectorAll('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]')
        .forEach(installPanel);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(install, 0), { once: true });
    } else {
        setTimeout(install, 0);
    }
})();