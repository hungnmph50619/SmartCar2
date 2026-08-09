(() => {
    let workerPromise = null;
    const state = new WeakMap();

    const labels = {
        cccdFront: 'CCCD mặt trước',
        cccdBack: 'CCCD mặt sau',
        licenseFront: 'GPLX mặt trước',
        licenseBack: 'GPLX mặt sau'
    };

    const normalize = value => (value || '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/đ/g, 'd')
        .replace(/Đ/g, 'D')
        .toUpperCase()
        .replace(/[\t\r]+/g, ' ')
        .replace(/\s+/g, ' ')
        .trim();

    const compact = value => normalize(value).replace(/[^A-Z0-9<]/g, '');

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
                        await worker.setParameters({
                            preserve_interword_spaces: '1',
                            tessedit_pageseg_mode: '11'
                        });
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

    const copyCanvas = source => {
        const canvas = document.createElement('canvas');
        canvas.width = source.width;
        canvas.height = source.height;
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(source, 0, 0);
        return canvas;
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
        const scale = Math.min(1, 2200 / Math.max(width, height));
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
        if (!angle) return copyCanvas(source);
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

    const cropRelative = (source, x, y, width, height) => {
        const sx = Math.max(0, Math.round(source.width * x));
        const sy = Math.max(0, Math.round(source.height * y));
        const sw = Math.max(1, Math.min(source.width - sx, Math.round(source.width * width)));
        const sh = Math.max(1, Math.min(source.height - sy, Math.round(source.height * height)));
        const canvas = document.createElement('canvas');
        canvas.width = sw;
        canvas.height = sh;
        canvas.getContext('2d', { willReadFrequently: true })
            .drawImage(source, sx, sy, sw, sh, 0, 0, sw, sh);
        return canvas;
    };

    const prepareForOcr = (source, mode = 'contrast', targetLongSide = 1800) => {
        const currentLongSide = Math.max(source.width, source.height);
        const scale = Math.min(3, Math.max(0.55, targetLongSide / Math.max(1, currentLongSide)));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(source.width * scale));
        canvas.height = Math.max(1, Math.round(source.height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, 0, canvas.width, canvas.height);
        const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = image.data;
        let luminance = 0;
        for (let i = 0; i < data.length; i += 4) {
            luminance += .299 * data[i] + .587 * data[i + 1] + .114 * data[i + 2];
        }
        const mean = luminance / Math.max(1, data.length / 4);
        const threshold = Math.max(100, Math.min(210, mean * .95));
        for (let i = 0; i < data.length; i += 4) {
            const gray = .299 * data[i] + .587 * data[i + 1] + .114 * data[i + 2];
            const value = mode === 'binary'
                ? (gray >= threshold ? 255 : 0)
                : Math.max(0, Math.min(255, (gray - 128) * 1.42 + 128));
            data[i] = value;
            data[i + 1] = value;
            data[i + 2] = value;
        }
        ctx.putImageData(image, 0, 0);
        return canvas;
    };

    const getClassifierSources = async file => {
        const original = await loadCanvas(file);
        let documentCanvas = original;
        let cropped = false;
        try {
            const prepared = await window.SmartCarDocumentVision?.prepareForOcr?.(file);
            if (prepared?.canvas?.width > 0 && prepared?.canvas?.height > 0) {
                documentCanvas = copyCanvas(prepared.canvas);
                cropped = true;
            }
        } catch {
            // Nếu detector vùng thẻ không chắc chắn, classifier vẫn thử trên ảnh gốc.
        }
        return { original, documentCanvas, cropped };
    };

    const phrase = (text, ...values) => values.some(value => text.includes(value));
    const regex = (text, pattern) => pattern.test(text);

    const classifyText = raw => {
        const text = normalize(raw);
        const packed = compact(raw);
        const scores = { cccdFront: 0, cccdBack: 0, licenseFront: 0, licenseBack: 0 };
        const evidence = { cccdFront: [], cccdBack: [], licenseFront: [], licenseBack: [] };
        const add = (kind, points, reason) => {
            scores[kind] += points;
            if (reason && !evidence[kind].includes(reason)) evidence[kind].push(reason);
        };

        const chevrons = (packed.match(/</g) || []).length;
        const hasLongMachineLine = /[A-Z0-9<]{24,}/.test(packed);
        const tolerantIdVnm = /(?:IDVNM|I[D0]VNM|1[D0]VNM)/.test(packed);

        // CCCD mặt sau: ưu tiên MRZ và vân tay. Các rule MRZ cố ý chịu lỗi OCR nhẹ.
        if (tolerantIdVnm && hasLongMachineLine) add('cccdBack', 175, 'MRZ IDVNM');
        if (packed.includes('VNM') && chevrons >= 6 && hasLongMachineLine) add('cccdBack', 125, 'dòng MRZ');
        if (chevrons >= 12 && /VNM/.test(packed)) add('cccdBack', 80, 'ký tự MRZ');
        if (phrase(text, 'DAC DIEM NHAN DANG', 'PERSONAL IDENTIFICATION')) add('cccdBack', 70, 'đặc điểm nhận dạng');
        if (phrase(text, 'NGON TRO TRAI', 'LEFT INDEX', 'NGON TRO PHAI', 'RIGHT INDEX')) add('cccdBack', 65, 'vân tay');
        if (phrase(text, 'CUC TRUONG CUC CANH SAT', 'DEPARTMENT FOR ADMINISTRATIVE MANAGEMENT')) add('cccdBack', 30, 'cơ quan cấp');

        // CCCD mặt trước: chỉ dùng tín hiệu đặc trưng cho CCCD.
        if (phrase(text, 'CAN CUOC CONG DAN')) add('cccdFront', 160, 'tiêu đề Căn cước công dân');
        if (phrase(text, 'CITIZEN IDENTITY CARD')) add('cccdFront', 145, 'Citizen Identity Card');
        if (regex(text, /CAN\s*CUOC.{0,12}(CONG\s*DAN)?/)) add('cccdFront', 75, 'tiêu đề căn cước');
        if (phrase(text, 'QUOC TICH', 'NATIONALITY')) add('cccdFront', 30, 'quốc tịch');
        if (phrase(text, 'NOI THUONG TRU', 'PLACE OF RESIDENCE', 'NOI CU TRU')) add('cccdFront', 34, 'nơi cư trú');
        if (phrase(text, 'QUE QUAN', 'PLACE OF ORIGIN')) add('cccdFront', 20, 'quê quán');
        if (/(?:SO|NO)[^0-9]{0,20}\d(?:[\s.-]?\d){11}/.test(text)) add('cccdFront', 40, 'số CCCD 12 chữ số');

        // GPLX mặt trước.
        if (phrase(text, 'GIAY PHEP LAI XE')) add('licenseFront', 175, 'tiêu đề Giấy phép lái xe');
        if (regex(text, /DRIVER.?S?\s+LICENSE/) || phrase(text, 'DRIVING LICENSE')) add('licenseFront', 165, "Driver's License");
        if (phrase(text, 'HANG/CLASS', 'HANG CLASS') || (text.includes('HANG') && text.includes('CLASS'))) add('licenseFront', 65, 'Hạng/Class');
        if (phrase(text, 'CO GIA TRI DEN', 'EXPIRES', 'DATE OF EXPIRY')) add('licenseFront', 42, 'thời hạn GPLX');
        if (phrase(text, 'BO GTVT', 'MINISTRY OF TRANSPORT')) add('licenseFront', 36, 'Bộ GTVT');

        // GPLX mặt sau: hỗ trợ cả mẫu có bảng phân hạng dài và mẫu ngắn hơn.
        if (phrase(text, 'CAC LOAI XE CO GIOI DUOC PHEP DIEU KHIEN')) add('licenseBack', 180, 'bảng loại xe được phép điều khiển');
        if (phrase(text, 'CLASSIFICATION OF MOTOR VEHICLES')) add('licenseBack', 175, 'Classification of motor vehicles');
        if (phrase(text, 'NGAY TRUNG TUYEN', 'BEGINNING DATE')) add('licenseBack', 75, 'ngày trúng tuyển');
        if (phrase(text, 'PASSENGER VEHICLES', 'PERMISSIBLE MAXIMUM MASS', 'TRAILER')) add('licenseBack', 45, 'mô tả hạng xe');
        if (phrase(text, '750 KG', '3,500 KG', '3500 KG', '175CM3')) add('licenseBack', 22, 'giới hạn phương tiện');

        // Loại trừ chéo.
        if (phrase(text, 'GIAY PHEP LAI XE') || regex(text, /DRIVER.?S?\s+LICENSE/)) {
            scores.cccdFront -= 160;
            scores.cccdBack -= 120;
        }
        if (phrase(text, 'CAN CUOC CONG DAN', 'CITIZEN IDENTITY CARD') || tolerantIdVnm) {
            scores.licenseFront -= 170;
            scores.licenseBack -= 150;
        }
        if (phrase(text, 'CLASSIFICATION OF MOTOR VEHICLES', 'CAC LOAI XE CO GIOI')) {
            scores.cccdFront -= 100;
            scores.cccdBack -= 100;
        }

        const ranked = Object.entries(scores).sort((a, b) => b[1] - a[1]);
        const [kind, points] = ranked[0];
        const second = ranked[1]?.[1] ?? -999;
        const margin = points - second;
        const primaryEvidence = evidence[kind].length;
        const confident = points >= 85 && margin >= 22 && primaryEvidence >= 1;
        const veryConfident = points >= 135 && margin >= 15;
        return {
            kind,
            points,
            margin,
            confident: confident || veryConfident,
            scores,
            evidence,
            reason: evidence[kind][0] || ''
        };
    };

    const recognizeVariant = async (worker, source, mode, targetLongSide = 1800) => {
        const output = await worker.recognize(prepareForOcr(source, mode, targetLongSide));
        return {
            text: output?.data?.text || '',
            confidence: Number(output?.data?.confidence || 0)
        };
    };

    const recognizeMrzBand = async (worker, source) => {
        if (source.width < source.height * 1.12) return { text: '', confidence: 0 };
        const band = cropRelative(source, 0.015, 0.49, 0.97, 0.49);
        return recognizeVariant(worker, band, 'binary', 2200);
    };

    const classify = async file => {
        const { original, documentCanvas, cropped } = await getClassifierSources(file);
        const worker = await getWorker();
        const attempts = [];

        for (const degrees of [0, 90, 270, 180]) {
            const rotated = rotateCanvas(documentCanvas, degrees);
            const contrast = await recognizeVariant(worker, rotated, 'contrast', 1850);
            let combinedText = contrast.text;
            let confidence = contrast.confidence;

            // Mặt sau CCCD dễ bị bỏ sót khi OCR toàn thẻ; đọc thêm riêng nửa dưới chứa MRZ.
            const mrz = await recognizeMrzBand(worker, rotated);
            if (mrz.text) {
                combinedText += `\n${mrz.text}`;
                confidence = Math.max(confidence, mrz.confidence);
            }

            let result = classifyText(combinedText);
            if (!result.confident || result.points < 125) {
                const binary = await recognizeVariant(worker, rotated, 'binary', 1850);
                combinedText = `${combinedText}\n${binary.text}`;
                confidence = Math.max(confidence, binary.confidence);
                result = classifyText(combinedText);
            }

            attempts.push({
                degrees,
                confidence,
                score: result,
                text: combinedText,
                rank: result.points + Math.min(14, Math.max(0, result.margin) * .16) + Math.min(5, confidence / 20)
            });

            if (result.confident && result.points >= 155 && result.margin >= 30) break;
        }

        attempts.sort((a, b) => b.rank - a.rank);
        const best = attempts[0];
        if (!best) throw new Error('Không thể đọc ảnh giấy tờ.');
        return {
            kind: best.score.confident ? best.score.kind : 'unknown',
            label: best.score.confident ? labels[best.score.kind] : 'Không xác định',
            degrees: best.degrees,
            confidence: best.confidence,
            source: original,
            rawText: best.text,
            reason: best.score.reason,
            score: best.score.points,
            margin: best.score.margin,
            croppedBeforeClassification: cropped
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
        return new File([blob], `${original.name.replace(/\.[^.]+$/, '')}-smartcar${extension}`, {
            type: mime,
            lastModified: Date.now()
        });
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
        const fileState = panel.querySelector(panel.matches('[data-ekyc-panel="citizen"]')
            ? '[data-ekyc-file-state]'
            : '[data-license-file-state]');
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
            if (result.kind === 'unknown') {
                return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold text-warning">⚠ ${name}</div><div class="small mt-1">SmartCar chưa đủ chắc chắn để nhận diện mặt này. Bạn có thể chọn ảnh rõ hơn hoặc chuyển sang nhập thủ công; ảnh vẫn bắt buộc để Quản trị viên đối chiếu.</div></div></div>`;
            }
            if (result.kind !== expected) {
                return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold text-danger">✕ ${name}</div><div class="small mt-1">Ảnh này được nhận diện là <strong>${result.label}</strong>. Vui lòng chọn đúng <strong>${labels[expected]}</strong>.</div></div></div>`;
            }
            const rotated = result.degrees ? ` · đã tự xoay ${result.degrees}°` : '';
            const cropped = result.croppedBeforeClassification ? ' · đã cắt vùng giấy tờ trước khi đọc' : '';
            const reason = result.reason ? ` · nhận diện từ ${result.reason}` : '';
            return `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold text-success">✓ ${name}</div><div class="small mt-1">Đúng ${result.label}${rotated}${cropped}${reason}.</div></div></div>`;
        };

        host.querySelector('div.small')?.remove();
        let body = host.querySelector('[data-pipeline-document-body]');
        if (!body) {
            body = document.createElement('div');
            body.dataset.pipelineDocumentBody = '';
            body.dataset.documentGateBody = '';
            host.appendChild(body);
        } else {
            body.dataset.documentGateBody = '';
        }
        const hasUnknown = front.kind === 'unknown' || back.kind === 'unknown';
        const footer = ok
            ? '✓ Đúng loại giấy tờ và đúng hai mặt.'
            : hasUnknown
                ? '⚠ Có mặt SmartCar chưa nhận diện chắc chắn. Có thể chọn ảnh khác hoặc nhập thủ công.'
                : '✕ Đã nhận diện sai loại/mặt giấy tờ. Hãy chọn lại đúng ảnh.';
        body.innerHTML = `<div class="row g-2">${item('Mặt trước', front, cfg.frontKind)}${item('Mặt sau', back, cfg.backKind)}</div><div class="small ${ok ? 'text-success' : hasUnknown ? 'text-warning' : 'text-danger'} fw-semibold mt-2">${footer}</div>`;
        return ok;
    };

    const showDocumentChecking = (panel, cfg) => {
        const host = ensureHost(panel, '[data-document-gate-host]', cfg.documentTitle, 'data-document-gate-host');
        const badge = host.querySelector('.badge');
        badge.textContent = 'Đang nhận diện';
        badge.className = 'badge bg-secondary';
        const existing = host.querySelector('[data-pipeline-document-body]') || host.querySelector('div.small');
        if (existing) existing.textContent = 'Đang cắt vùng giấy tờ, xác định loại, mặt trước/mặt sau và chiều ảnh...';
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
        let white = 0;
        for (let i = 0, p = 0; i < pixels.length; i += 4, p += 1) {
            const lum = .299 * pixels[i] + .587 * pixels[i + 1] + .114 * pixels[i + 2];
            gray[p] = lum;
            brightness += lum;
            if (lum > 245) white += 1;
        }
        brightness /= Math.max(1, gray.length);
        let sum = 0, sq = 0, count = 0;
        for (let y = 1; y < sample.height - 1; y += 2) {
            for (let x = 1; x < sample.width - 1; x += 2) {
                const c = gray[y * sample.width + x];
                const lap = gray[(y - 1) * sample.width + x] + gray[(y + 1) * sample.width + x] +
                    gray[y * sample.width + x - 1] + gray[y * sample.width + x + 1] - 4 * c;
                sum += lap;
                sq += lap * lap;
                count += 1;
            }
        }
        const mean = count ? sum / count : 0;
        const sharpness = count ? Math.max(0, sq / count - mean * mean) : 0;
        const shortSide = Math.min(canvas.width, canvas.height);
        const longSide = Math.max(canvas.width, canvas.height);
        const whiteRatio = white / Math.max(1, gray.length);
        const failures = [];
        if (shortSide < 430 || longSide < 760) failures.push('Ảnh quá nhỏ. Hãy chụp gần hơn.');
        if (sharpness < 35) failures.push('Ảnh bị mờ hoặc rung. Hãy giữ máy ổn định và chụp lại.');
        if (brightness < 40) failures.push('Ảnh quá tối. Hãy chụp ở nơi đủ sáng.');
        if (brightness > 225 || whiteRatio > .34) failures.push('Ảnh quá sáng hoặc bị lóa. Hãy đổi góc chụp.');
        return { passed: failures.length === 0, failures, warnings: [] };
    };

    const renderQuality = (panel, cfg, front, back) => {
        const host = ensureHost(panel, '[data-image-quality-host]', cfg.qualityTitle, 'data-image-quality-host');
        const badge = host.querySelector('.badge');
        const passed = front.passed && back.passed;
        badge.textContent = passed ? 'Đạt' : 'Chụp lại';
        badge.className = `badge ${passed ? 'bg-success' : 'bg-danger'}`;
        const detail = (name, result) => `<div class="col-md-6"><div class="border rounded-3 p-2 h-100 bg-white"><div class="fw-semibold ${result.passed ? 'text-success' : 'text-danger'}">${result.passed ? '✓' : '✕'} ${name}</div><div class="small mt-1">${result.failures?.length ? result.failures.join(' ') : 'Ảnh đạt yêu cầu.'}</div></div></div>`;
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
        const onlyBoundsFailure = !result.passed && result.failures?.length &&
            result.failures.every(message => /không nhận ra đủ vùng giấy tờ/i.test(message));
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
        panel.dataset.templatePipeline = 'v3';
        if (cfg.ocrButton) cfg.ocrButton.disabled = true;

        if (!frontFile || !backFile) {
            panel.dataset.documentGate = 'pending';
            panel.dataset.frontDocumentKind = '';
            panel.dataset.backDocumentKind = '';
            showQualityWaiting(panel, cfg, 'Chọn đủ hai mặt để SmartCar bắt đầu kiểm tra.');
            return;
        }
        if (panel.dataset.duplicateImages === 'true') return;

        showDocumentChecking(panel, cfg);
        showQualityWaiting(panel, cfg);
        panel.dataset.documentGate = 'checking';

        try {
            const front = await classify(frontFile);
            if (state.get(panel)?.version !== version) return;
            const back = await classify(backFile);
            if (state.get(panel)?.version !== version) return;

            panel.dataset.frontDocumentKind = front.kind;
            panel.dataset.backDocumentKind = back.kind;
            const documentOk = renderDocument(panel, front, back, cfg);
            if (!documentOk) {
                panel.dataset.documentGate = 'fail';
                showQualityWaiting(panel, cfg, 'Tự nhận diện chưa vượt qua. Nếu chỉ là “Không xác định”, bạn vẫn có thể nhập thủ công; ảnh hai mặt vẫn bắt buộc.');
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
            if (cfg.ocrButton) cfg.ocrButton.disabled = !qualityOk || panel.dataset.duplicateImages === 'true';
            if (qualityOk) {
                panel.dataset.documentGateBypass = 'true';
                panel.dataset.imageQualityBypass = 'true';
            }
        } catch (error) {
            if (state.get(panel)?.version !== version) return;
            panel.dataset.documentGate = 'fail';
            panel.dataset.frontDocumentKind = panel.dataset.frontDocumentKind || 'unknown';
            panel.dataset.backDocumentKind = panel.dataset.backDocumentKind || 'unknown';
            panel.dataset.imageQuality = 'pending';
            if (cfg.ocrButton) cfg.ocrButton.disabled = true;
            const host = ensureHost(panel, '[data-document-gate-host]', cfg.documentTitle, 'data-document-gate-host');
            const badge = host.querySelector('.badge');
            badge.textContent = 'Không nhận diện được';
            badge.className = 'badge bg-warning text-dark';
            const body = host.querySelector('[data-pipeline-document-body]') || host.querySelector('div.small');
            if (body) body.textContent = error?.message || 'SmartCar chưa nhận diện chắc chắn giấy tờ. Bạn có thể chọn ảnh khác hoặc nhập thủ công.';
            showQualityWaiting(panel, cfg, 'Chưa kiểm tra chất lượng vì SmartCar chưa nhận diện chắc chắn giấy tờ.');
        }
    };

    const installPanel = panel => {
        if (!panel || panel.dataset.templatePipelineInstalled === 'true') return;
        panel.dataset.templatePipelineInstalled = 'true';
        const cfg = configFor(panel);
        if (!cfg.frontInput || !cfg.backInput || !cfg.ocrButton) return;

        const onChange = event => {
            event.stopImmediatePropagation();
            void runPipeline(panel);
        };
        cfg.frontInput.addEventListener('change', onChange);
        cfg.backInput.addEventListener('change', onChange);
    };

    const install = () => document
        .querySelectorAll('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]')
        .forEach(installPanel);

    window.SmartCarTemplateClassifier = { classifyText, labels };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(install, 0), { once: true });
    } else {
        setTimeout(install, 0);
    }
})();