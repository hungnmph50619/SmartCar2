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

    const exactKey = value => (value || '')
        .normalize('NFC')
        .toUpperCase()
        .replace(/\s+/g, ' ')
        .trim();

    const fingerprint = file => file
        ? `${file.name}|${file.size}|${file.lastModified}|${file.type}`
        : '';

    const vietnameseMarkCount = value => {
        const text = value || '';
        const combining = (text.normalize('NFD').match(/[\u0300-\u036f]/g) || []).length;
        const dStroke = (text.match(/[Đđ]/g) || []).length;
        return combining + dStroke;
    };

    const hasVietnameseMarks = value => vietnameseMarkCount(value) > 0;

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

    const upgradeFormLayout = () => {
        const address = document.querySelector('[name="CitizenIdVerification.PermanentAddress"]');
        if (!address) return;

        const addressColumn = address.closest('[class*="col-md-"]');
        if (addressColumn) {
            [...addressColumn.classList]
                .filter(name => /^col-md-\d+$/.test(name))
                .forEach(name => addressColumn.classList.remove(name));
            addressColumn.classList.add('col-12');
        }

        if (address.tagName === 'TEXTAREA') return;

        const textarea = document.createElement('textarea');
        [...address.attributes].forEach(attribute => {
            if (attribute.name !== 'type') textarea.setAttribute(attribute.name, attribute.value);
        });
        textarea.rows = 2;
        textarea.value = address.value || '';
        textarea.classList.add('form-control');
        textarea.style.resize = 'vertical';
        textarea.style.minHeight = '76px';
        address.replaceWith(textarea);

        try {
            window.jQuery?.validator?.unobtrusive?.parseElement?.(textarea, true);
        } catch {
            // Native/server validation vẫn hoạt động nếu unobtrusive validation chưa sẵn sàng.
        }
    };

    const cropCanvas = (source, region, maxLong = 1800) => {
        const x = Math.max(0, Math.round(source.width * region.x));
        const y = Math.max(0, Math.round(source.height * region.y));
        const width = Math.min(source.width - x, Math.max(1, Math.round(source.width * region.width)));
        const height = Math.min(source.height - y, Math.max(1, Math.round(source.height * region.height)));
        const scale = Math.min(3.4, maxLong / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(
            source, x, y, width, height, 0, 0, canvas.width, canvas.height
        );
        return canvas;
    };

    const makeVariant = (source, mode = 'contrast') => {
        if (mode === 'original') return source;

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
            const value = mode === 'binary'
                ? (gray >= threshold ? 255 : 0)
                : Math.max(0, Math.min(255, (gray - 128) * 1.45 + 128));
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
        return tokens.join(' ').trim();
    };

    const isLikelyName = value => {
        const cleaned = cleanName(value);
        if (!cleaned || /\d/.test(cleaned)) return false;
        const normalized = normalize(cleaned);
        if ([
            'HO VA TEN', 'FULL NAME', 'NGAY SINH', 'DATE OF BIRTH', 'GIOI TINH', 'SEX',
            'QUOC TICH', 'NATIONALITY', 'QUE QUAN', 'PLACE OF ORIGIN', 'NOI THUONG TRU',
            'PLACE OF RESIDENCE', 'CAN CUOC', 'CITIZEN'
        ].some(label => normalized.includes(label))) return false;
        const words = cleaned.split(/\s+/).filter(Boolean);
        if (words.length < 2 || words.length > 7) return false;
        const letters = (cleaned.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length;
        return letters / Math.max(1, cleaned.length) >= 0.72;
    };

    const nameScore = (value, confidence = 0, mrzName = '') => {
        const cleaned = cleanName(value);
        if (!isLikelyName(cleaned)) return -1000;
        let score = Math.min(45, cleaned.length);
        score += Math.min(28, confidence * 0.28);
        score += Math.min(40, vietnameseMarkCount(cleaned) * 8);
        if (cleaned === cleaned.toUpperCase()) score += 5;
        if (mrzName && normalize(cleaned) === mrzName) score += 75;
        if (cleaned.split(/\s+/).some(repeatedNoiseToken)) score -= 80;
        return score;
    };

    const nameCandidates = (text, confidence, source, mrzName = '') => {
        const lines = splitLines(text);
        const values = [];
        const labelIndex = lines.findIndex(line =>
            line.normalized.includes('HO VA TEN') || line.normalized.includes('FULL NAME'));

        if (labelIndex >= 0) {
            const inline = lines[labelIndex].original
                .replace(/^.*?Họ\s+và\s+tên\s*[:：/-]*/iu, '')
                .replace(/^.*?Full\s+name\s*[:：/-]*/iu, '')
                .trim();
            if (inline) values.push(inline);
            for (let offset = 1; offset <= 2; offset += 1) {
                if (lines[labelIndex + offset]?.original) values.push(lines[labelIndex + offset].original);
            }
        }
        lines.forEach(line => values.push(line.original));

        return [...new Set(values.map(cleanName).filter(isLikelyName))]
            .map(value => ({
                value,
                confidence,
                source,
                score: nameScore(value, confidence, mrzName)
            }));
    };

    const parseMrzName = text => {
        const lines = (text || '')
            .toUpperCase()
            .split(/\r?\n/)
            .map(line => line.replace(/[^A-Z<]/g, '').trim())
            .filter(Boolean);
        const candidate = lines
            .filter(line => line.includes('<') && !line.includes('VNM'))
            .sort((a, b) => b.length - a.length)[0];
        if (!candidate) return '';
        const name = candidate.replace(/<+/g, ' ').replace(/\s+/g, ' ').trim();
        return name.split(' ').filter(Boolean).length >= 2 ? normalize(name) : '';
    };

    const cleanAddress = value => (value || '')
        .replace(/^.*?Nơi\s+(?:thường\s+)?trú\s*[:：/-]*/iu, '')
        .replace(/^.*?Place\s+of\s+residence\s*[:：/-]*/iu, '')
        .replace(/\b(?:Có\s+giá\s+trị\s+đến|Date\s+of\s+expiry)\b.*$/iu, '')
        .replace(/\b[0-3]?\d[\/\-.][01]?\d[\/\-.](?:19|20)\d{2}\b/g, ' ')
        .replace(/[<>|_=~“”"'`]+/g, ' ')
        .replace(/\s*,\s*/g, ', ')
        .replace(/\s+/g, ' ')
        .replace(/[,;:\s/.-]+$/g, '')
        .replace(/^[:\s/.,;\-]+/g, '')
        .trim();

    const addressStop = normalized => [
        'CO GIA TRI', 'DATE OF EXPIRY', 'HET HAN', 'NGAY SINH', 'DATE OF BIRTH',
        'GIOI TINH', 'SEX', 'QUOC TICH', 'NATIONALITY', 'QUE QUAN', 'PLACE OF ORIGIN',
        'CAN CUOC', 'CITIZEN', 'SO / NO', 'HO VA TEN', 'FULL NAME'
    ].some(label => normalized.includes(label));

    const addressHasNoise = value => {
        const raw = value || '';
        const normalized = normalize(raw);
        return /[|<>_=~“”"'`]/.test(raw) ||
            normalized.includes('CO GIA TRI') || normalized.includes('DATE OF EXPIRY') ||
            normalized.includes('IDVNM') || normalized.includes('<<<<');
    };

    const isLikelyAddress = value => {
        const cleaned = cleanAddress(value);
        if (!cleaned || cleaned.length < 8 || cleaned.length > 180) return false;
        if (addressHasNoise(value)) return false;
        const letters = (cleaned.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length;
        if (letters < 6 || letters / Math.max(1, cleaned.length) < 0.50) return false;
        if (cleaned.split(/\s+/).filter(Boolean).length < 2) return false;
        return !/[,;:\-]\s*$/.test(value || '');
    };

    const extractOriginSnippets = text => {
        const lines = splitLines(text);
        const snippets = [];
        const labelIndex = lines.findIndex(line =>
            line.normalized.includes('QUE QUAN') || line.normalized.includes('PLACE OF ORIGIN'));
        if (labelIndex >= 0) {
            for (let offset = 0; offset <= 2; offset += 1) {
                const line = lines[labelIndex + offset];
                if (!line) break;
                if (offset > 0 && (line.normalized.includes('NOI') || line.normalized.includes('RESIDENCE'))) break;
                const cleaned = cleanAddress(line.original
                    .replace(/^.*?Quê\s+quán\s*[:：/-]*/iu, '')
                    .replace(/^.*?Place\s+of\s+origin\s*[:：/-]*/iu, ''));
                if (cleaned.length >= 6) snippets.push(normalize(cleaned));
            }
        }
        return [...new Set(snippets)];
    };

    const overlapsOrigin = (value, originSnippets) => {
        const normalized = normalize(value);
        return originSnippets.some(snippet => snippet.length >= 6 &&
            (normalized.startsWith(snippet) || normalized.includes(snippet)));
    };

    const addressScore = (value, confidence = 0, anchored = false, originSnippets = []) => {
        const cleaned = cleanAddress(value);
        if (!isLikelyAddress(cleaned) || overlapsOrigin(cleaned, originSnippets)) return -1000;
        const words = cleaned.split(/\s+/).filter(Boolean);
        let score = Math.min(28, confidence * 0.28);
        score += anchored ? 36 : 12;
        score += Math.min(18, words.length * 1.8);
        score += Math.min(12, (cleaned.match(/,/g) || []).length * 4);
        if (hasVietnameseMarks(cleaned)) score += 10;
        if (/\b(?:Thị trấn|Thành phố|TP\.?|Phường|Xã|Quận|Huyện|Tỉnh)\b/iu.test(cleaned)) score += 16;
        if (/\b\d{4}\b/.test(cleaned)) score -= 25;
        return score;
    };

    const addressCandidates = (text, confidence, source, allowValueOnly, originSnippets = []) => {
        const lines = splitLines(text);
        const values = [];
        const labelIndex = lines.findIndex(line => {
            const n = line.normalized;
            return n.includes('PLACE OF RESIDENCE') || n.includes('NOI THUONG TRU') ||
                n.includes('NOI CU TRU') || (n.includes('NOI') && n.includes('TRU'));
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
            if (parts.length) values.push({ value: parts.join(', '), anchored: true });
        }

        if (allowValueOnly && labelIndex < 0) {
            const parts = lines
                .filter(line => !addressStop(line.normalized))
                .map(line => cleanAddress(line.original))
                .filter(isLikelyAddress);
            if (parts.length) values.push({ value: parts.join(', '), anchored: false });
            if (parts.length > 1) values.push({ value: parts.slice(-2).join(', '), anchored: false });
        }

        const deduped = new Map();
        values.forEach(item => {
            const cleaned = cleanAddress(item.value);
            if (!isLikelyAddress(cleaned) || overlapsOrigin(cleaned, originSnippets)) return;
            const candidate = {
                value: cleaned,
                confidence,
                source,
                anchored: item.anchored,
                score: addressScore(cleaned, confidence, item.anchored, originSnippets)
            };
            const key = exactKey(cleaned);
            const previous = deduped.get(key);
            if (!previous || candidate.score > previous.score) deduped.set(key, candidate);
        });
        return [...deduped.values()];
    };

    const recognize = async (worker, canvas, psm = 6) => {
        try {
            await worker.setParameters({ preserve_interword_spaces: '1', tessedit_pageseg_mode: String(psm) });
        } catch {
            // OCR vẫn chạy nếu build không hỗ trợ tham số trên.
        }
        const output = await worker.recognize(canvas);
        return {
            text: output?.data?.text || '',
            confidence: Number.isFinite(output?.data?.confidence) ? output.data.confidence : 0
        };
    };

    const best = candidates => [...candidates].sort((a, b) => b.score - a.score)[0] || null;

    const exactAgreementCount = (candidates, selected) => {
        if (!selected) return 0;
        const target = exactKey(selected.value);
        return new Set(candidates
            .filter(candidate => candidate.source !== 'initial' && exactKey(candidate.value) === target)
            .map(candidate => candidate.source)).size;
    };

    const normalizedAgreementCount = (candidates, selected) => {
        if (!selected) return 0;
        const target = normalize(selected.value);
        return new Set(candidates
            .filter(candidate => candidate.source !== 'initial' && normalize(candidate.value) === target)
            .map(candidate => candidate.source)).size;
    };

    const chooseBestName = (candidates, mrzName) => {
        if (!candidates.length) return null;
        const mrzMatches = mrzName ? candidates.filter(candidate => normalize(candidate.value) === mrzName) : [];
        const pool = mrzMatches.length ? mrzMatches : candidates;
        return [...pool].sort((a, b) => {
            if (normalize(a.value) === normalize(b.value)) {
                const markDiff = vietnameseMarkCount(b.value) - vietnameseMarkCount(a.value);
                if (markDiff !== 0) return markDiff;
            }
            return b.score - a.score;
        })[0] || null;
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
        return date.getFullYear() === Number(match[3]) && date.getMonth() === Number(match[2]) - 1 &&
            date.getDate() === Number(match[1]) ? date : null;
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
            isLikelyName(name), /^\d{12}$/.test(documentNumber), Boolean(parseDate(birth)),
            ['Nam', 'Nữ', 'Khác'].includes(gender), Boolean(parseDate(issued)), Boolean(parseDate(expiry)),
            isLikelyAddress(address)
        ];
        const issuedDate = parseDate(issued);
        const expiryDate = parseDate(expiry);
        const high = [
            nameHigh, /^\d{12}$/.test(documentNumber), Boolean(parseDate(birth)),
            ['Nam', 'Nữ', 'Khác'].includes(gender), Boolean(issuedDate),
            Boolean(expiryDate && (!issuedDate || expiryDate > issuedDate)), addressHigh
        ];

        const detectedCount = detected.filter(Boolean).length;
        const highCount = high.filter(Boolean).length;
        const needs = [];
        if (!nameHigh) needs.push('họ tên');
        if (!addressHigh) needs.push('nơi cư trú');

        const state = panel.querySelector('[data-ekyc-ocr-state]');
        if (state) {
            if (needs.length) {
                state.textContent = `⚠ Đã nhận diện ${detectedCount}/7 trường · ${highCount}/7 trường có độ tin cậy cao. Cần đối chiếu: ${needs.join(', ')}.`;
                state.className = 'small text-warning mb-3';
            } else {
                state.textContent = `✓ Đã nhận diện ${detectedCount}/7 trường · 7/7 trường có độ tin cậy cao. Hãy đối chiếu nhanh trước khi tiếp tục.`;
                state.className = 'small text-success mb-3';
            }
        }

        const nextButton = panel.querySelector('[data-ekyc-next-face]');
        if (nextButton) {
            nextButton.textContent = needs.length ? 'Tôi đã kiểm tra, tiếp tục →' : 'Thông tin đúng, tiếp tục →';
        }
    };

    const refine = async panel => {
        if (running || panel.dataset.localTestOcr !== 'on') return;
        const front = document.getElementById('citizen-front-file')?.files?.[0];
        const back = document.getElementById('citizen-back-file')?.files?.[0];
        if (!front || !window.Tesseract?.createWorker || !window.SmartCarDocumentVision?.prepareForOcr) return;

        const key = `${fingerprint(front)}|${fingerprint(back)}|${getValue('[name="CitizenIdVerification.FullNameOnDocument"]')}|${getValue('[name="CitizenIdVerification.PermanentAddress"]')}`;
        if (!key || key === lastRunKey) return;
        running = true;
        lastRunKey = key;

        const state = panel.querySelector('[data-ekyc-ocr-state]');
        if (state) {
            state.textContent = 'Đang kiểm tra riêng họ tên và nơi cư trú...';
            state.className = 'small text-muted mb-3';
        }

        let worker = null;
        try {
            const preparedFront = await window.SmartCarDocumentVision.prepareForOcr(front);
            const preparedBack = back ? await window.SmartCarDocumentVision.prepareForOcr(back) : null;

            const nameBroad = cropCanvas(preparedFront.canvas, { x: 0.25, y: 0.47, width: 0.73, height: 0.18 }, 1700);
            const nameTight = cropCanvas(preparedFront.canvas, { x: 0.27, y: 0.53, width: 0.71, height: 0.12 }, 1700);

            // Quê quán chỉ được OCR nội bộ để loại kết quả trộn; SmartCar không thêm/lưu trường quê quán.
            const originRegion = cropCanvas(preparedFront.canvas, { x: 0.25, y: 0.64, width: 0.73, height: 0.17 }, 1600);
            const residenceBroad = cropCanvas(preparedFront.canvas, { x: 0.24, y: 0.76, width: 0.75, height: 0.23 }, 2000);
            const residenceTight = cropCanvas(preparedFront.canvas, { x: 0.28, y: 0.82, width: 0.70, height: 0.16 }, 1900);
            const mrzRegion = preparedBack
                ? cropCanvas(preparedBack.canvas, { x: 0.05, y: 0.66, width: 0.90, height: 0.32 }, 1900)
                : null;

            worker = await window.Tesseract.createWorker(['vie', 'eng'], 1);

            let mrzName = '';
            if (mrzRegion) {
                const mrz = await recognize(worker, makeVariant(mrzRegion, 'binary'), 6);
                mrzName = parseMrzName(mrz.text);
            }

            const nameA = await recognize(worker, makeVariant(nameBroad, 'original'), 6);
            const nameB = await recognize(worker, makeVariant(nameTight, 'contrast'), 7);
            const nameC = await recognize(worker, makeVariant(nameTight, 'binary'), 7);

            const originOcr = await recognize(worker, makeVariant(originRegion, 'contrast'), 6);
            const originSnippets = extractOriginSnippets(originOcr.text);
            const addressA = await recognize(worker, makeVariant(residenceBroad, 'contrast'), 6);
            const addressB = await recognize(worker, makeVariant(residenceTight, 'original'), 6);
            const addressC = await recognize(worker, makeVariant(residenceTight, 'binary'), 6);

            const currentName = getValue('[name="CitizenIdVerification.FullNameOnDocument"]');
            const currentAddress = getValue('[name="CitizenIdVerification.PermanentAddress"]');

            const names = [
                ...nameCandidates(nameA.text, nameA.confidence, 'broad-original', mrzName),
                ...nameCandidates(nameB.text, nameB.confidence, 'tight-contrast', mrzName),
                ...nameCandidates(nameC.text, nameC.confidence, 'tight-binary', mrzName)
            ];
            if (isLikelyName(currentName)) {
                names.push({ value: cleanName(currentName), confidence: 40, source: 'initial', score: nameScore(currentName, 40, mrzName) });
            }

            const addresses = [
                ...addressCandidates(addressA.text, addressA.confidence, 'residence-label', false, originSnippets),
                ...addressCandidates(addressB.text, addressB.confidence, 'residence-tight-original', true, originSnippets),
                ...addressCandidates(addressC.text, addressC.confidence, 'residence-tight-binary', true, originSnippets)
            ];
            if (isLikelyAddress(currentAddress) && !overlapsOrigin(currentAddress, originSnippets)) {
                addresses.push({
                    value: cleanAddress(currentAddress), confidence: 35, source: 'initial', anchored: false,
                    score: addressScore(currentAddress, 35, false, originSnippets)
                });
            }

            const selectedName = chooseBestName(names, mrzName);
            const selectedAddress = best(addresses);

            if (selectedName?.value) writeValue('[name="CitizenIdVerification.FullNameOnDocument"]', selectedName.value);
            if (selectedAddress?.value) writeValue('[name="CitizenIdVerification.PermanentAddress"]', selectedAddress.value);

            const nameHigh = Boolean(
                selectedName && isLikelyName(selectedName.value) && selectedName.confidence >= 55 &&
                exactAgreementCount(names, selectedName) >= 2 && (!mrzName || normalize(selectedName.value) === mrzName)
            );
            const addressHigh = Boolean(
                selectedAddress && isLikelyAddress(selectedAddress.value) && selectedAddress.confidence >= 55 &&
                !addressHasNoise(selectedAddress.value) && !overlapsOrigin(selectedAddress.value, originSnippets) &&
                exactAgreementCount(addresses, selectedAddress) >= 2
            );

            const nameNormalizedAgreement = normalizedAgreementCount(names, selectedName);
            setFieldStatus(
                '[name="CitizenIdVerification.FullNameOnDocument"]',
                nameHigh,
                nameHigh
                    ? 'Họ tên được OCR nhiều lần và khớp chính xác cả dấu.'
                    : nameNormalizedAgreement >= 2
                        ? 'Các lần OCR nhận cùng tên nhưng chưa thống nhất dấu tiếng Việt. Hãy đối chiếu trực tiếp với CCCD.'
                        : 'Họ tên chưa ổn định giữa các lần OCR. Hãy đối chiếu trực tiếp với CCCD.'
            );
            setFieldStatus(
                '[name="CitizenIdVerification.PermanentAddress"]',
                addressHigh,
                addressHigh
                    ? 'Nơi cư trú được OCR riêng, không trộn với quê quán và kết quả ổn định.'
                    : 'Nơi cư trú chưa đủ ổn định hoặc có thể thiếu đầu/cuối dòng. Hãy đối chiếu trực tiếp với CCCD.'
            );

            summarize(panel, nameHigh, addressHigh);
        } catch {
            setFieldStatus('[name="CitizenIdVerification.FullNameOnDocument"]', false, 'Không đọc lại được vùng họ tên. Hãy đối chiếu trực tiếp với CCCD.');
            setFieldStatus('[name="CitizenIdVerification.PermanentAddress"]', false, 'Không đọc lại được vùng nơi cư trú. Hãy đối chiếu trực tiếp với CCCD.');
            summarize(panel, false, false);
        } finally {
            try { await worker?.terminate(); } catch { /* Không ảnh hưởng luồng chính. */ }
            running = false;
        }
    };

    const install = () => {
        const panel = document.querySelector('[data-ekyc-panel="citizen"]');
        const state = panel?.querySelector('[data-ekyc-ocr-state]');
        if (!panel || !state || state.dataset.cccdFieldLevelObserved === 'true') return;
        state.dataset.cccdFieldLevelObserved = 'true';
        upgradeFormLayout();

        document.addEventListener('click', event => {
            if (event.target.closest('[data-ekyc-ocr]')) lastRunKey = '';
        }, true);

        const evaluate = () => {
            if (!/OCR cục bộ đọc hợp lệ/i.test(state.textContent || '')) return;
            window.setTimeout(() => void refine(panel), 0);
        };
        new MutationObserver(evaluate).observe(state, { childList: true, subtree: true, characterData: true });
        evaluate();
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.setTimeout(install, 0), { once: true });
    } else {
        window.setTimeout(install, 0);
    }
})();