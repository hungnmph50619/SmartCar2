(() => {
    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('[data-identity-capture-widget]').forEach((widget) => {
            const button = widget.querySelector('[data-fallback-submit]');
            const fileInput = widget.querySelector('[data-fallback-file]');
            const reasonInput = widget.querySelector('[data-fallback-reason]');
            const hidden = widget.querySelector('[data-face-session]');
            const preview = widget.querySelector('[data-face-preview]');
            const status = widget.querySelector('[data-face-status]');
            const fallbackUrl = widget.dataset.fallbackUrl;
            if (!button || !fileInput || !reasonInput || !fallbackUrl) return;

            let localPreviewUrl = null;
            fileInput.addEventListener('change', () => {
                if (localPreviewUrl) URL.revokeObjectURL(localPreviewUrl);
                localPreviewUrl = null;
                const file = fileInput.files?.[0];
                if (!preview || !file || !file.type.startsWith('image/')) return;
                localPreviewUrl = URL.createObjectURL(file);
                preview.src = localPreviewUrl;
                preview.classList.remove('d-none');
                if (status) status.textContent = 'Xem ảnh đã chọn, ghi lý do rồi bấm “Dùng ảnh dự phòng” để lưu.';
            });
            window.addEventListener('pagehide', () => {
                if (localPreviewUrl) URL.revokeObjectURL(localPreviewUrl);
            }, { once: true });

            button.addEventListener('click', async () => {
                const file = fileInput.files?.[0];
                const reason = reasonInput.value.trim();
                if (!file) {
                    fileInput.setCustomValidity('Vui lòng chọn ảnh mặt được chụp trực tiếp tại quầy.');
                    fileInput.reportValidity();
                    return;
                }
                fileInput.setCustomValidity('');
                if (reason.length < 10) {
                    reasonInput.setCustomValidity('Ghi rõ lý do dùng upload dự phòng, ít nhất 10 ký tự.');
                    reasonInput.reportValidity();
                    return;
                }
                reasonInput.setCustomValidity('');

                const form = new FormData();
                form.append('purpose', widget.dataset.purpose || '');
                if (widget.dataset.bookingId) form.append('bookingId', widget.dataset.bookingId);
                if (widget.dataset.customerId) form.append('customerId', widget.dataset.customerId);
                form.append('reason', reason);
                form.append('image', file);

                const csrf = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
                button.disabled = true;
                if (status) {
                    status.textContent = 'Đang lưu ảnh dự phòng và ghi audit...';
                    status.className = 'small mt-2 text-muted';
                }
                try {
                    const response = await fetch(fallbackUrl, {
                        method: 'POST',
                        headers: csrf ? { RequestVerificationToken: csrf } : {},
                        body: form
                    });
                    const payload = await response.json();
                    if (!response.ok) throw new Error(payload?.error || 'Không lưu được ảnh dự phòng.');
                    if (hidden) hidden.value = payload.sessionId;
                    if (preview) {
                        if (localPreviewUrl) URL.revokeObjectURL(localPreviewUrl);
                        localPreviewUrl = null;
                        preview.src = payload.imageUrl;
                        preview.classList.remove('d-none');
                    }
                    if (status) {
                        status.textContent = '✓ Đã có ảnh do Staff tải dự phòng; Staff, thời gian và lý do đã được ghi audit.';
                        status.className = 'small mt-2 text-success';
                    }
                } catch (error) {
                    if (status) {
                        status.textContent = error.message || 'Không lưu được ảnh dự phòng.';
                        status.className = 'small mt-2 text-danger';
                    }
                } finally {
                    button.disabled = false;
                }
            });
        });
    });
})();
