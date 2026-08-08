(() => {
    let workerPromise = null;
    let progressHandler = null;

    const normalize = value => (value || '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/đ/g, 'd')
        .replace(/Đ/g, 'D')
        .toUpperCase()
        .replace(/\s+/g, ' ')
        .trim();

    const splitLines = text => (text || '')
        .split(/\r?\n/)
        .map(line => line.replace(/\s+/g, ' ').trim())
        .filter(Boolean)
        .map(original => ({ original, normalized: normalize(original) }));

    const dateRegex = /\b([0-3]?\d)[\/\-.]([01]?\d)[\/\-.]((?:19|20)\d{2})\b/;

    const parseDate = value => {
        const match = (value || '').match(dateRegex);
        if (!match) return null;
        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const date = new Date(year, month - 1, day);
        if (date.getFullYear() !== year ||
            date.getMonth() !== month - 1 ||
            date.getDate() !== day) return null;
        return {
            value: `${String(day).padStart(2, '0')}/${String(month).padStart(2, '0')}/${year}`,
            date
        };
    };

    const normalizeDate = value => parseDate(value)?.value || null;

    const yymmddToDate = value => {
        if (!/^\d{6}$/.test(value || '')) return null;
        const yy = Number(value.slice(0, 2));
        const mm = Number(value.slice(2, 4));
        const dd = Number(value.slice(4, 6));
        if (mm < 1 || mm > 12 || dd < 1 || dd > 31) return null;
        const currentYY = new Date().getFullYear() % 100;
        const year = yy > currentYY ? 1900 + yy : 2000 + yy;
        return normalizeDate(`${dd}/${mm}/${year}`);
    };

    const yymmddToExpiry = value => {
        if (!/^\d{6}$/.test(value || '')) return null;
        const yy = Number(value.slice(0, 2));
        const mm = Number(value.slice(2, 4));
        const dd = Number(value.slice(4, 6));
        return normalizeDate(`${dd}/${mm}/${2000 + yy}`);
    };

    const mrzCharValue = char => {
        if (char >= '0' && char <= '9') return Number(char);
        if (char >= 'A' && char <= 'Z') return char.charCodeAt(0) - 55;
        return 0;
    };

    const mrzCheckDigit = value => {
        const weights = [7, 3, 1];
        return [...value].reduce(
            (sum, char, index) => sum + mrzCharValue(char) * weights[index % 3],
            0
        ) % 10;
    };

    const normalizeMrzDigits = value => (value || '')
        .toUpperCase()
        .replace(/O/g, '0')
        .replace(/[IL]/g, '1')
        .replace(/Z/g, '2')
        .replace(/S/g, '5')
        .replace(/B/g, '8')
        .replace(/[^0-9]/g, '');

    const parseMrz = backText => {
        const lines = (backText || '')
            .split(/\r?\n/)
            .map(line => normalize(line).replace(/\s/g, ''))
            .filter(Boolean);

        let birthDate = null;
        let expiryDate = null;
        let gender = null;
        let name = null;
        let validBirthCheck = false;
        let validExpiryCheck = false;

        for (const line of lines) {
            const nationalityIndex = line.indexOf('VNM');
            if (nationalityIndex < 0) continue;
            const prefix = line.slice(0, nationalityIndex);
            const genderIndex = Math.max(prefix.lastIndexOf('F'), prefix.lastIndexOf('M'));
            if (genderIndex < 7) continue;

            const left = prefix.slice(Math.max(0, genderIndex - 7), genderIndex);
            const right = prefix.slice(genderIndex + 1);
            if (left.length < 7 || right.length < 7) continue;

            const birthRaw = normalizeMrzDigits(left.slice(-7, -1));
            const birthCheckRaw = normalizeMrzDigits(left.slice(-1));
            const expiryRaw = normalizeMrzDigits(right.slice(0, 6));
            const expiryCheckRaw = normalizeMrzDigits(right.slice(6, 7));
            if (birthRaw.length !== 6 || expiryRaw.length !== 6 ||
                birthCheckRaw.length !== 1 || expiryCheckRaw.length !== 1) continue;

            validBirthCheck = mrzCheckDigit(birthRaw) === Number(birthCheckRaw);
            validExpiryCheck = mrzCheckDigit(expiryRaw) === Number(expiryCheckRaw);

            if (validBirthCheck) birthDate = yymmddToDate(birthRaw);
            if (validExpiryCheck) expiryDate = yymmddToExpiry(expiryRaw);
            if (validBirthCheck || validExpiryCheck) {
                gender = line[genderIndex] === 'F' ? 'Nữ' : 'Nam';
            }
            if (birthDate || expiryDate) break;
        }

        const nameLine = lines
            .filter(line => !/\d/.test(line) && line.includes('<') && /[A-Z]/.test(line))
            .filter(line => !line.includes('VNM'))
            .sort((a, b) => b.length - a.length)[0];

        if (nameLine) {
            const cleaned = nameLine
                .replace(/[^A-Z<]/g, '')
                .replace(/<+/g, ' ')
                .replace(/\s+/g, ' ')
                .trim();
            if (cleaned.split(' ').filter(Boolean).length >= 2) name = cleaned;
        }

        return {
            birthDate,
            expiryDate,
            gender,
            name,
            validBirthCheck,
            validExpiryCheck
        };
    };

    const lineContainsAny = (line, labels) =>
        labels.some(label => line.normalized.includes(normalize(label)));

    const valueAfterColon = original => {
        const indexes = [original.indexOf(':'), original.indexOf('：')].filter(index => index >= 0);
        if (!indexes.length) return '';
        return original.slice(Math.min(...indexes) + 1).trim();
    };

    const stripKnownLabel = (value, labels) => {
        let result = value || '';
        for (const label of labels) {
            const escaped = label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            result = result.replace(new RegExp(`^.*?${escaped}\\s*[:：/-]*\\s*`, 'iu'), '');
        }
        return result.trim();
    };

    const isLikelyLabel = normalized => [
        'HO VA TEN', 'FULL NAME', 'SO / NO', 'SO:', 'NO.:',
        'NGAY SINH', 'DATE OF BIRTH', 'GIOI TINH', 'SEX',
        'NOI CU TRU', 'NOI THUONG TRU', 'PLACE OF RESIDENCE',
        'NGAY CAP', 'DATE OF ISSUE', 'DATE MONTH YEAR',
        'CO GIA TRI DEN', 'DATE OF EXPIRY', 'QUOC TICH', 'NATIONALITY',
        'QUE QUAN', 'PLACE OF ORIGIN', 'CAN CUOC', 'CITIZEN'
    ].some(label => normalized.includes(label));

    const isValidHumanName = value => {
        if (!value) return false;
        const cleaned = value.replace(/\s+/g, ' ').trim();
        if (cleaned.length < 6 || cleaned.length > 60) return false;
        if (/\d/.test(cleaned)) return false;
        if (isLikelyLabel(normalize(cleaned))) return false;
        const words = cleaned.split(/\s+/).filter(Boolean);
        if (words.length < 2 || words.length > 7) return false;
        const letters = (cleaned.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length;
        return letters / cleaned.length >= 0.65;
    };

    const cleanName = value => (value || '')
        .replace(/^[:\s/.-]+/, '')
        .replace(/\s+/g, ' ')
        .trim();

    const tokenOverlap = (a, b) => {
        const aTokens = new Set(normalize(a).split(/[^A-Z]+/).filter(token => token.length > 1));
        const bTokens = new Set(normalize(b).split(/[^A-Z]+/).filter(token => token.length > 1));
        if (!aTokens.size || !bTokens.size) return 0;
        let matched = 0;
        bTokens.forEach(token => {
            if (aTokens.has(token)) matched += 1;
        });
        return matched / bTokens.size;
    };

    const looksLikeHeader = normalized => [
        'CONG HOA', 'CHU NGHIA', 'CAN CUOC', 'CITIZEN', 'SOCIALIST',
        'INDEPENDENCE', 'FREEDOM', 'HAPPINESS', 'QUOC TICH', 'NATIONALITY',
        'NOI THUONG TRU', 'NOI CU TRU', 'PLACE OF RESIDENCE',
        'DATE OF BIRTH', 'NGAY SINH', 'GIOI TINH', 'SEX',
        'SMARTCAR TEST', 'KHONG CO GIA TRI', 'DATE OF EXPIRY'
    ].some(value => normalized.includes(value));

    const extractFullName = (frontLines, mrzName) => {
        const labels = ['Họ và tên', 'Full name'];
        for (let index = 0; index < frontLines.length; index += 1) {
            if (!lineContainsAny(frontLines[index], labels)) continue;

            const inlineCandidates = [
                valueAfterColon(frontLines[index].original),
                stripKnownLabel(frontLines[index].original, labels)
            ];
            for (const candidate of inlineCandidates) {
                const cleaned = cleanName(candidate);
                if (isValidHumanName(cleaned)) return cleaned;
            }

            for (let offset = 1; offset <= 2; offset += 1) {
                const next = frontLines[index + offset];
                if (!next || looksLikeHeader(next.normalized)) continue;
                const cleaned = cleanName(next.original);
                if (isValidHumanName(cleaned)) return cleaned;
            }
        }

        if (mrzName && isValidHumanName(mrzName)) {
            const matched = frontLines
                .filter(line => isValidHumanName(line.original))
                .map(line => ({ line, score: tokenOverlap(line.original, mrzName) }))
                .sort((left, right) => right.score - left.score)[0];
            if (matched?.score >= 0.65) return cleanName(matched.line.original);
            return mrzName;
        }

        const fallback = frontLines
            .filter(line => isValidHumanName(line.original))
            .sort((left, right) => right.original.length - left.original.length)[0];
        return fallback?.original?.trim() || null;
    };

    const dateNearLabels = (lines, labels, maxFollowing = 2) => {
        for (let index = 0; index < lines.length; index += 1) {
            if (!lineContainsAny(lines[index], labels)) continue;
            for (let offset = 0; offset <= maxFollowing; offset += 1) {
                const parsed = normalizeDate(lines[index + offset]?.original || '');
                if (parsed) return parsed;
            }
        }
        return null;
    };

    const allFormattedDates = text => {
        const matches = (text || '').match(
            /\b[0-3]?\d[\/\-.][01]?\d[\/\-.](?:19|20)\d{2}\b/g
        ) || [];
        return [...new Set(matches.map(normalizeDate).filter(Boolean))];
    };

    const extractIssuedDate = (backLines, backText, birthDate, expiryDate) => {
        const labeled = dateNearLabels(backLines, [
            'Ngày cấp', 'Date of issue', 'Issue date',
            'Ngày, tháng, năm', 'Ngày tháng năm', 'Date, month, year'
        ], 2);
        const today = new Date();
        const plausible = value => {
            const parsed = parseDate(value);
            if (!parsed) return false;
            if (parsed.date > today) return false;
            if (parsed.date.getFullYear() < 2010) return false;
            return value !== birthDate && value !== expiryDate;
        };
        if (labeled && plausible(labeled)) return labeled;
        return allFormattedDates(backText).find(plausible) || null;
    };

    const documentNumberFromText = text => {
        const compact = (text || '').replace(/[Oo]/g, '0');
        const labeled = compact.match(
            /(?:S[oố]|No\.?)[^\d]{0,18}(\d(?:[\s.-]?\d){11})/iu
        );
        if (labeled?.[1]) return labeled[1].replace(/\D/g, '');

        const matches = compact.match(/(?<!\d)\d(?:[\s.-]?\d){11}(?!\d)/g) || [];
        for (const candidate of matches) {
            const digits = candidate.replace(/\D/g, '');
            if (digits.length === 12) return digits;
        }
        return null;
    };

    const extractGender = (frontLines, mrzGender) => {
        for (const line of frontLines) {
            if (!lineContainsAny(line, ['Giới tính', 'Sex'])) continue;
            let value = valueAfterColon(line.original);
            if (!value) value = stripKnownLabel(line.original, ['Giới tính', 'Sex']);
            value = value.replace(/\b(Quốc tịch|Nationality)\b.*$/iu, '');
            const normalized = normalize(value);
            if (/(^|\s)(NU|FEMALE|F)(\s|$)/.test(normalized)) return 'Nữ';
            if (/(^|\s)(NAM|MALE|M)(\s|$)/.test(normalized)) return 'Nam';
        }
        return mrzGender || null;
    };

    const cleanAddressFragment = value => (value || '')
        .replace(/\b[0-3]?\d[\/\-.][01]?\d[\/\-.](?:19|20)\d{2}\b/g, '')
        .replace(/[<>]{2,}/g, ' ')
        .replace(/\s+/g, ' ')
        .replace(/^[:\s/.,;-]+|[:\s/.,;-]+$/g, '')
        .trim();

    const isAddressStop = line => {
        const normalized = line?.normalized || '';
        return [
            'CO GIA TRI', 'DATE OF EXPIRY', 'HET HAN', 'NGAY CAP',
            'DATE OF ISSUE', 'QUOC TICH', 'NATIONALITY', 'QUE QUAN',
            'PLACE OF ORIGIN', 'NGAY SINH', 'DATE OF BIRTH',
            'GIOI TINH', 'SEX', 'IDVNM'
        ].some(label => normalized.includes(label));
    };

    const isPlausibleAddress = value => {
        if (!value) return false;
        const normalized = normalize(value);
        if (normalized.length < 5 || normalized.length > 180) return false;
        if (normalized.includes('CO GIA TRI') || normalized.includes('DATE OF EXPIRY')) return false;
        if (normalized.includes('IDVNM') || normalized.includes('<<<<')) return false;
        const letters = (value.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length;
        return letters >= 4;
    };

    const extractAddress = frontLines => {
        const labels = ['Nơi cư trú', 'Nơi thường trú', 'Place of residence', 'Address'];
        for (let index = 0; index < frontLines.length; index += 1) {
            if (!lineContainsAny(frontLines[index], labels)) continue;
            const parts = [];

            const inline = cleanAddressFragment(
                stripKnownLabel(frontLines[index].original, labels)
            );
            if (inline && isPlausibleAddress(inline)) parts.push(inline);

            for (let offset = 1; offset <= 2; offset += 1) {
                const next = frontLines[index + offset];
                if (!next || isAddressStop(next)) break;
                const fragment = cleanAddressFragment(next.original);
                if (!fragment) continue;
                if (isPlausibleAddress(fragment)) parts.push(fragment);
            }

            const result = parts.join(', ').replace(/\s*,\s*/g, ', ').trim();
            if (isPlausibleAddress(result)) return result;
        }
        return null;
    };

    const ageAt = (birthValue, atDate = new Date()) => {
        const parsed = parseDate(birthValue);
        if (!parsed) return null;
        let age = atDate.getFullYear() - parsed.date.getFullYear();
        const beforeBirthday = atDate.getMonth() < parsed.date.getMonth() ||
            (atDate.getMonth() === parsed.date.getMonth() &&
                atDate.getDate() < parsed.date.getDate());
        if (beforeBirthday) age -= 1;
        return age;
    };

    const validateParsed = raw => {
        const result = { ...raw };
        const invalid = [];

        if (!/^\d{12}$/.test(result.documentNumber || '')) {
            result.documentNumber = null;
            invalid.push('số CCCD');
        }
        if (!isValidHumanName(result.fullName)) {
            result.fullName = null;
            invalid.push('họ tên');
        }

        const birth = parseDate(result.dateOfBirth);
        const age = result.dateOfBirth ? ageAt(result.dateOfBirth) : null;
        if (!birth || age === null || age < 18 || age > 100) {
            result.dateOfBirth = null;
            invalid.push('ngày sinh');
        }

        if (!['Nam', 'Nữ', 'Khác'].includes(result.gender || '')) {
            result.gender = null;
            invalid.push('giới tính');
        }

        const issued = parseDate(result.issuedDate);
        if (!issued || issued.date > new Date()) {
            result.issuedDate = null;
            invalid.push('ngày cấp');
        }

        const expiry = parseDate(result.expiryDate);
        if (!expiry) {
            result.expiryDate = null;
            invalid.push('ngày hết hạn');
        }

        if (result.issuedDate && result.expiryDate) {
            const issuedDate = parseDate(result.issuedDate)?.date;
            const expiryDate = parseDate(result.expiryDate)?.date;
            if (issuedDate && expiryDate && expiryDate <= issuedDate) {
                result.expiryDate = null;
                if (!invalid.includes('ngày hết hạn')) invalid.push('ngày hết hạn');
            }
        }

        if (!isPlausibleAddress(result.address)) {
            result.address = null;
            invalid.push('nơi cư trú');
        }

        result.invalid = [...new Set(invalid)];
        result.missing = [];
        if (!result.fullName) result.missing.push('họ tên');
        if (!result.documentNumber) result.missing.push('số CCCD');
        if (!result.dateOfBirth) result.missing.push('ngày sinh');
        if (!result.gender) result.missing.push('giới tính');
        if (!result.issuedDate) result.missing.push('ngày cấp');
        if (!result.expiryDate) result.missing.push('ngày hết hạn');
        if (!result.address) result.missing.push('nơi cư trú');
        result.fieldCount = 7 - result.missing.length;
        return result;
    };

    const parseCitizenId = (frontText, backText, confidence) => {
        const frontLines = splitLines(frontText);
        const backLines = splitLines(backText);
        const mrz = parseMrz(backText);

        const labeledBirth = dateNearLabels(frontLines, [
            'Ngày sinh', 'Date of birth', 'DOB'
        ]);
        const labeledExpiry = dateNearLabels(frontLines, [
            'Có giá trị đến', 'Ngày hết hạn', 'Date of expiry', 'Expiry', 'DOE'
        ]);

        const dateOfBirth = mrz.validBirthCheck && mrz.birthDate
            ? mrz.birthDate
            : labeledBirth;
        const expiryDate = mrz.validExpiryCheck && mrz.expiryDate
            ? mrz.expiryDate
            : labeledExpiry;

        const issuedDate = extractIssuedDate(
            backLines,
            backText,
            dateOfBirth,
            expiryDate
        );

        const raw = {
            documentNumber: documentNumberFromText(frontText),
            fullName: extractFullName(frontLines, mrz.name),
            dateOfBirth,
            gender: extractGender(frontLines, mrz.gender),
            issuedDate,
            expiryDate,
            address: extractAddress(frontLines),
            confidence,
            mrz
        };

        return validateParsed(raw);
    };

    const getWorker = async () => {
        if (!window.Tesseract?.createWorker) {
            throw new Error('Không tải được Tesseract.js. Hãy kiểm tra Internet rồi thử lại.');
        }
        if (!workerPromise) {
            workerPromise = window.Tesseract.createWorker(['vie', 'eng'], 1, {
                logger: message => {
                    if (typeof progressHandler === 'function') progressHandler(message);
                }
            }).then(async worker => {
                try {
                    await worker.setParameters({
                        preserve_interword_spaces: '1'
                    });
                } catch {
                    // Một số build Tesseract không hỗ trợ tham số này; OCR vẫn dùng được.
                }
                return worker;
            }).catch(error => {
                workerPromise = null;
                throw error;
            });
        }
        return workerPromise;
    };

    const prepareImage = async file => {
        const vision = window.SmartCarDocumentVision;
        if (!vision?.prepareForOcr) {
            throw new Error('Bộ tìm vùng giấy tờ chưa sẵn sàng. Hãy tải lại trang rồi thử lại.');
        }

        const extracted = await vision.prepareForOcr(file);
        const source = extracted.canvas;
        const targetLong = 1800;
        const scale = targetLong / Math.max(source.width, source.height);
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(source.width * scale));
        canvas.height = Math.max(1, Math.round(source.height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, 0, canvas.width, canvas.height);

        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = pixels.data;
        const contrast = 1.35;
        for (let index = 0; index < data.length; index += 4) {
            const gray = 0.299 * data[index] +
                0.587 * data[index + 1] +
                0.114 * data[index + 2];
            const adjusted = Math.max(
                0,
                Math.min(255, (gray - 128) * contrast + 128)
            );
            data[index] = adjusted;
            data[index + 1] = adjusted;
            data[index + 2] = adjusted;
        }
        ctx.putImageData(pixels, 0, 0);
        return canvas;
    };

    const cropCanvas = (source, region) => {
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
        const canvas = document.createElement('canvas');
        const scale = Math.min(2.2, 1500 / Math.max(width, height));
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(
            source,
            x, y, width, height,
            0, 0, canvas.width, canvas.height
        );
        return canvas;
    };

    const recognizePrepared = async (prepared, onProgress) => {
        progressHandler = onProgress;
        try {
            const worker = await getWorker();
            const output = await worker.recognize(prepared);
            return {
                text: output?.data?.text || '',
                confidence: Number.isFinite(output?.data?.confidence)
                    ? output.data.confidence
                    : null
            };
        } finally {
            progressHandler = null;
        }
    };

    const averageConfidence = values => {
        const usable = values.filter(Number.isFinite);
        return usable.length
            ? usable.reduce((sum, value) => sum + value, 0) / usable.length
            : null;
    };

    const antiForgeryToken = form =>
        form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    const createDemoSession = async (form, front, back) => {
        const data = new FormData();
        const token = antiForgeryToken(form);
        if (token) data.append('__RequestVerificationToken', token);
        data.append('frontImage', front);
        data.append('backImage', back);
        const response = await fetch('/Ekyc/PreviewCitizenId', {
            method: 'POST',
            credentials: 'same-origin',
            body: data
        });
        let payload = null;
        try {
            payload = await response.json();
        } catch {
            payload = null;
        }
        if (!response.ok) {
            const errors = Array.isArray(payload?.errors)
                ? payload.errors
                : [payload?.message || 'Không thể tạo phiên xác minh test.'];
            throw new Error(errors.join(' '));
        }
        return payload;
    };

    const dispatchValue = input => {
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const writeInput = (selector, value) => {
        const input = document.querySelector(selector);
        if (!input) return;
        input.value = value || '';
        dispatchValue(input);
    };

    const toIsoDate = value => {
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value || '');
        return match ? `${match[3]}-${match[2]}-${match[1]}` : '';
    };

    const writeDate = (displaySelector, hiddenSelector, value) => {
        const display = document.querySelector(displaySelector);
        const hidden = document.querySelector(hiddenSelector);
        if (display) {
            display.value = value || '';
            dispatchValue(display);
        }
        if (hidden) {
            hidden.value = value ? toIsoDate(value) : '';
            dispatchValue(hidden);
        }
    };

    const clearOcrFields = () => {
        writeInput('[name="CitizenIdVerification.FullNameOnDocument"]', '');
        writeInput('[name="CitizenIdVerification.DocumentNumber"]', '');
        writeInput('[name="CitizenIdVerification.Gender"]', '');
        writeInput('[name="CitizenIdVerification.PermanentAddress"]', '');
        writeDate('#citizen-birth-display', '#citizen-birth-value', '');
        writeDate('#citizen-issued-display', '#citizen-issued-value', '');
        writeDate('#citizen-expiry-display', '#citizen-expiry-value', '');
    };

    const applyParsed = parsed => {
        writeInput('[name="CitizenIdVerification.FullNameOnDocument"]', parsed.fullName);
        writeInput('[name="CitizenIdVerification.DocumentNumber"]', parsed.documentNumber);
        writeInput('[name="CitizenIdVerification.Gender"]', parsed.gender);
        writeInput('[name="CitizenIdVerification.PermanentAddress"]', parsed.address);
        writeDate('#citizen-birth-display', '#citizen-birth-value', parsed.dateOfBirth);
        writeDate('#citizen-issued-display', '#citizen-issued-value', parsed.issuedDate);
        writeDate('#citizen-expiry-display', '#citizen-expiry-value', parsed.expiryDate);
    };

    const setPanelMessage = (panel, message, type) => {
        const box = panel.querySelector('[data-ekyc-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const showStepTwo = panel => {
        panel.querySelectorAll('[data-ekyc-step-pane]').forEach(pane => {
            pane.classList.toggle(
                'd-none',
                Number(pane.dataset.ekycStepPane) !== 2
            );
        });
        panel.querySelectorAll('[data-ekyc-step-indicator]').forEach(indicator => {
            const step = Number(indicator.dataset.ekycStepIndicator);
            indicator.classList.toggle('is-active', step === 2);
            indicator.classList.toggle('is-done', step < 2);
        });
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    const patchDesktopTestUi = async () => {
        const panel = document.querySelector('[data-ekyc-panel="citizen"]');
        if (!panel) return;
        panel.dataset.localTestOcr = 'pending';
        try {
            const response = await fetch('/Ekyc/Status', { credentials: 'same-origin' });
            const status = response.ok ? await response.json() : null;
            if (!status?.isDemo) {
                panel.dataset.localTestOcr = 'off';
                return;
            }

            panel.dataset.localTestOcr = 'on';
            const badge = panel.querySelector('[data-ekyc-provider]');
            if (badge) {
                badge.textContent = 'OCR cục bộ · TEST';
                badge.className = 'badge bg-info text-dark';
            }
            const notice = panel.querySelector('[data-ekyc-demo-notice]');
            if (notice) {
                notice.className = 'alert alert-info py-2 small mb-3';
                notice.innerHTML =
                    '<strong>Chế độ test trên máy tính:</strong> SmartCar tự tìm/cắt vùng CCCD rồi OCR cục bộ bằng Tesseract.js. Dữ liệu không được đối soát với cơ sở dữ liệu nhà nước; bước khuôn mặt trong Demo vẫn là mô phỏng.';
            }
            const title = panel.querySelector('.ekyc-wizard-header h4');
            if (title) title.textContent = 'CCCD mô phỏng và webcam';
            const faceHeading = panel.querySelector('[data-ekyc-step-pane="3"] h5');
            if (faceHeading) faceHeading.textContent = 'Xác minh bằng webcam máy tính';
            const cameraStart = panel.querySelector('[data-ekyc-camera-start]');
            if (cameraStart) cameraStart.textContent = '📷 Bật webcam';
            panel.querySelector('[data-ekyc-video-input]')?.removeAttribute('capture');
        } catch {
            panel.dataset.localTestOcr = 'off';
        }
    };

    document.addEventListener('click', async event => {
        const button = event.target.closest('[data-ekyc-ocr]');
        if (!button) return;
        const panel = button.closest('[data-ekyc-panel="citizen"]');
        if (!panel || panel.dataset.localTestOcr !== 'on') return;

        event.preventDefault();
        event.stopImmediatePropagation();

        const form = panel.closest('form');
        const front = document.getElementById('citizen-front-file')?.files?.[0];
        const back = document.getElementById('citizen-back-file')?.files?.[0];
        if (!form || !front || !back) {
            setPanelMessage(panel, 'Vui lòng chọn đủ ảnh CCCD mặt trước và mặt sau.', 'warning');
            return;
        }

        const oldText = button.textContent;
        button.disabled = true;
        clearOcrFields();

        try {
            const session = await createDemoSession(form, front, back);
            const sessionInput = panel.querySelector('[data-ekyc-session]');
            if (sessionInput) sessionInput.value = session.sessionId || '';

            button.textContent = 'Đang cắt/chỉnh mặt trước...';
            const frontPrepared = await prepareImage(front);
            button.textContent = 'Đang OCR mặt trước... 0%';
            let frontOcr = await recognizePrepared(frontPrepared, message => {
                if (message?.status === 'recognizing text' &&
                    Number.isFinite(message.progress)) {
                    button.textContent =
                        `Đang OCR mặt trước... ${Math.round(message.progress * 100)}%`;
                }
            });

            button.textContent = 'Đang cắt/chỉnh mặt sau...';
            const backPrepared = await prepareImage(back);
            button.textContent = 'Đang OCR mặt sau... 0%';
            let backOcr = await recognizePrepared(backPrepared, message => {
                if (message?.status === 'recognizing text' &&
                    Number.isFinite(message.progress)) {
                    button.textContent =
                        `Đang OCR mặt sau... ${Math.round(message.progress * 100)}%`;
                }
            });

            let confidence = averageConfidence([frontOcr.confidence, backOcr.confidence]);
            let parsed = parseCitizenId(frontOcr.text, backOcr.text, confidence);

            if (parsed.fieldCount < 7) {
                if (!parsed.fullName || !parsed.address || !parsed.dateOfBirth ||
                    !parsed.gender || !parsed.expiryDate) {
                    button.textContent = 'Đang đọc chi tiết mặt trước...';
                    const focusedFront = cropCanvas(frontPrepared, {
                        x: 0.20, y: 0.24, width: 0.79, height: 0.74
                    });
                    const extraFront = await recognizePrepared(focusedFront);
                    frontOcr = {
                        text: `${frontOcr.text}\n${extraFront.text}`,
                        confidence: averageConfidence([
                            frontOcr.confidence,
                            extraFront.confidence
                        ])
                    };
                }

                if (!parsed.issuedDate) {
                    button.textContent = 'Đang đọc ngày cấp...';
                    const issueRegion = cropCanvas(backPrepared, {
                        x: 0.00, y: 0.00, width: 0.72, height: 0.42
                    });
                    const extraIssue = await recognizePrepared(issueRegion);
                    backOcr = {
                        text: `${backOcr.text}\n${extraIssue.text}`,
                        confidence: averageConfidence([
                            backOcr.confidence,
                            extraIssue.confidence
                        ])
                    };
                }

                const mrzNow = parseMrz(backOcr.text);
                if (!mrzNow.validBirthCheck || !mrzNow.validExpiryCheck || !mrzNow.name) {
                    button.textContent = 'Đang đối chiếu vùng MRZ...';
                    const mrzRegion = cropCanvas(backPrepared, {
                        x: 0.03, y: 0.58, width: 0.94, height: 0.40
                    });
                    const extraMrz = await recognizePrepared(mrzRegion);
                    backOcr = {
                        text: `${backOcr.text}\n${extraMrz.text}`,
                        confidence: averageConfidence([
                            backOcr.confidence,
                            extraMrz.confidence
                        ])
                    };
                }

                confidence = averageConfidence([
                    frontOcr.confidence,
                    backOcr.confidence
                ]);
                parsed = parseCitizenId(frontOcr.text, backOcr.text, confidence);
            }

            applyParsed(parsed);

            const state = panel.querySelector('[data-ekyc-ocr-state]');
            if (state) {
                const confidenceText = Number.isFinite(parsed.confidence)
                    ? ` · độ tin cậy OCR ${parsed.confidence.toFixed(1)}%`
                    : '';
                const missingText = parsed.missing.length
                    ? ` Cần kiểm tra/nhập lại: ${parsed.missing.join(', ')}.`
                    : ' Đã đọc đủ 7 trường hợp lệ.';
                state.textContent =
                    `✓ OCR cục bộ đọc hợp lệ ${parsed.fieldCount}/7 trường${confidenceText}.${missingText}`;
                state.className =
                    `small ${parsed.fieldCount >= 6 ? 'text-success' : 'text-warning'} mb-3`;
            }

            const mrzValidated = parsed.mrz?.validBirthCheck || parsed.mrz?.validExpiryCheck;
            const mrzText = mrzValidated
                ? ' MRZ hợp lệ đã được dùng để đối chiếu ngày sinh/giới tính/ngày hết hạn.'
                : '';
            setPanelMessage(
                panel,
                parsed.missing.length
                    ? `SmartCar chỉ điền những trường vượt qua kiểm tra hợp lệ; trường nghi ngờ đã để trống.${mrzText}`
                    : `Đã tự cắt giấy tờ, OCR và kiểm tra tính hợp lý của dữ liệu.${mrzText} Hãy đối chiếu lại trước khi tiếp tục.`,
                parsed.missing.length ? 'warning' : 'success'
            );
            showStepTwo(panel);
        } catch (error) {
            clearOcrFields();
            setPanelMessage(
                panel,
                error?.message ||
                    'OCR cục bộ không thành công. Hãy thử ảnh rõ hơn hoặc chụp gần hơn.',
                'danger'
            );
        } finally {
            button.disabled = false;
            button.textContent = oldText || 'Đọc CCCD và tiếp tục';
        }
    }, true);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', patchDesktopTestUi, { once: true });
    } else {
        patchDesktopTestUi();
    }
})();
