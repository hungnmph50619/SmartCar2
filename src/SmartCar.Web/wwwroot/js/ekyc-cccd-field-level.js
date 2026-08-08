(() => {
    let running = false;
    let lastRunKey = '';

    const normalize = value => (value || '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/đ/g, 'd')
        .replace(/Đ/g, 'D')
        .toUpperCase()
        .replace(/\s+/g, ' ')
        .trim();

    const fingerprint = file => file
        ? `${file.name}|${file.size}|${file.lastModified}|${file.type}`
        : '';

    const hasVietnameseMarks = value => /[À-ỹĐđ]/u.test(value || '');

    const splitLines = text => (text || '')
        .split(/\r?\n/)
        .map(line => line.replace(/\s+/g, ' ').trim())
        .filter(Boolean)
        .map(original => ({ original, normalized: normalize(original) }));

    const getValue = selector =>
        document.querySelector(selector)?.value?.trim() || '';

    const writeValue = (selector, value) => {
        const input = document.querySelector(selector);
        if (!input || !value) return false;
        const next = value.trim();
        if ((input.value || '').trim() === next) return false;
        input.value = next;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    };

    const cropCanvas = (source, region, maxLong = 1800) => {
        const x = Math.max(0, Math.round(source.width * region.x));
        const y = Math.max(0, Math.round(source.height * region.y));
        const width = Math.min(
            source.width - x,
            Math.max(1, Math.round(source.width * region.width))
        );
        const height = Math.min(
            source.height - y,
            Math.max(1, Math.round(source.height * region.height))
        );
        const scale = Math.min(3.2, maxLong / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(
            source,
            x, y, width, height,
            0, 0, canvas.width, canvas.height
        );
        return canvas;
    };

    const makeVariant = (source, binary = false) => {
        const canvas = document.createElement('canvas');
        canvas.width = source.width;
        canvas.height = source.height;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, 0);
        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = pixels.data;
        let sum = 0;

        for (let i = 0; i < data.length; i += 4) {
            sum += 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
        }
        const mean = sum / Math.max(1, data.length / 4);
        const threshold = Math.max(105, Math.min(205, mean * 0.92));

        for (let i = 0; i < data.length; i += 4) {
            const gray = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
            const value = binary
                ? (gray >= threshold ? 255 : 0)
                : Math.max(0, Math.min(255, (gray - 128) * 1.55 + 128));
            data[i] = value;
            data[i + 1] = value;
            data[i + 2] = value;
        }
        ctx.putImageData(pixels, 0, 0);
        return canvas;
    };

    const repeatedNoiseToken = token => {
        const compact = normalize(token).replace(/[^A-Z]/g, '');
        if (compact.length < 3 || compact.length > 6) return false;
        return new Set(compact).size <= 1 ||
            /^(SSS|III|LLL|XXX|VVV|OOO|CCC|EEE)$/.test(compact);
    };

    const cleanName = value => {
        let text = (value || '')
            .replace(/\b(Họ\s+và\s+tên|Full\s+name)\b\s*[:：/-]*/giu, ' ')
            .replace(/[^A-Za-zÀ-ỹĐđ'\-\s]/g, ' ')
            .replace(/\s+/g, ' ')
            .trim();
        const tokens = text.split(/\s+/).filter(Boolean);
        while (tokens.length > 2 && repeatedNoiseToken(tokens[tokens.length - 1])) {
            tokens.pop();
        }
        return tokens.join(' ').trim();
    };

    const isLikelyName = value => {
        const cleaned = cleanName(value);
        if (!cleaned || /\d/.test(cleaned)) return false;
        const normalized = normalize(cleaned);
        if ([
            'HO VA TEN', 'FULL NAME', 'NGAY SINH', 'DATE OF BIRTH',
            'GIOI TINH', 'SEX', 'QUOC TICH', 'NATIONALITY',
            'QUE QUAN', 'PLACE OF ORIGIN', 'NOI THUONG TRU',
            'PLACE OF RESIDENCE', 'CAN CUOC', 'CITIZEN'
        ].some(label => normalized.includes(label))) return false;
        const words = cleaned.split(/\s+/).filter(Boolean);
        if (words.length < 2 || words.length > 7) return false;
        const letters = (cleaned.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length;
        return letters / Math.max(1, cleaned.length) >= 0.72;
    };

    const nameScore = (value, confidence = 0) => {
        const cleaned = cleanName(value);
        if (!isLikelyName(cleaned)) return -1000;
        let score = Math.min(55, cleaned.length);
        score += Math.min(30, confidence * 0.3);
        if (hasVietnameseMarks(cleaned)) score += 26;
        if (cleaned === cleaned.toUpperCase()) score += 6;
        if (cleaned.split(/\s+/).some(repeatedNoiseToken)) score -= 80;
        return score;
    };

    const nameCandidates = (text, confidence) => {
        const lines = splitLines(text);
        const values = [];
        const labelIndex = lines.findIndex(line =>
            line.normalized.includes('HO VA TEN') ||
            line.normalized.includes('FULL NAME'));

        if (labelIndex >= 0) {
            const inline = lines[labelIndex].original
                .replace(/^.*?Họ\s+và\s+tên\s*[:：/-]*/iu, '')
                .replace(/^.*?Full\s+name\s*[:：/-]*/iu, '')
                .trim();
            if (inline) values.push(inline);
            for (let offset = 1; offset <= 2; offset += 1) {
                if (lines[labelIndex + offset]?.original) {
                    values.push(lines[labelIndex + offset].original);
                }
            }
        }
        lines.forEach(line => values.push(line.original));

        return [...new Set(values.map(cleanName).filter(isLikelyName))]
            .map(value => ({ value, confidence, score: nameScore(value, confidence) }));
    };

    const cleanAddress = value => (value || '')
        .replace(/^.*?Nơi\s+(?:thường\s+)?trú\s*[:：/-]*/iu, '')
        .replace(/^.*?Place\s+of\s+residence\s*[:：/-]*/iu, '')
        .replace(/\b(?:Có\s+giá\s+trị\s+đến|Date\s+of\s+expiry)\b.*$/iu, '')
        .replace(/\b[0-3]?\d[\/\-.][01]?\d[\/\-.](?:19|20)\d{2}\b/g, ' ')
        .replace(/[<>|_=]+/g, ' ')
        .replace(/\s*,\s*/g, ', ')
        .replace(/\s+/g, ' ')
        .replace(/[,;:\s/.-]+$/g, '')
        .replace(/^[:\s/.,;\-]+/g, '')
        .trim();

    const addressStop = normalized => [
        'CO GIA TRI', 'DATE OF EXPIRY', 'HET HAN', 'NGAY SINH',
        'DATE OF BIRTH', 'GIOI TINH', 'SEX', 'QUOC TICH',
        'NATIONALITY', 'QUE QUAN', 'PLACE OF ORIGIN', 'CAN CUOC',
        'CITIZEN', 'SO / NO', 'HO VA TEN', 'FULL NAME'
    ].some(label => normalized.includes(label));

    const addressHasNoise = value => {
        const raw = value || '';
        const normalized = normalize(raw);
        return /[|<>_=]/.test(raw) ||
            /\b(?:19|20)\d{2}\b/.test(raw) ||
            normalized.includes('CO GIA TRI') ||
            normalized.includes('DATE OF EXPIRY') ||
            normalized.includes('IDVNM') ||
            normalized.includes('<<<<');
    };

    const isLikelyAddress = value => {
        const cleaned = cleanAddress(value);
        if (!cleaned || cleaned.length < 8 || cleaned.length > 180) return false;
        if (addressHasNoise(value)) return false;
        const letters = (cleaned.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length;
        if (letters < 6 || letters / Math.max(1, cleaned.length) < 0.50) return false;
        if (cleaned.split(/\s+/).filter(Boolean).length < 2) return false;
        if (/[,;:\-]\s*$/.test(value || '')) return false;
        return true;
    };

    const addressScore = (value, confidence = 0) => {
        const cleaned = cleanAddress(value);
        if (!isLikelyAddress(cleaned)) return -1000;
        let score = Math.min(80, cleaned.length * 0.8);
        score += Math.min(25, confidence * 0.25);
        score += Math.min(28, (cleaned.match(/,/g) || []).length * 7);
        if (hasVietnameseMarks(cleaned)) score += 12;
        if (/\b(?:Thị trấn|Thành phố|TP\.?|Phường|Xã|Quận|Huyện|Tỉnh)\b/iu.test(cleaned)) {
            score += 10;
        }
        return score;
    };

    const addressCandidates = (text, confidence) => {
        const lines = splitLines(text);
        const values = [];
        const labelIndex = lines.findIndex(line => {
            const n = line.normalized;
            return n.includes('PLACE OF RESIDENCE') ||
                n.includes('NOI THUONG TRU') ||
                n.includes('NOI CU TRU') ||
                (n.includes('NOI') && n.includes('TRU'));
        });

        if (labelIndex >= 0) {
            const parts = [];
            const inline = cleanAddress(lines[labelIndex].original);
            if (isLikelyAddress(inline)) parts.push(inline);
            for (let offset = 1; offset <= 3; offset += 1) {
                const next = lines[labelIndex + offset];
                if (!next || addressStop(next.normalized)) break;
                const part = cleanAddress(next.original);
                if (isLikelyAddress(part)) parts.push(part);
            }
            if (parts.length) values.push(parts.join(', '));
        }

        const fallback = lines
            .filter(line => !addressStop(line.normalized))
            .map(line => cleanAddress(line.original))
            .filter(isLikelyAddress);
        if (fallback.length) {
            values.push(fallback.slice(-3).join(', '));
            values.push(fallback.slice(-2).join(', '));
        }

        return [...new Set(values.map(cleanAddress).filter(isLikelyAddress))]
            .map(value => ({ value, confidence, score: addressScore(value, confidence) }));
    };

    const recognize = async (worker, canvas, psm = 6) => {
        try {
            await worker.setParameters({
                preserve_interword_spaces: '1',
                tessedit_pageseg_mode: String(psm)
            });
        } catch {
            // OCR vẫn chạy nếu build không hỗ trợ tham số trên.
        }
        const output = await worker.recognize(canvas);
        return {
            text: output?.data?.text || '',
            confidence: Number.isFinite(output?.data?.confidence)
                ? output.data.confidence
                : 0
        };
    };

    const best = candidates =>
        [...candidates].sort((a, b) => b.score - a.score)[0] || null;

    const agreementCount = (candidates, selected) => {
        if (!selected) return 0;
        const target = normalize(selected.value);
        return candidates.filter(candidate => normalize(candidate.value) === target).length;
    };

    const setFieldStatus = (selector, high, message) => {
        const input = document.querySelector(selector);
        if (!input) return;
        let status = input.parentElement?.querySelector('[data-ocr-field-status]');
        if (!status) {
            status = document.createElement('div');
            status.dataset.ocrFieldStatus = '';
            input.insertAdjacentElement('afterend', status);
        }
        status.className = `small mt-1 ${high ? 'text-success' : 'text-warning'}`;
        status.textContent = `${high ? '✓' : '⚠'} ${message}`;
    };

    const parseDate = value => {
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value || '');
        if (!match) return null;
        const date = new Date(Number(match[3]), Number(match[2]) - 1, Number(match[1]));
        return date.getFullYear() === Number(match[3]) &&
            date.getMonth() === Number(match[2]) - 1 &&
            date.getDate() === Number(match[1])
            ? date
            : null;
    };

    const summarize = (panel, nameHigh, addressHigh) => {
        const documentNumber = getValue('[name="CitizenIdVerification.DocumentNumber"]');
        const birth = getValue('#citizen-birth-display');
        const gender = getValue('[name="CitizenIdVerification.Gender"]');
        const issued = getValue('#citizen-issued-display');
        const expiry = getValue('#citizen-expiry-display');
        const name = getValue('[name="CitizenIdVerification.FullNameOnDocument"]');
        const address = getValue('[name="CitizenIdVerification.PermanentAddress"]');

        const detected = [
            isLikelyName(name),
            /^\d{12}$/.test(documentNumber),
            Boolean(parseDate(birth)),
            ['Nam', 'Nữ', 'Khác'].includes(gender),
            Boolean(parseDate(issued)),
            Boolean(parseDate(expiry)),
            isLikelyAddress(address)
        ];

        const issuedDate = parseDate(issued);
        const expiryDate = parseDate(expiry);
        const high = [
            nameHigh,
            /^\d{12}$/.test(documentNumber),
            Boolean(parseDate(birth)),
            ['Nam', 'Nữ', 'Khác'].includes(gender),
            Boolean(issuedDate),
            Boolean(expiryDate && (!issuedDate || expiryDate > issuedDate)),
            addressHigh
        ];

        const detectedCount = detected.filter(Boolean).length;
        const highCount = high.filter(Boolean).length;
        const needs = [];
        if (!nameHigh) needs.push('họ tên');
        if (!addressHigh) needs.push('nơi cư trú');

        const state = panel.querySelector('[data-ekyc-ocr-state]');
        if (state) {
            state.textContent = `✓ Đã nhận diện ${detectedCount}/7 trường · ${highCount}/7 trường có độ tin cậy cao.` +
                (needs.length
                    ? ` Cần đối chiếu: ${needs.join(', ')}.`
                    : ' Hãy đối chiếu nhanh trước khi tiếp tục.');
            state.className = `small ${highCount >= 6 ? 'text-success' : 'text-warning'} mb-3`;
        }
    };

    const refine = async panel => {
        if (running || panel.dataset.localTestOcr !== 'on') return;
        const front = document.getElementById('citizen-front-file')?.files?.[0];
        if (!front || !window.Tesseract?.createWorker ||
            !window.SmartCarDocumentVision?.prepareForOcr) return;

        const key = `${fingerprint(front)}|${getValue('[name="CitizenIdVerification.FullNameOnDocument"]')}|${getValue('[name="CitizenIdVerification.PermanentAddress"]')}`;
        if (!key || key === lastRunKey) return;
        running = true;
        lastRunKey = key;

        const state = panel.querySelector('[data-ekyc-ocr-state]');
        if (state) {
            state.textContent = 'Đang OCR riêng họ tên và nơi cư trú để tăng độ chính xác...';
            state.className = 'small text-muted mb-3';
        }

        let worker = null;
        try {
            const prepared = await window.SmartCarDocumentVision.prepareForOcr(front);

            const nameBroad = cropCanvas(prepared.canvas, {
                x: 0.25, y: 0.48, width: 0.73, height: 0.18
            }, 1600);
            const nameTight = cropCanvas(prepared.canvas, {
                x: 0.27, y: 0.54, width: 0.71, height: 0.11
            }, 1500);
            const addressBroad = cropCanvas(prepared.canvas, {
                x: 0.24, y: 0.73, width: 0.75, height: 0.24
            }, 1900);
            const addressTight = cropCanvas(prepared.canvas, {
                x: 0.28, y: 0.80, width: 0.70, height: 0.17
            }, 1800);

            worker = await window.Tesseract.createWorker(['vie', 'eng'], 1);

            const nameA = await recognize(worker, makeVariant(nameBroad, false), 6);
            const nameB = await recognize(worker, makeVariant(nameTight, true), 6);
            const addressA = await recognize(worker, makeVariant(addressBroad, false), 6);
            const addressB = await recognize(worker, makeVariant(addressTight, true), 6);

            const currentName = getValue('[name="CitizenIdVerification.FullNameOnDocument"]');
            const currentAddress = getValue('[name="CitizenIdVerification.PermanentAddress"]');

            const names = [
                ...nameCandidates(nameA.text, nameA.confidence),
                ...nameCandidates(nameB.text, nameB.confidence)
            ];
            if (isLikelyName(currentName)) {
                names.push({
                    value: cleanName(currentName),
                    confidence: 45,
                    score: nameScore(currentName, 45)
                });
            }

            const addresses = [
                ...addressCandidates(addressA.text, addressA.confidence),
                ...addressCandidates(addressB.text, addressB.confidence)
            ];
            if (isLikelyAddress(currentAddress)) {
                addresses.push({
                    value: cleanAddress(currentAddress),
                    confidence: 40,
                    score: addressScore(currentAddress, 40)
                });
            }

            const selectedName = best(names);
            const selectedAddress = best(addresses);

            if (selectedName?.value) {
                writeValue('[name="CitizenIdVerification.FullNameOnDocument"]', selectedName.value);
            }
            if (selectedAddress?.value) {
                writeValue('[name="CitizenIdVerification.PermanentAddress"]', selectedAddress.value);
            }

            const nameHigh = Boolean(
                selectedName &&
                isLikelyName(selectedName.value) &&
                selectedName.confidence >= 55 &&
                hasVietnameseMarks(selectedName.value) &&
                agreementCount(names, selectedName) >= 2
            );

            const addressHigh = Boolean(
                selectedAddress &&
                isLikelyAddress(selectedAddress.value) &&
                selectedAddress.confidence >= 55 &&
                !addressHasNoise(selectedAddress.value) &&
                agreementCount(addresses, selectedAddress) >= 2 &&
                !/[,;:\-]\s*$/.test(selectedAddress.value)
            );

            setFieldStatus(
                '[name="CitizenIdVerification.FullNameOnDocument"]',
                nameHigh,
                nameHigh
                    ? 'Họ tên được OCR riêng và kết quả ổn định.'
                    : 'Họ tên có thể mất dấu hoặc sai ký tự. Hãy đối chiếu trực tiếp với CCCD.'
            );
            setFieldStatus(
                '[name="CitizenIdVerification.PermanentAddress"]',
                addressHigh,
                addressHigh
                    ? 'Nơi cư trú được OCR riêng và kết quả ổn định.'
                    : 'Nơi cư trú có thể thiếu đầu/cuối dòng hoặc còn ký tự sai. Hãy đối chiếu trực tiếp với CCCD.'
            );

            summarize(panel, nameHigh, addressHigh);
        } catch {
            setFieldStatus(
                '[name="CitizenIdVerification.FullNameOnDocument"]',
                false,
                'Không đọc lại được vùng họ tên. Hãy đối chiếu trực tiếp với CCCD.'
            );
            setFieldStatus(
                '[name="CitizenIdVerification.PermanentAddress"]',
                false,
                'Không đọc lại được vùng nơi cư trú. Hãy đối chiếu trực tiếp với CCCD.'
            );
            summarize(panel, false, false);
        } finally {
            try {
                await worker?.terminate();
            } catch {
                // Không ảnh hưởng luồng chính.
            }
            running = false;
        }
    };

    const install = () => {
        const panel = document.querySelector('[data-ekyc-panel="citizen"]');
        const state = panel?.querySelector('[data-ekyc-ocr-state]');
        if (!panel || !state || state.dataset.cccdFieldLevelObserved === 'true') return;
        state.dataset.cccdFieldLevelObserved = 'true';

        document.addEventListener('click', event => {
            if (event.target.closest('[data-ekyc-ocr]')) lastRunKey = '';
        }, true);

        const evaluate = () => {
            if (!/OCR cục bộ đọc hợp lệ/i.test(state.textContent || '')) return;
            window.setTimeout(() => void refine(panel), 0);
        };
        new MutationObserver(evaluate).observe(state, {
            childList: true,
            subtree: true,
            characterData: true
        });
        evaluate();
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.setTimeout(install, 0), { once: true });
    } else {
        window.setTimeout(install, 0);
    }
})();
