(() => {
    let running = false;
    let lastFingerprint = '';

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
        if ((input.value || '').trim() === value.trim()) return false;
        input.value = value.trim();
        dispatchValue(input);
        return true;
    };

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
        const scale = Math.min(3, maxLong / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, x, y, width, height, 0, 0, canvas.width, canvas.height);

        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = pixels.data;
        const contrast = 1.45;
        for (let index = 0; index < data.length; index += 4) {
            const gray = 0.299 * data[index] + 0.587 * data[index + 1] + 0.114 * data[index + 2];
            const adjusted = Math.max(0, Math.min(255, (gray - 128) * contrast + 128));
            data[index] = adjusted;
            data[index + 1] = adjusted;
            data[index + 2] = adjusted;
        }
        ctx.putImageData(pixels, 0, 0);
        return canvas;
    };

    const repeatedNoiseToken = token => {
        const compact = normalize(token).replace(/[^A-Z]/g, '');
        if (compact.length < 3 || compact.length > 6) return false;
        return new Set(compact).size <= 1 || /^(SSS|III|LLL|XXX|VVV|OOO|CCC|EEE)$/.test(compact);
    };

    const cleanName = value => {
        let text = (value || '')
            .replace(/\b(Họ\s+và\s+tên|Full\s+name)\b\s*[:：/-]*/giu, ' ')
            .replace(/[^A-Za-zÀ-ỹĐđ'\-\s]/g, ' ')
            .replace(/\s+/g, ' ')
            .trim();
        const tokens = text.split(/\s+/).filter(Boolean);
        while (tokens.length > 2 && repeatedNoiseToken(tokens[tokens.length - 1])) tokens.pop();
        text = tokens.join(' ').trim();
        return text;
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

    const extractName = lines => {
        const labelIndex = lines.findIndex(line =>
            line.normalized.includes('HO VA TEN') || line.normalized.includes('FULL NAME'));

        if (labelIndex >= 0) {
            const current = lines[labelIndex].original;
            const inline = current
                .replace(/^.*?Họ\s+và\s+tên\s*[:：/-]*/iu, '')
                .replace(/^.*?Full\s+name\s*[:：/-]*/iu, '')
                .trim();
            if (isLikelyName(inline)) return cleanName(inline);

            for (let offset = 1; offset <= 2; offset += 1) {
                const candidate = lines[labelIndex + offset]?.original || '';
                if (isLikelyName(candidate)) return cleanName(candidate);
            }
        }

        const candidates = lines
            .map(line => cleanName(line.original))
            .filter(isLikelyName)
            .map(value => ({
                value,
                score: value.length + (value === value.toUpperCase() ? 12 : 0)
            }))
            .sort((left, right) => right.score - left.score);
        return candidates[0]?.value || null;
    };

    const addressStop = normalized => [
        'CO GIA TRI', 'DATE OF EXPIRY', 'HET HAN', 'NGAY SINH',
        'DATE OF BIRTH', 'GIOI TINH', 'SEX', 'QUOC TICH',
        'NATIONALITY', 'QUE QUAN', 'PLACE OF ORIGIN', 'CAN CUOC',
        'CITIZEN', 'SO / NO', 'HO VA TEN', 'FULL NAME'
    ].some(label => normalized.includes(label));

    const cleanAddress = value => (value || '')
        .replace(/^.*?Nơi\s+(?:thường\s+)?trú\s*[:：/-]*/iu, '')
        .replace(/^.*?Place\s+of\s+residence\s*[:：/-]*/iu, '')
        .replace(/\b(?:Có\s+giá\s+trị\s+đến|Date\s+of\s+expiry)\b.*$/iu, '')
        .replace(/[<>]{2,}/g, ' ')
        .replace(/\s+/g, ' ')
        .replace(/^[:\s/.,;\-]+|[:\s/.,;\-]+$/g, '')
        .trim();

    const isLikelyAddress = value => {
        const cleaned = cleanAddress(value);
        if (!cleaned || cleaned.length < 5 || cleaned.length > 180) return false;
        const normalized = normalize(cleaned);
        if ([
            'CO GIA TRI', 'DATE OF EXPIRY', 'IDVNM', '<<<<',
            'HO VA TEN', 'FULL NAME', 'NGAY SINH', 'DATE OF BIRTH'
        ].some(label => normalized.includes(label))) return false;
        const letters = (cleaned.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length;
        return letters >= 4 && letters / Math.max(1, cleaned.length) >= 0.45;
    };

    const extractAddress = lines => {
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
            const result = parts.join(', ').replace(/\s*,\s*/g, ', ').trim();
            if (isLikelyAddress(result)) return result;
        }

        const fallback = lines
            .filter(line => !addressStop(line.normalized))
            .map(line => cleanAddress(line.original))
            .filter(isLikelyAddress)
            .slice(-2)
            .join(', ')
            .trim();
        return isLikelyAddress(fallback) ? fallback : null;
    };

    const currentLooksWrong = () => {
        const name = document.querySelector('[name="CitizenIdVerification.FullNameOnDocument"]')?.value || '';
        const address = document.querySelector('[name="CitizenIdVerification.PermanentAddress"]')?.value || '';
        const nameTokens = name.split(/\s+/).filter(Boolean);
        const nameWrong = !isLikelyName(name) ||
            (nameTokens.length > 2 && repeatedNoiseToken(nameTokens[nameTokens.length - 1]));
        const normalizedAddress = normalize(address);
        const addressWrong = !isLikelyAddress(address) ||
            /\b(?:19|20)\d{2}\b/.test(address) ||
            normalizedAddress.includes('CO GIA') ||
            normalizedAddress.includes('DATE OF EXPIRY') ||
            normalizedAddress.includes('IDVNM');
        return nameWrong || addressWrong;
    };

    const refine = async panel => {
        if (running || panel.dataset.localTestOcr !== 'on') return;
        const front = document.getElementById('citizen-front-file')?.files?.[0];
        if (!front || !window.Tesseract?.createWorker || !window.SmartCarDocumentVision?.prepareForOcr) return;

        const key = fingerprint(front);
        if (!key || key === lastFingerprint) return;
        if (!currentLooksWrong()) {
            lastFingerprint = key;
            return;
        }

        running = true;
        lastFingerprint = key;
        const state = panel.querySelector('[data-ekyc-ocr-state]');
        const originalState = state?.textContent || '';
        if (state) state.textContent = `${originalState} Đang đọc lại họ tên và nơi cư trú...`;

        let worker = null;
        try {
            const prepared = await window.SmartCarDocumentVision.prepareForOcr(front);
            const detail = cropCanvas(prepared.canvas, {
                x: 0.27,
                y: 0.27,
                width: 0.72,
                height: 0.70
            });

            worker = await window.Tesseract.createWorker(['vie', 'eng'], 1);
            try {
                await worker.setParameters({
                    preserve_interword_spaces: '1',
                    tessedit_pageseg_mode: '6'
                });
            } catch {
                // OCR vẫn chạy nếu build không hỗ trợ tham số trên.
            }
            const output = await worker.recognize(detail);
            const lines = splitLines(output?.data?.text || '');
            const refinedName = extractName(lines);
            const refinedAddress = extractAddress(lines);

            let changed = false;
            if (refinedName && isLikelyName(refinedName)) {
                changed = writeValue(
                    '[name="CitizenIdVerification.FullNameOnDocument"]',
                    refinedName
                ) || changed;
            } else {
                const currentName = document.querySelector('[name="CitizenIdVerification.FullNameOnDocument"]')?.value || '';
                const cleanedCurrent = cleanName(currentName);
                if (isLikelyName(cleanedCurrent)) {
                    changed = writeValue(
                        '[name="CitizenIdVerification.FullNameOnDocument"]',
                        cleanedCurrent
                    ) || changed;
                }
            }

            if (refinedAddress && isLikelyAddress(refinedAddress)) {
                changed = writeValue(
                    '[name="CitizenIdVerification.PermanentAddress"]',
                    refinedAddress
                ) || changed;
            }

            if (state) {
                state.textContent = changed
                    ? `${originalState} Đã đọc lại vùng họ tên/nơi cư trú để giảm lỗi OCR.`
                    : originalState;
            }
        } catch {
            if (state) state.textContent = originalState;
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

        const evaluate = () => {
            const text = state.textContent || '';
            if (!/OCR cục bộ đọc hợp lệ/i.test(text)) return;
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
