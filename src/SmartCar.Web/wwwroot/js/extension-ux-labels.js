(() => {
    const pathname = window.location.pathname.toLowerCase();
    if (!pathname.startsWith('/bookings/details/')) {
        return;
    }

    const form = document.getElementById('customer-extension-form');
    if (!(form instanceof HTMLFormElement)) {
        return;
    }

    const customerNote = form.querySelector('input[name="CustomerNote"]');
    if (customerNote instanceof HTMLInputElement) {
        const label = customerNote.closest('.col-md-6')?.querySelector('label.form-label');
        if (label) {
            label.textContent = 'Lý do gia hạn';
        }
        customerNote.placeholder = 'Vì sao bạn cần gia hạn?';
    }

    const evidenceNote = document.getElementById('extension-evidence-note');
    if (evidenceNote instanceof HTMLTextAreaElement) {
        const label = form.querySelector('label[for="extension-evidence-note"]');
        if (label) {
            label.textContent = 'Mô tả sự cố';
        }
        evidenceNote.placeholder = 'Mô tả sự cố đang xảy ra, tình trạng xe và nội dung ảnh minh chứng...';
    }
})();
