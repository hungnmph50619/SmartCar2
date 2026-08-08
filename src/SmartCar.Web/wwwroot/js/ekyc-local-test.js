(() => {
    const TEST_MODE_BADGE = 'OCR cục bộ · TEST';
    let tesseractWorkerPromise = null;
    let currentProgress = null;

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

    const isLikelyLabel = normalized => [
        'HO VA TEN', 'FULL NAME', 'SO / NO', 'SO:', 'NO.:', 'NGAY SINH', 'DATE OF BIRTH',
        'GIOI TINH', 'SEX', 'NOI CU TRU', 'PLACE OF RESIDENCE', 'ADDRESS', 'NGAY CAP',
        'DATE OF ISSUE', 'ISSUE DATE', 'CO GIA TRI DEN', 'DATE OF EXPIRY', 'EXPIRY', 'HET HAN',
        'QUOC TICH', 'NATIONALITY', 'SMARTCAR TEST DOCUMENT', 'CAN CUOC CONG DAN MO PHONG'
    ].some(label => normalized.includes(label));

    const valueFromLabel = (lines, labels, maxFollowingLines = 1) => {
        const normalizedLabels = labels.map(normalize);
        for (let index = 0; index < lines.length; index += 1) {
            const line = lines[index];
            if (!normalizedLabels.some(label => line.normalized.includes(label))) continue;

            const colonMatch = line.original.match(/[:：]\s*(.+)$/);
            if (colonMatch?.[1]?.trim()) return colonMatch[1].trim();

            for (let offset = 1; offset <= maxFollowingLines; offset += 1) {
                const next = lines[index + offset];
                if (!next || isLikelyLabel(next.normalized)) break;
                if (next.original.length >= 2) return next.original.trim();
            }
        }
        return null;
    };

    const normalizeDate = value => {
        if (!value) return null;
        const match = value.match(/\b([0-3]?\d)[\/\-.]([01]?\d)[\/\-.]((?:19|20)\d{2})\b/);
        if (!match) return null;
        return `${match[1].padStart(2, '0')}/${match[2].padStart(2, '0')}/${match[3]}`;
    };

    const dateFromLabels = (lines, labels) => {
        const value = valueFromLabel(lines, labels, 2);
        return normalizeDate(value);
    };

    const genderFromText = lines => {
        const value = valueFromLabel(lines, ['Giới tính', 'Sex'], 1);
        const normalized = normalize(value);
        if (normalized.includes('NU') || normalized.includes('FEMALE') || normalized === 'F') return 'Nữ';
        if (normalized.includes('NAM') || normalized.includes('MALE') || normalized === 'M') return 'Nam';
        return null;
    };

    const documentNumberFromText = text => {
        const compact = (text || '').replace(/[Oo]/g, '0');
        const matches = compact.match(/(?<!\d)\d(?:[\s.-]?\d){11}(?!\d)/g) || [];
        for (const candidate of matches) {
            const digits = candidate.replace(/\D/g, '');
            if (digits.length === 12) return digits;
        }
        return null;
    };

    const addressFromLines = lines => {
        const direct = valueFromLabel(lines, ['Nơi cư trú', 'Place of residence', 'Address'], 2);
        if (direct) return direct;
        return null;
    };

    const cleanupName = value => {
        if (!value) return null;
        return value
            .replace(/^[:\s/-]+/, '')
            .replace(/\s+/g, ' ')
            .trim();
    };

    const parseCitizenId = (frontText, backText, confidence) => {
        const frontLines = splitLines(frontText);
        const backLines = splitLines(backText);
        const allLines = [...frontLines, ...backLines];
        const combinedText = `${frontText || ''}\n${backText || ''}`;

        const fullName = cleanupName(
            valueFromLabel(frontLines, ['Họ và tên', 'Full name'], 2) ||
            valueFromLabel(allLines, ['Họ và tên', 'Full name'], 2));

        const result = {
            documentNumber: documentNumberFromText(frontText) || documentNumberFromText(combinedText),
            fullName,
            dateOfBirth: dateFromLabels(frontLines, ['Ngày sinh', 'Date of birth', 'DOB']) ||
                dateFromLabels(allLines, ['Ngày sinh', 'Date of birth', 'DOB']),
            gender: genderFromText(frontLines) || genderFromText(allLines),
            issuedDate: dateFromLabels(backLines, ['Ngày cấp', 'Date of issue', 'Issue date']) ||
                dateFromLabels(allLines, ['Ngày cấp', 'Date of issue', 'Issue date']),
            expiryDate: dateFromLabels(frontLines, ['Có giá trị đến', 'Ngày hết hạn', 'Date of expiry', 'Expiry', 'DOE']) ||
                dateFromLabels(allLines, ['Có giá trị đến', 'Ngày hết hạn', 'Date of expiry', 'Expiry', 'DOE']),
            address: addressFromLines(frontLines) || addressFromLines(allLines),
            confidence,
            isSmartCarTestDocument: normalize(combinedText).includes('SMARTCAR TEST') ||
                normalize(combinedText).includes('MO PHONG')
        };

        result.fieldCount = [
            result.documentNumber,
            result.fullName,
            result.dateOfBirth,
            result.gender,
            result.issuedDate,
            result.expiryDate,
            result.address
        ].filter(Boolean).length;

        return result;
    };

    const getTesseractWorker = async () => {
        if (!window.Tesseract?.createWorker) {
            throw new Error('Không tải được bộ OCR miễn phí Tesseract.js. Hãy kiểm tra kết nối Internet rồi thử lại.');
        }

        if (!tesseractWorkerPromise) {
            tesseractWorkerPromise = window.Tesseract.createWorker(['vie', 'eng'], 1, {
                logger: message => {
                    if (typeof currentProgress === 'function') currentProgress(message);
                }
            }).catch(error => {
                tesseractWorkerPromise = null;
                throw error;
            });
        }
        return tesseractWorkerPromise;
    };

    const recognizeFile = async (file, onProgress) => {
        currentProgress = onProgress;
        try {
            const worker = await getTesseractWorker();
            const output = await worker.recognize(file);
            return {
                text: output?.data?.text || '',
                confidence: Number.isFinite(output?.data?.confidence) ? output.data.confidence : null
            };
        } finally {
            currentProgress = null;
        }
    };

    const averageConfidence = values => {
        const usable = values.filter(Number.isFinite);
        if (!usable.length) return null;
        return usable.reduce((sum, value) => sum + value, 0) / usable.length;
    };

    const getAntiForgeryToken = form => form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    const createDemoSession = async (form, front, back) => {
        const data = new FormData();
        const token = getAntiForgeryToken(form);
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

    const setInputValue = (selector, value) => {
        if (!value) return;
        const input = document.querySelector(selector);
        if (!input) return;
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const toIsoDate = value => {
        const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value || '');
        return match ? `${match[3]}-${match[2]}-${match[1]}` : '';
    };

    const setDateValue = (displaySelector, hiddenSelector, value) => {
        if (!value) return;
        const display = document.querySelector(displaySelector);
        const hidden = document.querySelector(hiddenSelector);
        if (display) {
            display.value = value;
            display.dispatchEvent(new Event('input', { bubbles: true }));
            display.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (hidden) {
            hidden.value = toIsoDate(value);
            hidden.dispatchEvent(new Event('change', { bubbles: true }));
        }
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
                badge.textContent = TEST_MODE_BADGE;
                badge.className = 'badge bg-info text-dark';
            }

            const notice = panel.querySelector('[data-ekyc-demo-notice]');
            if (notice) {
                notice.className = 'alert alert-info py-2 small mb-3';
                notice.innerHTML = '<strong>Chế độ test trên máy tính:</strong> OCR chạy cục bộ trong trình duyệt bằng Tesseract.js trên CCCD mô phỏng. Không cần CCCD thật và không mất phí. Bước khuôn mặt hiện vẫn là kết quả mô phỏng, hồ sơ vẫn cần Quản trị viên duyệt.';
            }

            const title = panel.querySelector('.ekyc-wizard-header h4');
            if (title) title.textContent = 'CCCD mô phỏng và webcam';
            const firstHeading = panel.querySelector('[data-ekyc-step-pane="1"] h5');
            if (firstHeading) firstHeading.textContent = 'Tải CCCD mô phỏng';
            const firstDescription = panel.querySelector('[data-ekyc-step-pane="1"] .ekyc-pane-heading p');
            if (firstDescription) firstDescription.textContent = 'Dùng ảnh giấy tờ TEST có chữ rõ, đủ 4 góc. Nên có watermark “SMARTCAR TEST DOCUMENT / KHÔNG CÓ GIÁ TRỊ”.';

            const faceHeading = panel.querySelector('[data-ekyc-step-pane="3"] h5');
            if (faceHeading) faceHeading.textContent = 'Xác minh bằng webcam máy tính';
            const cameraStart = panel.querySelector('[data-ekyc-camera-start]');
            if (cameraStart) cameraStart.textContent = '📷 Bật webcam';
            const selfieInput = panel.querySelector('[data-ekyc-video-input]');
            selfieInput?.removeAttribute('capture');
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
            setPanelMessage(panel, 'Vui lòng chọn đủ ảnh CCCD mô phỏng mặt trước và mặt sau.', 'warning');
            return;
        }

        const oldText = button.textContent;
        button.disabled = true;
        button.textContent = 'Đang chuẩn bị OCR...';

        try {
            const session = await createDemoSession(form, front, back);
            const sessionInput = panel.querySelector('[data-ekyc-session]');
            if (sessionInput) sessionInput.value = session.sessionId || '';

            button.textContent = 'Đang OCR mặt trước... 0%';
            const frontOcr = await recognizeFile(front, message => {
                if (message?.status === 'recognizing text' && Number.isFinite(message.progress)) {
                    button.textContent = `Đang OCR mặt trước... ${Math.round(message.progress * 100)}%`;
                }
            });

            button.textContent = 'Đang OCR mặt sau... 0%';
            const backOcr = await recognizeFile(back, message => {
                if (message?.status === 'recognizing text' && Number.isFinite(message.progress)) {
                    button.textContent = `Đang OCR mặt sau... ${Math.round(message.progress * 100)}%`;
                }
            });

            const confidence = averageConfidence([frontOcr.confidence, backOcr.confidence]);
            const parsed = parseCitizenId(frontOcr.text, backOcr.text, confidence);

            setInputValue('[name="CitizenIdVerification.FullNameOnDocument"]', parsed.fullName);
            setInputValue('[name="CitizenIdVerification.DocumentNumber"]', parsed.documentNumber);
            setInputValue('[name="CitizenIdVerification.Gender"]', parsed.gender);
            setInputValue('[name="CitizenIdVerification.PermanentAddress"]', parsed.address);
            setDateValue('#citizen-birth-display', '#citizen-birth-value', parsed.dateOfBirth);
            setDateValue('#citizen-issued-display', '#citizen-issued-value', parsed.issuedDate);
            setDateValue('#citizen-expiry-display', '#citizen-expiry-value', parsed.expiryDate);

            const state = panel.querySelector('[data-ekyc-ocr-state]');
            if (state) {
                const confidenceText = Number.isFinite(parsed.confidence) ? ` · độ tin cậy OCR ${parsed.confidence.toFixed(1)}%` : '';
                state.textContent = `✓ OCR cục bộ đã đọc được ${parsed.fieldCount}/7 trường${confidenceText}. Hãy kiểm tra và sửa những trường còn thiếu.`;
                state.className = `small ${parsed.fieldCount >= 4 ? 'text-success' : 'text-warning'} mb-3`;
            }

            const documentTypeText = parsed.isSmartCarTestDocument ? 'Đã nhận diện giấy tờ SmartCar TEST.' : 'Đang dùng OCR cục bộ dành cho dữ liệu test.';
            setPanelMessage(
                panel,
                `${documentTypeText} Kết quả này chỉ phục vụ đồ án và không xác thực CCCD với cơ sở dữ liệu nhà nước.`,
                parsed.fieldCount >= 4 ? 'success' : 'warning');
            showStepTwo(panel);
        } catch (error) {
            setPanelMessage(panel, error?.message || 'OCR cục bộ không thành công. Bạn có thể thử ảnh rõ hơn hoặc chuyển sang nhập thủ công.', 'danger');
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
