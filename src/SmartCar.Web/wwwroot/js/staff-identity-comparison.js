(() => {
    document.addEventListener('DOMContentLoaded', () => {
        if (!/^\/Handovers\/Create/i.test(window.location.pathname)) return;

        const block = document.querySelector('[data-counter-citizen-evidence]');
        if (!(block instanceof HTMLElement)) return;

        const form = block.closest('form');
        const bookingId = Number(
            form?.querySelector('input[name="BookingId"]')?.value ||
            new URLSearchParams(window.location.search).get('bookingId'));
        if (!Number.isInteger(bookingId) || bookingId <= 0) return;

        if (block.querySelector('[data-kyc-citizen-reference]')) return;

        const reference = document.createElement('div');
        reference.className = 'staff-citizen-comparison mt-3';
        reference.dataset.kycCitizenReference = 'true';
        reference.innerHTML = `
            <div class="small fw-semibold mb-2">CCCD hồ sơ KYC đã duyệt để đối chiếu</div>
            <div class="border rounded-3 p-2 bg-body">
                <div class="row g-2">
                    <div class="col-sm-6">
                        <div class="small text-muted mb-1">Mặt trước</div>
                        <div class="staff-secure-doc-frame">
                            <img src="/StaffCounterIdentityEvidence/KycCitizen?bookingId=${bookingId}&side=front"
                                 alt="CCCD KYC mặt trước"
                                 loading="lazy" />
                            <span class="small text-muted staff-secure-doc-empty">Chưa có ảnh khả dụng</span>
                        </div>
                    </div>
                    <div class="col-sm-6">
                        <div class="small text-muted mb-1">Mặt sau</div>
                        <div class="staff-secure-doc-frame">
                            <img src="/StaffCounterIdentityEvidence/KycCitizen?bookingId=${bookingId}&side=back"
                                 alt="CCCD KYC mặt sau"
                                 loading="lazy" />
                            <span class="small text-muted staff-secure-doc-empty">Chưa có ảnh khả dụng</span>
                        </div>
                    </div>
                </div>
            </div>
            <div class="form-text mt-2">
                Hai ảnh CCCD người đang có mặt đã được preview ngay tại ô chọn phía trên.
                Không render lại lần hai để tránh Staff nhầm ảnh hiện tại với ảnh KYC.
            </div>`;

        const actionRow = block.querySelector('[data-counter-citizen-save]')?.closest('.d-flex');
        if (actionRow) actionRow.before(reference);
        else block.appendChild(reference);
    });

    document.addEventListener('error', (event) => {
        const img = event.target;
        if (!(img instanceof HTMLImageElement) || !img.closest('.staff-secure-doc-frame')) return;
        img.classList.add('d-none');
        img.nextElementSibling?.classList.remove('d-none');
    }, true);

    document.addEventListener('load', (event) => {
        const img = event.target;
        if (!(img instanceof HTMLImageElement) || !img.closest('.staff-secure-doc-frame')) return;
        img.classList.remove('d-none');
        img.nextElementSibling?.classList.add('d-none');
    }, true);
})();
