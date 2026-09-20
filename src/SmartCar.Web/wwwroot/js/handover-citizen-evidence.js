(() => {
    document.addEventListener('DOMContentLoaded', () => {
        const root = document.querySelector('[data-handover-citizen-evidence]');
        if (!(root instanceof HTMLElement)) return;

        const bookingId = Number(root.dataset.bookingId || 0);
        const statusUrl = root.dataset.statusUrl || '';
        const uploadUrl = root.dataset.uploadUrl || '';
        const frontInput = root.querySelector('[data-counter-citizen-front]');
        const backInput = root.querySelector('[data-counter-citizen-back]');
        const frontPreview = root.querySelector('[data-counter-citizen-front-preview]');
        const backPreview = root.querySelector('[data-counter-citizen-back-preview]');
        const currentFront = root.querySelector('[data-counter-citizen-current-front]');
        const currentBack = root.querySelector('[data-counter-citizen-current-back]');
        const status = root.querySelector('[data-counter-citizen-status]');
        const saveButton = root.querySelector('[data-counter-citizen-save]');
        const submitButton = document.querySelector('[data-handover-submit]');
        const antiForgery = document.querySelector('input[name="__RequestVerificationToken"]');

        const maximumBytes = 5 * 1024 * 1024;
        const allowedMimeTypes = new Set(['image/jpeg', 'image/png', 'image/webp']);
        const allowedExtensions = ['.jpg', '.jpeg', '.png', '.webp'];
        let ready = false;
        let frontObjectUrl = null;
        let backObjectUrl = null;

        if (!(frontInput instanceof HTMLInputElement) ||
            !(backInput instanceof HTMLInputElement) ||
            !(saveButton instanceof HTMLButtonElement)) {
            return;
        }

        function setReady(value, message) {
            ready = value;
            if (submitButton instanceof HTMLButtonElement) {
                submitButton.disabled = !value;
                submitButton.title = value
                    ? ''
                    : 'Cần lưu đủ CCCD mặt trước và mặt sau tại quầy trước khi lưu biên bản.';
            }
            root.classList.toggle('border-success', value);
            root.classList.toggle('border-warning', !value);
            if (status) {
                status.textContent = message;
                status.className = value
                    ? 'badge text-bg-success'
                    : 'badge text-bg-warning';
            }
        }

        function validateFile(file, label) {
            if (!file) return `Chưa chọn ${label}.`;
            const lower = file.name.toLowerCase();
            const extensionOkay = allowedExtensions.some(ext => lower.endsWith(ext));
            const mimeOkay = !file.type || allowedMimeTypes.has(file.type.toLowerCase());
            if (!extensionOkay || !mimeOkay) {
                return `${label}: chỉ chấp nhận JPG, PNG hoặc WEBP.`;
            }
            if (file.size <= 0) return `${label}: file ảnh rỗng.`;
            if (file.size > maximumBytes) return `${label}: ảnh không được vượt quá 5 MB.`;
            return null;
        }

        async function hashFile(file) {
            if (!globalThis.crypto?.subtle) {
                return `${file.name}:${file.size}:${file.lastModified}`;
            }
            const buffer = await file.arrayBuffer();
            const digest = await crypto.subtle.digest('SHA-256', buffer);
            return Array.from(new Uint8Array(digest))
                .map(value => value.toString(16).padStart(2, '0'))
                .join('');
        }

        function renderLocalPreview(input, preview, side) {
            if (!(preview instanceof HTMLImageElement)) return;

            if (side === 'front' && frontObjectUrl) {
                URL.revokeObjectURL(frontObjectUrl);
                frontObjectUrl = null;
            }
            if (side === 'back' && backObjectUrl) {
                URL.revokeObjectURL(backObjectUrl);
                backObjectUrl = null;
            }

            const file = input.files?.[0] || null;
            if (!file) {
                preview.removeAttribute('src');
                preview.classList.add('d-none');
                return;
            }

            const url = URL.createObjectURL(file);
            if (side === 'front') frontObjectUrl = url;
            if (side === 'back') backObjectUrl = url;
            preview.src = url;
            preview.classList.remove('d-none');
        }

        async function refreshStatus() {
            if (!statusUrl || bookingId <= 0) {
                setReady(false, 'Không xác định được trạng thái CCCD');
                return;
            }

            try {
                const response = await fetch(
                    `${statusUrl}${statusUrl.includes('?') ? '&' : '?'}bookingId=${encodeURIComponent(bookingId)}`,
                    { credentials: 'same-origin', cache: 'no-store' });

                if (!response.ok) {
                    setReady(false, 'Chưa xác minh CCCD tại quầy');
                    return;
                }

                const data = await response.json();
                const frontReady = data.frontReady === true;
                const backReady = data.backReady === true;

                if (currentFront instanceof HTMLImageElement) {
                    if (frontReady && data.frontUrl) {
                        currentFront.src = `${data.frontUrl}${data.frontUrl.includes('?') ? '&' : '?'}v=${Date.now()}`;
                        currentFront.classList.remove('d-none');
                    } else {
                        currentFront.removeAttribute('src');
                        currentFront.classList.add('d-none');
                    }
                }

                if (currentBack instanceof HTMLImageElement) {
                    if (backReady && data.backUrl) {
                        currentBack.src = `${data.backUrl}${data.backUrl.includes('?') ? '&' : '?'}v=${Date.now()}`;
                        currentBack.classList.remove('d-none');
                    } else {
                        currentBack.removeAttribute('src');
                        currentBack.classList.add('d-none');
                    }
                }

                if (frontReady && backReady && data.ready === true) {
                    setReady(true, 'Đã lưu đủ CCCD trước + sau');
                    saveButton.textContent = 'Chụp / lưu lại hai mặt CCCD';
                } else {
                    const missing = [];
                    if (!frontReady) missing.push('mặt trước');
                    if (!backReady) missing.push('mặt sau');
                    setReady(false, `Thiếu ${missing.join(' + ')}`);
                }
            } catch {
                setReady(false, 'Không tải được trạng thái CCCD');
            }
        }

        async function saveEvidence() {
            const front = frontInput.files?.[0] || null;
            const back = backInput.files?.[0] || null;
            const frontError = validateFile(front, 'CCCD mặt trước');
            const backError = validateFile(back, 'CCCD mặt sau');

            if (frontError || backError) {
                setReady(false, frontError || backError || 'Ảnh CCCD chưa hợp lệ');
                (frontError ? frontInput : backInput).reportValidity?.();
                alert(frontError || backError);
                return;
            }

            if (await hashFile(front) === await hashFile(back)) {
                setReady(false, 'Hai mặt CCCD không được dùng cùng một ảnh');
                alert('CCCD mặt trước và mặt sau không được dùng cùng một ảnh.');
                return;
            }

            const formData = new FormData();
            formData.append('bookingId', String(bookingId));
            formData.append('citizenFront', front);
            formData.append('citizenBack', back);
            if (antiForgery instanceof HTMLInputElement && antiForgery.value) {
                formData.append('__RequestVerificationToken', antiForgery.value);
            }

            saveButton.disabled = true;
            saveButton.textContent = 'Đang lưu CCCD...';

            try {
                const response = await fetch(uploadUrl, {
                    method: 'POST',
                    body: formData,
                    credentials: 'same-origin'
                });

                const contentType = response.headers.get('content-type') || '';
                const payload = contentType.includes('application/json')
                    ? await response.json()
                    : null;

                if (!response.ok) {
                    throw new Error(payload?.error || 'Không thể lưu ảnh CCCD tại quầy.');
                }

                frontInput.value = '';
                backInput.value = '';
                renderLocalPreview(frontInput, frontPreview, 'front');
                renderLocalPreview(backInput, backPreview, 'back');

                await refreshStatus();
            } catch (error) {
                setReady(false, error instanceof Error ? error.message : 'Không thể lưu ảnh CCCD');
                alert(error instanceof Error ? error.message : 'Không thể lưu ảnh CCCD tại quầy.');
            } finally {
                saveButton.disabled = false;
                if (!ready) saveButton.textContent = 'Lưu CCCD trước + sau tại quầy';
            }
        }

        frontInput.addEventListener('change', () => {
            renderLocalPreview(frontInput, frontPreview, 'front');
        });
        backInput.addEventListener('change', () => {
            renderLocalPreview(backInput, backPreview, 'back');
        });
        saveButton.addEventListener('click', saveEvidence);

        setReady(false, 'Đang kiểm tra CCCD tại quầy...');
        refreshStatus();
    });
})();
