(() => {
    const allowedImageTypes = new Set(['image/jpeg', 'image/png', 'image/webp']);
    const allowedImageExtensions = ['.jpg', '.jpeg', '.png', '.webp'];
    const defaultMaximumImageBytes = 5 * 1024 * 1024;
    const signedMaximumImageBytes = 8 * 1024 * 1024;

    document.addEventListener('DOMContentLoaded', () => {
        hardenImageInputs();
        initializeHandoverCitizenEvidence();
        explainBlockedInspectionState();
        normalizeOptionalImageErrors();
    });

    function hardenImageInputs() {
        document.querySelectorAll('input[type="file"]').forEach((input) => {
            if (!(input instanceof HTMLInputElement)) return;
            if (!looksLikeImageInput(input)) return;
            attachImageValidation(input);
        });
    }

    function attachImageValidation(input) {
        if (input.dataset.staffImageValidation === 'true') return;
        input.dataset.staffImageValidation = 'true';

        const signedInput = /signed/i.test(`${input.name} ${input.id}`);
        const maximumBytes = Number(input.dataset.maxBytes || (signedInput ? signedMaximumImageBytes : defaultMaximumImageBytes));
        const maximumFiles = Number(input.dataset.maxFiles || (input.multiple ? (signedInput ? 12 : 25) : 1));

        input.addEventListener('change', () => {
            const files = Array.from(input.files || []);
            input.setCustomValidity('');
            clearClientError(input);

            if (files.length > maximumFiles) {
                rejectSelection(input, `Chỉ được chọn tối đa ${maximumFiles} ảnh cho mục này.`);
                return;
            }

            const badFormat = files.find((file) => !isAllowedImage(file));
            if (badFormat) {
                rejectSelection(input, `${badFormat.name}: chỉ chấp nhận JPG, PNG hoặc WEBP.`);
                return;
            }

            const tooLarge = files.find((file) => file.size > maximumBytes);
            if (tooLarge) {
                rejectSelection(input, `${tooLarge.name}: ảnh vượt quá ${Math.floor(maximumBytes / 1024 / 1024)} MB.`);
            }
        });
    }

    function looksLikeImageInput(input) {
        const accept = (input.accept || '').toLowerCase();
        const name = `${input.name} ${input.id}`.toLowerCase();
        return accept.includes('image/') ||
            accept.includes('.jpg') ||
            /image|photo|document|evidence|signed|citizen/.test(name);
    }

    function isAllowedImage(file) {
        const lower = file.name.toLowerCase();
        const extensionOkay = allowedImageExtensions.some((ext) => lower.endsWith(ext));
        const mimeOkay = !file.type || allowedImageTypes.has(file.type.toLowerCase());
        return extensionOkay && mimeOkay;
    }

    function rejectSelection(input, message) {
        input.setCustomValidity(message);
        showClientError(input, message);
        input.reportValidity();
    }

    function showClientError(input, message) {
        const host = input.closest('[data-evidence-slot], [data-evidence-multiple], [data-counter-citizen-evidence], .mb-3, .col-md-6, .col-md-4') || input.parentElement;
        if (!host) return;
        let error = host.querySelector(':scope > .staff-evidence-error');
        if (!error) {
            error = document.createElement('div');
            error.className = 'staff-evidence-error';
            host.appendChild(error);
        }
        error.textContent = message;
        host.classList.add('border-danger');
    }

    function clearClientError(input) {
        const host = input.closest('[data-evidence-slot], [data-evidence-multiple], [data-counter-citizen-evidence], .mb-3, .col-md-6, .col-md-4') || input.parentElement;
        if (!host) return;
        host.querySelector(':scope > .staff-evidence-error')?.remove();
        host.classList.remove('border-danger');
    }

    async function initializeHandoverCitizenEvidence() {
        if (!/^\/Handovers\/Create/i.test(window.location.pathname)) return;

        const form = document.querySelector('form[action*="/Handovers/Create"], form[action$="/Handovers/Create"]') ||
            document.querySelector('form[enctype="multipart/form-data"]');
        if (!(form instanceof HTMLFormElement)) return;

        const bookingField = form.querySelector('input[name="BookingId"]');
        const bookingId = Number(bookingField?.value || new URLSearchParams(window.location.search).get('bookingId'));
        if (!Number.isInteger(bookingId) || bookingId <= 0) return;

        const firstSection = form.querySelector('section');
        if (!firstSection) return;

        const block = document.createElement('div');
        block.className = 'border rounded-4 p-3 mt-3 bg-body-tertiary';
        block.dataset.counterCitizenEvidence = 'true';
        block.innerHTML = `
            <div class="d-flex justify-content-between gap-3 flex-wrap align-items-start">
                <div>
                    <div class="fw-semibold">CCCD khách đang có mặt tại quầy *</div>
                    <div class="small text-muted">Chụp đủ mặt trước + mặt sau để đối chiếu với hồ sơ KYC đã duyệt. Hai ảnh được lưu trong vùng tài liệu bảo mật, không nằm trong thư mục ảnh xe công khai.</div>
                </div>
                <span class="badge text-bg-secondary" data-counter-citizen-status>Đang kiểm tra...</span>
            </div>
            <div class="row g-3 mt-1">
                <div class="col-md-6">
                    <label class="form-label fw-semibold" for="counter-citizen-front">CCCD mặt trước</label>
                    <input id="counter-citizen-front" type="file" class="form-control" accept=".jpg,.jpeg,.png,.webp,image/jpeg,image/png,image/webp" data-file-draft-key="handover-${bookingId}-citizen-front" />
                    <div class="form-text">Ảnh phải thấy đầy đủ bốn góc giấy tờ; không crop mất thông tin.</div>
                </div>
                <div class="col-md-6">
                    <label class="form-label fw-semibold" for="counter-citizen-back">CCCD mặt sau</label>
                    <input id="counter-citizen-back" type="file" class="form-control" accept=".jpg,.jpeg,.png,.webp,image/jpeg,image/png,image/webp" data-file-draft-key="handover-${bookingId}-citizen-back" />
                    <div class="form-text">Không được dùng lại ảnh mặt trước cho mặt sau.</div>
                </div>
            </div>
            <div class="d-flex gap-2 flex-wrap align-items-center mt-3">
                <button type="button" class="btn btn-outline-primary" data-counter-citizen-save>Lưu ảnh CCCD đối chiếu</button>
                <span class="small text-muted" data-counter-citizen-message>Chưa có đủ bằng chứng CCCD tại quầy.</span>
            </div>`;

        const confirmationPanel = firstSection.querySelector('.bg-light, .bg-body-tertiary');
        if (confirmationPanel && confirmationPanel.parentElement === firstSection) {
            confirmationPanel.before(block);
        } else {
            firstSection.appendChild(block);
        }

        const front = block.querySelector('#counter-citizen-front');
        const back = block.querySelector('#counter-citizen-back');
        const saveButton = block.querySelector('[data-counter-citizen-save]');
        const status = block.querySelector('[data-counter-citizen-status]');
        const message = block.querySelector('[data-counter-citizen-message]');
        if (!(front instanceof HTMLInputElement) || !(back instanceof HTMLInputElement) || !(saveButton instanceof HTMLButtonElement)) return;

        attachImageValidation(front);
        attachImageValidation(back);

        let saved = false;
        let uploading = false;

        const setState = (kind, text) => {
            if (status) {
                status.textContent = kind === 'success' ? 'Đã lưu an toàn' : kind === 'error' ? 'Chưa đạt' : kind === 'loading' ? 'Đang lưu...' : 'Chưa lưu';
                status.className = `badge ${kind === 'success' ? 'text-bg-success' : kind === 'error' ? 'text-bg-danger' : kind === 'loading' ? 'text-bg-primary' : 'text-bg-secondary'}`;
            }
            if (message) {
                message.textContent = text;
                message.className = `small ${kind === 'error' ? 'text-danger' : kind === 'success' ? 'text-success' : 'text-muted'}`;
            }
        };

        const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

        const upload = async () => {
            if (uploading) return false;
            const frontFile = front.files?.[0] || null;
            const backFile = back.files?.[0] || null;

            if (!frontFile || !backFile) {
                setState('error', 'Phải chọn đủ CCCD mặt trước và mặt sau trước khi tiếp tục.');
                return false;
            }
            if (!front.checkValidity() || !back.checkValidity()) {
                (front.checkValidity() ? back : front).reportValidity();
                return false;
            }
            if (frontFile.size === backFile.size && frontFile.name === backFile.name && frontFile.lastModified === backFile.lastModified) {
                setState('error', 'CCCD mặt trước và mặt sau không được dùng cùng một file.');
                return false;
            }

            uploading = true;
            saveButton.disabled = true;
            setState('loading', 'Đang lưu hai ảnh vào vùng tài liệu bảo mật...');

            try {
                const payload = new FormData();
                payload.append('bookingId', String(bookingId));
                payload.append('citizenFront', frontFile, frontFile.name);
                payload.append('citizenBack', backFile, backFile.name);
                if (token) payload.append('__RequestVerificationToken', token);

                const response = await fetch('/StaffCounterIdentityEvidence/UploadHandover', {
                    method: 'POST',
                    body: payload,
                    credentials: 'same-origin',
                    headers: { 'Accept': 'application/json' }
                });
                const data = await response.json().catch(() => ({}));
                if (!response.ok || data.saved !== true) {
                    saved = false;
                    setState('error', data.error || 'Không thể lưu ảnh CCCD đối chiếu. Vui lòng kiểm tra lại ảnh.');
                    return false;
                }

                saved = true;
                setState('success', 'Đã lưu đủ CCCD mặt trước + mặt sau. Có thể tiếp tục lập biên bản giao xe.');
                return true;
            } catch {
                saved = false;
                setState('error', 'Không thể kết nối để lưu ảnh CCCD. Vui lòng thử lại.');
                return false;
            } finally {
                uploading = false;
                saveButton.disabled = false;
            }
        };

        const markDirty = () => {
            if (!saved) return;
            saved = false;
            setState('idle', 'Ảnh đã thay đổi. Cần lưu lại bộ CCCD mới trước khi gửi biên bản.');
        };
        front.addEventListener('change', markDirty);
        back.addEventListener('change', markDirty);
        saveButton.addEventListener('click', upload);

        try {
            const response = await fetch(`/StaffCounterIdentityEvidence/HandoverStatus?bookingId=${bookingId}`, {
                headers: { 'Accept': 'application/json' },
                credentials: 'same-origin'
            });
            if (response.ok) {
                const data = await response.json();
                saved = data.ready === true;
                if (saved) {
                    setState('success', 'Bộ CCCD tại quầy đã được lưu trước đó và vẫn còn hiệu lực. Chỉ chọn ảnh mới nếu cần thay bộ chứng cứ.');
                } else {
                    setState('idle', 'Chưa lưu đủ CCCD mặt trước + mặt sau cho lần bàn giao này.');
                }
            } else {
                setState('idle', 'Chưa xác định được trạng thái CCCD tại quầy. Hãy chọn và lưu đủ hai mặt.');
            }
        } catch {
            setState('idle', 'Không tải được trạng thái CCCD. Hãy chọn và lưu đủ hai mặt trước khi gửi.');
        }

        form.addEventListener('submit', async (event) => {
            if (saved) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            const submitter = event.submitter instanceof HTMLElement ? event.submitter : null;
            const okay = await upload();
            if (!okay) return;

            if (submitter instanceof HTMLButtonElement || submitter instanceof HTMLInputElement) {
                form.requestSubmit(submitter);
            } else {
                form.requestSubmit();
            }
        }, true);
    }

    function normalizeOptionalImageErrors() {
        document.querySelectorAll('input[type="file"]:not([required])').forEach((input) => {
            if (!(input instanceof HTMLInputElement)) return;
            const validationSpan = document.querySelector(`[data-valmsg-for="${cssEscape(input.name)}"]`);
            if (!validationSpan) return;

            const text = (validationSpan.textContent || '').trim();
            if (/không được dùng cùng một ảnh|ảnh trùng nội dung/i.test(text)) {
                const form = input.closest('form');
                if (form) {
                    let banner = form.querySelector('.staff-duplicate-evidence-banner');
                    if (!banner) {
                        banner = document.createElement('div');
                        banner.className = 'alert alert-danger staff-duplicate-evidence-banner';
                        const firstSection = form.querySelector('section');
                        firstSection?.before(banner);
                    }
                    banner.textContent = text;
                }
                validationSpan.textContent = '';
            }
        });
    }

    async function explainBlockedInspectionState() {
        const match = /^\/Staff\/Details\/(\d+)/i.exec(window.location.pathname);
        if (!match) return;

        const heading = Array.from(document.querySelectorAll('h2, h3')).find((node) =>
            /đối chiếu xe trả.*quyết toán/i.test(node.textContent || ''));
        if (!heading) return;

        const section = heading.closest('section');
        if (!section) return;

        const currentInspectLink = section.querySelector('a[href*="/Returns/Inspect"], a[href*="/returns/inspect"]');
        if (currentInspectLink) return;

        const bookingId = Number(match[1]);
        if (!Number.isInteger(bookingId) || bookingId <= 0) return;

        try {
            const response = await fetch(`/StaffWorkflowDiagnostics/Inspection?bookingId=${bookingId}`, {
                headers: { 'Accept': 'application/json' },
                credentials: 'same-origin'
            });
            if (!response.ok) return;
            const data = await response.json();

            if (data.canInspect && data.inspectUrl) {
                const action = document.createElement('a');
                action.className = 'btn btn-primary mt-3';
                action.href = data.inspectUrl;
                action.textContent = 'Đối chiếu, thêm phí & quyết toán';
                section.appendChild(action);
                return;
            }

            const blockers = Array.isArray(data.blockers) ? data.blockers : [];
            if (blockers.length === 0) return;

            const alert = document.createElement('div');
            alert.className = 'alert alert-warning staff-stuck-workflow-alert mt-3 mb-0';
            const title = document.createElement('strong');
            title.textContent = 'Chưa thể mở quyết toán.';
            const intro = document.createElement('div');
            intro.className = 'small mt-1 mb-2';
            intro.textContent = 'Không để đơn treo im lặng: hệ thống đã xác định các điều kiện còn thiếu bên dưới.';
            const list = document.createElement('ul');
            list.className = 'mb-0';
            blockers.forEach((reason) => {
                const item = document.createElement('li');
                item.textContent = reason;
                list.appendChild(item);
            });
            alert.append(title, intro, list);
            section.appendChild(alert);
        } catch {
            // Không chặn trang nếu endpoint chẩn đoán tạm thời không truy cập được.
        }
    }

    function cssEscape(value) {
        if (window.CSS && typeof window.CSS.escape === 'function') return window.CSS.escape(value);
        return String(value).replace(/["\\]/g, '\\$&');
    }
})();
