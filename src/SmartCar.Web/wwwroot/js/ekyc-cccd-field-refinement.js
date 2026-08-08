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

    const dispatchValue = input => {
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const writeValue = (selector, value) => {
        const input = document.querySelector(selector);
        if (!input || !value) return false;
        const next = value.trim();
        if ((input.value || '').trim() === next) return false;
        input.value = next;
        dispatchValue(input);
        return true;
    };

    const getValue = selector =>
        document.querySelector(selector)?.value?.trim() || '';

    const cropCanvas = (source, region, maxLong = 1900) => {
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

    const makeVariant = (source, mode) => {
        const canvas = document.createElement('canvas');
        canvas.width = source.width;
        canvas.height = source.height;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, 0);
        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = pixels.data;
        let graySum = 0;

        for (let index = 0; index < data.length; index += 4) {
            const gray = 0.299 * data[index] +
                0.587 * data[index + 1] +
                0.114 * data[index + 2];
            graySum += gray;
        }

        const mean = graySum / Math.max(1, data.length / 4);
        const threshold = Math.max(105, Math.min(205, mean * 0.92));

        for (let index = 0; index < data.length; index += 4) {
            const gray = 0.299 * data[index] +
                0.587 * data[index + 1] +
                0.114 * data[index + 2];
            let value;
            if (mode === 'binary') {
                value = gray >= threshold ? 255 : 0;
            } else {
                value = Math.max(0, Math.min(255, (gray - 128) * 1.55 + 128));
            }
            data[index] = value;
            data[index + 1] = value;
            data[index + 2] = value;
        }
        ctx.putImageData(pixels, 0, 0);
        return canvas;
    };

    const repeatedNoiseToken = token => {
        const compact = normalize(token).replace(/[^A-Z]/g, '');
        if (compact.length < 3 || compact.length > 6) return false;
        return new Set(compact).size <= 1 ||
            /^(SSS|III|LLL|XXX|VVV|OOO|CCC|EEE||||)$/.test(compact);
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
        const tokens = cleaned.split(/\s+/).filter(Boolean);
        let score = Math.min(55, cleaned.length);
        score += Math.min(30, Number.isFinite(confidence) ? confidence * 0.3 : 0);
        if (hasVietnameseMarks(cleaned)) score += 24;
        if (cleaned === cleaned.toUpperCase()) score += 6;
        if (tokens.some(repeatedNoiseToken)) score -= 80;
        return score;
    };

    const nameCandidatesFromText = (text, confidence) => {
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
        const tokens = cleaned.split(/\s+/).filter(Boolean);
        if (tokens.length < 2) return false;
        if (/[,;:\-]\s*$/.test(value || '')) return false;
        return true;
    };

    const addressScore = (value, confidence = 0) => {
        const cleaned = cleanAddress(value);
        if (!isLikelyAddress(cleaned)) return -1000;
        let score = Math.min(80, cleaned.length * 0.8);
        score += Math.min(25, Number.isFinite(confidence) ? confidence * 0.25 : 0);
        score += Math.min(28, (cleaned.match(/,/g) || []).length * 7);
        if (hasVietnameseMarks(cleaned)) score += 12;
        if (addressHasNoise(value)) score -= 100;
        if (/\b(?:Thị trấn|Thành phố|TP\.?|Phường|Xã|Quận|Huyện|Tỉnh)\b/iu.test(cleaned)) {
            score += 10;
        }
        return score;
    };

    const addressCandidatesFromText = (text, confidence) => {
        const lines = splitLines(text);
        const candidates = [];
        const labelIndex = lines.findIndex(line => {
            const value = line.normalized;
            return value.includes('PLACE OF RESIDENCE') ||
                value.includes('NOI THUONG TRU') ||
                value.includes('NOI CU TRU') ||
                (value.includes('NOI') && value.includes('TRU'));
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
            if (parts.length) candidates.push(parts.join(', '));
        }

        const fallbackParts = lines
            .filter(line => !addressStop(line.normalized))
            .map(line => cleanAddress(line.original))
            .filter(isLikelyAddress);

        if (fallbackParts.length) {
            candidates.push(fallbackParts.slice(-3).join(', '));
            candidates.push(fallbackParts.slice(-2).join(', '));
        }

        return [...new Set(candidates.map(cleanAddress).filter(isLikelyAddress))]
            .map(value => ({ value, confidence, score: addressScore(value, confidence) }));
    };

    const recognize = async (worker, canvas, psm) => {
        try {
            await worker.setParameters({
                preserve_interword_spaces: '1',
                tessedit_pageseg_mode: String(psm)
            });
        } catch {
            // Tesseract vẫn chạy nếu build không hỗ trợ tham số này.
        }
        const output = await worker.recognize(canvas);
        return {
            text: output?.data?.text || '',
            confidence: Number.isFinite(output?.data?.confidence)
                ? output.data.confidence
                : 0
        };
    };

    const bestCandidate = candidates =>
        [...candidates].sort((left, right) => right.score - left.score)[0] || null;

    const exactAgreement = (candidates, selected) => {
        if (!selected) return false;
        return candidates.filter(candidate =>
            candidate.value.localeCompare(selected.value, 'vi', { sensitivity: 'variant' }) === 0
        ).length >= 2;
    };

    const normalizedAgreement = (candidates, selected) => {
        if (!selected) return false;
        const target = normalize(selected.value);
        return candidates.filter(candidate => normalize(candidate.value) === target).length >= 2;
    };

    const setFieldStatus = (selector, level, text) => {
        const input = document.querySelector(selector);
        if (!input) return;
        let status = input.parentElement?.querySelector('[data-ocr-field-status]');
        if (!status) {
            status = document.createElement('div');
            status.dataset.ocrFieldStatus = '';
            status.className = 'small mt-1';
            input.insertAdjacentElement('afterend', status);
        }
        status.className = `small mt-1 ${level === 'high' ? 'text-success' : 'text-warning'}`;
        status.textContent = `${level === 'high' ? '✓' : '⚠'} ${text}`;
    };

    const parseDisplayDate = value => {
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value || '');
        if (!match) return null;
        const date = new Date(Number(match[3]), Number(match[2]) - 1, Number(match[1]));
        if (date.getFullYear() !== Number(match[3]) ||
            date.getMonth() !== Number(match[2]) - 1 ||
            date.getDate() !== Number(match[1])) return null;
        return date;
    };

    const summarize = (panel, nameReview, addressReview) => {
        const fields = [
            {
                selector: '[name="CitizenIdVerification.FullNameOnDocument"]',
                detected: value => isLikelyName(value),
                high: () => nameReview.high
            },
            {
                selector: '[name="CitizenIdVerification.DocumentNumber"]',
                detected: value => /^\d{12}$/.test(value),
                high: value => /^\d{12}$/.test(value)
            },
            {
                selector: '#citizen-birth-display',
                detected: value => Boolean(parseDisplayDate(value)),
                high: value => Boolean(parseDisplayDate(value))
            },
            {
                selector: '[name="CitizenIdVerification.Gender"]',
                detected: value => ['Nam', 'Nữ', 'Khác'].includes(value),
                high: value => ['Nam', 'Nữ', 'Khác'].includes(value)
            },
            {
                selector: '#citizen-issued-display',
                detected: value => Boolean(parseDisplayDate(value)),
                high: value => Boolean(parseDisplayDate(value))
            },
            {
                selector: '#citizen-expiry-display',
                detected: value => Boolean(parseDisplayDate(value)),
                high: value => Boolean(parseDisplayDate(value)
                    && (!parseDisplayDate(getValue('#citizen-issued-display')) ||
                        parseDisplayDate(value) > parseDisplayDate(getValue('#citizen-issued-display'))))
            },
            {
                selector: '[name="CitizenIdVerification.PermanentAddress"]',
                detected: value => isLikelyAddress(value),
                high: () => addressReview.high
            }
        ];

        let detected = 0;
        let high = 0;
        fields.forEach(field => {
            const value = getValue(field.selector);
            if (field.detected(value)) detected += 1;
            if (field.high(value)) high += 1;
        });

        const needs = [];
        if (!nameReview.high) needs.push('họ tên');
        if (!addressReview.high) needs.push('nơi cư trú');

        const state = panel.querySelector('[data-ekyc-ocr-state]');
        if (state) {
            state.textContent = `✓ Đã nhận diện ${detected}/7 trường · ${high}/7 trường có độ tin cậy cao.` +
                (needs.length ? ` Cần đối chiếu: ${needs.join(', ')}.` : ' Hãy đối chiếu nhanh trước khi tiếp tục.');
            state.className = `small ${high >= 6 ? 'text-success' : 'text-warning'} mb-3`;
        }
    };

    const refine = async panel => {
        if (running || panel.dataset.localTestOcr !== 'on') return;
        const front = document.getElementById('citizen-front-file')?.files?.[0];
        if (!front || !window.Tesseract?.createWorker ||
            !window.SmartCarDocumentVision?.prepareForOcr) return;

        const currentName = getValue('[name="CitizenIdVerification.FullNameOnDocument"]');
        const currentAddress = getValue('[name="CitizenIdVerification.PermanentAddress"]');
        const key = `${fingerprint(front)}|${currentName}|${currentAddress}`;
        if (!key || key === lastRunKey) return;

        running = true;
        lastRunKey = key;
        const state = panel.querySelector('[data-ekyc-ocr-state]');
        if (state) state.textContent = 'Đang đọc riêng họ tên và nơi cư trú để tăng độ chính xác...';

        let worker = null;
        try {
            const prepared = await window.SmartCarDocumentVision.prepareForOcr(front);

            // CCCD chip có bố cục ổn định: OCR sát vùng giá trị thay vì cả khối thông tin.
            const nameCrop = cropCanvas(prepared.canvas, {
                x: 0.33, y: 0.39, width: 0.65, height: 0.14
            }, 1500);
            const addressCrop = cropCanvas(prepared.canvas, {
                x: 0.31, y: 0.69, width: 0.68, height: 0.25
            }, 1800);

            worker = await window.Tesseract.createWorker(['vie', 'eng'], 1);

            const nameGray = await recognize(worker, makeVariant(nameCrop, 'gray'), 6);
            const nameBinary = await recognize(worker, makeVariant(nameCrop, 'binary'), 6);
            const addressGray = await recognize(worker, makeVariant(addressCrop, 'gray'), 6);
            const addressBinary = await recognize(worker, makeVariant(addressCrop, 'binary'), 6);

            const nameCandidates = [
                ...nameCandidatesFromText(nameGray.text, nameGray.confidence),
                ...nameCandidatesFromText(nameBinary.text, nameBinary.confidence)
            ];
            if (isLikelyName(currentName)) {
                nameCandidates.push({
                    value: cleanName(currentName),
                    confidence: 45,
                    score: nameScore(currentName, 45)
                });
            }

            const addressCandidates = [
                ...addressCandidatesFromText(addressGray.text, addressGray.confidence),
                ...addressCandidatesFromText(addressBinary.text, addressBinary.confidence)
            ];
            if (isLikelyAddress(currentAddress)) {
                addressCandidates.push({
                    value: cleanAddress(currentAddress),
                    confidence: 40,
                    score: addressScore(currentAddress, 40)
                });
            }

            const bestName = bestCandidate(nameCandidates);
            const bestAddress = bestCandidate(addressCandidates);

            if (bestName?.value) {
                writeValue('[name="CitizenIdVerification.FullNameOnDocument"]', bestName.value);
            }
            if (bestAddress?.value) {
                writeValue('[name="CitizenIdVerification.PermanentAddress"]', bestAddress.value);
            }

            const nameExact = exactAgreement(nameCandidates, bestName);
            const nameNormalized = normalizedAgreement(nameCandidates, bestName);
            const nameHigh = Boolean(bestName &&
                isLikelyName(bestName.value) &&
                bestName.confidence >= 55 &&
                hasVietnameseMarks(bestName.value) &&
                (nameExact || nameNormalized));

            const addressExact = exactAgreement(addressCandidates, bestAddress);
            const addressNormalized = normalizedAgreement(addressCandidates, bestAddress);
            const addressHigh = Boolean(bestAddress &&
                isLikelyAddress(bestAddress.value) &&
                bestAddress.confidence >= 55 &&
                !addressHasNoise(bestAddress.value) &&
                (addressExact || addressNormalized) &&
                !/[,;:\-]\s*$/.test(bestAddress.value));

            setFieldStatus(
                '[name="CitizenIdVerification.FullNameOnDocument"]',
                nameHigh ? 'high' : 'review',
                nameHigh
                    ? 'Họ tên đã được OCR riêng và cho kết quả ổn định.'
                    : 'Họ tên đã nhận diện nhưng có thể mất dấu hoặc còn sai ký tự. Hãy đối chiếu CCCD.'
            );
            setFieldStatus(
                '[name="CitizenIdVerification.PermanentAddress"]',
                addressHigh ? 'high' : 'review',
                addressHigh
                    ? 'Nơi cư trú đã được OCR riêng và cho kết quả ổn định.'
                    : 'Nơi cư trú có thể thiếu đầu/cuối dòng hoặc còn ký tự sai. Hãy đối chiếu CCCD.'
            );

            summarize(panel, { high: nameHigh }, { high: addressHigh });
        } catch {
            setFieldStatus(
                '[name="CitizenIdVerification.FullNameOnDocument"]',
                'review',
                'Không đọc lại được vùng họ tên. Hãy đối chiếu trực tiếp với CCCD.'
            );
            setFieldStatus(
                '[name="CitizenIdVerification.PermanentAddress"]',
                'review',
                'Không đọc lại được vùng nơi cư trú. Hãy đối chiếu trực tiếp với CCCD.'
            );
            summarize(panel, { high: false }, { high: false });
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
        if (!panel || !state || state.dataset.cccdRefinementObserved === 'true') return;
        state.dataset.cccdRefinementObserved = 'true';

        document.addEventListener('click', event => {
            if (event.target.closest('[data-ekyc-ocr]')) lastRunKey = '';
        }, true);

        const evaluate = () => {
            const text = state.textContent || '';
            if (!/(?:OCR cục bộ đọc hợp lệ|Đã đọc lại vùng họ tên\/nơi cư trú)/i.test(text)) return;
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
