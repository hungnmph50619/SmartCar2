(() => {
    async function enhanceRefundApprovalForm(form) {
        const bookingInput = form.querySelector('input[name="bookingId"]');
        const button = form.querySelector('button[type="submit"]');
        if (!bookingInput || !button) return;

        const bookingId = bookingInput.value;
        if (!bookingId) return;

        try {
            const response = await fetch(`/AdminBusinessSettings/RefundEligibility?bookingId=${encodeURIComponent(bookingId)}`, {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            });

            if (!response.ok) return;
            const data = await response.json();
            if (!data?.applies) return;

            const notice = document.createElement('div');
            notice.className = data.isEligible
                ? 'alert alert-success py-2 px-3 small mb-2'
                : 'alert alert-warning py-2 px-3 small mb-2';
            notice.textContent = data.message;
            form.parentElement?.insertBefore(notice, form);

            if (!data.isEligible) {
                button.disabled = true;
                button.classList.remove('btn-warning');
                button.classList.add('btn-outline-secondary');
                button.textContent = `Giữ cọc đến ${data.eligibleAt}`;
                button.title = 'Chưa đủ thời gian giữ cọc theo chính sách đã chụp trên booking.';
            } else if (data.holdDays === 0) {
                button.title = 'Booking áp dụng 0 ngày giữ cọc nên có thể duyệt ngay sau hậu kiểm.';
            }
        } catch {
            // Nếu phần hiển thị phụ không tải được, backend/database vẫn giữ chốt nghiệp vụ.
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        document
            .querySelectorAll('form[action*="AdminPayments/ApproveRefundBatch"]')
            .forEach(form => enhanceRefundApprovalForm(form));
    });
})();
