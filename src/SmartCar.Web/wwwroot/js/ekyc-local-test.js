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

    const normalizeDate = value => {
        const match = (value || '').match(dateRegex);
        if (!match) return null;
        return `${match[1].padStart(2, '0')}/${match[2].padStart(2, '0')}/${match[3]}`;
    };

    const yymmddToDate = (value, kind) => {
        if (!/^\d{6}$/.test(value || '')) return null;
        const yy = Number(value.slice(0, 2));
        const mm = Number(value.slice(2, 4));
        const dd = Number(value.slice(4, 6));
        if (mm < 1 || mm > 12 || dd < 1 || dd > 31) return null;
        const currentYY = new Date().getFullYear() % 100;
        const year = kind === 'expiry'
            ? 2000 + yy
            : (yy > currentYY ? 1900 + yy : 2000 + yy);
        return `${String(dd).padStart(2, '0')}/${String(mm).padStart(2, '0')}/${year}`;
    };

    const lineContainsAny = (line, labels) => labels.some(label => line.normalized.includes(normalize(label)));

    const valueAfterColon = original => {
        const index = Math.max(original.indexOf(':'), original.indexOf('：'));
        if (index < 0) return '';
        return original.slice(index + 1).trim();
    };

    const cleanInlineValue = value => (value || '')
        .replace(/\b(Quốc tịch|Nationality|Dân tộc|Ethnicity|Có giá trị đến|Date of expiry|Ngày sinh|Date of birth|Giới tính|Sex)\b.*$/iu, '')
        .replace(/^[:\s/.-]+/, '')
        .replace(/\s+/g, ' ')
        .trim();

    const dateNearLabels = (lines, labels, maxFollowing = 2) => {
        for (let i = 0; i < lines.length; i += 1) {
            if (!lineContainsAny(lines[i], labels)) continue;
            for (let offset = 0; offset <= maxFollowing; offset += 1) {
                const candidate = lines[i + offset]?.original || '';
                const parsed = normalizeDate(candidate);
                if (parsed) return parsed;
            }
        }
        return null;
    };

    const firstFormattedDate = text => normalizeDate(text);

    const documentNumberFromText = text => {
        const compact = (text || '').replace(/[Oo]/g, '0');
        const labeled = compact.match(/(?:S[oố]|No\.?)[^\d]{0,15}(\d(?:[\s.-]?\d){11})/iu);
        if (labeled?.[1]) return labeled[1].replace(/\D/g, '');

        const matches = compact.match(/(?<!\d)\d(?:[\s.-]?\d){11}(?!\d)/g) || [];
        for (const candidate of matches) {
            const digits = candidate.replace(/\D/g, '');
            if (digits.length === 12) return digits;
        }
        return null;
    };

    const parseMrz = backText => {
        const lines = (backText || '')
            .split(/\r?\n/)
            .map(line => normalize(line).replace(/\s/g, ''))
            .filter(Boolean);

        let birthDate = null;
        let expiryDate = null;
        let gender = null;
        let name = null;

        for (const line of lines) {
            const data = line.match(/(\d{6})\d([MF<])(\d{6})\d/);
            if (data) {
                birthDate = yymmddToDate(data[1], 'birth');
                expiryDate = yymmddToDate(data[3], 'expiry');
                if (data[2] === 'F') gender = 'Nữ';
                if (data[2] === 'M') gender = 'Nam';
            }
        }

        const nameLine = lines
            .filter(line => !/\d/.test(line) && line.includes('<<') && /[A-Z]/.test(line))
            .sort((a, b) => b.length - a.length)[0];
        if (nameLine) {
            name = nameLine
                .replace(/[^A-Z<]/g, '')
                .replace(/<+/g, ' ')
                .replace(/\s+/g, ' ')
                .trim();
        }

        return { birthDate, expiryDate, gender, name };
    };

    const tokenOverlap = (a, b) => {
        const aTokens = new Set(normalize(a).split(/[^A-Z]+/).filter(token => token.length > 1));
        const bTokens = new Set(normalize(b).split(/[^A-Z]+/).filter(token => token.length > 1));
        if (!aTokens.size || !bTokens.size) return 0;
        let matched = 0;
        bTokens.forEach(token => { if (aTokens.has(token)) matched += 1; });
        return matched / bTokens.size;
    };

    const looksLikeHeader = normalized => [
        'CONG HOA', 'CHU NGHIA', 'CAN CUOC', 'CITIZEN', 'SOCIALIST', 'INDEPENDENCE',
        'FREEDOM', 'HAPPINESS', 'QUOC TICH', 'NATIONALITY', 'NOI THUONG TRU',
        'NOI CU TRU', 'PLACE OF RESIDENCE', 'DATE OF BIRTH', 'NGAY SINH', 'GIOI TINH',
        'SEX', 'SMARTCAR TEST', 'KHONG CO GIA TRI'
    ].some(value => normalized.includes(value));

    const extractFullName = (frontLines, mrzName) => {
        for (let i = 0; i < frontLines.length; i += 1) {
            if (!lineContainsAny(frontLines[i], ['Họ và tên', 'Full name'])) continue;
            const inline = cleanInlineValue(valueAfterColon(frontLines[i].original));
            if (inline && !looksLikeHeader(normalize(inline))) return inline;
            for (let offset = 1; offset <= 2; offset += 1) {
                const next = frontLines[i + offset];
                if (!next || looksLikeHeader(next.normalized) || /\d/.test(next.original)) continue;
                if (next.original.length >= 5) return next.original.trim();
            }
        }

        if (mrzName) {
            const matched = frontLines
                .filter(line => !looksLikeHeader(line.normalized) && !/\d/.test(line.original))
                .map(line => ({ line, score: tokenOverlap(line.original, mrzName) }))
                .sort((a, b) => b.score - a.score)[0];
            if (matched?.score >= 0.75) return matched.line.original.trim();
            return mrzName;
        }

        const candidate = frontLines.find(line => {
            if (looksLikeHeader(line.normalized) || /\d/.test(line.original)) return false;
            const words = line.original.split(/\s+/).filter(Boolean);
            return words.length >= 2 && words.length <= 6 && line.original.length >= 8 && line.original.length <= 45;
        });
        return candidate?.original?.trim() || null;
    };

    const extractGender = (frontLines, mrzGender) => {
        for (const line of frontLines) {
            if (!lineContainsAny(line, ['Giới tính', 'Sex'])) continue;
            let value = valueAfterColon(line.original);
            if (!value) value = line.original;
            value = value.replace(/\b(Quốc tịch|Nationality)\b.*$/iu, '');
            const normalized = normalize(value);
            if (/(^|\s)(NU|FEMALE|F)(\s|$)/.test(normalized)) return 'Nữ';
            if (/(^|\s)(NAM|MALE|M)(\s|$)/.test(normalized)) return 'Nam';
        }
        return mrzGender || null;
    };

    const extractAddress = frontLines => {
        const labels = ['Nơi cư trú', 'Nơi thường trú', 'Place of residence', 'Address'];
        const stopLabels = [
            'Có giá trị đến', 'Date of expiry', 'Ngày sinh', 'Date of birth', 'Giới tính', 'Sex',
            'Quốc tịch', 'Nationality', 'Quê quán', 'Place of origin'
        ];

        for (let i = 0; i < frontLines.length; i += 1) {
            if (!lineContainsAny(frontLines[i], labels)) continue;
            const parts = [];
            let inline = valueAfterColon(frontLines[i].original);
            if (inline) {
                inline = inline.replace(/\b(Có giá trị đến|Date of expiry)\b.*$/iu, '').trim();
                if (inline) parts.push(inline);
            }

            for (let offset = 1; offset <= 3; offset += 1) {
                const next = frontLines[i + offset];
                if (!next) break;
                if (lineContainsAny(next, stopLabels)) break;
                if (next.original.length < 3) continue;
                parts.push(next.original.trim());
            }

            const result = parts
                .join(', ')
                .replace(/\s*,\s*/g, ', ')
                .replace(/(?:,\s*){2,}/g, ', ')
                .replace(/\s+/g, ' ')
                .trim();
            if (result) return result;
        }
        return null;
    };

    const parseCitizenId = (frontText, backText, confidence) => {
        const frontLines = splitLines(frontText);
        const backLines = splitLines(backText);
        const mrz = parseMrz(backText);

        const dateOfBirth = dateNearLabels(frontLines, ['Ngày sinh', 'Date of birth', 'DOB']) || mrz.birthDate;
        const expiryDate = dateNearLabels(frontLines, ['Có giá trị đến', 'Ngày hết hạn', 'Date of expiry', 'Expiry', 'DOE']) || mrz.expiryDate;
        const issuedDate = dateNearLabels(backLines, [
            'Ngày cấp', 'Date of issue', 'Issue date', 'Ngày, tháng, năm', 'Ngày tháng năm', 'Date, month, year'
        ], 2) || firstFormattedDate(backText);

        const result = {
            documentNumber: documentNumberFromText(frontText),
            fullName: extractFullName(frontLines, mrz.name),
            dateOfBirth,
            gender: extractGender(frontLines, mrz.gender),
            issuedDate,
            expiryDate,
            address: extractAddress(frontLines),
            confidence
        };

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

    const getWorker = async () => {
        if (!window.Tesseract?.createWorker) {
            throw new Error('Không tải được Tesseract.js. Hãy kiểm tra Internet rồi thử lại.');
        }
        if (!workerPromise) {
            workerPromise = window.Tesseract.createWorker(['vie', 'eng'], 1, {
                logger: message => {
                    if (typeof progressHandler === 'function') progressHandler(message);
                }
            }).catch(error => {
                workerPromise = null;
                throw error;
            });
        }
        return workerPromise;
    };

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

    const prepareImage = async file => {
        const image = await loadBitmap(file);
        const width = image.width || image.naturalWidth;
        const height = image.height || image.naturalHeight;
        const maxSide = 2200;
        const scale = Math.min(2, maxSide / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(image, 0, 0, canvas.width, canvas.height);

        const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const data = pixels.data;
        const contrast = 1.45;
        for (let i = 0; i < data.length; i += 4) {
            const gray = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
            const adjusted = Math.max(0, Math.min(255, (gray - 128) * contrast + 128));
            data[i] = adjusted;
            data[i + 1] = adjusted;
            data[i + 2] = adjusted;
        }
        ctx.putImageData(pixels, 0, 0);
        image.close?.();
        return canvas;
    };

    const recognize = async (file, onProgress) => {
        progressHandler = onProgress;
        try {
            const worker = await getWorker();
            const prepared = await prepareImage(file);
            const output = await worker.recognize(prepared);
            return {
                text: output?.data?.text || '',
                confidence: Number.isFinite(output?.data?.confidence) ? output.data.confidence : null
            };
        } finally {
            progressHandler = null;
        }
    };

    const averageConfidence = values => {
        const usable = values.filter(Number.isFinite);
        return usable.length ? usable.reduce((sum, value) => sum + value, 0) / usable.length : null;
    };

    const antiForgeryToken = form => form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

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
        try { payload = await response.json(); } catch { payload = null; }
        if (!response.ok) {
            const errors = Array.isArray(payload?.errors) ? payload.errors : [payload?.message || 'Không thể tạo phiên xác minh test.'];
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
            pane.classList.toggle('d-none', Number(pane.dataset.ekycStepPane) !== 2);
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
                notice.innerHTML = '<strong>Chế độ test trên máy tính:</strong> OCR chạy cục bộ bằng Tesseract.js. Dữ liệu không được đối soát với cơ sở dữ liệu nhà nước; bước khuôn mặt trong Demo vẫn là mô phỏng.';
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
        button.textContent = 'Đang chuẩn bị OCR...';

        try {
            const session = await createDemoSession(form, front, back);
            const sessionInput = panel.querySelector('[data-ekyc-session]');
            if (sessionInput) sessionInput.value = session.sessionId || '';

            button.textContent = 'Đang xử lý mặt trước...';
            const frontOcr = await recognize(front, message => {
                if (message?.status === 'recognizing text' && Number.isFinite(message.progress)) {
                    button.textContent = `Đang OCR mặt trước... ${Math.round(message.progress * 100)}%`;
                }
            });

            button.textContent = 'Đang xử lý mặt sau...';
            const backOcr = await recognize(back, message => {
                if (message?.status === 'recognizing text' && Number.isFinite(message.progress)) {
                    button.textContent = `Đang OCR mặt sau... ${Math.round(message.progress * 100)}%`;
                }
            });

            const confidence = averageConfidence([frontOcr.confidence, backOcr.confidence]);
            const parsed = parseCitizenId(frontOcr.text, backOcr.text, confidence);
            applyParsed(parsed);

            const state = panel.querySelector('[data-ekyc-ocr-state]');
            if (state) {
                const confidenceText = Number.isFinite(parsed.confidence)
                    ? ` · độ tin cậy OCR ${parsed.confidence.toFixed(1)}%`
                    : '';
                const missingText = parsed.missing.length
                    ? ` Thiếu: ${parsed.missing.join(', ')}.`
                    : ' Đã đọc đủ các trường chính.';
                state.textContent = `✓ OCR cục bộ đọc được ${parsed.fieldCount}/7 trường${confidenceText}.${missingText}`;
                state.className = `small ${parsed.fieldCount >= 6 ? 'text-success' : 'text-warning'} mb-3`;
            }

            setPanelMessage(
                panel,
                parsed.missing.length
                    ? 'Các trường OCR không đọc được đã được để trống, không giữ dữ liệu từ lần test trước. Hãy kiểm tra và nhập bổ sung nếu cần.'
                    : 'Đã đọc dữ liệu từ ảnh. Hãy đối chiếu lại trước khi tiếp tục.',
                parsed.missing.length ? 'warning' : 'success');
            showStepTwo(panel);
        } catch (error) {
            clearOcrFields();
            setPanelMessage(panel, error?.message || 'OCR cục bộ không thành công. Hãy thử ảnh rõ hơn hoặc nhập thủ công.', 'danger');
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
