(() => {
    const init = () => {
        if (!/\/AdminCustomers\/Details/i.test(window.location.pathname)) return;
        const params = new URLSearchParams(window.location.search);
        if ((params.get('tab') || '').toLowerCase() !== 'documents') return;

        const citizenForm = [...document.querySelectorAll('form')]
            .find(form => /AdminCustomers\/VerifyCitizenId/i.test(form.action));
        const licenseForm = [...document.querySelectorAll('form')]
            .find(form => /AdminKyc\/VerifyDrivingLicense/i.test(form.action));
        if (!citizenForm || !licenseForm) return;
        if (document.querySelector('[data-kyc-bundle-review]')) return;

        const customerId = citizenForm.querySelector('input[name="customerId"]')?.value ||
            licenseForm.querySelector('input[name="customerId"]')?.value;
        const token = citizenForm.querySelector('input[name="__RequestVerificationToken"]')?.value ||
            licenseForm.querySelector('input[name="__RequestVerificationToken"]')?.value;
        if (!customerId || !token) return;

        citizenForm.classList.add('d-none');
        licenseForm.classList.add('d-none');

        const card = document.createElement('section');
        card.className = 'card border-primary shadow-sm mb-4';
        card.dataset.kycBundleReview = '';
        card.innerHTML = `
            <div class="card-body p-4">
                <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap mb-3">
                    <div>
                        <div class="text-uppercase small text-primary fw-semibold">DUYỆT HỒ SƠ KYC</div>
                        <h3 class="h4 fw-bold mb-1">CCCD + GPLX — duyệt một lần</h3>
                        <p class="text-muted mb-0">Khách hàng đã gửi đủ hai loại giấy tờ. Hãy xem cả hai phần bên dưới rồi xác nhận toàn bộ hồ sơ trong một lần.</p>
                    </div>
                    <span class="badge bg-warning text-dark px-3 py-2">Chờ duyệt</span>
                </div>

                <form method="post" action="/AdminKyc/VerifyAll" data-loading-form data-confirm="Xác nhận toàn bộ CCCD và GPLX của khách hàng đều hợp lệ?">
                    <input type="hidden" name="__RequestVerificationToken" value="${escapeHtml(token)}" />
                    <input type="hidden" name="customerId" value="${escapeHtml(customerId)}" />
                    <fieldset class="document-checklist mb-3">
                        <legend>Checklist trước khi duyệt</legend>
                        <label><input class="form-check-input" type="checkbox" required /> CCCD: thông tin khai báo khớp với mặt trước và mặt sau.</label>
                        <label><input class="form-check-input" type="checkbox" required /> GPLX: số bằng, hạng bằng và thời hạn khớp với ảnh.</label>
                        <label><input class="form-check-input" type="checkbox" required /> Cả CCCD và GPLX còn hiệu lực, ảnh rõ và không có dấu hiệu bất thường.</label>
                        <label><input class="form-check-input" type="checkbox" required /> Đã đối chiếu đầy đủ cả 4 ảnh trước khi phê duyệt.</label>
                    </fieldset>
                    <button class="btn btn-success" type="submit" data-loading-text="Đang duyệt toàn bộ KYC...">✓ Duyệt toàn bộ hồ sơ KYC</button>
                    <div class="small text-muted mt-2">Nếu một giấy tờ có vấn đề, dùng nút “Yêu cầu cập nhật” ngay tại phần CCCD hoặc GPLX thay vì duyệt toàn bộ.</div>
                </form>
            </div>`;

        const firstSection = citizenForm.closest('section');
        firstSection?.insertAdjacentElement('beforebegin', card);
    };

    const escapeHtml = value => (value || '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
