(() => {
    document.addEventListener('DOMContentLoaded', () => {
        if (!/^\/Handovers\/Create/i.test(window.location.pathname)) return;

        const block = document.querySelector('[data-counter-citizen-evidence]');
        if (!(block instanceof HTMLElement) || block.querySelector('[data-kyc-citizen-reference]')) return;

        const form = block.closest('form');
        const bookingId = Number(
            form?.querySelector('input[name="BookingId"]')?.value ||
            new URLSearchParams(window.location.search).get('bookingId'));
        if (!Number.isInteger(bookingId) || bookingId <= 0) return;

        const currentInputsRow = block.querySelector('.row.g-3.mt-1');
        if (!(currentInputsRow instanceof HTMLElement)) return;

        const reference = document.createElement('div');
        reference.className = 'staff-citizen-comparison mt-3';
        reference.dataset.kycCitizenReference = 'true';
        reference.innerHTML = `
            <div class="small fw-semibold mb-2">CCCD KYC đã duyệt để đối chiếu</div>
            <div class="row g-2">
                ${referenceCard('front', 'Mặt trước', bookingId)}
                ${referenceCard('back', 'Mặt sau', bookingId)}
            </div>
            <div class="form-text mt-2">
                Ảnh KYC chỉ là tham chiếu. Bộ ảnh CCCD người đang có mặt tại quầy được preview đúng một lần ngay dưới ô chọn ảnh.
            </div>`;

        currentInputsRow.before(reference);
    });

    function referenceCard(side, label, bookingId) {
        const src = `/StaffCounterIdentityEvidence/KycCitizen?bookingId=${bookingId}&side=${side}`;
        return `<div class="col-sm-6">
            <div class="staff-secure-doc-frame border rounded-3 bg-body p-2 h-100">
                <div class="small text-muted mb-1">${label}</div>
                <img src="${src}" alt="CCCD KYC ${label}" loading="lazy"
                     class="img-fluid rounded-2 border bg-white w-100"
                     style="height:150px;object-fit:contain" />
                <span class="small text-muted staff-secure-doc-empty d-none">Chưa có ảnh KYC khả dụng</span>
            </div>
        </div>`;
    }

    document.addEventListener('error', event => {
        const img = event.target;
        if (!(img instanceof HTMLImageElement) || !img.closest('[data-kyc-citizen-reference]')) return;
        img.classList.add('d-none');
        img.nextElementSibling?.classList.remove('d-none');
    }, true);
})();