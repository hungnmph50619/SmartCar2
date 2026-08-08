(() => {
    const cache = new Map();
    let workerPromise = null;

    const normalize = value => (value || '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/đ/g, 'd')
        .replace(/Đ/g, 'D')
        .toUpperCase()
        .replace(/[’']/g, '')
        .replace(/\s+/g, ' ')
        .trim();

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

    const sourceCanvas = async file => {
        const bitmap = await loadBitmap(file);
        const width = bitmap.width || bitmap.naturalWidth || 0;
        const height = bitmap.height || bitmap.naturalHeight || 0;
        const scale = Math.min(1, 1800 / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        canvas.getContext('2d', { willReadFrequently: true })
            .drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        bitmap.close?.();
        return canvas;
    };

    const rotateCanvas = (source, degrees) => {
        const angle = ((degrees % 360) + 360) % 360;
        if (angle === 0) {
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

    const makeOcrCanvas = source => {
        const maxSide = 1450;
        const scale = Math.min(1, maxSide / Math.max(source.width, source.height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(source.width * scale));
        canvas.height = Math.max(1, Math.round(source.height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, 0, canvas.width, canvas.height);
        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = pixels.data;
        for (let i = 0; i < data.length; i += 4) {
            const gray = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
            const value = Math.max(0, Math.min(255, (gray - 128) * 1.25 + 128));
            data[i] = value;
            data[i + 1] = value;
            data[i + 2] = value;
        }
        ctx.putImageData(pixels, 0, 0);
        return canvas;
    };

    const getWorker = async () => {
        if (!window.Tesseract?.createWorker) {
            throw new Error('Bộ đọc giấy tờ chưa sẵn sàng. Hãy tải lại trang.');
        }
        if (!workerPromise) {
            workerPromise = window.Tesseract.createWorker(['vie', 'eng'], 1)
                .then(async worker => {
                    try {
                        await worker.setParameters({
                            preserve_interword_spaces: '1',
                            tessedit_pageseg_mode: '11'
                        });
                    } catch {
                        // Vẫn dùng được OCR nếu build không hỗ trợ tham số này.
                    }
                    return worker;
                })
                .catch(error => {
                    workerPromise = null;
                    throw error;
                });
        }
        return workerPromise;
    };

    const includesAny = (text, values) => values.some(value => text.includes(value));

    const classificationScores = rawText => {
        const text = normalize(rawText);
        const score = { cccdFront: 0, cccdBack: 0, licenseFront: 0, licenseBack: 0 };
        const add = (target, points, keywords) => {
            if (includesAny(text, keywords)) score[target] += points;
        };

        add('cccdFront', 8, ['CAN CUOC CONG DAN', 'CITIZEN IDENTITY CARD', 'CAN CUOC']);
        add('cccdFront', 4, ['HO VA TEN', 'FULL NAME']);
        add('cccdFront', 3, ['NGAY SINH', 'DATE OF BIRTH']);
        add('cccdFront', 3, ['GIOI TINH', 'SEX']);
        add('cccdFront', 3, ['NOI THUONG TRU', 'NOI CU TRU', 'PLACE OF RESIDENCE']);
        add('cccdFront', 2, ['QUOC TICH', 'NATIONALITY']);
        add('cccdFront', 2, ['CO GIA TRI DEN', 'DATE OF EXPIRY']);

        add('cccdBack', 10, ['IDVNM']);
        add('cccdBack', 7, ['DAC DIEM NHAN DANG', 'PERSONAL IDENTIFICATION']);
        add('cccdBack', 5, ['NGAY THANG NAM', 'DATE MONTH YEAR']);
        add('cccdBack', 4, ['NGON TRO TRAI', 'LEFT INDEX', 'NGON TRO PHAI', 'RIGHT INDEX']);
        add('cccdBack', 3, ['CUC TRUONG CUC CANH SAT', 'ADMINISTRATIVE MANAGEMENT']);

        add('licenseFront', 11, ['GIAY PHEP LAI XE', 'DRIVERS LICENSE', 'DRIVER LICENSE', 'DRIVING LICENSE']);
        add('licenseFront', 4, ['HANG CLASS', 'HANG/CLASS', 'CLASS']);
        add('licenseFront', 3, ['HO VA TEN', 'FULL NAME']);
        add('licenseFront', 2, ['NGAY SINH', 'DATE OF BIRTH']);
        add('licenseFront', 2, ['CO GIA TRI DEN', 'DATE OF EXPIRY']);

        add('licenseBack', 10, ['CAC LOAI XE CO GIOI', 'VEHICLES PERMITTED', 'CATEGORIES OF VEHICLES']);
        add('licenseBack', 6, ['CAC HANG GIAY PHEP', 'HANG GIAY PHEP']);
        add('licenseBack', 4, ['CHU Y', 'NOTES', 'DIEU KIEN HAN CHE']);
        add('licenseBack', 3, ['HANG CLASS', 'CLASS']);

        if (includesAny(text, ['GIAY PHEP LAI XE', 'DRIVER LICENSE', 'DRIVERS LICENSE', 'DRIVING LICENSE'])) {
            score.cccdFront -= 8;
            score.cccdBack -= 8;
        }
        if (includesAny(text, ['CAN CUOC CONG DAN', 'CITIZEN IDENTITY CARD', 'IDVNM'])) {
            score.licenseFront -= 8;
            score.licenseBack -= 8;
        }

        return { score, text };
    };

    const classLabels = {
        cccdFront: 'CCCD mặt trước',
        cccdBack: 'CCCD mặt sau',
        licenseFront: 'GPLX mặt trước',
        licenseBack: 'GPLX mặt sau'
    };

    const bestClass = scores => {
        const ranked = Object.entries(scores).sort((a, b) => b[1] - a[1]);
        const [kind, points] = ranked[0];
        const second = ranked[1]?.[1] ?? -999;
        return {
            kind,
            points,
            margin: points - second,
            confident: points >= 7 && (points - second >= 2 || points >= 12)
        };
    };

    const recognizeOrientation = async (file, expectedKind) => {
        const key = `${fingerprint(file)}|${expectedKind}`;
        if (cache.has(key)) return cache.get(key);

        const source = await sourceCanvas(file);
        const worker = await getWorker();
        const order = [0, 90, 270, 180];
        const attempts = [];

        for (const degrees of order) {
            const rotated = rotateCanvas(source, degrees);
            const ocrCanvas = makeOcrCanvas(rotated);
            const output = await worker.recognize(ocrCanvas);
            const text = output?.data?.text || '';
            const confidence = Number.isFinite(output?.data?.confidence) ? output.data.confidence : 0;
            const classified = classificationScores(text);
            const best = bestClass(classified.score);
            const expectedScore = classified.score[expectedKind] ?? -999;
            const orientationScore = Math.max(best.points, expectedScore) + Math.min(3, confidence / 30);
            attempts.push({ degrees, text, confidence, scores: classified.score, best, orientationScore });

            if (best.confident && best.points >= 12 && degrees === 0) break;
            if (best.confident && best.points >= 16) break;
        }

        attempts.sort((a, b) => b.orientationScore - a.orientationScore);
        const selected = attempts[0];
        const result = {
            ...selected,
            recognizedKind: selected.best.confident ? selected.best.kind : 'unknown',
            recognizedLabel: selected.best.confident ? classLabels[selected.best.kind] : 'Không xác định',
            expectedKind,
            expectedLabel: classLabels[expectedKind],
            sourceCanvas: source
        };
        cache.set(key, result);
        return result;
    };

    const canvasToFile = async (source, degrees, original) => {
        if (!degrees) return original;
        const rotated = rotateCanvas(source, degrees);
        const mime = ['image/jpeg', 'image/png', 'image/webp'].includes(original.type)
            ? original.type
            : 'image/jpeg';
        const blob = await new Promise((resolve, reject) => {
            rotated.toBlob(value => value ? resolve(value) : reject(new Error('Không thể xoay ảnh.')), mime, 0.94);
        });
        const extension = mime === 'image/png' ? '.png' : mime === 'image/webp' ? '.webp' : '.jpg';
        const baseName = original.name.replace(/\.[^.]+$/, '');
        return new File([blob], `${baseName}-smartcar${extension}`, {
            type: mime,
            lastModified: Date.now()
        });
    };

    const replaceInputFile = (input, file) => {
        if (!input || !file || !window.DataTransfer) return false;
        const transfer = new DataTransfer();
        transfer.items.add(file);
        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    };

    const expectedConfig = panel => {
        const citizen = panel.matches('[data-ekyc-panel="citizen"]');
        return citizen ? {
            frontInput: document.getElementById('citizen-front-file'),
            backInput: document.getElementById('citizen-back-file'),
            frontKind: 'cccdFront',
            backKind: 'cccdBack',
            title: 'Kiểm tra đúng CCCD'
        } : {
            frontInput: document.getElementById('license-front-file'),
            backInput: document.getElementById('license-back-file'),
            frontKind: 'licenseFront',
            backKind: 'licenseBack',
            title: 'Kiểm tra đúng GPLX'
        };
    };

    const createHost = panel => {
        let host = panel.querySelector('[data-document-gate-host]');
        if (host) return host;
        const config = expectedConfig(panel);
        const fileState = panel.querySelector(
            panel.matches('[data-ekyc-panel="citizen"]') ? '[data-ekyc-file-state]' : '[data-license-file-state]'
        );
        host = document.createElement('div');
        host.dataset.documentGateHost = '';
        host.className = 'border rounded-3 p-3 mt-3 bg-light';
        host.innerHTML = `
            <div class="d-flex justify-content-between align-items-center gap-2 flex-wrap mb-2">
                <strong>${config.title}</strong>
                <span class="badge bg-secondary" data-document-gate-badge>Chưa kiểm tra</span>
            </div>
            <div class="small text-muted" data-document-gate-body>SmartCar sẽ kiểm tra đúng loại giấy tờ, đúng mặt và tự xoay ảnh nếu cần.</div>`;
        fileState?.insertAdjacentElement('afterend', host);
        return host;
    };

    const renderItem = (label, result, expectedKind) => {
        const correct = result.recognizedKind === expectedKind;
        if (result.recognizedKind === 'unknown') {
            return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white">
                <div class="fw-semibold text-danger">✕ ${label}</div>
                <div class="small mt-1">Không xác định được loại hoặc mặt giấy tờ. Hãy đặt giấy tờ nằm ngang, để đủ 4 góc, chụp gần và giữ camera gần song song với mặt thẻ.</div>
            </div></div>`;
        }
        if (!correct) {
            return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white">
                <div class="fw-semibold text-danger">✕ ${label}</div>
                <div class="small mt-1">Ảnh này được nhận diện là <strong>${result.recognizedLabel}</strong>. Vui lòng chọn đúng <strong>${classLabels[expectedKind]}</strong>.</div>
            </div></div>`;
        }
        const rotated = result.degrees
            ? ` SmartCar đã tự xoay ảnh ${result.degrees}° để đọc đúng chiều.`
            : '';
        return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white">
            <div class="fw-semibold text-success">✓ ${label}</div>
            <div class="small mt-1">Đúng ${result.recognizedLabel}.${rotated}</div>
        </div></div>`;
    };

    const render = (panel, front, back, checking = false) => {
        const host = createHost(panel);
        const badge = host.querySelector('[data-document-gate-badge]');
        const body = host.querySelector('[data-document-gate-body]');
        if (checking) {
            badge.textContent = 'Đang nhận diện';
            badge.className = 'badge bg-secondary';
            body.textContent = 'Đang kiểm tra loại giấy tờ, mặt trước/mặt sau và chiều ảnh...';
            return;
        }
        const config = expectedConfig(panel);
        const passed = front.recognizedKind === config.frontKind && back.recognizedKind === config.backKind;
        badge.textContent = passed ? 'Đúng giấy tờ' : 'Sai giấy tờ';
        badge.className = `badge ${passed ? 'bg-success' : 'bg-danger'}`;
        body.innerHTML = `<div class="row g-2">
            ${renderItem('Mặt trước', front, config.frontKind)}
            ${renderItem('Mặt sau', back, config.backKind)}
        </div>
        <div class="small ${passed ? 'text-success' : 'text-danger'} fw-semibold mt-2">
            ${passed ? '✓ Đúng loại giấy tờ và đúng hai mặt. Tiếp tục kiểm tra chất lượng ảnh.' : '✕ Hãy chọn lại đúng ảnh theo hướng dẫn ở trên.'}
        </div>`;
    };

    const setPanelMessage = (panel, message, type = 'danger') => {
        const selector = panel.matches('[data-ekyc-panel="citizen"]') ? '[data-ekyc-message]' : '[data-license-message]';
        const box = panel.querySelector(selector);
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const classifyPanel = async panel => {
        const config = expectedConfig(panel);
        const frontFile = config.frontInput?.files?.[0];
        const backFile = config.backInput?.files?.[0];
        if (!frontFile || !backFile) return { passed: false, incomplete: true };

        render(panel, null, null, true);
        panel.dataset.documentGate = 'checking';

        const front = await recognizeOrientation(frontFile, config.frontKind);
        const back = await recognizeOrientation(backFile, config.backKind);
        const passed = front.recognizedKind === config.frontKind && back.recognizedKind === config.backKind;

        if (!passed) {
            panel.dataset.documentGate = 'fail';
            render(panel, front, back);
            return { passed: false, front, back };
        }

        let changed = false;
        if (front.degrees) {
            const rotated = await canvasToFile(front.sourceCanvas, front.degrees, frontFile);
            changed = replaceInputFile(config.frontInput, rotated) || changed;
        }
        if (back.degrees) {
            const rotated = await canvasToFile(back.sourceCanvas, back.degrees, backFile);
            changed = replaceInputFile(config.backInput, rotated) || changed;
        }

        panel.dataset.documentGate = 'pass';
        render(panel, front, back);
        return { passed: true, front, back, changed };
    };

    const resetPanel = panel => {
        panel.dataset.documentGate = 'pending';
        const host = createHost(panel);
        const badge = host.querySelector('[data-document-gate-badge]');
        const body = host.querySelector('[data-document-gate-body]');
        badge.textContent = 'Chưa kiểm tra';
        badge.className = 'badge bg-secondary';
        body.textContent = 'SmartCar sẽ kiểm tra đúng loại giấy tờ, đúng mặt và tự xoay ảnh nếu cần.';
    };

    const installPanel = panel => {
        if (!panel || panel.dataset.documentGateInstalled === 'true') return;
        panel.dataset.documentGateInstalled = 'true';
        const config = expectedConfig(panel);
        createHost(panel);
        config.frontInput?.addEventListener('change', () => resetPanel(panel));
        config.backInput?.addEventListener('change', () => resetPanel(panel));
    };

    document.addEventListener('click', async event => {
        const button = event.target.closest('[data-ekyc-ocr], [data-license-ocr], [data-ekyc-manual], [data-license-manual]');
        if (!button) return;
        const panel = button.closest('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!panel) return;

        if (panel.dataset.documentGateBypass === 'true') {
            panel.dataset.documentGateBypass = 'false';
            return;
        }

        const config = expectedConfig(panel);
        if (!config.frontInput?.files?.[0] || !config.backInput?.files?.[0]) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        button.disabled = true;

        try {
            const result = await classifyPanel(panel);
            if (!result.passed) {
                setPanelMessage(panel, 'Giấy tờ chưa đúng. Hãy kiểm tra loại giấy tờ và mặt trước/mặt sau ở mục “Kiểm tra đúng giấy tờ”.');
                return;
            }
            panel.dataset.documentGateBypass = 'true';
            button.disabled = false;
            button.click();
        } catch (error) {
            panel.dataset.documentGate = 'fail';
            setPanelMessage(panel, error?.message || 'Không thể nhận diện giấy tờ. Hãy chọn ảnh rõ hơn và thử lại.');
            button.disabled = false;
        }
    }, true);

    const install = () => {
        document.querySelectorAll('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]')
            .forEach(installPanel);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(install, 0), { once: true });
    } else {
        setTimeout(install, 0);
    }
})();