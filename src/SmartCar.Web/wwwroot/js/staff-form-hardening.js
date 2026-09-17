(() => {
    const allowedImageTypes = new Set(['image/jpeg', 'image/png', 'image/webp']);
    const allowedImageExtensions = ['.jpg', '.jpeg', '.png', '.webp'];
    const defaultMaximumImageBytes = 5 * 1024 * 1024;

    document.addEventListener('DOMContentLoaded', () => {
        hardenImageInputs();
        explainBlockedInspectionState();
        normalizeOptionalImageErrors();
    });

    function hardenImageInputs() {
        document.querySelectorAll('input[type="file"]').forEach((input) => {
            if (!(input instanceof HTMLInputElement)) return;
            if (!looksLikeImageInput(input)) return;

            const maximumBytes = Number(input.dataset.maxBytes || defaultMaximumImageBytes);
            const maximumFiles = Number(input.dataset.maxFiles || (input.multiple ? 25 : 1));

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
        });
    }

    function looksLikeImageInput(input) {
        const accept = (input.accept || '').toLowerCase();
        const name = `${input.name} ${input.id}`.toLowerCase();
        return accept.includes('image/') ||
            accept.includes('.jpg') ||
            /image|photo|document|evidence|signed/.test(name);
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
        const host = input.closest('[data-evidence-slot], [data-evidence-multiple], .mb-3, .col-md-6, .col-md-4') || input.parentElement;
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
        const host = input.closest('[data-evidence-slot], [data-evidence-multiple], .mb-3, .col-md-6, .col-md-4') || input.parentElement;
        if (!host) return;
        host.querySelector(':scope > .staff-evidence-error')?.remove();
        host.classList.remove('border-danger');
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

    function explainBlockedInspectionState() {
        const path = window.location.pathname.toLowerCase();
        if (!path.includes('/staff/details/')) return;

        const heading = Array.from(document.querySelectorAll('h2, h3')).find((node) =>
            /đối chiếu xe trả.*quyết toán/i.test(node.textContent || ''));
        if (!heading) return;

        const section = heading.closest('section');
        if (!section) return;
        const inspectLink = section.querySelector('a[href*="/Returns/Inspect"], a[href*="/returns/inspect"]');
        if (inspectLink) return;

        const existingActionableForm = section.querySelector('form button:not([disabled]), form input[type="submit"]:not([disabled])');
        if (existingActionableForm) return;

        const alert = document.createElement('div');
        alert.className = 'alert alert-warning staff-stuck-workflow-alert mt-3 mb-0';
        alert.innerHTML = '<strong>Chưa thể mở quyết toán.</strong> Hồ sơ giao/trả còn ít nhất một điều kiện xác minh chưa đạt. Hãy kiểm tra lại trạng thái danh tính và bản ký; hệ thống không nên để đơn đứng im mà không giải thích bước tiếp theo.';
        section.appendChild(alert);
    }

    function cssEscape(value) {
        if (window.CSS && typeof window.CSS.escape === 'function') return window.CSS.escape(value);
        return String(value).replace(/["\\]/g, '\\$&');
    }
})();
