(() => {
    let enabled = false;
    let workerPromise = null;

    const normalize = value => (value || '')
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/đ/g, 'd')
        .replace(/Đ/g, 'D')
        .toUpperCase()
        .replace(/\s+/g, ' ')
        .trim();

    const linesOf = text => (text || '')
        .split(/\r?\n/)
        .map(original => original.replace(/\s+/g, ' ').trim())
        .filter(Boolean)
        .map(original => ({ original, normalized: normalize(original) }));

    const getWorker = async () => {
        if (!window.Tesseract?.createWorker) throw new Error('Bộ OCR cục bộ chưa sẵn sàng.');
        if (!workerPromise) {
            workerPromise = window.Tesseract.createWorker(['vie', 'eng'], 1)
                .then(async worker => {
                    try {
                        await worker.setParameters({ preserve_interword_spaces: '1', tessedit_pageseg_mode: '6' });
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

    const crop = (source, region, maxLong = 1700) => {
        const x = Math.max(0, Math.round(source.width * region.x));
        const y = Math.max(0, Math.round(source.height * region.y));
        const width = Math.min(source.width - x, Math.max(1, Math.round(source.width * region.width)));
        const height = Math.min(source.height - y, Math.max(1, Math.round(source.height * region.height)));
        const scale = Math.min(3.2, maxLong / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        canvas.getContext('2d', { willReadFrequently: true }).drawImage(
            source, x, y, width, height, 0, 0, canvas.width, canvas.height
        );
        return canvas;
    };

    const variant = (source, mode = 'contrast') => {
        if (mode === 'original') return source;
        const canvas = document.createElement('canvas');
        canvas.width = source.width;
        canvas.height = source.height;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(source, 0, 0);
        const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
        let sum = 0;
        for (let i = 0; i < image.data.length; i += 4) {
            sum += .299 * image.data[i] + .587 * image.data[i + 1] + .114 * image.data[i + 2];
        }
        const mean = sum / Math.max(1, image.data.length / 4);
        const threshold = Math.max(100, Math.min(210, mean * .93));
        for (let i = 0; i < image.data.length; i += 4) {
            const gray = .299 * image.data[i] + .587 * image.data[i + 1] + .114 * image.data[i + 2];
            const value = mode === 'binary'
                ? (gray >= threshold ? 255 : 0)
                : Math.max(0, Math.min(255, (gray - 128) * 1.45 + 128));
            image.data[i] = value;
            image.data[i + 1] = value;
            image.data[i + 2] = value;
        }
        ctx.putImageData(image, 0, 0);
        return canvas;
    };

    const recognize = async (worker, canvas, psm = 6) => {
        try { await worker.setParameters({ tessedit_pageseg_mode: String(psm), preserve_interword_spaces: '1' }); } catch { }
        const output = await worker.recognize(canvas);
        return {
            text: output?.data?.text || '',
            confidence: Number(output?.data?.confidence || 0)
        };
    };

    const validClasses = new Set(['B', 'C1', 'C', 'D1', 'D2', 'D', 'BE', 'C1E', 'CE', 'D1E', 'D2E', 'DE', 'B1', 'B2', 'E', 'FB2', 'FC', 'FD', 'FE']);
    const dateRegex = /\b([0-3]?\d)[\/\-.]([01]?\d)[\/\-.]((?:19|20)\d{2})\b/g;

    const normalizedDate = value => {
        const match = /([0-3]?\d)[\/\-.]([01]?\d)[\/\-.]((?:19|20)\d{2})/.exec(value || '');
        if (!match) return '';
        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const date = new Date(year, month - 1, day);
        if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return '';
        return `${String(day).padStart(2, '0')}/${String(month).padStart(2, '0')}/${year}`;
    };

    const valueNearLabel = (lines, labels, maxFollowing = 2) => {
        const normalizedLabels = labels.map(normalize);
        for (let i = 0; i < lines.length; i += 1) {
            if (!normalizedLabels.some(label => lines[i].normalized.includes(label))) continue;
            const same = lines[i].original.replace(/^.*?(?:[:：]|Full\s*name|Họ\s*tên|Họ\s*và\s*tên|Số\s*\/\s*No\.?|Hạng\s*\/\s*class|Có\s*giá\s*trị\s*đến|Expires)\s*/iu, '').trim();
            if (same) return same;
            for (let offset = 1; offset <= maxFollowing; offset += 1) {
                if (lines[i + offset]?.original) return lines[i + offset].original;
            }
        }
        return '';
    };

    const cleanName = value => (value || '')
        .replace(/^.*?(?:Họ\s*(?:và\s*)?tên|Full\s*name)\s*[:：/-]*/iu, '')
        .replace(/[^A-Za-zÀ-ỹĐđ'\-\s]/g, ' ')
        .replace(/\s+/g, ' ')
        .trim();

    const isName = value => {
        const cleaned = cleanName(value);
        const words = cleaned.split(/\s+/).filter(Boolean);
        if (words.length < 2 || words.length > 7 || /\d/.test(cleaned)) return false;
        const n = normalize(cleaned);
        if (['GIAY PHEP', 'DRIVER', 'CONG HOA', 'QUOC TICH', 'NATIONALITY', 'NOI CU TRU', 'ADDRESS', 'HANG CLASS'].some(x => n.includes(x))) return false;
        return (cleaned.match(/[A-Za-zÀ-ỹĐđ]/g) || []).length / Math.max(1, cleaned.length) > .72;
    };

    const extractName = texts => {
        const candidates = [];
        for (const text of texts) {
            const lines = linesOf(text.text);
            const labeled = valueNearLabel(lines, ['HO TEN', 'HO VA TEN', 'FULL NAME'], 2);
            if (isName(labeled)) candidates.push({ value: cleanName(labeled), score: 100 + text.confidence });
            lines.forEach(line => {
                if (isName(line.original)) candidates.push({ value: cleanName(line.original), score: text.confidence + (line.original === line.original.toUpperCase() ? 10 : 0) });
            });
        }
        candidates.sort((a, b) => b.score - a.score);
        return candidates[0]?.value || '';
    };

    const extractNumber = texts => {
        for (const text of texts) {
            const normalized = text.text.replace(/[Oo]/g, '0');
            const labeled = normalized.match(/(?:S[oố]|No\.?)[^A-Z0-9]{0,12}([A-Z0-9](?:[\s.-]?[A-Z0-9]){7,19})/iu);
            if (labeled?.[1]) {
                const value = labeled[1].replace(/[^A-Z0-9]/gi, '').toUpperCase();
                if (/^[A-Z0-9]{8,20}$/.test(value)) return value;
            }
        }
        for (const text of texts) {
            const matches = text.text.replace(/[Oo]/g, '0').match(/(?<!\d)\d(?:[\s.-]?\d){8,14}(?!\d)/g) || [];
            const value = matches.map(x => x.replace(/\D/g, '')).find(x => x.length >= 9 && x.length <= 15);
            if (value) return value;
        }
        return '';
    };

    const extractClass = texts => {
        for (const text of texts) {
            const n = normalize(text.text);
            const matches = [
                n.match(/HANG\s*\/?\s*CLASS\s*[:：-]?\s*([A-Z0-9]{1,3})/),
                n.match(/HANG\s*[:：-]?\s*([A-Z0-9]{1,3})/),
                n.match(/CLASS\s*[:：-]?\s*([A-Z0-9]{1,3})/)
            ];
            for (const match of matches) {
                const value = match?.[1]?.toUpperCase();
                if (validClasses.has(value)) return value;
            }
        }
        return '';
    };

    const extractExpiry = texts => {
        for (const text of texts) {
            const lines = linesOf(text.text);
            for (let i = 0; i < lines.length; i += 1) {
                if (!['CO GIA TRI DEN', 'EXPIRES', 'DATE OF EXPIRY'].some(label => lines[i].normalized.includes(label))) continue;
                for (let offset = 0; offset <= 2; offset += 1) {
                    const date = normalizedDate(lines[i + offset]?.original || '');
                    if (date) return date;
                }
            }
        }
        return '';
    };

    const extractIssued = (texts, expiry) => {
        const today = new Date();
        const candidates = [];
        for (const text of texts) {
            const lines = linesOf(text.text);
            for (let i = 0; i < lines.length; i += 1) {
                const line = lines[i];
                const anchored = ['NGAY CAP', 'DATE OF ISSUE', 'ISSUED', 'NGAY'].some(label => line.normalized.includes(label)) &&
                    !['NGAY SINH', 'DATE OF BIRTH', 'NGAY TRUNG TUYEN', 'BEGINNING DATE'].some(label => line.normalized.includes(label));
                const dates = (line.original.match(dateRegex) || []).map(normalizedDate).filter(Boolean);
                dates.forEach(value => candidates.push({ value, anchored, confidence: text.confidence }));
            }
        }
        const valid = candidates.filter(item => {
            if (!item.value || item.value === expiry) return false;
            const [d, m, y] = item.value.split('/').map(Number);
            const date = new Date(y, m - 1, d);
            return y >= 2005 && date <= today;
        });
        valid.sort((a, b) => Number(b.anchored) - Number(a.anchored) || b.confidence - a.confidence);
        return valid[0]?.value || '';
    };

    const writeInput = (selector, value) => {
        const input = document.querySelector(selector);
        if (!input || !value) return;
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const writeDate = (displaySelector, hiddenSelector, value) => {
        if (!value) return;
        const display = document.querySelector(displaySelector);
        const hidden = document.querySelector(hiddenSelector);
        if (display) {
            display.value = value;
            display.dispatchEvent(new Event('input', { bubbles: true }));
            display.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (hidden) {
            const [day, month, year] = value.split('/');
            hidden.value = `${year}-${month}-${day}`;
            hidden.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };

    const setMessage = (panel, text, type = 'info') => {
        const box = panel.querySelector('[data-license-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = text;
        box.classList.remove('d-none');
    };

    const showStepTwo = panel => {
        panel.querySelectorAll('[data-license-step-pane]').forEach(pane => {
            pane.classList.toggle('d-none', Number(pane.dataset.licenseStepPane) !== 2);
        });
        panel.querySelectorAll('[data-license-step-indicator]').forEach(indicator => {
            const step = Number(indicator.dataset.licenseStepIndicator);
            indicator.classList.toggle('is-active', step === 2);
            indicator.classList.toggle('is-done', step < 2);
        });
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };

    const run = async (button, panel) => {
        const front = document.getElementById('license-front-file')?.files?.[0];
        if (!front || panel.dataset.documentGate !== 'pass' || panel.dataset.imageQuality !== 'pass') {
            setMessage(panel, 'Hãy chọn đúng GPLX mặt trước/mặt sau và dùng ảnh đạt chất lượng trước khi OCR.', 'warning');
            return;
        }
        if (!window.SmartCarDocumentVision?.prepareForOcr) {
            setMessage(panel, 'Bộ xử lý ảnh cục bộ chưa sẵn sàng.', 'danger');
            return;
        }

        const oldText = button.textContent;
        button.disabled = true;
        try {
            button.textContent = 'Đang chuẩn hóa GPLX...';
            const prepared = await window.SmartCarDocumentVision.prepareForOcr(front);
            const worker = await getWorker();

            const full = await recognize(worker, variant(prepared.canvas, 'contrast'), 11);
            button.textContent = 'Đang đọc từng vùng GPLX...';
            const identity = crop(prepared.canvas, { x: .28, y: .24, width: .70, height: .50 }, 1900);
            const lower = crop(prepared.canvas, { x: .02, y: .52, width: .96, height: .46 }, 1900);
            const classExpiry = crop(prepared.canvas, { x: .02, y: .38, width: .52, height: .58 }, 1700);
            const signature = crop(prepared.canvas, { x: .43, y: .62, width: .55, height: .36 }, 1700);

            const [identityOcr, lowerOcr, classOcr, signatureOcr] = await Promise.all([
                recognize(worker, variant(identity, 'contrast'), 6),
                recognize(worker, variant(lower, 'contrast'), 6),
                recognize(worker, variant(classExpiry, 'binary'), 6),
                recognize(worker, variant(signature, 'contrast'), 6)
            ]);
            const texts = [full, identityOcr, lowerOcr, classOcr, signatureOcr];

            const name = extractName(texts);
            const number = extractNumber(texts);
            const licenseClass = extractClass(texts);
            const expiry = extractExpiry(texts);
            const issued = extractIssued(texts, expiry);

            writeInput('[name="DrivingLicenseVerification.FullNameOnDocument"]', name);
            writeInput('[name="DrivingLicenseVerification.DocumentNumber"]', number);
            writeInput('[name="DrivingLicenseVerification.LicenseClass"]', licenseClass);
            writeDate('#license-issued-display', '#license-issued-value', issued);
            writeDate('#license-expiry-display', '#license-expiry-value', expiry);

            const fields = [name, number, licenseClass, issued, expiry];
            const count = fields.filter(Boolean).length;
            const missing = [
                ['họ tên', name], ['số GPLX', number], ['hạng GPLX', licenseClass],
                ['ngày cấp', issued], ['ngày hết hạn', expiry]
            ].filter(([, value]) => !value).map(([label]) => label);

            setMessage(
                panel,
                missing.length
                    ? `OCR theo mẫu GPLX đọc được ${count}/5 trường. Cần kiểm tra/nhập lại: ${missing.join(', ')}.`
                    : 'OCR theo mẫu GPLX đã đọc đủ 5 trường. Hãy đối chiếu lại với giấy phép trước khi gửi.',
                missing.length ? 'warning' : 'success'
            );
            showStepTwo(panel);
        } catch (error) {
            setMessage(panel, error?.message || 'Không đọc được GPLX. Hãy thử ảnh rõ hơn.', 'danger');
        } finally {
            button.disabled = false;
            button.textContent = oldText || 'Đọc GPLX và tiếp tục';
        }
    };

    const install = async () => {
        const panel = document.querySelector('[data-ekyc-panel="license"]');
        if (!panel) return;
        try {
            const response = await fetch('/Ekyc/Status', { credentials: 'same-origin' });
            const status = response.ok ? await response.json() : null;
            enabled = Boolean(status?.isDemo);
        } catch {
            enabled = false;
        }
    };

    document.addEventListener('click', event => {
        const button = event.target.closest('[data-license-ocr]');
        if (!button || !enabled) return;
        const panel = button.closest('[data-ekyc-panel="license"]');
        if (!panel) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        void run(button, panel);
    }, true);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', install, { once: true });
    } else {
        void install();
    }
})();